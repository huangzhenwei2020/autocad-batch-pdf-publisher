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
            // 统计被跳过的坏对象：单个图形本身有缺陷时只跳过它，不让整次插入失败。
            var skipped = 0;
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
                    // 边框线与填充是两件事：边框是识别结果，先独立画出来；填充失败或用户选择不填充，
                    // 边框都必须留在图上。之前两者绑在一起，填充一出错连边框都没有。
                    var boundaries = new List<ObjectId>();
                    try
                    {
                        boundaries.Add(AppendBoundary(space, transaction, wall.Outer, insertion, result.Height, unitsPerPixel, ucsToWorld, layers.WallBoundary));
                        foreach (var hole in wall.Holes.Where(value => value.Count >= 3)) boundaries.Add(AppendBoundary(space, transaction, hole, insertion, result.Height, unitsPerPixel, ucsToWorld, layers.WallBoundary));
                        inserted.WallBoundaryCount++;
                    }
                    catch (System.Exception) { skipped++; continue; }

                    if (wall.FillMode == LineVisionWallFillMode.None) continue;
                    try
                    {
                        // 写法对齐楼梯模块里已验证可用的填充（CadLineRenderer）：
                        // 必须先 SetDatabaseDefaults，且用非关联填充。缺了前者会抛 eNotInDatabase。
                        var hatch = new Hatch { LayerId = layers.WallFill, Associative = false };
                        hatch.SetDatabaseDefaults(document.Database);
                        space.AppendEntity(hatch); transaction.AddNewlyCreatedDBObject(hatch, true);
                        hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { boundaries[0] });
                        for (var index = 1; index < boundaries.Count; index++) hatch.AppendLoop(HatchLoopTypes.Default, new ObjectIdCollection { boundaries[index] });
                        if (wall.FillMode == LineVisionWallFillMode.Solid)
                        {
                            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                        }
                        else
                        {
                            var patternName = string.IsNullOrWhiteSpace(wall.HatchPatternName) ? "ANSI31" : wall.HatchPatternName.Trim();
                            try { hatch.SetHatchPattern(HatchPatternType.PreDefined, patternName); }
                            catch (System.Exception)
                            {
                                // 图案名不存在时退回 ANSI31，而不是让这块墙整体失败。
                                hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31");
                            }
                            hatch.PatternScale = Math.Max(0.001d, wall.HatchPatternScale);
                        }
                        hatch.EvaluateHatch(true);
                        inserted.WallFillCount++;
                    }
                    catch (System.Exception)
                    {
                        // 填充失败就跳过这一个区域，边框线已经画上了，不影响其余内容。
                        skipped++;
                    }
                }
                foreach (var circle in circles)
                {
                    try
                    {
                        var center = ToCad(circle.CenterX, circle.CenterY, insertion, result.Height, unitsPerPixel).TransformBy(ucsToWorld);
                        var entity = new Circle(center, ucsToWorld.CoordinateSystem3d.Zaxis, circle.Radius * unitsPerPixel) { LayerId = layers[LineVisionDirection.Uncertain] };
                        space.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true);
                        inserted.CircleCount++;
                    }
                    catch (System.Exception) { skipped++; }
                }
                foreach (var arc in arcs)
                {
                    try
                    {
                        var center = ToCad(arc.CenterX, arc.CenterY, insertion, result.Height, unitsPerPixel);
                        var startAngle = -(arc.StartAngleDegrees + arc.SweepAngleDegrees) * Math.PI / 180d;
                        var endAngle = -arc.StartAngleDegrees * Math.PI / 180d;
                        var entity = new Arc(center, arc.Radius * unitsPerPixel, startAngle, endAngle) { LayerId = layers[LineVisionDirection.Uncertain] };
                        entity.TransformBy(ucsToWorld);
                        space.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true);
                        inserted.ArcCount++;
                    }
                    catch (System.Exception) { skipped++; }
                }
                foreach (var region in textRegions)
                {
                    try
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
                    catch (System.Exception) { skipped++; }
                }
                transaction.Commit();
            }
            document.Editor.WriteMessage("\n图像转 CAD 完成，共插入 " + inserted.LineCount + " 根直线、" + inserted.PolylineCount + " 条折线、"
                + inserted.WallBoundaryCount + " 条墙体边框、" + inserted.WallFillCount + " 个墙体填充、" + inserted.ArcCount + " 段圆弧、"
                + inserted.CircleCount + " 个圆、" + inserted.TextCount + " 个文字。"
                + (skipped > 0 ? "（有 " + skipped + " 个对象本身有缺陷，已跳过，未影响其余内容）" : string.Empty) + "\n");
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
