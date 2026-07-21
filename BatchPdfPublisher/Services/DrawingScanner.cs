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
                    try
                    {
                        var blockName = GetBlockName(reference, transaction);
                        var frame = frames.FirstOrDefault(x => string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));
                        if (frame == null) continue;
                        if (!TryGetUsableExtents(reference, out var extents)) continue;
                        var attributes = ReadAttributes(reference, transaction);
                        result.Add(new SheetItem
                        {
                        BlockId = reference.ObjectId,
                        BlockHandle = reference.Handle.ToString(),
                            Building = GetAttribute(attributes, frame.BuildingAttributeTag, frame.DefaultBuilding, "未分组"),
                            SheetNumber = GetAttribute(attributes, frame.SheetNumberAttributeTag, frame.DefaultSheetNumber, "未填写图号"),
                            SheetName = GetAttribute(attributes, frame.SheetNameAttributeTag, frame.DefaultSheetName, "未填写图名"),
                            Frame = frame.PaperSize,
                            Extension = frame.Extension,
                            FrameNote = frame.Note,
                            PaperOrientation = string.IsNullOrWhiteSpace(frame.PaperOrientation) ? (extents.MaxPoint.X - extents.MinPoint.X >= extents.MaxPoint.Y - extents.MinPoint.Y ? "横向" : "纵向") : frame.PaperOrientation,
                            PrintScale = GetAttribute(attributes, frame.PrintScaleAttributeTag, frame.DefaultPrintScale, "1:1"),
                            PlotStyle = "使用输出设置",
                            SourceFile = document.Name,
                            MinX = extents.MinPoint.X,
                            MinY = extents.MinPoint.Y,
                            MaxX = extents.MaxPoint.X,
                            MaxY = extents.MaxPoint.Y
                        });
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Proxy or partially loaded third-party blocks can report invalid extents.
                        // A single damaged block must not abort the entire drawing scan.
                    }
                }
                transaction.Commit();
            }
            var sorted = result.OrderBy(x => x.Building)
                .ThenBy(x => TitlePriority(x.SheetName))
                .ThenBy(x => x.SheetNumber)
                .ThenBy(x => x.SheetName)
                .ToList();
            foreach (var group in sorted.GroupBy(x => x.Building))
            {
                var order = 1;
                foreach (var item in group) item.Order = order++;
            }
            return sorted;
        }

        private static int TitlePriority(string sheetName)
        {
            if (string.IsNullOrWhiteSpace(sheetName)) return 2;
            if (sheetName.IndexOf("封面", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (sheetName.IndexOf("目录", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 2;
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

        private static string GetAttribute(IDictionary<string, string> values, string selectedTag, string configuredFallback, string emptyFallback)
        {
            if (!string.IsNullOrWhiteSpace(selectedTag) && values.TryGetValue(selectedTag, out var selectedValue) && !string.IsNullOrWhiteSpace(selectedValue)) return selectedValue.Trim();
            return string.IsNullOrWhiteSpace(configuredFallback) ? emptyFallback : configuredFallback.Trim();
        }

        private static bool TryGetUsableExtents(BlockReference reference, out Extents3d extents)
        {
            extents = default(Extents3d);
            try { extents = reference.GeometricExtents; }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // T20/proxy block references can reject GeometricExtents even though
                // AutoCAD has a usable display bounds box. Bounds keeps those registered
                // frames visible in the scan and preview without exploding the command.
                try
                {
                    var bounds = reference.Bounds;
                    if (bounds.HasValue) extents = bounds.Value;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception) { return false; }
            }
            var values = new[] { extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y };
            if (values.Any(x => double.IsNaN(x) || double.IsInfinity(x))) return false;
            return extents.MaxPoint.X > extents.MinPoint.X && extents.MaxPoint.Y > extents.MinPoint.Y;
        }
    }
}
