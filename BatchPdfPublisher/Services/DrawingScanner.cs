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
        public IList<string> GetLayoutNames(Database database)
        {
            var names = new List<string>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId recordId in blockTable)
                {
                    var record = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                    if (record == null || !record.IsLayout) continue;
                    var layout = transaction.GetObject(record.LayoutId, OpenMode.ForRead) as Layout;
                    if (layout != null && layout.ModelType) continue;
                    names.Add(layout == null ? record.Name : layout.LayoutName);
                }
                transaction.Commit();
            }
            return names.OrderBy(x => x).ToList();
        }

        public IList<SheetItem> Scan(Document document, IEnumerable<FrameDefinition> frameDefinitions, bool scanModelSpace = true, bool scanAllLayouts = true, IEnumerable<string> selectedLayouts = null)
        {
            return Scan(document.Database, string.IsNullOrWhiteSpace(document.Database.Filename) ? document.Name : document.Database.Filename, frameDefinitions, scanModelSpace, scanAllLayouts, selectedLayouts);
        }

        public IList<SheetItem> Scan(Database database, string sourceFile, IEnumerable<FrameDefinition> frameDefinitions, bool scanModelSpace = true, bool scanAllLayouts = true, IEnumerable<string> selectedLayouts = null)
        {
            var frames = frameDefinitions.ToList();
            var result = new List<SheetItem>();
            var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wantedLayouts = new HashSet<string>(selectedLayouts ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var spaces = new List<BlockTableRecord>();
                if (scanModelSpace) spaces.Add((BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead));
                foreach (ObjectId recordId in blockTable)
                {
                    var record = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                    if (record == null || !record.IsLayout) continue;
                    var layout = transaction.GetObject(record.LayoutId, OpenMode.ForRead) as Layout;
                    if (layout != null && layout.ModelType) continue;
                    var layoutName = layout == null ? record.Name : layout.LayoutName;
                    if (scanAllLayouts || wantedLayouts.Contains(layoutName)) spaces.Add(record);
                }
                foreach (var space in spaces)
                foreach (ObjectId id in space)
                {
                    var reference = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (reference == null) continue;
                    // A layout record must only be visited once. Keep an explicit
                    // identity guard as some third-party/T20 drawings can expose
                    // the same reference through proxy layout records.
                    if (!seenReferences.Add(reference.Handle.ToString())) continue;
                    try
                    {
                        var blockName = GetBlockName(reference, transaction);
                        if (!TryGetFrameGeometryExtents(reference, transaction, out var extents)) continue;
                        var attributes = ReadAttributes(reference, transaction);
                        var frame = FrameIdentityService.SelectBest(frames, blockName,
                            FrameIdentityService.AttributeSignature(attributes.Keys),
                            FrameIdentityService.DefinitionSignature(reference, transaction),
                            FrameIdentityService.AspectRatio(extents));
                        if (frame == null) continue;
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
                            SourceFile = sourceFile,
                            SourceLayout = SpaceName(space, transaction),
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
                .ThenBy(TitlePriority)
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

        private static int TitlePriority(SheetItem sheet)
        {
            var note = sheet?.FrameNote ?? string.Empty;
            if (note.IndexOf("封面", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (note.IndexOf("目录", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (note.IndexOf("总平图", StringComparison.OrdinalIgnoreCase) >= 0 || (sheet?.SheetNumber ?? string.Empty).IndexOf("总平图", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 3;
        }

        private static string SpaceName(BlockTableRecord space, Transaction transaction)
        {
            var layout = transaction.GetObject(space.LayoutId, OpenMode.ForRead) as Layout;
            if (layout != null && layout.ModelType) return "模型空间";
            return layout == null ? space.Name : layout.LayoutName;
        }

        private static string GetBlockName(BlockReference reference, Transaction transaction)
        {
            var record = (BlockTableRecord)transaction.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead);
            return record.Name;
        }

        private static bool TryGetFrameGeometryExtents(BlockReference reference, Transaction transaction, out Extents3d extents)
        {
            extents = default(Extents3d);
            var hasExtents = false;
            try
            {
                var definitionId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                var definition = transaction.GetObject(definitionId, OpenMode.ForRead, false) as BlockTableRecord;
                if (definition != null)
                {
                    foreach (ObjectId id in definition)
                    {
                        var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null || entity is AttributeDefinition) continue;
                        try
                        {
                            var child = entity.GeometricExtents;
                            child.TransformBy(reference.BlockTransform);
                            if (!hasExtents) { extents = child; hasExtents = true; } else extents.AddExtents(child);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            if (!hasExtents) return TryGetUsableExtents(reference, out extents);
            var values = new[] { extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y };
            return values.All(x => !double.IsNaN(x) && !double.IsInfinity(x)) && extents.MaxPoint.X > extents.MinPoint.X && extents.MaxPoint.Y > extents.MinPoint.Y;
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
