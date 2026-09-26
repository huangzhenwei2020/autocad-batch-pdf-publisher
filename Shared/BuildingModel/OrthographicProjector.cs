using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.Models;

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
            return Project(model, view, null);
        }

        /// <summary>
        /// 投影。<paramref name="openingLibrary"/> 是 CAD 导出的门窗类型库：
        /// 立面里的门窗分格与开启线按**编号查库**取做法（尺寸仍以模型里的洞口为准），
        /// 库为空或编号查不到时只画洞口轮廓。类型库改了，重算视图就会跟着变。
        /// </summary>
        public static ViewDocument Project(BuildingModelDocument model, ViewDefinitionModel view,
            OpeningTypeLibraryDocument openingLibrary)
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
            var extraLines = new List<ViewLine>();
            var openingDetails = new List<OpeningDetail>();

            foreach (var wall in model.Walls ?? new List<WallModel>())
            {
                if (wall == null || !Include(includeAll, view.StoreyIds, wall.StoreyId)) continue;
                AddWall(model, wall, frame, isSection, cutProj, view.ViewDepth, rects, cutRects, extraLines,
                    openingDetails, openingLibrary, Math.Max(1, view.Scale) >= 100, document.Warnings);
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
            foreach (var cut in cutRects) document.Hatches.Add(CreateHatch(cut, view));
            document.Lines.AddRange(extraLines);      // 剖面上的窗台线/窗顶线（在剖切面之内，不必再遮挡）

            // 门窗分格/开启线：同样要被前面的墙挡住（用同一批矩形做遮挡裁剪）
            foreach (var detail in openingDetails) EmitOpeningDetail(detail, rects, document.Lines);

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
            double cutProj, double viewDepth, List<Rect> rects, List<Rect> cutRects, List<ViewLine> extraLines,
            List<OpeningDetail> openingDetails, OpeningTypeLibraryDocument openingLibrary, bool simplifiedDetail,
            List<string> warnings)
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
                    if (!TryClipAtP(ps, us, cutProj, out u0, out u1)) return;

                    // 剖切面穿过这道墙时，墙上的洞口会把断面"断开"：
                    // 断面上应看到窗台线与窗顶线，中间是洞口（不能一律画成实心墙）。
                    var bands = new List<Interval> { new Interval(zBase, zTop) };
                    var axisParameter = AxisParameterAtCut(wall, frame, cutProj);
                    if (axisParameter.HasValue)
                    {
                        foreach (var opening in model.Openings ?? new List<OpeningModel>())
                        {
                            if (opening == null || !string.Equals(opening.HostWallId, wall.Id, StringComparison.OrdinalIgnoreCase)) continue;
                            var halfWidth = opening.Width / 2d;
                            if (axisParameter.Value < opening.Offset - halfWidth - Epsilon
                                || axisParameter.Value > opening.Offset + halfWidth + Epsilon) continue;
                            var bottom = Math.Max(zBase + opening.Sill, zBase);
                            var top = Math.Min(bottom + opening.Height, zTop);
                            if (top - bottom < 0.5d) continue;
                            bands = SubtractIntervals(bands, bottom, top);
                            var label = string.IsNullOrWhiteSpace(opening.Code) ? opening.Kind : opening.Code;
                            extraLines.Add(new ViewLine { Layer = ViewLayers.Opening, X1 = u0, Y1 = bottom, X2 = u1, Y2 = bottom });
                            extraLines.Add(new ViewLine { Layer = ViewLayers.Opening, X1 = u0, Y1 = top, X2 = u1, Y2 = top });
                            if (!string.IsNullOrWhiteSpace(label) && !warnings.Contains(SectionOpeningWarning))
                                warnings.Add(SectionOpeningWarning);
                        }
                    }
                    foreach (var band in bands)
                    {
                        if (band.B - band.A < 0.5d) continue;
                        var cut = new Rect
                        {
                            U0 = u0, U1 = u1, Z0 = band.A, Z1 = band.B,
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

                // 立面门窗的分格与开启线：按编号查类型库，查不到就只留洞口轮廓
                var type = OpeningElevationAdapter.Resolve(openingLibrary, opening);
                if (type == null)
                {
                    var label = string.IsNullOrWhiteSpace(opening.Code) ? opening.Kind : opening.Code;
                    var note = "门窗「" + label + "」不在类型库里：立面只画洞口轮廓（在 CAD 里用 TQLX 导出类型库后可补上分格与开启线）。";
                    if (!string.IsNullOrWhiteSpace(label) && !warnings.Contains(note)) warnings.Add(note);
                    continue;
                }
                var mirrored = frame.U(wall.X2, wall.Y2) < frame.U(wall.X1, wall.Y1);   // 背立面看到的是镜像
                openingDetails.Add(new OpeningDetail
                {
                    Area = new Rect { U0 = ou0, U1 = ou1, Z0 = oz0, Z1 = oz1, Depth = body.HasValue ? body.Value.Depth + 100d : 0d, Layer = ViewLayers.Opening },
                    Mirrored = mirrored,
                    Segments = BuildOpeningDetail(opening, type, ou1 - ou0, oz1 - oz0, simplifiedDetail, warnings)
                });
            }
        }

        // ───────────────────── 门窗分格与开启线（复用插件生成器） ─────────────────────

        /// <summary>洞口局部的做法线：x 从洞口左边起、y 从洞口底起（mm）。</summary>
        private struct DetailSegment
        {
            public double X1, Y1, X2, Y2;
        }

        /// <summary>一个洞口的分格/开启线，等所有矩形收齐后再按遮挡裁剪输出。</summary>
        private sealed class OpeningDetail
        {
            public Rect Area;
            public bool Mirrored;
            public List<DetailSegment> Segments;
        }

        /// <summary>
        /// 调用插件已有的门窗立面生成器算分格与开启线（与 CAD 里画门窗立面是同一份代码）。
        /// 参数不合法时不让整张视图失败：记一条提示，退回"只画洞口"。
        /// </summary>
        private static List<DetailSegment> BuildOpeningDetail(OpeningModel opening, OpeningTypeModel type,
            double width, double height, bool simplifiedDetail, List<string> warnings)
        {
            var segments = new List<DetailSegment>();
            if (width < 1d || height < 1d) return segments;
            try
            {
                var item = OpeningElevationAdapter.ToScheduleItem(opening, type, width, height, simplifiedDetail);
                var geometry = DoorWindowElevationGeometryBuilder.Build(item);
                foreach (var line in geometry.Lines)
                {
                    if (Math.Abs(line.X2 - line.X1) < 0.05d && Math.Abs(line.Y2 - line.Y1) < 0.05d) continue;
                    // 正好落在洞口四边上的分格线不用再画：洞口轮廓那 4 条边已经画了，
                    // 否则 CAD 里会出现两条完全重合的线（用户最烦这个）。
                    if (OnHoleBoundary(line.X1, line.Y1, line.X2, line.Y2, width, height)) continue;
                    segments.Add(new DetailSegment { X1 = line.X1, Y1 = line.Y1, X2 = line.X2, Y2 = line.Y2 });
                }
            }
            catch (Exception exception)
            {
                var label = string.IsNullOrWhiteSpace(opening.Code) ? opening.Kind : opening.Code;
                var note = "门窗「" + label + "」的分格参数无效，立面按单格绘制：" + exception.Message;
                if (!string.IsNullOrWhiteSpace(label) && !warnings.Contains(note)) warnings.Add(note);
            }
            return segments;
        }

        /// <summary>这条分格线是否正好落在洞口四边上（落了就不画，交给洞口轮廓）。</summary>
        private static bool OnHoleBoundary(double x1, double y1, double x2, double y2, double width, double height)
        {
            const double tolerance = 0.05d;
            if (Math.Abs(x1 - x2) < tolerance && (Math.Abs(x1) < tolerance || Math.Abs(x1 - width) < tolerance)) return true;
            if (Math.Abs(y1 - y2) < tolerance && (Math.Abs(y1) < tolerance || Math.Abs(y1 - height) < tolerance)) return true;
            return false;
        }

        /// <summary>
        /// 把洞口的分格/开启线裁到"洞口还没被前面构件挡住的部分"，再输出成视图线条。
        /// 遮挡用与轮廓同一批矩形（更近的才挡），所以北立面的窗不会被画到南墙上。
        /// </summary>
        private static void EmitOpeningDetail(OpeningDetail detail, List<Rect> allRects, List<ViewLine> output)
        {
            if (detail == null || detail.Segments == null || detail.Segments.Count == 0) return;
            var area = detail.Area;

            // 先换算到视图坐标，再逐块"减去"比它更近的构件矩形。
            // 用线段减矩形（而不是按 U/Z 区间分开减）：斜的开启线必须二维裁剪，
            // 否则"只挡住左边一段"的前墙会把右边整段也判成不可见。
            var pieces = new List<DetailSegment>();
            foreach (var segment in detail.Segments)
            {
                pieces.Add(new DetailSegment
                {
                    X1 = ToViewU(segment.X1, detail), Y1 = area.Z0 + segment.Y1,
                    X2 = ToViewU(segment.X2, detail), Y2 = area.Z0 + segment.Y2
                });
            }
            foreach (var other in allRects)
            {
                if (other.Depth <= area.Depth + Epsilon) continue;                        // 不比洞口更近
                pieces = SubtractRect(pieces, other);
                if (pieces.Count == 0) return;
            }

            var emitted = new HashSet<string>();
            foreach (var piece in pieces)
            {
                if (Math.Abs(piece.X2 - piece.X1) < 0.5d && Math.Abs(piece.Y2 - piece.Y1) < 0.5d) continue;
                var key = Math.Round(piece.X1, 2) + ":" + Math.Round(piece.Y1, 2)
                    + ":" + Math.Round(piece.X2, 2) + ":" + Math.Round(piece.Y2, 2);
                if (!emitted.Add(key)) continue;
                output.Add(new ViewLine { Layer = area.Layer, X1 = piece.X1, Y1 = piece.Y1, X2 = piece.X2, Y2 = piece.Y2 });
            }
        }

        /// <summary>洞口局部 x → 视图 U；背立面（U 轴与墙轴反向）时左右镜像，门的合页侧才不会画反。</summary>
        private static double ToViewU(double localX, OpeningDetail detail)
        {
            return detail.Mirrored ? detail.Area.U1 - localX : detail.Area.U0 + localX;
        }

        /// <summary>把一组线段各自减去一个矩形（落在矩形里的那一段不要）。</summary>
        private static List<DetailSegment> SubtractRect(List<DetailSegment> source, Rect rect)
        {
            var result = new List<DetailSegment>();
            foreach (var segment in source)
            {
                double enter, exit;
                if (!TryClipSegment(segment, rect, out enter, out exit)) { result.Add(segment); continue; }
                var dx = segment.X2 - segment.X1;
                var dy = segment.Y2 - segment.Y1;
                if (enter > 1e-6d)
                    result.Add(new DetailSegment
                    {
                        X1 = segment.X1, Y1 = segment.Y1,
                        X2 = segment.X1 + enter * dx, Y2 = segment.Y1 + enter * dy
                    });
                if (exit < 1d - 1e-6d)
                    result.Add(new DetailSegment
                    {
                        X1 = segment.X1 + exit * dx, Y1 = segment.Y1 + exit * dy,
                        X2 = segment.X2, Y2 = segment.Y2
                    });
            }
            return result;
        }

        /// <summary>
        /// Liang-Barsky：线段与矩形相交时给出交段的参数区间 [enter, exit]（0..1）；
        /// 不相交返回 false，整段落在矩形里时 enter=0、exit=1。
        /// </summary>
        private static bool TryClipSegment(DetailSegment segment, Rect rect, out double enter, out double exit)
        {
            enter = 0d;
            exit = 1d;
            var dx = segment.X2 - segment.X1;
            var dy = segment.Y2 - segment.Y1;
            if (!ClipEdge(-dx, segment.X1 - rect.U0, ref enter, ref exit)) return false;
            if (!ClipEdge(dx, rect.U1 - segment.X1, ref enter, ref exit)) return false;
            if (!ClipEdge(-dy, segment.Y1 - rect.Z0, ref enter, ref exit)) return false;
            if (!ClipEdge(dy, rect.Z1 - segment.Y1, ref enter, ref exit)) return false;
            return exit > enter;
        }

        private static bool ClipEdge(double p, double q, ref double enter, ref double exit)
        {
            if (Math.Abs(p) < 1e-12d) return q >= 0d;          // 与这条边界平行：在外侧就整段丢掉
            var r = q / p;
            if (p < 0d)
            {
                if (r > exit) return false;
                if (r > enter) enter = r;
            }
            else
            {
                if (r < enter) return false;
                if (r < exit) exit = r;
            }
            return true;
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
        private const string SectionOpeningWarning = "剖面里的门窗洞按参数断开显示（P0 只画窗台/窗顶线，不做窗框分格）。";

        /// <summary>
        /// 剖切面与墙轴线的交点，用"沿墙轴线到起点的距离"表示。
        /// 注意用**视线方向**投影求解（墙轴平行于视线时才有唯一解）；
        /// 墙轴与视线垂直（墙贴着剖切面）时返回 null——那种情形不做洞口断开。
        /// </summary>
        private static double? AxisParameterAtCut(WallModel wall, Frame frame, double cutProj)
        {
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1d) return null;
            var ux = dx / length;
            var uy = dy / length;
            var denominator = ux * frame.Vx + uy * frame.Vy;      // 轴线在视线方向的投影
            if (Math.Abs(denominator) < 1e-9d) return null;
            var start = frame.P(wall.X1, wall.Y1);
            return (cutProj - start) / denominator;
        }

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

        /// <summary>
        /// 剖切填充：默认 45° 细线，间距按出图比例换算成模型尺寸
        /// （1:50 → 75mm ≈ 图上 1.5mm；1:100 → 150mm ≈ 图上 1.5mm）。
        /// 实心（SOLID）会把断面盖成一块灰板、又看不出材料，所以不用它。
        /// 图案与间距将来由材质决定（P5 接制图标准与填充素材）。
        /// </summary>
        private static ViewHatch CreateHatch(Rect rect, ViewDefinitionModel view)
        {
            var scale = Math.Max(1, view.Scale);
            return new ViewHatch
            {
                Layer = ViewLayers.CutHatch,
                Pattern = "ANSI31",
                Spacing = Math.Max(30d, scale * 1.5d),
                Angle = 45d,
                Scale = 0d,
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
