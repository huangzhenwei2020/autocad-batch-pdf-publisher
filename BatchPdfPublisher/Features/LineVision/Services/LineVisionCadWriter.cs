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
            var arcs = result.Arcs.Where(x => x.IsEnabled && x.Radius > 0d && x.SweepAngleDegrees > 0d).ToList();
            var polylines = result.Polylines.Where(x => x.IsEnabled && x.Points.Count >= 2).ToList();
            var walls = result.WallRegions.Where(x => x.IsEnabled && x.Outer.Count >= 3).ToList();
            var textRegions = includeText
                ? result.TextRegions.Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.Text)).ToList()
                : new List<LineVisionOcrTextRegion>();
            if (enabled.Count == 0 && circles.Count == 0 && arcs.Count == 0 && polylines.Count == 0 && walls.Count == 0 && textRegions.Count == 0) throw new InvalidOperationException("没有启用的线段、折线、墙体填充、圆弧、圆形或文字可插入。");
            var picked = document.Editor.GetPoint(new PromptPointOptions("\n指定图像转 CAD 结果的左下角插入点："));
            if (picked.Status != PromptStatus.OK) return inserted;
            var insertion = picked.Value;
            var ucsToWorld = document.Editor.CurrentUserCoordinateSystem;
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var layers = EnsureLayers(document.Database, transaction);
                // 文字使用制图标准里的"标注"文字样式，而不是图形当前的默认样式；
                // 这样图像转 CAD 的结果与其他模块的文字保持一致。
                var textStyle = DraftingStandardService.EnsureAll(document.Database, transaction).AnnotationTextStyleId;
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
                foreach (var source in polylines)
                {
                    var entity = new Polyline { LayerId = layers[LineVisionDirection.Angled], Closed = source.IsClosed };
                    for (var index = 0; index < source.Points.Count; index++)
                    {
                        var point = ToCad(source.Points[index].X, source.Points[index].Y, insertion, result.Height, unitsPerPixel);
                        entity.AddVertexAt(index, new Point2d(point.X, point.Y), 0d, 0d, 0d);
                    }
                    entity.TransformBy(ucsToWorld); space.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true); inserted.PolylineCount++;
                }
                foreach (var wall in walls)
                {
                    var boundaries = new List<ObjectId>();
                    boundaries.Add(AppendBoundary(space, transaction, wall.Outer, insertion, result.Height, unitsPerPixel, ucsToWorld, layers.WallBoundary));
                    foreach (var hole in wall.Holes.Where(value => value.Count >= 3)) boundaries.Add(AppendBoundary(space, transaction, hole, insertion, result.Height, unitsPerPixel, ucsToWorld, layers.WallBoundary));
                    var hatch = new Hatch { LayerId = layers.WallFill, Associative = true }; space.AppendEntity(hatch); transaction.AddNewlyCreatedDBObject(hatch, true);
                    hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                    hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { boundaries[0] });
                    for (var index = 1; index < boundaries.Count; index++) hatch.AppendLoop(HatchLoopTypes.Default, new ObjectIdCollection { boundaries[index] });
                    hatch.EvaluateHatch(true); inserted.WallFillCount++;
                }
                foreach (var circle in circles)
                {
                    var center = ToCad(circle.CenterX, circle.CenterY, insertion, result.Height, unitsPerPixel).TransformBy(ucsToWorld);
                    var entity = new Circle(center, ucsToWorld.CoordinateSystem3d.Zaxis, circle.Radius * unitsPerPixel) { LayerId = layers[LineVisionDirection.Uncertain] };
                    space.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true);
                    inserted.CircleCount++;
                }
                foreach (var arc in arcs)
                {
                    var center = ToCad(arc.CenterX, arc.CenterY, insertion, result.Height, unitsPerPixel);
                    var startAngle = -(arc.StartAngleDegrees + arc.SweepAngleDegrees) * Math.PI / 180d;
                    var endAngle = -arc.StartAngleDegrees * Math.PI / 180d;
                    var entity = new Arc(center, arc.Radius * unitsPerPixel, startAngle, endAngle) { LayerId = layers[LineVisionDirection.Uncertain] };
                    entity.TransformBy(ucsToWorld);
                    space.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true);
                    inserted.ArcCount++;
                }
                foreach (var region in textRegions)
                {
                    var placement = LineVisionOcrGeometry.GetPlacement(region);
                    if (placement.TextHeightPixels < 1d) continue;
                    var position = ToCad(placement.BaselineOrigin.X, placement.BaselineOrigin.Y, insertion, result.Height, unitsPerPixel);
                    var text = new DBText
                    {
                        Position = position,
                        Height = Math.Max(unitsPerPixel, placement.TextHeightPixels * unitsPerPixel * 0.78d),
                        Rotation = -placement.RotationDegrees * Math.PI / 180d,
                        TextString = region.Text.Trim(),
                        LayerId = layers.Text,
                        TextStyleId = textStyle
                    };
                    text.TransformBy(ucsToWorld);
                    space.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true);
                    inserted.TextCount++;
                }
                transaction.Commit();
            }
            document.Editor.WriteMessage("\n图像转 CAD 完成，共插入 " + inserted.LineCount + " 根直线、" + inserted.PolylineCount + " 条折线、" + inserted.WallFillCount + " 个墙体填充、" + inserted.ArcCount + " 段圆弧、" + inserted.CircleCount + " 个圆、" + inserted.TextCount + " 个文字。\n");
            return inserted;
        }

        private static Point3d ToCad(double x, double y, Point3d insertion, double imageHeight, double scale)
        {
            return new Point3d(insertion.X + x * scale, insertion.Y + (imageHeight - y) * scale, insertion.Z);
        }

        private sealed class LayerIds : Dictionary<LineVisionDirection, ObjectId>
        {
            public ObjectId WallBoundary;
            public ObjectId WallFill;
            public ObjectId Text;
        }

        private static ObjectId AppendBoundary(BlockTableRecord space, Transaction transaction, IList<System.Drawing.PointF> points, Point3d insertion, double imageHeight, double scale, Matrix3d transform, ObjectId layer)
        {
            var entity = new Polyline { LayerId = layer, Closed = true };
            for (var index = 0; index < points.Count; index++) { var point = ToCad(points[index].X, points[index].Y, insertion, imageHeight, scale); entity.AddVertexAt(index, new Point2d(point.X, point.Y), 0d, 0d, 0d); }
            entity.TransformBy(transform); space.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true); return entity.ObjectId;
        }

        /// <summary>
        /// 图层全部取自制图标准（BZS），不再写死 LV-* 名与颜色：
        /// 用户在标准里改图层名或颜色，图像转 CAD 的输出随之变化。
        /// 想让输出沿用旧的 LV-* 命名，把标准里对应图层的"名称"改回 LV-* 即可。
        /// </summary>
        private static LayerIds EnsureLayers(Database database, Transaction transaction)
        {
            var result = new LayerIds();
            result[LineVisionDirection.Horizontal] = EnsureStandardLayer(database, transaction, DraftingStandardProfile.OutlineKey);
            result[LineVisionDirection.Vertical] = EnsureStandardLayer(database, transaction, DraftingStandardProfile.StructureKey);
            result[LineVisionDirection.Diagonal] = EnsureStandardLayer(database, transaction, DraftingStandardProfile.HiddenKey);
            result[LineVisionDirection.Angled] = result[LineVisionDirection.Diagonal];
            // 无法判定方向的曲线归入"建筑细线"，不再单开一个游离图层。
            result[LineVisionDirection.Uncertain] = EnsureStandardLayer(database, transaction, DraftingStandardProfile.FineKey);
            result.WallBoundary = EnsureStandardLayer(database, transaction, DraftingStandardProfile.OutlineKey);
            result.WallFill = EnsureStandardLayer(database, transaction, DraftingStandardProfile.HatchKey);
            result.Text = EnsureStandardLayer(database, transaction, DraftingStandardProfile.AnnotationTextLayerKey);
            return result;
        }

        private static ObjectId EnsureStandardLayer(Database database, Transaction transaction, string key)
        {
            return DraftingStandardService.EnsureLayerFor(database, transaction, key);
        }
    }
}
