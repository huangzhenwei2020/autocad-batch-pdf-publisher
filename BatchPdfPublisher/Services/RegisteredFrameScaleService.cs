using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    internal static class RegisteredFrameScaleService
    {
        public static FrameDefinition Match(BlockReference reference, Transaction transaction, IEnumerable<FrameDefinition> source)
        {
            if (reference == null || transaction == null) return null;
            var recordId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
            var record = transaction.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
            if (record == null) return null;
            var candidates = (source ?? Enumerable.Empty<FrameDefinition>())
                .Where(x => x != null && string.Equals(x.BlockName, record.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) return null;

            var tags = new List<string>();
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeReference;
                if (attribute != null && !string.IsNullOrWhiteSpace(attribute.Tag)) tags.Add(attribute.Tag);
            }
            var attributeSignature = FrameIdentityService.AttributeSignature(tags);
            var definitionSignature = FrameIdentityService.DefinitionSignature(reference, transaction);
            double aspectRatio;
            try { aspectRatio = FrameIdentityService.AspectRatio(reference.GeometricExtents); }
            catch { aspectRatio = 0d; }

            // Prefer an exact registered variant. If the user has made a small
            // temporary edit to that block definition, fall back to the same
            // registered block name, TAG set and closest frame aspect ratio.
            return FrameIdentityService.SelectBest(candidates, record.Name,
                attributeSignature, definitionSignature, aspectRatio);
        }

        public static bool UpdateScaleAttribute(BlockReference reference, Transaction transaction,
            FrameDefinition frame, int targetScale)
        {
            if (reference == null || transaction == null || frame == null) return false;
            var candidates = new List<AttributeReference>();
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeReference;
                if (attribute != null && !string.IsNullOrWhiteSpace(attribute.Tag) &&
                    attribute.Tag.IndexOf("比例", StringComparison.OrdinalIgnoreCase) >= 0)
                    candidates.Add(attribute);
            }
            if (candidates.Count == 0) return false;
            var configuredTag = (frame.PrintScaleAttributeTag ?? string.Empty).Trim();
            var target = candidates.FirstOrDefault(x => string.Equals(x.Tag, configuredTag, StringComparison.OrdinalIgnoreCase))
                ?? (candidates.Count == 1 ? candidates[0] : null);
            if (target == null) return false;
            var value = "1:" + Math.Max(1, targetScale);
            if (string.Equals(target.TextString, value, StringComparison.Ordinal)) return false;
            target.UpgradeOpen();
            target.TextString = value;
            target.RecordGraphicsModified(true);
            return true;
        }
    }
}
