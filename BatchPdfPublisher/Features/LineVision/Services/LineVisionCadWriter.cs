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
        public static LineVisionInsertResult PromptAndInsert(Document document, LineVisionResult result, double unitsPerPixel, bool includeText)
        {
            var inserted = new LineVisionInsertResult();
            if (document == null || result == null) return inserted;
            if (unitsPerPixel <= 0d || double.IsNaN(unitsPerPixel) || double.IsInfinity(unitsPerPixel)) throw new InvalidOperationException("像素比例必须大于 0。");
            var enabled = result.Segments.Where(x => x.IsEnabled).ToList();
            var circles = result.Circles.Where(x => x.IsEnabled && x.Radius > 0d).ToList();
            var textRegions = includeText
                ? result.TextRegions.Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.Text)).ToList()
                : new List<LineVisionOcrTextRegion>();
            if (enabled.Count == 0 && circles.Count == 0 && textRegions.Count == 0) throw new InvalidOperationException("没有启用的线段、圆形或文字可插入。");
            var picked = document.Editor.GetPoint(new PromptPointOptions("\n指定图像转 CAD 结果的左下角插入点："));
            if (picked.Status != PromptStatus.OK) return inserted;
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
                    inserted.LineCount++;
                }
                foreach (var circle in circles)
                {
                    var center = ToCad(circle.CenterX, circle.CenterY, insertion, result.Height, unitsPerPixel).TransformBy(ucsToWorld);
                    var entity = new Circle(center, ucsToWorld.CoordinateSystem3d.Zaxis, circle.Radius * unitsPerPixel) { LayerId = layers[LineVisionDirection.Uncertain] };
                    space.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true);
                    inserted.CircleCount++;
                }
                var textLayer = EnsureLayer(document.Database, transaction, "LV-TEXT", 6);
                foreach (var region in textRegions)
                {
                    var bounds = region.Bounds;
                    if (bounds.Width < 1f || bounds.Height < 1f) continue;
                    var position = ToCad(bounds.Left, bounds.Bottom, insertion, result.Height, unitsPerPixel);
                    var text = new DBText
                    {
                        Position = position,
                        Height = Math.Max(unitsPerPixel, bounds.Height * unitsPerPixel * 0.78d),
                        Rotation = -region.RotationDegrees * Math.PI / 180d,
                        TextString = region.Text.Trim(),
                        LayerId = textLayer,
                        TextStyleId = document.Database.Textstyle
                    };
                    text.TransformBy(ucsToWorld);
                    space.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true);
                    inserted.TextCount++;
                }
                transaction.Commit();
            }
            document.Editor.WriteMessage("\n图像转 CAD 完成，共插入 " + inserted.LineCount + " 根直线、" + inserted.CircleCount + " 个圆、" + inserted.TextCount + " 个文字。\n");
            return inserted;
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
