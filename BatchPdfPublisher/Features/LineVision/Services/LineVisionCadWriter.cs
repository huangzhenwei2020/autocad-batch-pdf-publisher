using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    internal static class LineVisionCadWriter
    {
        public static int PromptAndInsert(Document document, LineVisionResult result, double unitsPerPixel)
        {
            if (document == null || result == null) return 0;
            if (unitsPerPixel <= 0d || double.IsNaN(unitsPerPixel) || double.IsInfinity(unitsPerPixel)) throw new InvalidOperationException("像素比例必须大于 0。");
            var enabled = result.Segments.Where(x => x.IsEnabled).ToList();
            if (enabled.Count == 0) throw new InvalidOperationException("没有启用的识别线段可插入。");
            var picked = document.Editor.GetPoint(new PromptPointOptions("\n指定图像转 CAD 结果的左下角插入点："));
            if (picked.Status != PromptStatus.OK) return 0;
            var insertion = picked.Value;
            var ucsToWorld = document.Editor.CurrentUserCoordinateSystem;
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var layers = EnsureLayers(document.Database, transaction);
                var space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
                foreach (var segment in enabled)
                {
                    var start = ToCad(segment.X1, segment.Y1, insertion, result.Height, unitsPerPixel).TransformBy(ucsToWorld);
                    var end = ToCad(segment.X2, segment.Y2, insertion, result.Height, unitsPerPixel).TransformBy(ucsToWorld);
                    if (start.DistanceTo(end) < 1e-8) continue;
                    var line = new Line(start, end) { LayerId = layers[segment.Direction] };
                    space.AppendEntity(line); transaction.AddNewlyCreatedDBObject(line, true);
                }
                transaction.Commit();
            }
            document.Editor.WriteMessage("\n图像转 CAD 完成，共插入 " + enabled.Count + " 根可编辑直线。\n");
            return enabled.Count;
        }

        private static Point3d ToCad(double x, double y, Point3d insertion, double imageHeight, double scale)
        {
            return new Point3d(insertion.X + x * scale, insertion.Y + (imageHeight - y) * scale, insertion.Z);
        }

        private static Dictionary<LineVisionDirection, ObjectId> EnsureLayers(Database database, Transaction transaction)
        {
            var result = new Dictionary<LineVisionDirection, ObjectId>();
            result[LineVisionDirection.Horizontal] = EnsureLayer(database, transaction, "LV-LINE-H", 3);
            result[LineVisionDirection.Vertical] = EnsureLayer(database, transaction, "LV-LINE-V", 5);
            result[LineVisionDirection.Diagonal] = EnsureLayer(database, transaction, "LV-LINE-DIAG", 2);
            result[LineVisionDirection.Uncertain] = EnsureLayer(database, transaction, "LV-UNCERTAIN", 1);
            return result;
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name, short color)
        {
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var record = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color) };
            var id = table.Add(record); transaction.AddNewlyCreatedDBObject(record, true); return id;
        }
    }
}
