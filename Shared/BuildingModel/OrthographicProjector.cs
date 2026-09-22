using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// 自研正交投影：把体量模型算成一张二维视图（不依赖 AutoCAD，也不依赖任何几何内核）。
    ///
    /// 做法（P0 范围）：
    /// 1. 每个构件（墙/柱/楼板）先在视图平面里算成一个"矩形 + 离视点远近"（正交投影下，
    ///    竖向棱柱的轮廓就是一个矩形；斜墙按投影包围盒近似并在 Warnings 里说明）；
    /// 2. 门窗洞口作为独立矩形输出到门窗图层（立面只画轮廓，不挖洞——
    ///    P0 不表达"透过窗看到室内"，这是已知简化）；
    /// 3. 按远近排序，**近的矩形把远的边裁掉**（一维区间相减）——这就是隐藏线消除；
    /// 4. 剖面视图里被剖切面穿过的构件输出"剖切矩形 + 填充"（剖到图层），
    ///    剖切面之后的构件按背景投影，剖切面之前的构件不画；
    /// 5. 补标高与图名，最后把整张视图平移到 (0,0) 起。
    /// </summary>
    public static class OrthographicProjector
    {
        private const double Epsilon = 0.01d;

        private struct Frame
        {
            /// <summary>视线方向（观察者看向的方向），平面单位向量。</summary>
            public double Vx, Vy;
            /// <summary>视图水平向右的方向，平面单位向量。</summary>
            public double Rx, Ry;

            public double U(double x, double y) { return x * Rx + y * Ry; }
            /// <summary>沿视线方向的坐标：越大越远。</summary>
            public double P(double x, double y) { return x * Vx + y * Vy; }
            /// <summary>离视点的远近：越大越近。</summary>
            public double Depth(double x, double y) { return -P(x, y); }
        }

        private struct Rect
        {
            public double U0, U1, Z0, Z1;
            public double Depth;
            public string Layer;
            public bool IsCut;
        }

        private sealed class Interval
        {
            public double A, B;
            public Interval(double a, double b) { A = a; B = b; }
        }

        public static ViewDocument Project(BuildingModelDocument model, ViewDefinitionModel view)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (view == null) throw new ArgumentNullException(nameof(view));

            var document = new ViewDocument
            {
                Id = view.Id,
                Title = view.Title,
                Scale = Math.Max(1, view.Scale),
                Kind = view.Kind
            };

            var frame = BuildFrame(view);
            var isSection = view.Kind == ViewKind.Section;
            var cutProj = isSection ? (view.ViewSign >= 0 ? 1d : -1d) * view.CutPosition : 0d;
            var includeAll = view.StoreyIds == null || view.StoreyIds.Count == 0;

            var rects = new List<Rect>();
            var cutRects = new List<Rect>();

            foreach (var wall in model.Walls ?? new List<WallModel>())
            {
                if (wall == null || !Include(includeAll, view.StoreyIds, wall.StoreyId)) continue;
                AddWall(model, wall, frame, isSection, cutProj, view.ViewDepth, rects, cutRects, document.Warnings);
            }
            foreach (var column in model.Columns ?? new List<ColumnModel>())
            {
                if (column == null || !Include(includeAll, view.StoreyIds, column.StoreyId)) continue;
                AddColumn(model, column, frame, isSection, cutProj, view.ViewDepth, rects, cutRects);
            }
            foreach (var slab in model.Slabs ?? new List<SlabModel>())
            {
                if (slab == null || !Include(includeAll, view.StoreyIds, slab.StoreyId)) continue;
                AddSlab(slab, frame, isSection, cutProj, view.ViewDepth, rects, cutRects, document.Warnings);
            }

            // 近的在前：近的矩形负责把远的边裁掉（剖切矩形最靠前）。
            var nearer = new List<Rect>();
            foreach (var rect in rects.OrderByDescending(r => r.Depth))
            {
                EmitRectEdges(rect, nearer, document.Lines);
                nearer.Add(rect);
            }
            foreach (var cut in cutRects) document.Hatches.Add(CreateHatch(cut));

            AddLevelAnnotations(model, view, document);
            AddTitle(document, view);
            Normalize(document);
            return document;
        }

        // ───────────────────────────── 视图坐标系 ─────────────────────────────

        private static Frame BuildFrame(ViewDefinitionModel view)
        {
            double vx, vy;
            if (view.Kind == ViewKind.Section)
            {
                var sign = view.ViewSign >= 0 ? 1d : -1d;
                if (view.CutAxis == SectionAxis.CutX) { vx = sign; vy = 0d; }
                else { vx = 0d; vy = sign; }
            }
            else
            {
                switch (view.Direction)
                {
                    case ElevationDirection.North: vx = 0d; vy = -1d; break;   // 从北往南看
                    case ElevationDirection.East: vx = -1d; vy = 0d; break;    // 从东往西看
                    case ElevationDirection.West: vx = 1d; vy = 0d; break;     // 从西往东看
                    default: vx = 0d; vy = 1d; break;                          // 南：从南往北看
                }
            }
            return new Frame { Vx = vx, Vy = vy, Rx = vy, Ry = -vx };
        }

        private static bool Include(bool includeAll, List<string> storeyIds, string storeyId)
        {
            if (includeAll) return true;
            foreach (var id in storeyIds)
                if (string.Equals(id, storeyId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>把一组平面点换算成 (沿视线 P, 视图水平 U)。</summary>
        private static void ToPlane(IEnumerable<PointModel> points, Frame frame, out double[] ps, out double[] us)
        {
            var list = points.ToList();
            ps = new double[list.Count];
            us = new double[list.Count];
            for (var index = 0; index < list.Count; index++)
            {
                ps[index] = frame.P(list[index].X, list[index].Y);
                us[index] = frame.U(list[index].X, list[index].Y);
            }
        }

        /// <summary>凸多边形在 P = cutP 处的截面（返回 U 区间）。</summary>
        private static bool TryClipAtP(double[] ps, double[] us, double cutP, out double u0, out double u1)
        {
            u0 = double.MaxValue;
            u1 = double.MinValue;
            var found = false;
            var count = ps.Length;
            for (var index = 0; index < count; index++)
            {
                var next = (index + 1) % count;
                var pa = ps[index];
                var pb = ps[next];
                var ua = us[index];
                var ub = us[next];
                if (Math.Abs(pa - cutP) <= Epsilon) { Add(ref u0, ref u1, ua, ref found); }
                if (Math.Abs(pb - cutP) <= Epsilon) { Add(ref u0, ref u1, ub, ref found); }
                if ((pa - cutP) * (pb - cutP) >= 0d) continue;
                var ratio = (cutP - pa) / (pb - pa);
                Add(ref u0, ref u1, ua + (ub - ua) * ratio, ref found);
            }
            return found && u1 - u0 > 0.5d;
        }

        private static void Add(ref double min, ref double max, double value, ref bool found)
        {
            if (value < min) min = value;
            if (value > max) max = value;
            found = true;
        }

        // ───────────────────────────── 墙 ─────────────────────────────

        private static void AddWall(BuildingModelDocument model, WallModel wall, Frame frame, bool isSection,
            double cutProj, double viewDepth, List<Rect> rects, List<Rect> cutRects, List<string> warnings)
        {
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1d) return;
            var ux = dx / length;
            var uy = dy / length;
            var half = (wall.Thickness > 0.5d ? wall.Thickness : 200d) / 2d;
            var nx = -uy * half;
            var ny = ux * half;
            var zBase = model.BaseElevationOf(wall);
            var zTop = zBase + model.HeightOf(wall);

            var corners = new List<PointModel>
            {
                new PointModel(wall.X1 + nx, wall.Y1 + ny),
                new PointModel(wall.X2 + nx, wall.Y2 + ny),
                new PointModel(wall.X2 - nx, wall.Y2 - ny),
                new PointModel(wall.X1 - nx, wall.Y1 - ny)
            };

            // 斜墙：正交投影的轮廓不再是矩形，P0 用包围盒近似并提示
            var oblique = Math.Abs(ux * frame.Rx + uy * frame.Ry) > 0.02d;
            if (oblique && !warnings.Contains(ObliqueWarning)) warnings.Add(ObliqueWarning);

            var projA = frame.P(wall.X1, wall.Y1);
            var projB = frame.P(wall.X2, wall.Y2);
            var crossesCut = (projA - cutProj) * (projB - cutProj) <= Epsilon * Epsilon;

            if (isSection)
            {
                if (crossesCut)
                {
                    double[] ps, us;
                    ToPlane(corners, frame, out ps, out us);
                    double u0, u1;
                    if (TryClipAtP(ps, us, cutProj, out u0, out u1))
                    {
                        var cut = new Rect
                        {
                            U0 = u0, U1 = u1, Z0 = zBase, Z1 = zTop,
                            Depth = double.MaxValue - 1d,     // 剖切面最靠前
                            Layer = ViewLayers.Cut,
                            IsCut = true
                        };
                        rects.Add(cut);
                        cutRects.Add(cut);
                    }
                    return;
                }
                var behind = Math.Min(projA, projB);
                if (behind < cutProj - Epsilon) return;                                  // 剖切面之前：不画
                if (viewDepth > 0.5d && behind > cutProj + viewDepth) return;             // 超出视图深度
            }

            var body = Silhouette(corners, zBase, zTop, frame, ViewLayers.Elevation);
            if (body.HasValue) rects.Add(body.Value);

            // 洞口：立面画在门窗图层；剖面里洞口属于被剖切的墙，不再单独画
            if (isSection) return;
            foreach (var opening in model.Openings ?? new List<OpeningModel>())
            {
                if (opening == null || !string.Equals(opening.HostWallId, wall.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var halfWidth = opening.Width / 2d;
                var ax = wall.X1 + ux * (opening.Offset - halfWidth);
                var ay = wall.Y1 + uy * (opening.Offset - halfWidth);
                var bx = wall.X1 + ux * (opening.Offset + halfWidth);
                var by = wall.Y1 + uy * (opening.Offset + halfWidth);
                var ou0 = Math.Min(frame.U(ax, ay), frame.U(bx, by));
                var ou1 = Math.Max(frame.U(ax, ay), frame.U(bx, by));
                if (ou1 - ou0 < 0.5d) continue;                                          // 洞口朝视线方向，本视图看不见
                var oz0 = Math.Max(zBase + opening.Sill, zBase);
                var oz1 = Math.Min(oz0 + opening.Height, zTop);
                if (oz1 - oz0 < 0.5d) continue;
                rects.Add(new Rect
                {
                    U0 = ou0, U1 = ou1, Z0 = oz0, Z1 = oz1,
                    Depth = body.HasValue ? body.Value.Depth + 100d : 0d,                // 画在墙前面
                    Layer = ViewLayers.Opening
                });
            }
        }

        private static Rect? Silhouette(List<PointModel> corners, double z0, double z1, Frame frame, string layer)
        {
            double uMin = double.MaxValue, uMax = double.MinValue, depth = double.MinValue;
            foreach (var corner in corners)
            {
                var u = frame.U(corner.X, corner.Y);
                if (u < uMin) uMin = u;
                if (u > uMax) uMax = u;
                var d = frame.Depth(corner.X, corner.Y);
                if (d > depth) depth = d;
            }
            if (uMax - uMin < 0.5d || z1 - z0 < 0.5d) return null;
            return new Rect { U0 = uMin, U1 = uMax, Z0 = z0, Z1 = z1, Depth = depth, Layer = layer };
        }

        private const string ObliqueWarning = "斜墙按投影包围盒近似（P0 只保证正交墙的立面轮廓精确）。";

        // ───────────────────────────── 柱 / 楼板 ─────────────────────────────

        private static void AddColumn(BuildingModelDocument model, ColumnModel column, Frame frame, bool isSection,
            double cutProj, double viewDepth, List<Rect> rects, List<Rect> cutRects)
        {
            var halfW = (column.Width > 0.5d ? column.Width : 400d) / 2d;
            var halfD = (column.Depth > 0.5d ? column.Depth : 400d) / 2d;
            var storey = model.FindStorey(column.StoreyId);
            var zBase = storey == null ? 0d : storey.Elevation;
            var zTop = zBase + model.HeightOf(column);
            var corners = new List<PointModel>
            {
                new PointModel(column.X - halfW, column.Y - halfD),
                new PointModel(column.X + halfW, column.Y - halfD),
                new PointModel(column.X + halfW, column.Y + halfD),
                new PointModel(column.X - halfW, column.Y + halfD)
            };

            var proj = frame.P(column.X, column.Y);
            var span = Math.Sqrt(halfW * halfW + halfD * halfD);
            if (isSection)
            {
                if (Math.Abs(proj - cutProj) <= span)
                {
                    double[] ps, us;
                    ToPlane(corners, frame, out ps, out us);
                    double u0, u1;
                    if (TryClipAtP(ps, us, cutProj, out u0, out u1))
                    {
                        var cut = new Rect
                        {
                            U0 = u0, U1 = u1, Z0 = zBase, Z1 = zTop,
                            Depth = double.MaxValue - 1d,
                            Layer = ViewLayers.Cut,
                            IsCut = true
                        };
                        rects.Add(cut);
                        cutRects.Add(cut);
                    }
                    return;
                }
                if (proj < cutProj - Epsilon) return;
                if (viewDepth > 0.5d && proj - span > cutProj + viewDepth) return;
            }

            var body = Silhouette(corners, zBase, zTop, frame, ViewLayers.Elevation);
            if (body.HasValue) rects.Add(body.Value);
        }

        private static void AddSlab(SlabModel slab, Frame frame, bool isSection, double cutProj, double viewDepth,
            List<Rect> rects, List<Rect> cutRects, List<string> warnings)
        {
            var outline = slab.Outline ?? new List<PointModel>();
            if (outline.Count < 3) return;
            var zTop = slab.TopElevation;
            var zBase = zTop - (slab.Thickness > 0.5d ? slab.Thickness : 120d);

            double[] ps, us;
            ToPlane(outline, frame, out ps, out us);
            var projMin = ps.Min();
            var projMax = ps.Max();

            if (isSection)
            {
                if (projMin <= cutProj + Epsilon && projMax >= cutProj - Epsilon)
                {
                    double u0, u1;
                    if (TryClipAtP(ps, us, cutProj, out u0, out u1))
                    {
                        var cut = new Rect
                        {
                            U0 = u0, U1 = u1, Z0 = zBase, Z1 = zTop,
                            Depth = double.MaxValue - 1d,
                            Layer = ViewLayers.Cut,
                            IsCut = true
                        };
                        rects.Add(cut);
                        cutRects.Add(cut);
                    }
                    return;
                }
                if (projMin < cutProj - Epsilon) return;
                if (viewDepth > 0.5d && projMin > cutProj + viewDepth) return;
            }

            var body = Silhouette(outline, zBase, zTop, frame, ViewLayers.Elevation);
            if (body.HasValue) rects.Add(body.Value);
        }

        // ───────────────────────────── 遮挡与输出 ─────────────────────────────

        private static void EmitRectEdges(Rect rect, List<Rect> nearer, List<ViewLine> output)
        {
            if (rect.U1 - rect.U0 < 0.5d || rect.Z1 - rect.Z0 < 0.5d) return;
            EmitHorizontal(rect.U0, rect.U1, rect.Z0, rect.Layer, nearer, output);
            EmitHorizontal(rect.U0, rect.U1, rect.Z1, rect.Layer, nearer, output);
            EmitVertical(rect.U0, rect.Z0, rect.Z1, rect.Layer, nearer, output);
            EmitVertical(rect.U1, rect.Z0, rect.Z1, rect.Layer, nearer, output);
        }

        private static void EmitHorizontal(double u0, double u1, double z, string layer, List<Rect> nearer, List<ViewLine> output)
        {
            var pieces = new List<Interval> { new Interval(Math.Min(u0, u1), Math.Max(u0, u1)) };
            foreach (var other in nearer)
            {
                if (other.Z0 > z + Epsilon || other.Z1 < z - Epsilon) continue;
                pieces = SubtractIntervals(pieces, other.U0, other.U1);
                if (pieces.Count == 0) return;
            }
            foreach (var piece in pieces)
                if (piece.B - piece.A > 0.5d)
                    output.Add(new ViewLine { Layer = layer, X1 = piece.A, Y1 = z, X2 = piece.B, Y2 = z });
        }

        private static void EmitVertical(double u, double z0, double z1, string layer, List<Rect> nearer, List<ViewLine> output)
        {
            var pieces = new List<Interval> { new Interval(Math.Min(z0, z1), Math.Max(z0, z1)) };
            foreach (var other in nearer)
            {
                if (other.U0 > u + Epsilon || other.U1 < u - Epsilon) continue;
                pieces = SubtractIntervals(pieces, other.Z0, other.Z1);
                if (pieces.Count == 0) return;
            }
            foreach (var piece in pieces)
                if (piece.B - piece.A > 0.5d)
                    output.Add(new ViewLine { Layer = layer, X1 = u, Y1 = piece.A, X2 = u, Y2 = piece.B });
        }

        private static List<Interval> SubtractIntervals(List<Interval> source, double cutA, double cutB)
        {
            var result = new List<Interval>();
            foreach (var interval in source)
            {
                if (cutB <= interval.A + Epsilon || cutA >= interval.B - Epsilon) { result.Add(interval); continue; }
                if (cutA > interval.A + Epsilon) result.Add(new Interval(interval.A, Math.Min(cutA, interval.B)));
                if (cutB < interval.B - Epsilon) result.Add(new Interval(Math.Max(cutB, interval.A), interval.B));
            }
            return result;
        }

        private static ViewHatch CreateHatch(Rect rect)
        {
            return new ViewHatch
            {
                Layer = ViewLayers.CutHatch,
                Pattern = "SOLID",
                Boundary = new List<PointModel>
                {
                    new PointModel(rect.U0, rect.Z0),
                    new PointModel(rect.U1, rect.Z0),
                    new PointModel(rect.U1, rect.Z1),
                    new PointModel(rect.U0, rect.Z1)
                }
            };
        }

        // ───────────────────────────── 标高与图名 ─────────────────────────────

        private static void AddLevelAnnotations(BuildingModelDocument model, ViewDefinitionModel view, ViewDocument document)
        {
            var includeAll = view.StoreyIds == null || view.StoreyIds.Count == 0;
            var storeys = (model.Storeys ?? new List<StoreyModel>())
                .Where(s => s != null && Include(includeAll, view.StoreyIds, s.Id))
                .OrderBy(s => s.Elevation).ToList();
            if (storeys.Count == 0) return;

            var width = document.Lines.Count == 0 ? 0d : document.Lines.Max(l => Math.Max(l.X1, l.X2));
            foreach (var storey in storeys)
            {
                document.Lines.Add(new ViewLine
                {
                    Layer = ViewLayers.LevelText,
                    X1 = width + 400d, Y1 = storey.Elevation,
                    X2 = width + 1400d, Y2 = storey.Elevation
                });
                document.Texts.Add(new ViewText
                {
                    Layer = ViewLayers.LevelText,
                    Text = FormatElevation(storey.Elevation),
                    X = width + 500d,
                    Y = storey.Elevation + 250d,
                    Height = 300d
                });
            }
        }

        /// <summary>按建筑制图习惯写法：零写 ±0.000，正数写 3.600，负数写 -0.450。</summary>
        private static string FormatElevation(double millimetres)
        {
            var metres = millimetres / 1000d;
            if (Math.Abs(metres) < 0.0005d) return "±0.000";
            return metres > 0d ? metres.ToString("0.000") : metres.ToString("0.000");
        }

        private static void AddTitle(ViewDocument document, ViewDefinitionModel view)
        {
            var minZ = document.Lines.Count == 0 ? 0d : document.Lines.Min(l => Math.Min(l.Y1, l.Y2));
            var minU = document.Lines.Count == 0 ? 0d : document.Lines.Min(l => Math.Min(l.X1, l.X2));
            var title = string.IsNullOrWhiteSpace(view.Title) ? "视图" : view.Title;
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.Title,
                Text = title + "  1:" + Math.Max(1, view.Scale),
                X = minU,
                Y = minZ - 1200d,
                Height = 500d
            });
        }

        /// <summary>
        /// 把整张视图平移到 (0,0) 起。
        /// **只按几何（线 + 填充）算包围盒**：图名与标高文字挂在视图外侧（下方/右侧），
        /// 这样落图时"插入点 = 视图左下角"，图名自然落在下面，符合 CAD 的放置习惯。
        /// </summary>
        private static void Normalize(ViewDocument document)
        {
            var uMin = double.MaxValue;
            var zMin = double.MaxValue;
            foreach (var line in document.Lines)
            {
                uMin = Math.Min(uMin, Math.Min(line.X1, line.X2));
                zMin = Math.Min(zMin, Math.Min(line.Y1, line.Y2));
            }
            foreach (var hatch in document.Hatches)
                foreach (var point in hatch.Boundary)
                {
                    uMin = Math.Min(uMin, point.X);
                    zMin = Math.Min(zMin, point.Y);
                }
            if (uMin == double.MaxValue) return;
            document.OriginX = uMin;
            document.OriginY = zMin;
            foreach (var line in document.Lines) { line.X1 -= uMin; line.X2 -= uMin; line.Y1 -= zMin; line.Y2 -= zMin; }
            foreach (var hatch in document.Hatches)
                foreach (var point in hatch.Boundary) { point.X -= uMin; point.Y -= zMin; }
            foreach (var text in document.Texts) { text.X -= uMin; text.Y -= zMin; }
        }
    }
}
