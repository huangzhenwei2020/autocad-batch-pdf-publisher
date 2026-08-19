using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    internal static class FrameIdentityService
    {
        public static string AttributeSignature(IEnumerable<string> tags) => string.Join("|",
            (tags ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant()).Distinct().OrderBy(x => x));

        public static string DefinitionSignature(BlockReference reference, Transaction transaction)
        {
            try
            {
                var recordId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                var record = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                if (record == null) return string.Empty;
                var parts = new List<string>();
                foreach (ObjectId id in record)
                {
                    if (!IsUsable(id)) continue;
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;
                    var part = entity.GetRXClass().Name;
                    var attribute = entity as AttributeDefinition;
                    if (attribute != null) part += ":" + (attribute.Tag ?? string.Empty).ToUpperInvariant();
                    try
                    {
                        var extents = entity.GeometricExtents;
                        part += ":" + Coordinate(extents.MinPoint.X) + "," + Coordinate(extents.MinPoint.Y)
                            + "," + Coordinate(extents.MaxPoint.X) + "," + Coordinate(extents.MaxPoint.Y);
                    }
                    catch { }
                    parts.Add(part);
                }
                parts.Sort(StringComparer.Ordinal);
                using (var sha = SHA256.Create())
                    return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(";", parts))));
            }
            catch { return string.Empty; }
        }

        public static double AspectRatio(Extents3d extents)
        {
            var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
            var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            return Math.Min(width, height) <= 1e-9 ? 0d : Math.Max(width, height) / Math.Min(width, height);
        }

        public static FrameDefinition SelectBest(IEnumerable<FrameDefinition> source, string blockName,
            string attributeSignature, string definitionSignature, double aspectRatio)
        {
            var candidates = (source ?? Enumerable.Empty<FrameDefinition>())
                .Where(x => string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count <= 1) return candidates.FirstOrDefault();
            var exactDefinition = candidates.Where(x => !string.IsNullOrWhiteSpace(x.DefinitionSignature)
                && string.Equals(x.DefinitionSignature, definitionSignature, StringComparison.Ordinal)).ToList();
            if (exactDefinition.Count == 1) return exactDefinition[0];
            var exactAttributes = candidates.Where(x => !string.IsNullOrWhiteSpace(x.AttributeTagSignature)
                && string.Equals(x.AttributeTagSignature, attributeSignature, StringComparison.OrdinalIgnoreCase)).ToList();
            var pool = exactAttributes.Count > 0 ? exactAttributes : candidates;
            return pool.OrderBy(x => x.ReferenceAspectRatio <= 0d || aspectRatio <= 0d
                ? double.MaxValue : Math.Abs(x.ReferenceAspectRatio - aspectRatio)).FirstOrDefault();
        }

        public static bool IsSameVariant(FrameDefinition frame, string attributeSignature,
            string definitionSignature, double aspectRatio)
        {
            if (frame == null) return false;
            if (!string.IsNullOrWhiteSpace(frame.DefinitionSignature) && !string.IsNullOrWhiteSpace(definitionSignature))
                return string.Equals(frame.DefinitionSignature, definitionSignature, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(frame.AttributeTagSignature) &&
                !string.Equals(frame.AttributeTagSignature, attributeSignature, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(frame.AttributeTagSignature) && string.IsNullOrWhiteSpace(frame.DefinitionSignature))
            {
                var actualTags = new HashSet<string>((attributeSignature ?? string.Empty).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                var configuredTags = new[] { frame.BuildingAttributeTag, frame.SheetNumberAttributeTag, frame.SheetNameAttributeTag, frame.PrintScaleAttributeTag }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim());
                if (configuredTags.Any(x => !actualTags.Contains(x))) return false;
                var expected = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, frame.PaperOrientation);
                var expectedRatio = expected == null || expected.Length < 2 || Math.Min(expected[0], expected[1]) <= 0d
                    ? 0d : Math.Max(expected[0], expected[1]) / Math.Min(expected[0], expected[1]);
                return expectedRatio <= 0d || aspectRatio <= 0d || Math.Abs(expectedRatio - aspectRatio) / expectedRatio <= 0.02d;
            }
            return frame.ReferenceAspectRatio <= 0d || aspectRatio <= 0d || Math.Abs(frame.ReferenceAspectRatio - aspectRatio) <= 0.005d;
        }

        private static string Coordinate(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
        private static bool IsUsable(ObjectId id)
        {
            try { return id.IsValid && !id.IsNull && !id.IsErased; }
            catch { return false; }
        }
    }
}
