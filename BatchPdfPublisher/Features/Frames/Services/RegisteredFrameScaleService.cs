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

            var tags = new List<string>();
            var attributes = new List<AttributeReference>();
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeReference;
                if (attribute == null || string.IsNullOrWhiteSpace(attribute.Tag)) continue;
                tags.Add(attribute.Tag);
                attributes.Add(attribute);
            }
            if (candidates.Count == 0)
            {
                // Scale Manager must still protect a title block when the active
                // project changed, the block was renamed, or ATTSYNC changed its
                // definition signature. A TAG/prompt containing "比例" is an
                // explicit enough signal: update that one attribute and never run
                // the whole block through ordinary text/block scale processing.
                var scaleAttribute = attributes
                    .Select(x => new { Attribute = x, Rank = ScaleMatchRank(reference, transaction, x) })
                    .Where(x => x.Rank < int.MaxValue)
                    .OrderBy(x => x.Rank)
                    .ThenBy(x => (x.Attribute.Tag ?? string.Empty).Length)
                    .Select(x => x.Attribute)
                    .FirstOrDefault();
                if (scaleAttribute == null) return null;
                return new FrameDefinition
                {
                    BlockName = record.Name,
                    PrintScaleAttributeTag = (scaleAttribute.Tag ?? string.Empty).Trim(),
                    AttributeTagSignature = FrameIdentityService.AttributeSignature(tags)
                };
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
            var attributes = new List<AttributeReference>();
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeReference;
                if (attribute != null && !string.IsNullOrWhiteSpace(attribute.Tag)) attributes.Add(attribute);
            }
            if (attributes.Count == 0) return false;

            // The registration mapping is a TAG, not a displayed value. Older
            // projects may contain a scale value (for example "1:100") in this
            // field, so only use it when it actually matches an attribute TAG.
            var configuredTag = (frame.PrintScaleAttributeTag ?? string.Empty).Trim();
            var target = attributes.FirstOrDefault(x => string.Equals((x.Tag ?? string.Empty).Trim(), configuredTag, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                // Prefer semantic TAG/prompt matches, then use the current text
                // value as a compatibility fallback for legacy registrations.
                target = attributes
                    .Select(x => new { Attribute = x, Rank = ScaleMatchRank(reference, transaction, x) })
                    .Where(x => x.Rank < int.MaxValue)
                    .OrderBy(x => x.Rank)
                    .ThenBy(x => (x.Attribute.Tag ?? string.Empty).Length)
                    .Select(x => x.Attribute)
                    .FirstOrDefault();
                if (target == null)
                {
                    var valueMatches = attributes.Where(x => IsScaleText(x.TextString)).ToList();
                    if (valueMatches.Count == 1) target = valueMatches[0];
                }
            }
            if (target == null) return false;

            var value = "1:" + Math.Max(1, targetScale);
            var textChanged = !string.Equals((target.TextString ?? string.Empty).Trim(), value, StringComparison.Ordinal);

            // A registered frame is paper/title-block data. Never rescale,
            // realign or synchronize its other attributes here: doing so can
            // overwrite their MText fragments or move them out of the title-block
            // cells. Only the mapped scale attribute is allowed to change.
            var invisible = target.Invisible;
            target.UpgradeOpen();
            if (target.IsMTextAttribute)
            {
                // TextString is only the plain-text facade for an MText
                // attribute. Persist through MTextAttribute itself, otherwise
                // the displayed value may remain unchanged after regeneration.
                using (var mtext = target.MTextAttribute)
                {
                    mtext.Contents = value;
                    target.MTextAttribute = mtext;
                }
                target.UpdateMTextAttribute();
            }
            else target.TextString = value;
            target.Invisible = invisible;
            target.RecordGraphicsModified(true);
            reference.UpgradeOpen();
            reference.RecordGraphicsModified(true);
            return textChanged;
        }

        private static int ScaleMatchRank(BlockReference reference, Transaction transaction, AttributeReference attribute)
        {
            if (attribute == null) return int.MaxValue;
            var tag = (attribute.Tag ?? string.Empty).Trim();
            if (string.Equals(tag, "比例", StringComparison.OrdinalIgnoreCase)) return 0;
            if (tag.IndexOf("比例", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            try
            {
                var recordId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                var record = transaction.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
                if (record == null) return int.MaxValue;
                foreach (ObjectId id in record)
                {
                    var definition = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                    if (definition == null || !string.Equals((definition.Tag ?? string.Empty).Trim(), tag, StringComparison.OrdinalIgnoreCase)) continue;
                    var prompt = (definition.Prompt ?? string.Empty).Trim();
                    if (string.Equals(prompt, "比例", StringComparison.OrdinalIgnoreCase)) return 2;
                    if (prompt.IndexOf("比例", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
                    break;
                }
            }
            catch { }
            return int.MaxValue;
        }

        private static bool IsScaleText(string value)
        {
            var text = (value ?? string.Empty).Trim().Replace('：', ':');
            var separator = text.LastIndexOf(':');
            if (separator < 0 || separator >= text.Length - 1) return false;
            double denominator;
            return double.TryParse(text.Substring(separator + 1).Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out denominator) && denominator > 0d;
        }
    }
}
