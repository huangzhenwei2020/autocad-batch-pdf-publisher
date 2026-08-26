using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WL.Stair.Core.Layout;

namespace WL.Stair.Cad2022
{
    /// <summary>
    /// Writes the whole-stair layout grid as native CAD lines.  Every layout item
    /// remains a native entity but is placed in a CAD group, so a divider grip can
    /// move the neighbouring cell contents without converting Tianzheng objects to
    /// an ordinary block.
    /// </summary>
    internal static class StairLayoutGridService
    {
        internal const string RegAppName = "WL_STAIR_LAYOUT_GRID";
        private const string LayerName = "WL-大样-分隔";

        public static void Insert(Document document, Point3d origin,
            StairLayoutPlan layout, double pageGap,
            IDictionary<StairLayoutSlot, IList<ObjectId>> insertedBySlot, int scale)
        {
            if (document == null || layout == null || layout.Columns < 1 || layout.Rows < 1)
                return;
            var layoutId = Guid.NewGuid().ToString("N");
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var database = document.Database;
                EnsureRegApp(database, transaction);
                var layer = EnsureLayer(database, transaction);
                var space = (BlockTableRecord)transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForWrite);
                var descriptors = CreateGroups(database, transaction, layoutId,
                    layout.Slots, insertedBySlot);
                for (var page = 0; page < layout.PageCount; page++)
                    AddPageGrid(space, transaction, origin, layout, pageGap,
                        page, layer, layoutId, descriptors, Math.Max(1, scale));
                transaction.Commit();
            }
        }

        private static IList<string> CreateGroups(Database database,
            Transaction transaction, string layoutId, IList<StairLayoutSlot> slots,
            IDictionary<StairLayoutSlot, IList<ObjectId>> insertedBySlot)
        {
            var result = new List<string>();
            var dictionary = (DBDictionary)transaction.GetObject(
                database.GroupDictionaryId, OpenMode.ForWrite);
            for (var index = 0; index < slots.Count; index++)
            {
                var slot = slots[index];
                IList<ObjectId> source;
                if (!insertedBySlot.TryGetValue(slot, out source)) source = new List<ObjectId>();
                var ids = source.Where(id => !id.IsNull && id.IsValid).Distinct().ToArray();
                var name = "WL_STAIR_" + layoutId.Substring(0, 12) + "_" + index;
                var group = new Group("楼梯整套排版格 " + (index + 1), true);
                dictionary.SetAt(name, group);
                transaction.AddNewlyCreatedDBObject(group, true);
                if (ids.Length > 0) group.Append(new ObjectIdCollection(ids));
                result.Add(string.Format(CultureInfo.InvariantCulture,
                    "S|{0}|{1}|{2}|{3}|{4}|{5}", name, slot.Page,
                    slot.Row, slot.Column, Math.Max(1, slot.RowSpan),
                    Math.Max(1, slot.ColumnSpan)));
            }
            return result;
        }

        private static void AddPageGrid(BlockTableRecord space,
            Transaction transaction, Point3d origin, StairLayoutPlan layout,
            double pageGap, int page, ObjectId layer, string layoutId,
            IList<string> descriptors, int scale)
        {
            var pageX = origin.X + page * (layout.PageWidth + pageGap);
            var xs = new List<double> { pageX + layout.ContentLeft };
            foreach (var width in layout.ColumnWidths) xs.Add(xs[xs.Count - 1] + width);
            var ys = new List<double> { origin.Y + layout.ContentTop };
            foreach (var height in layout.RowHeights) ys.Add(ys[ys.Count - 1] - height);
            var pageSlots = layout.Slots.Where(value => value.Page == page).ToList();
            var clearance = Math.Max(1.0, 5.0 * scale);

            // Horizontal cell-edge segments.  Edges inside a merged slot are omitted.
            for (var boundary = 0; boundary <= layout.Rows; boundary++)
                for (var column = 0; column < layout.Columns; column++)
                {
                    if (boundary > 0 && boundary < layout.Rows
                        && pageSlots.Any(slot => slot.Row < boundary
                            && slot.Row + Math.Max(1, slot.RowSpan) > boundary
                            && slot.Column <= column
                            && slot.Column + Math.Max(1, slot.ColumnSpan) > column))
                        continue;
                    var divider = boundary > 0 && boundary < layout.Rows;
                    AddLine(space, transaction,
                        new Point3d(xs[column], ys[boundary], origin.Z),
                        new Point3d(xs[column + 1], ys[boundary], origin.Z),
                        layer, layoutId, divider ? "D-H" : "E-H",
                        divider ? boundary - 1 : -1, column, page,
                        ys[boundary],
                        divider ? ys[boundary + 1] + clearance : 0.0,
                        divider ? ys[boundary - 1] - clearance : 0.0,
                        divider ? descriptors : null);
                }

            // Vertical cell-edge segments.  Edges inside a merged slot are omitted.
            for (var boundary = 0; boundary <= layout.Columns; boundary++)
                for (var row = 0; row < layout.Rows; row++)
                {
                    if (boundary > 0 && boundary < layout.Columns
                        && pageSlots.Any(slot => slot.Column < boundary
                            && slot.Column + Math.Max(1, slot.ColumnSpan) > boundary
                            && slot.Row <= row
                            && slot.Row + Math.Max(1, slot.RowSpan) > row))
                        continue;
                    var divider = boundary > 0 && boundary < layout.Columns;
                    AddLine(space, transaction,
                        new Point3d(xs[boundary], ys[row + 1], origin.Z),
                        new Point3d(xs[boundary], ys[row], origin.Z),
                        layer, layoutId, divider ? "D-V" : "E-V",
                        divider ? boundary - 1 : -1, row, page,
                        xs[boundary],
                        divider ? xs[boundary - 1] + clearance : 0.0,
                        divider ? xs[boundary + 1] - clearance : 0.0,
                        divider ? descriptors : null);
                }
        }

        private static void AddLine(BlockTableRecord space, Transaction transaction,
            Point3d start, Point3d end, ObjectId layer, string layoutId,
            string kind, int boundaryIndex, int segmentIndex, int page,
            double baseCoordinate, double minimum, double maximum,
            IList<string> descriptors)
        {
            var line = new Line(start, end)
            {
                LayerId = layer,
                ColorIndex = 8,
                LineWeight = LineWeight.LineWeight018
            };
            space.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, layoutId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, boundaryIndex),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, segmentIndex),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, page),
                new TypedValue((int)DxfCode.ExtendedDataReal, baseCoordinate),
                new TypedValue((int)DxfCode.ExtendedDataReal, minimum),
                new TypedValue((int)DxfCode.ExtendedDataReal, maximum)
            };
            if (descriptors != null)
                foreach (var descriptor in descriptors)
                    values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, descriptor));
            line.XData = new ResultBuffer(values.ToArray());
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction)
        {
            var lineType = ObjectId.Null;
            var types = (LinetypeTable)transaction.GetObject(
                database.LinetypeTableId, OpenMode.ForRead);
            foreach (var name in new[] { "DASHED", "DASH" })
            {
                if (!types.Has(name))
                {
                    try { database.LoadLineTypeFile(name, "acadiso.lin"); }
                    catch { }
                    types = (LinetypeTable)transaction.GetObject(
                        database.LinetypeTableId, OpenMode.ForRead);
                }
                if (types.Has(name)) { lineType = types[name]; break; }
            }
            var layers = (LayerTable)transaction.GetObject(
                database.LayerTableId, OpenMode.ForRead);
            if (layers.Has(LayerName))
            {
                var existing = (LayerTableRecord)transaction.GetObject(
                    layers[LayerName], OpenMode.ForWrite);
                existing.IsOff = false;
                existing.IsFrozen = false;
                existing.IsLocked = false;
                existing.IsPlottable = false;
                existing.Color = Color.FromColorIndex(ColorMethod.ByAci, 8);
                existing.LineWeight = LineWeight.LineWeight018;
                if (!lineType.IsNull) existing.LinetypeObjectId = lineType;
                return existing.ObjectId;
            }
            layers.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = LayerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 8),
                LineWeight = LineWeight.LineWeight018,
                IsPlottable = false
            };
            if (!lineType.IsNull) layer.LinetypeObjectId = lineType;
            var id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            var table = (RegAppTable)transaction.GetObject(
                database.RegAppTableId, OpenMode.ForRead);
            if (table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
    }
}
