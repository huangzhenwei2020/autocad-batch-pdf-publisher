using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace BatchPdfPublisher.Services
{
    public sealed class AttributeDefinitionEditRow
    {
        public ObjectId DefinitionId { get; set; }
        public string OldTag { get; set; }
        public string Tag { get; set; }
        public string Prompt { get; set; }
        public string DefaultValue { get; set; }
        public double Height { get; set; }
        public double WidthFactor { get; set; }
        public string TextStyle { get; set; }
        public string Alignment { get; set; }
        public string OriginalAlignment { get; set; }
        public bool Invisible { get; set; }
        public bool Constant { get; set; }
    }

    public sealed class AttributeDefinitionEditContext
    {
        public ObjectId DefinitionId { get; set; }
        public string BlockName { get; set; }
        public List<string> TextStyles { get; } = new List<string>();
        public List<AttributeDefinitionEditRow> Rows { get; } = new List<AttributeDefinitionEditRow>();
    }

    public static class AttributeDefinitionEditorService
    {
        public static AttributeDefinitionEditContext Read(Document document, ObjectId blockReferenceId)
        {
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var reference = transaction.GetObject(blockReferenceId, OpenMode.ForRead, false) as BlockReference;
                if (reference == null) throw new InvalidOperationException("选择的对象不是图块参照。");
                var definitionId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                return ReadContext(document.Database, definitionId, transaction);
            }
        }

        public static AttributeDefinitionEditContext Read(Document document, string blockName)
        {
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var table = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (table == null) throw new InvalidOperationException("无法读取图块表。");
                ObjectId definitionId = ObjectId.Null;
                foreach (ObjectId id in table)
                {
                    var record = transaction.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                    if (record != null && string.Equals(record.Name, blockName, StringComparison.OrdinalIgnoreCase)) { definitionId = id; break; }
                }
                if (definitionId.IsNull) throw new InvalidOperationException("当前 DWG 中找不到图块“" + blockName + "”。");
                return ReadContext(document.Database, definitionId, transaction);
            }
        }

        public static int Apply(Document document, AttributeDefinitionEditContext context, IEnumerable<AttributeDefinitionEditRow> sourceRows, string requestedBlockName)
        {
            var rows = sourceRows?.ToList() ?? new List<AttributeDefinitionEditRow>();
            if (rows.Count == 0) throw new InvalidOperationException("当前图块没有属性定义。");
            var emptyTags = rows.Where(x => string.IsNullOrWhiteSpace(x.Tag)).ToList();
            if (emptyTags.Count > 0) throw new InvalidOperationException("属性 TAG 不能为空。");
            var duplicateTags = rows.GroupBy(x => x.Tag.Trim(), StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
            if (duplicateTags.Count > 0) throw new InvalidOperationException("仍存在重复属性 TAG：" + string.Join("、", duplicateTags));

            var updatedReferences = 0;
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                if (!IsUsable(context.DefinitionId)) throw new InvalidOperationException("图块定义已经失效，请重新拾取图块。");
                var record = transaction.GetObject(context.DefinitionId, OpenMode.ForWrite) as BlockTableRecord;
                if (record == null) throw new InvalidOperationException("无法读取图块定义。");
                var newBlockName = (requestedBlockName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(newBlockName)) throw new InvalidOperationException("图块名称不能为空。");
                if (record.IsAnonymous || record.IsFromExternalReference || record.IsFromOverlayReference || record.IsLayout)
                    throw new InvalidOperationException("匿名块、外部参照块或布局块不能在此重命名。");
                var blockTable = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (!string.Equals(record.Name, newBlockName, StringComparison.OrdinalIgnoreCase) && blockTable != null && blockTable.Has(newBlockName))
                    throw new InvalidOperationException("图块名称“" + newBlockName + "”已经存在，请换一个名称。");
                if (!string.Equals(record.Name, newBlockName, StringComparison.Ordinal)) record.Name = newBlockName;
                var styleTable = transaction.GetObject(document.Database.TextStyleTableId, OpenMode.ForRead) as TextStyleTable;
                var definitions = new Dictionary<ObjectId, AttributeDefinition>();
                foreach (var row in rows)
                {
                    if (!IsUsable(row.DefinitionId)) continue;
                    var definition = transaction.GetObject(row.DefinitionId, OpenMode.ForWrite, false) as AttributeDefinition;
                    if (definition == null) continue;
                    definition.Tag = row.Tag.Trim();
                    definition.Prompt = row.Prompt ?? string.Empty;
                    definition.TextString = row.DefaultValue ?? string.Empty;
                    definition.Height = Math.Max(row.Height, 0.0001d);
                    definition.WidthFactor = Math.Max(0.01d, Math.Min(100d, row.WidthFactor));
                    if (styleTable != null && !string.IsNullOrWhiteSpace(row.TextStyle) && styleTable.Has(row.TextStyle)) definition.TextStyleId = styleTable[row.TextStyle];
                    var alignmentChanged = !string.Equals(row.Alignment, row.OriginalAlignment, StringComparison.Ordinal);
                    if (alignmentChanged) ApplyAlignment(definition, row.Alignment, document.Database);
                    definition.Invisible = row.Invisible;
                    definitions[row.DefinitionId] = definition;
                }

                foreach (var reference in FindReferences(document.Database, context.DefinitionId, transaction))
                {
                    var queues = rows.GroupBy(x => x.OldTag ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => new Queue<AttributeDefinitionEditRow>(x), StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId attributeId in reference.AttributeCollection)
                    {
                        var attribute = transaction.GetObject(attributeId, OpenMode.ForWrite, false) as AttributeReference;
                        if (attribute == null || !queues.TryGetValue(attribute.Tag ?? string.Empty, out var queue) || queue.Count == 0) continue;
                        var row = queue.Dequeue();
                        if (!definitions.TryGetValue(row.DefinitionId, out var definition)) continue;
                        var currentValue = attribute.TextString;
                        var rotation = attribute.Rotation;
                        var oldAnchor = DisplayAnchor(attribute);
                        ApplyDefinitionFormatting(attribute, definition);
                        if (!string.Equals(row.Alignment, row.OriginalAlignment, StringComparison.Ordinal))
                            ApplyAlignment(attribute, definition.HorizontalMode, definition.VerticalMode, oldAnchor, document.Database);
                        attribute.TextString = currentValue;
                        attribute.Tag = row.Tag.Trim();
                        attribute.Rotation = rotation;
                        updatedReferences++;
                    }
                }
                transaction.Commit();
            }
            return updatedReferences;
        }

        public static AttributeTarget FindFirstInstance(Document document, AttributeDefinitionEditContext context, AttributeDefinitionEditRow selectedRow)
        {
            if (document == null || context == null || selectedRow == null || !IsUsable(context.DefinitionId)) return null;
            var sameTagRows = context.Rows.Where(x => string.Equals(x.OldTag ?? string.Empty, selectedRow.OldTag ?? string.Empty, StringComparison.OrdinalIgnoreCase)).ToList();
            var occurrence = sameTagRows.FindIndex(x => x.DefinitionId == selectedRow.DefinitionId);
            if (occurrence < 0) occurrence = 0;

            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var reference in FindReferences(document.Database, context.DefinitionId, transaction))
                {
                    var matches = new List<AttributeReference>();
                    foreach (ObjectId attributeId in reference.AttributeCollection)
                    {
                        var attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                        if (attribute != null && string.Equals(attribute.Tag ?? string.Empty, selectedRow.OldTag ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                            matches.Add(attribute);
                    }
                    if (matches.Count <= occurrence) continue;
                    var match = matches[occurrence];
                    var minPoint = reference.Position;
                    var maxPoint = reference.Position;
                    try
                    {
                        var extents = reference.GeometricExtents;
                        minPoint = extents.MinPoint;
                        maxPoint = extents.MaxPoint;
                    }
                    catch { }
                    return new AttributeTarget
                    {
                        BlockId = reference.ObjectId,
                        AttributeId = match.ObjectId,
                        BlockName = context.BlockName,
                        BlockHandle = reference.Handle.ToString(),
                        Tag = match.Tag ?? selectedRow.Tag ?? string.Empty,
                        OldValue = match.TextString ?? string.Empty,
                        NewValue = match.TextString ?? string.Empty,
                        Center = reference.Position,
                        MinPoint = minPoint,
                        MaxPoint = maxPoint,
                        AttributePosition = match.Position
                    };
                }
            }
            return null;
        }

        private static void ApplyDefinitionFormatting(AttributeReference attribute, AttributeDefinition definition)
        {
            attribute.Height = definition.Height;
            attribute.WidthFactor = definition.WidthFactor;
            attribute.TextStyleId = definition.TextStyleId;
            attribute.Invisible = definition.Invisible;
        }

        private static void ApplyAlignment(AttributeReference attribute, TextHorizontalMode horizontal, TextVerticalMode vertical, Point3d anchor, Database database)
        {
            attribute.HorizontalMode = horizontal;
            attribute.VerticalMode = vertical;
            RestoreDisplayAnchor(attribute, anchor);
            try { attribute.AdjustAlignment(database); } catch { }
            RestoreDisplayAnchor(attribute, anchor);
        }

        private static Point3d DisplayAnchor(AttributeReference attribute)
        {
            try
            {
                if (!attribute.IsDefaultAlignment) return attribute.AlignmentPoint;
            }
            catch { }
            return attribute.Position;
        }

        private static void RestoreDisplayAnchor(AttributeReference attribute, Point3d anchor)
        {
            try
            {
                if (!attribute.IsDefaultAlignment) attribute.AlignmentPoint = anchor;
                else attribute.Position = anchor;
            }
            catch { attribute.Position = anchor; }
        }

        private static AttributeDefinitionEditContext ReadContext(Database database, ObjectId definitionId, Transaction transaction)
        {
            var record = transaction.GetObject(definitionId, OpenMode.ForRead) as BlockTableRecord;
            if (record == null) throw new InvalidOperationException("无法读取图块定义。");
            var context = new AttributeDefinitionEditContext { DefinitionId = definitionId, BlockName = record.Name };
            var styles = transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead) as TextStyleTable;
            if (styles != null) foreach (ObjectId styleId in styles) { var style = transaction.GetObject(styleId, OpenMode.ForRead) as TextStyleTableRecord; if (style != null) context.TextStyles.Add(style.Name); }
            foreach (ObjectId id in record)
            {
                var definition = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                if (definition == null) continue;
                var style = transaction.GetObject(definition.TextStyleId, OpenMode.ForRead) as TextStyleTableRecord;
                context.Rows.Add(new AttributeDefinitionEditRow
                {
                    DefinitionId = id, OldTag = definition.Tag ?? string.Empty, Tag = definition.Tag ?? string.Empty,
                    Prompt = definition.Prompt ?? string.Empty, DefaultValue = definition.TextString ?? string.Empty,
                    Height = definition.Height, WidthFactor = definition.WidthFactor, TextStyle = style?.Name ?? string.Empty,
                    Alignment = AlignmentName(definition.HorizontalMode, definition.VerticalMode),
                    OriginalAlignment = AlignmentName(definition.HorizontalMode, definition.VerticalMode), Invisible = definition.Invisible, Constant = definition.Constant
                });
            }
            return context;
        }

        private static IEnumerable<BlockReference> FindReferences(Database database, ObjectId definitionId, Transaction transaction)
        {
            var table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (table == null) yield break;
            foreach (ObjectId recordId in table)
            {
                var space = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                if (space == null || !space.IsLayout) continue;
                foreach (ObjectId id in space)
                {
                    var reference = transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                    if (reference == null) continue;
                    var effectiveId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                    if (effectiveId == definitionId) yield return reference;
                }
            }
        }

        private static void ApplyAlignment(AttributeDefinition definition, string alignment, Database database)
        {
            var anchor = DisplayAnchor(definition);
            var value = alignment ?? "左下";
            definition.HorizontalMode = value.Contains("右") ? TextHorizontalMode.TextRight : value.Contains("中") || value == "居中" ? TextHorizontalMode.TextCenter : TextHorizontalMode.TextLeft;
            definition.VerticalMode = value.Contains("上") ? TextVerticalMode.TextTop : value.Contains("中") || value == "居中" ? TextVerticalMode.TextVerticalMid : TextVerticalMode.TextBottom;
            RestoreDisplayAnchor(definition, anchor);
            try { definition.AdjustAlignment(database); } catch { }
            RestoreDisplayAnchor(definition, anchor);
        }

        private static Point3d DisplayAnchor(AttributeDefinition definition)
        {
            try { if (!definition.IsDefaultAlignment) return definition.AlignmentPoint; } catch { }
            return definition.Position;
        }

        private static void RestoreDisplayAnchor(AttributeDefinition definition, Point3d anchor)
        {
            try
            {
                if (!definition.IsDefaultAlignment) definition.AlignmentPoint = anchor;
                else definition.Position = anchor;
            }
            catch { definition.Position = anchor; }
        }

        private static string AlignmentName(TextHorizontalMode horizontal, TextVerticalMode vertical)
        {
            var h = horizontal == TextHorizontalMode.TextRight ? "右" : horizontal == TextHorizontalMode.TextCenter || horizontal == TextHorizontalMode.TextMid ? "中" : "左";
            var v = vertical == TextVerticalMode.TextTop ? "上" : vertical == TextVerticalMode.TextVerticalMid ? "中" : "下";
            return h == "中" && v == "中" ? "居中" : h + v;
        }

        private static bool IsUsable(ObjectId id) { try { return id.IsValid && !id.IsNull && !id.IsErased; } catch { return false; } }
    }
}
