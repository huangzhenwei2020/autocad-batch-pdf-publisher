using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public sealed class DrawingScanner
    {
        private static readonly string[] BuildingTags = { "楼栋", "BUILDING", "栋号" };
        private static readonly string[] NumberTags = { "图号", "SHEETNO", "SHEET_NO", "DRAWINGNO" };
        private static readonly string[] NameTags = { "图名", "SHEETNAME", "SHEET_NAME", "DRAWINGNAME" };
        private static readonly string[] ScaleTags = { "比例", "SCALE", "PRINTSCALE" };

        public IReadOnlyList<SheetItem> Scan(Document document, IEnumerable<FrameDefinition> frameDefinitions)
        {
            var frames = frameDefinitions.ToList();
            var result = new List<SheetItem>();
            var database = document.Database;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    var reference = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (reference == null) continue;
                    var blockName = GetBlockName(reference, transaction);
                    var frame = frames.FirstOrDefault(x => string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));
                    if (frame == null) continue;
                    var attributes = ReadAttributes(reference, transaction);
                    result.Add(new SheetItem
                    {
                        BlockId = reference.ObjectId,
                        Building = GetAttribute(attributes, frame.BuildingAttributeTag, BuildingTags, "未分组"),
                        SheetNumber = GetAttribute(attributes, frame.SheetNumberAttributeTag, NumberTags, "未填写图号"),
                        SheetName = GetAttribute(attributes, frame.SheetNameAttributeTag, NameTags, "未填写图名"),
                        Frame = frame.PaperSize,
                        Extension = frame.Extension,
                        PrintScale = GetAttribute(attributes, frame.PrintScaleAttributeTag, ScaleTags, "1:1"),
                        SourceFile = document.Name
                    });
                }
                transaction.Commit();
            }
            return result.OrderBy(x => x.Building).ThenBy(x => x.SheetNumber).Select((x, i) => { x.Order = i + 1; return x; }).ToList();
        }

        private static string GetBlockName(BlockReference reference, Transaction transaction)
        {
            var record = (BlockTableRecord)transaction.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead);
            return record.Name;
        }

        private static Dictionary<string, string> ReadAttributes(BlockReference reference, Transaction transaction)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = transaction.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (attribute != null) values[attribute.Tag] = attribute.TextString;
            }
            return values;
        }

        private static string GetAttribute(IDictionary<string, string> values, string selectedTag, IEnumerable<string> aliases, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(selectedTag) && values.TryGetValue(selectedTag, out var selectedValue) && !string.IsNullOrWhiteSpace(selectedValue)) return selectedValue.Trim();
            foreach (var alias in aliases)
                if (values.TryGetValue(alias, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
            return fallback;
        }
    }
}
