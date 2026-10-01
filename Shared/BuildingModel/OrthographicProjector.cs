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
            model = StandardStoreyLayout.Materialize(model);
            if (view.Kind == ViewKind.Plan) return ProjectPlan(model, view, openingLibrary);
            if (view.Kind == ViewKind.Axonometric) return ProjectAxonometric(model, view);

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
                    openingDetails, openingLibrary, Math.Max(1, view.Scale), document);
            }
            foreach (var column in model.Columns ?? new List<ColumnModel>())
            {
                if (column == null || !Include(includeAll, view.StoreyIds, column.StoreyId)) continue;
                AddColumn(model, column, frame, isSection, cutProj, view.ViewDepth, rects, cutRects);
            }
            foreach (var slab in model.Slabs ?? new List<SlabModel>())
            {
                if (slab == null || !Include(includeAll, view.StoreyIds, slab.StoreyId)) continue;
                AddSlab(model, slab, frame, isSection, cutProj, view.ViewDepth, rects, cutRects, document.Warnings);
            }
            foreach (var stair in model.Stairs ?? new List<StairModel>())
            {
                if (stair == null || !Include(includeAll, view.StoreyIds, stair.StoreyId)) continue;
                AddStairProfile(model, stair, frame, isSection, cutProj, view.ViewDepth, extraLines);
            }
            foreach (var roof in model.Roofs ?? new List<RoofModel>())
            {
                if (roof == null || !Include(includeAll, view.StoreyIds, roof.StoreyId)) continue;
                AddRoofProfile(model, roof, frame, isSection, cutProj, view.ViewDepth, rects, extraLines);
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
            AddDimensions(model, view, document);
            AddTitle(document, view);
            Normalize(document);
            return document;
        }

        // ───────────────────────────── 尺寸标注 ─────────────────────────────

        /// <summary>
        /// 立面/剖面的竖向尺寸（两条链 + 一条总高）：
        /// 左侧（靠建筑）是**洞口定位链**（每层从楼面到窗台、窗台到窗顶，只取该层实际用到的标高值），
        /// 右侧依次是**层高链**与**总高**。数值留给 CAD 自己量（`Text` 留空），预览按测量值显示。
        /// 剖面只标层高与总高 —— 剖切面之外的洞口定位没有意义。
        /// </summary>
        private static void AddDimensions(BuildingModelDocument model, ViewDefinitionModel view, ViewDocument document)
        {
            if (document.Lines.Count == 0) return;
            var frame = BuildFrame(view);
            var usable = (model.Storeys ?? new List<StoreyModel>())
                .Where(s => s != null && Include(view.StoreyIds == null || view.StoreyIds.Count == 0, view.StoreyIds, s.Id))
                .OrderBy(s => s.Elevation)
                .ToList();
            if (usable.Count == 0) return;

            // 取范围要用"建筑几何"那一批线：标高符号线在图名/轮廓之外，算进去总长就不对了
            var geometry = document.Lines.Where(l => l != null && IsGeometryLayer(l.Layer)).ToList();
            if (geometry.Count == 0) return;
            var minU = geometry.Min(l => Math.Min(l.X1, l.X2));
            var maxU = geometry.Max(l => Math.Max(l.X1, l.X2));
            var minZ = geometry.Min(l => Math.Min(l.Y1, l.Y2));
            var baseZ = usable[0].Elevation;
            var topZ = usable.Max(s => s.Elevation + (s.Height > 0.5d ? s.Height : 3000d));
            // 有坡屋面时，总高要算到**屋脊**（立面图上的实际最高点）
            var roofs = (model.Roofs ?? new List<RoofModel>())
                .Where(r => r != null && Include(view.StoreyIds == null || view.StoreyIds.Count == 0, view.StoreyIds, r.StoreyId))
                .Select(r => RoofGeometry.Build(model, r))
                .Where(g => g != null && g.IsValid)
                .ToList();
            foreach (var roofGeometry in roofs) topZ = Math.Max(topZ, roofGeometry.RidgeElevation);

            // 先算出"这个立面自己的洞口"：图上有锚点 + 落在**朝观察者的最近一道外墙**上。
            // 立面图的尺寸只标这个立面的门窗，背面的窗不能混进来。
            var facade = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var facadeRange = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            if (view.Kind != ViewKind.Section)
            {
                var drawn = new HashSet<string>(
                    (document.Anchors ?? new List<ViewAnchor>())
                        .Where(a => a != null && string.Equals(a.Kind, "opening", StringComparison.OrdinalIgnoreCase))
                        .Select(a => a.ElementId ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var storey in usable)
                {
                    var candidates = new List<KeyValuePair<OpeningModel, WallModel>>();
                    foreach (var opening in model.Openings ?? new List<OpeningModel>())
                    {
                        if (opening == null || !drawn.Contains(opening.Id ?? string.Empty)) continue;
                        var host = FindHostWall(model, opening);
                        if (host == null || !string.Equals(host.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!FacesViewer(host, frame)) continue;
                        candidates.Add(new KeyValuePair<OpeningModel, WallModel>(opening, host));
                    }
                    if (candidates.Count == 0) continue;
                    var nearest = candidates.Max(c => frame.Depth((c.Value.X1 + c.Value.X2) / 2d, (c.Value.Y1 + c.Value.Y2) / 2d));
                    foreach (var pair in candidates)
                    {
                        var depth = frame.Depth((pair.Value.X1 + pair.Value.X2) / 2d, (pair.Value.Y1 + pair.Value.Y2) / 2d);
                        if (depth < nearest - 1d) continue;                 // 不是最近的那道墙：属于别的立面
                        facade.Add(pair.Key.Id ?? string.Empty);
                    }
                }
                // 洞口在视图里的左右范围（横向尺寸要用），取自锚点，和画出来的几何完全一致
                foreach (var anchor in document.Anchors ?? new List<ViewAnchor>())
                {
                    if (anchor == null || !facade.Contains(anchor.ElementId ?? string.Empty)) continue;
                    facadeRange[anchor.ElementId ?? string.Empty] = new[] { Math.Min(anchor.X1, anchor.X2), Math.Max(anchor.X1, anchor.X2) };
                }
            }

            // 左侧：每层的洞口定位链（竖向）
            if (view.Kind != ViewKind.Section)
            {
                var offsets = new double[] { -1200d, -2000d };
                for (var index = 0; index < usable.Count; index++)
                {
                    var storey = usable[index];
                    var storeyHeight = storey.Height > 0.5d ? storey.Height : 3000d;
                    var values = new List<double> { storey.Elevation };
                    foreach (var opening in model.Openings ?? new List<OpeningModel>())
                    {
                        if (opening == null || !facade.Contains(opening.Id ?? string.Empty)) continue;
                        var host = FindHostWall(model, opening);
                        if (host == null || !string.Equals(host.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase)) continue;
                        var sill = Math.Max(0d, opening.Sill);
                        var head = Math.Min(sill + Math.Max(0d, opening.Height), storeyHeight);
                        values.Add(storey.Elevation + sill);
                        values.Add(storey.Elevation + head);
                    }
                    values = values.Where(IsFinite).Distinct().OrderBy(v => v).ToList();
                    if (values.Count < 2) continue;
                    var linePosition = minU + offsets[Math.Min(index, offsets.Length - 1)];
                    for (var i = 0; i + 1 < values.Count; i++)
                    {
                        if (values[i + 1] - values[i] < 1d) continue;
                        document.Dimensions.Add(new ViewDimension
                        {
                            Layer = ViewLayers.Dimension, Vertical = true,
                            From = values[i], To = values[i + 1],
                            AnchorPosition = minU, LinePosition = linePosition,
                            Note = "洞口定位（" + (storey.Name ?? storey.Id) + "）"
                        });
                    }
                }
            }

            // 右侧：层高链 + 总高
            var storeyLinePosition = maxU + 2000d;
            var totalLinePosition = maxU + 3000d;
            foreach (var storey in usable)
            {
                var top = storey.Elevation + (storey.Height > 0.5d ? storey.Height : 3000d);
                if (top - storey.Elevation < 1d) continue;
                document.Dimensions.Add(new ViewDimension
                {
                    Layer = ViewLayers.Dimension, Vertical = true,
                    From = storey.Elevation, To = top,
                    AnchorPosition = maxU, LinePosition = storeyLinePosition,
                    Note = "层高（" + (storey.Name ?? storey.Id) + "）"
                });
            }
            if (topZ - baseZ > 1d)
            {
                document.Dimensions.Add(new ViewDimension
                {
                    Layer = ViewLayers.Dimension, Vertical = true,
                    From = baseZ, To = topZ,
                    AnchorPosition = maxU, LinePosition = totalLinePosition,
                    Note = roofs.Count > 0 ? "总高（到屋脊）" : "总高"
                });
            }
            // 屋面的"檐口 → 屋脊"单独标一道（制图习惯：坡度与脊高要能看出来）
            foreach (var roofGeometry in roofs)
            {
                if (roofGeometry.RidgeElevation - roofGeometry.EaveElevation < 1d) continue;
                document.Dimensions.Add(new ViewDimension
                {
                    Layer = ViewLayers.Dimension, Vertical = true,
                    From = roofGeometry.EaveElevation, To = roofGeometry.RidgeElevation,
                    AnchorPosition = maxU, LinePosition = totalLinePosition + 1000d,
                    Note = "屋面（檐口→屋脊，坡度 " + Math.Round(roofGeometry.PitchDegrees, 1) + "°）"
                });
            }

            // 底部：横向尺寸（内层洞口定位链 + 外层总长）。
            // 与竖向链同一个套路：数值留空由 CAD 实测，位置在建筑底边以下 1200 / 2000。
            var horizontalValues = new List<double> { minU, maxU };
            foreach (var range in facadeRange.Values)
            {
                horizontalValues.Add(range[0]);
                horizontalValues.Add(range[1]);
            }
            horizontalValues = horizontalValues.Where(IsFinite).Distinct().OrderBy(v => v).ToList();
            var innerLine = minZ - 1200d;
            var outerLine = minZ - 2000d;
            for (var i = 0; i + 1 < horizontalValues.Count; i++)
            {
                if (horizontalValues[i + 1] - horizontalValues[i] < 1d) continue;
                document.Dimensions.Add(new ViewDimension
                {
                    Layer = ViewLayers.Dimension, Vertical = false,
                    From = horizontalValues[i], To = horizontalValues[i + 1],
                    AnchorPosition = minZ, LinePosition = innerLine,
                    Note = "洞口定位（横向）"
                });
            }
            if (maxU - minU > 1d)
            {
                document.Dimensions.Add(new ViewDimension
                {
                    Layer = ViewLayers.Dimension, Vertical = false,
                    From = minU, To = maxU,
                    AnchorPosition = minZ, LinePosition = outerLine,
                    Note = "总长"
                });
            }
        }

        /// <summary>这条线属不属于"建筑几何"（标注类图层不算：标高符号线、图名、尺寸、门窗表）。</summary>
        private static bool IsGeometryLayer(string layer)
        {
            return !string.Equals(layer, ViewLayers.LevelText, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(layer, ViewLayers.Title, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(layer, ViewLayers.Dimension, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(layer, ViewLayers.Schedule, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>墙是不是"朝着观察者"（墙轴与视线方向垂直 → 这面墙就是当前立面）。</summary>
        private static bool FacesViewer(WallModel wall, Frame frame)
        {
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1d) return false;
            return Math.Abs((dx / length) * frame.Vx + (dy / length) * frame.Vy) < 0.02d;
        }

        private static WallModel FindHostWall(BuildingModelDocument model, OpeningModel opening)        {
            if (model == null || opening == null) return null;
            var wanted = opening.HostWallId ?? string.Empty;
            foreach (var wall in model.Walls ?? new List<WallModel>())
                if (wall != null && string.Equals(wall.Id ?? string.Empty, wanted, StringComparison.OrdinalIgnoreCase)) return wall;
            return null;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // ───────────────────────────── 门窗表 ─────────────────────────────

        /// <summary>
        /// 门窗表：按编号汇总模型里的洞口（宽高、樘数、做法取自类型库），画成表格线 + 文字。
        /// 与立面同理 —— 数据全部从模型与类型库现算，手改一次模型这里就跟着变，不用重新录表。
        /// </summary>
        public static ViewDocument ProjectSchedule(BuildingModelDocument model, OpeningTypeLibraryDocument library, string title)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            model = StandardStoreyLayout.Materialize(model);
            var document = new ViewDocument
            {
                Id = "schedule",
                Title = string.IsNullOrWhiteSpace(title) ? "门窗表" : title,
                Kind = ViewKind.Schedule,
                Scale = 100
            };

            var rows = new List<ScheduleRow>();
            foreach (var opening in model.Openings ?? new List<OpeningModel>())
            {
                if (opening == null) continue;
                var code = string.IsNullOrWhiteSpace(opening.Code) ? "未编号" : opening.Code.Trim();
                var row = rows.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
                if (row == null)
                {
                    row = new ScheduleRow { Code = code, Kind = opening.Kind, Width = opening.Width, Height = opening.Height, Sill = opening.Sill };
                    rows.Add(row);
                }
                row.Count++;
            }
            rows = rows.OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToList();
            if (rows.Count == 0)
            {
                document.Texts.Add(new ViewText { Layer = ViewLayers.Schedule, Text = "模型里还没有门窗。", X = 0d, Y = 0d, Height = 300d });
                Normalize(document);
                return document;
            }

            const double textHeight = 250d;
            const double rowHeight = 700d;
            var columns = new[] { 2400d, 2000d, 2800d, 2000d, 1400d, 4200d };
            var headers = new[] { "编号", "类型", "洞口尺寸", "窗台/落地", "樘数", "做法（分格 / 开启）" };
            var tableWidth = columns.Sum();
            var tableHeight = rowHeight * (rows.Count + 1);

            // 表名
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.Schedule, Text = document.Title, X = 0d, Y = tableHeight + 700d, Height = 400d
            });

            // 表格线：外框 + 每行横线 + 每列竖线
            AddScheduleRectangle(document, 0d, 0d, tableWidth, tableHeight);
            for (var row = 1; row <= rows.Count; row++)
                AddScheduleLine(document, 0d, rowHeight * row, tableWidth, rowHeight * row);
            var x = 0d;
            for (var column = 0; column < columns.Length; column++)
            {
                x += columns[column];
                if (column < columns.Length - 1) AddScheduleLine(document, x, 0d, x, tableHeight);
            }

            // 表头与内容（文字左下角定位：竖直居中，左右各留 150 的边距）
            var headerY = tableHeight - rowHeight + (rowHeight - textHeight) / 2d;
            x = 0d;
            for (var column = 0; column < headers.Length; column++)
            {
                document.Texts.Add(new ViewText
                {
                    Layer = ViewLayers.Schedule, Text = headers[column],
                    X = x + 150d, Y = headerY, Height = textHeight
                });
                x += columns[column];
            }
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                var type = library == null ? null : library.FindType(row.Code);
                var cells = new[]
                {
                    row.Code,
                    string.IsNullOrWhiteSpace(row.Kind) ? "—" : row.Kind,
                    Math.Round(row.Width) + "×" + Math.Round(row.Height),
                    row.Sill > 0.5d ? Math.Round(row.Sill).ToString("0") : "落地",
                    row.Count.ToString(),
                    DescribeType(type)
                };
                var y = tableHeight - rowHeight * (index + 2) + (rowHeight - textHeight) / 2d;
                x = 0d;
                for (var column = 0; column < cells.Length; column++)
                {
                    document.Texts.Add(new ViewText
                    {
                        Layer = ViewLayers.Schedule, Text = cells[column],
                        X = x + 150d, Y = y, Height = textHeight
                    });
                    x += columns[column];
                }
            }

            document.Warnings.Add("门窗表由模型与类型库现算：改模型或改类型库后「生成全部视图」重算即可。");
            Normalize(document);
            return document;
        }

        private sealed class ScheduleRow
        {
            public string Code;
            public string Kind;
            public double Width, Height, Sill;
            public int Count;
        }

        private static string DescribeType(OpeningTypeModel type)
        {
            if (type == null) return "类型库里没有这一条";
            var division = string.IsNullOrWhiteSpace(type.DivisionPreset) ? "—" : type.DivisionPreset;
            var mode = string.IsNullOrWhiteSpace(type.OpeningMode) ? "—" : type.OpeningMode;
            return division + " / " + mode + (string.IsNullOrWhiteSpace(type.Material) ? "" : "，" + type.Material);
        }

        private static void AddScheduleLine(ViewDocument document, double x1, double y1, double x2, double y2)
        {
            document.Lines.Add(new ViewLine { Layer = ViewLayers.Schedule, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
        }

        private static void AddScheduleRectangle(ViewDocument document, double x, double y, double width, double height)
        {
            AddScheduleLine(document, x, y, x + width, y);
            AddScheduleLine(document, x + width, y, x + width, y + height);
            AddScheduleLine(document, x + width, y + height, x, y + height);
            AddScheduleLine(document, x, y + height, x, y);
        }

        // ───────────────────────────── 平面图 ─────────────────────────────

        /// <summary>
        /// 平面图（水平剖切俯视，v1）：墙画两条面线（洞口处断开 + 端头封口）、门窗按平面图例
        /// （窗两条玻璃线、门一条扇线 + 90° 开启弧）、柱断面画矩形；外围补横向/竖向定位链与总尺寸。
        ///
        /// 已知简化（P5 再深化）：不画楼板填充、不画房间名与轴网轴号、门扇开启方向固定取洞口起点一侧。
        /// </summary>
        public static ViewDocument ProjectPlan(BuildingModelDocument model, ViewDefinitionModel view,
            OpeningTypeLibraryDocument openingLibrary)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (view == null) throw new ArgumentNullException(nameof(view));
            model = StandardStoreyLayout.Materialize(model);
            var storeyId = view.StoreyIds != null && view.StoreyIds.Count > 0 ? view.StoreyIds[0] : null;
            var storey = model.FindStorey(storeyId);
            var document = new ViewDocument
            {
                Id = view.Id,
                Title = view.Title,
                Kind = ViewKind.Plan,
                Scale = Math.Max(1, view.Scale)
            };
            if (storey == null)
            {
                document.Warnings.Add("这张平面图没有指定楼层（StoreyIds 为空或找不到），只画了图名。");
                AddTitle(document, view);
                Normalize(document);
                return document;
            }

            var walls = (model.Walls ?? new List<WallModel>())
                .Where(w => w != null && string.Equals(w.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var openings = new List<OpeningModel>();
            foreach (var opening in model.Openings ?? new List<OpeningModel>())
            {
                if (opening == null) continue;
                var host = FindHostWall(model, opening);
                if (host == null || !string.Equals(host.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase)) continue;
                openings.Add(opening);
            }

            // 同层、同高、无洞口的正交相接墙使用与三维体量相同的融合边界。
            // 逐墙画封口会把内部交接线也推到 CAD，并在端点角部留下缺口。
            var unionVolume = new BuildingVolume();
            var unionFirst = true;
            var joinedIds = OrthogonalWallUnion.AddJoinedWalls(unionVolume, model, walls, ref unionFirst);
            AddJoinedWallPlanBoundary(document, unionVolume,
                storey.Elevation + Math.Min(1200d, storey.Height / 2d));
            foreach (var seam in WallJunctionLines.Resolve(model, walls,
                storey.Elevation + Math.Min(1200d, storey.Height / 2d)))
                AddLine(document, ViewLayers.Cut,
                    seam.Item1.X, seam.Item1.Y, seam.Item2.X, seam.Item2.Y);

            // 其他墙：洞口把墙断开，两段面线 + 洞口两端的封口
            foreach (var wall in walls)
            {
                if (joinedIds.Contains(wall.Id)) continue;
                var spans = OpeningsOnWall(wall, openings)
                    .Select(o => OpeningEdges(wall, o))
                    .OrderBy(e => e[0])
                    .ToList();
                var length = WallLength(wall);
                var cursor = 0d;
                foreach (var span in spans)
                {
                    if (span[0] - cursor > 1d) AddWallFaces(document, wall, cursor, span[0]);
                    cursor = Math.Max(cursor, span[1]);
                }
                if (length - cursor > 1d) AddWallFaces(document, wall, cursor, length);
                foreach (var span in spans) AddWallFaces(document, wall, span[0], span[1], jambsOnly: true);
            }

            // 门窗图例
            foreach (var opening in openings)
            {
                var host = FindHostWall(model, opening);
                if (host == null) continue;
                AddOpeningPlanSymbol(document, host, opening);
                var first = PlanPoint(host, opening.Offset - opening.Width / 2d);
                var second = PlanPoint(host, opening.Offset + opening.Width / 2d);
                var pad = (host.Thickness > 0.5d ? host.Thickness : 200d) / 2d;
                document.Anchors.Add(new ViewAnchor
                {
                    Kind = "opening", ElementId = opening.Id,
                    X1 = Math.Min(first.X, second.X) - pad, Y1 = Math.Min(first.Y, second.Y) - pad,
                    X2 = Math.Max(first.X, second.X) + pad, Y2 = Math.Max(first.Y, second.Y) + pad
                });
                AddPlanOpeningLabel(document, host, opening, view.Scale);
            }

            // 柱：断面矩形（与墙一样属于"剖到"，用最粗的图层）
            foreach (var column in model.Columns ?? new List<ColumnModel>())
            {
                if (column == null || !string.Equals(column.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var halfWidth = Math.Max(1d, column.Width) / 2d;
                var halfDepth = Math.Max(1d, column.Depth) / 2d;
                AddRect(document, ViewLayers.Cut,
                    column.X - halfWidth, column.Y - halfDepth, column.X + halfWidth, column.Y + halfDepth);
            }

            foreach (var slab in model.Slabs.Where(s => s != null
                && string.Equals(s.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (slab.Outline == null || slab.Outline.Count < 3) continue;
                var geometry = SlabGeometry.Build(slab);
                foreach (var contour in geometry.Contours)
                    for (var i = 0; i < contour.Count; i++)
                    {
                        var a = contour[i]; var b = contour[(i + 1) % contour.Count];
                        AddLine(document, ViewLayers.Slab, a.X, a.Y, b.X, b.Y);
                    }
            }

            // 轴网与房间：平面图的两个"信息层"
            var bounds = PlanBounds(walls);
            AddPlanAxes(document, model, bounds[0], bounds[1], bounds[2], bounds[3], view.Scale);
            AddPlanRooms(document, model, storey.Id, view.Scale, walls);
            AddPlanStairs(document, model, storey, view.Scale);

            AddPlanDimensions(document, view, walls, openings, model);
            AddTitle(document, view);
            Normalize(document);
            return document;
        }

        private static void AddJoinedWallPlanBoundary(ViewDocument document, BuildingVolume volume,
            double cutElevation)
        {
            var groups = volume.Faces.Where(f => f.Kind == "wall" && Math.Abs(f.NormalZ) < 0.5d
                && f.Points.Count >= 2 && f.Points.Min(p => p.Z) <= cutElevation + 0.001d
                && f.Points.Max(p => p.Z) >= cutElevation - 0.001d)
                .Select(f => new { A = f.Points[0], B = f.Points[1] })
                .Where(edge => Math.Abs(edge.A.X - edge.B.X) > 0.001d
                    || Math.Abs(edge.A.Y - edge.B.Y) > 0.001d)
                .GroupBy(edge => Math.Abs(edge.A.X - edge.B.X) < 0.001d
                    ? "V|" + Math.Round(edge.A.X, 3).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    : "H|" + Math.Round(edge.A.Y, 3).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var group in groups)
            {
                var vertical = group.Key[0] == 'V';
                var coordinate = vertical ? group.First().A.X : group.First().A.Y;
                var spans = group.Select(edge => new[]
                {
                    vertical ? Math.Min(edge.A.Y, edge.B.Y) : Math.Min(edge.A.X, edge.B.X),
                    vertical ? Math.Max(edge.A.Y, edge.B.Y) : Math.Max(edge.A.X, edge.B.X)
                }).OrderBy(span => span[0]).ToList();
                if (spans.Count == 0) continue;
                var start = spans[0][0]; var end = spans[0][1];
                for (var index = 1; index <= spans.Count; index++)
                {
                    if (index < spans.Count && spans[index][0] <= end + 0.001d)
                    { end = Math.Max(end, spans[index][1]); continue; }
                    if (vertical) AddLine(document, ViewLayers.Cut, coordinate, start, coordinate, end);
                    else AddLine(document, ViewLayers.Cut, start, coordinate, end, coordinate);
                    if (index < spans.Count) { start = spans[index][0]; end = spans[index][1]; }
                }
            }
        }

        /// <summary>
        /// 轴网：竖轴（沿 Y，标 X）与横轴（沿 X，标 Y）画成点划线 + 两端轴号圆圈，
        /// 每条都带"图上元素 ↔ 模型构件"的锚点，平面里也能点选轴线。
        /// </summary>
        private static void AddPlanAxes(ViewDocument document, BuildingModelDocument model,
            double minX, double maxX, double minY, double maxY, int scale)
        {
            var axes = BuildingAxisLayout.Resolve(model);
            if (axes.Count == 0) return;
            var margin = Math.Max(3200d, Math.Max(1, scale) * 32d);      // 轴线伸出建筑 3200：轴号圆圈要落最外一道尺寸线之外
            var radius = Math.Max(400d, Math.Max(1, scale) * 4d);        // 轴号圆圈半径 400（图上 4mm）
            var textHeight = Math.Max(250d, Math.Max(1, scale) * 2.5d);

            foreach (var axis in axes)
            {
                var start = axis.ExtentStart > 0.5d || axis.ExtentEnd > 0.5d
                    ? Math.Min(axis.ExtentStart, axis.ExtentEnd)
                    : (axis.Vertical ? minY : minX) - margin;
                var end = axis.ExtentStart > 0.5d || axis.ExtentEnd > 0.5d
                    ? Math.Max(axis.ExtentStart, axis.ExtentEnd)
                    : (axis.Vertical ? maxY : maxX) + margin;
                if (end - start < 1d) continue;

                if (axis.Vertical)
                {
                    document.Lines.Add(new ViewLine
                    {
                        Layer = ViewLayers.Axis, LineType = "CENTER",
                        X1 = axis.Position, Y1 = start, X2 = axis.Position, Y2 = end
                    });
                    AddAxisBubble(document, axis.StartName ?? axis.Name, axis.Position, start - radius * 0.4d, radius, textHeight);
                    AddAxisBubble(document, axis.EndName ?? axis.Name, axis.Position, end + radius * 0.4d, radius, textHeight);
                    document.Anchors.Add(new ViewAnchor
                    {
                        Kind = "axis", ElementId = axis.Id,
                        X1 = axis.Position - radius, Y1 = start, X2 = axis.Position + radius, Y2 = end
                    });
                }
                else
                {
                    document.Lines.Add(new ViewLine
                    {
                        Layer = ViewLayers.Axis, LineType = "CENTER",
                        X1 = start, Y1 = axis.Position, X2 = end, Y2 = axis.Position
                    });
                    AddAxisBubble(document, axis.StartName ?? axis.Name, start - radius * 0.4d, axis.Position, radius, textHeight);
                    AddAxisBubble(document, axis.EndName ?? axis.Name, end + radius * 0.4d, axis.Position, radius, textHeight);
                    document.Anchors.Add(new ViewAnchor
                    {
                        Kind = "axis", ElementId = axis.Id,
                        X1 = start, Y1 = axis.Position - radius, X2 = end, Y2 = axis.Position + radius
                    });
                }
            }
        }

        /// <summary>轴号：一个圆圈 + 圈里的轴号（文字按圆心与字宽估算居中）。</summary>
        private static void AddAxisBubble(ViewDocument document, string name, double x, double y, double radius, double textHeight)
        {
            var label = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim();
            document.Circles.Add(new ViewCircle { Layer = ViewLayers.Axis, X = x, Y = y, Radius = radius });
            var estimated = textHeight * 0.62d * label.Length;
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.Axis, Text = label,
                X = x - estimated / 2d, Y = y - textHeight * 0.35d, Height = textHeight
            });
            _ = radius;
        }

        /// <summary>房间：轮廓（细线）+ 名称 + 面积（m²，按轮廓现算）。</summary>
        private static void AddPlanRooms(ViewDocument document, BuildingModelDocument model, string storeyId, int scale,
            List<WallModel> walls)
        {
            var rooms = (model.Rooms ?? new List<RoomModel>()).Where(r => r != null
                && string.Equals(r.StoreyId ?? string.Empty, storeyId ?? string.Empty, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rooms.Count == 0) return;
            var textHeight = Math.Max(250d, Math.Max(1, scale) * 2.5d);
            var index = 0;
            foreach (var room in rooms)
            {
                var points = (room.Outline ?? new List<PointModel>()).Where(p => p != null && IsFinite(p.X) && IsFinite(p.Y)).ToList();
                if (points.Count < 3) continue;
                for (var i = 0; i < points.Count; i++)
                {
                    var next = points[(i + 1) % points.Count];
                    document.Lines.Add(new ViewLine
                    {
                        Layer = ViewLayers.Room,
                        X1 = points[i].X, Y1 = points[i].Y, X2 = next.X, Y2 = next.Y
                    });
                }
                var centerX = points.Average(p => p.X);
                var centerY = points.Average(p => p.Y);
                var name = string.IsNullOrWhiteSpace(room.Name) ? "房间" + (index + 1) : room.Name.Trim();
                var area = room.AreaSquareMetres.ToString("0.00") + " m²";
                var nameWidth = textHeight * 0.62d * name.Length;
                var areaWidth = textHeight * 0.62d * area.Length;
                document.Texts.Add(new ViewText
                {
                    Layer = ViewLayers.Room, Text = name,
                    X = centerX - nameWidth / 2d, Y = centerY + textHeight * 0.7d, Height = textHeight
                });
                document.Texts.Add(new ViewText
                {
                    Layer = ViewLayers.Room, Text = area,
                    X = centerX - areaWidth / 2d, Y = centerY - textHeight * 0.9d, Height = textHeight * 0.85d
                });
                document.Anchors.Add(new ViewAnchor
                {
                    Kind = "room", ElementId = room.Id,
                    X1 = points.Min(p => p.X), Y1 = points.Min(p => p.Y),
                    X2 = points.Max(p => p.X), Y2 = points.Max(p => p.Y)
                });
                index++;
                _ = walls;
            }
        }

        /// <summary>
        /// 平面图里的楼梯（双跑）：两跑梯段 + 踏步线 + 休息平台 + 梯井两侧扶手线 + 剖断线 + 上下行箭头。
        ///
        /// 画法与制图习惯一致：踏步线只画在两条梯段条带内；剖断线画在第一跑"越过 1.2m 剖切高度"的那一级；
        /// 箭头沿"第一跑起步 → 平台 → 第二跑到达"走（上），最底层只画"上"，其余楼层多画一条反向的"下"。
        /// </summary>
        private static void AddPlanStairs(ViewDocument document, BuildingModelDocument model,
            StoreyModel storey, int scale)
        {
            var stairs = (model.Stairs ?? new List<StairModel>())
                .Where(s => s != null && string.Equals(s.StoreyId ?? string.Empty, storey.Id ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (stairs.Count == 0) return;

            var textHeight = Math.Max(250d, Math.Max(1, scale) * 2.5d);
            var lowest = (model.Storeys ?? new List<StoreyModel>()).Where(s => s != null)
                .OrderBy(s => s.Elevation).FirstOrDefault();
            var hasDown = lowest != null
                && !string.Equals(lowest.Id ?? string.Empty, storey.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            foreach (var stair in stairs)
            {
                var geometry = StairGeometry.Build(model, stair);
                if (geometry == null) continue;
                if (geometry.Flights.Count == 0)
                {
                    document.Warnings.Add("楼梯「" + (stair.Id ?? "?") + "」的尺寸放不下踏步与平台（净长 "
                        + Math.Round(stair.Length) + "、净宽 " + Math.Round(stair.Width) + "），只画了楼梯间范围。");
                    AddRect(document, ViewLayers.Stair, geometry.X0, geometry.Y0, geometry.X1, geometry.Y1);
                    continue;
                }

                foreach (var flight in geometry.Flights)
                {
                    // 梯段外框（两条长边 + 两端封口）
                    AddRect(document, ViewLayers.Stair, flight.X0, flight.Y0, flight.X1, flight.Y1);
                    foreach (var tread in flight.Treads)
                        AddLine(document, ViewLayers.Stair, tread[0].X, tread[0].Y, tread[1].X, tread[1].Y);
                }
                // 休息平台外沿（靠房间一侧的两条边；靠梯段那侧已经由梯段封口线画过）
                AddRect(document, ViewLayers.Stair, geometry.LandingX0, geometry.LandingY0,
                    geometry.LandingX1, geometry.LandingY1);

                foreach (var rail in geometry.Handrails)
                    AddLine(document, ViewLayers.Stair, rail[0].X, rail[0].Y, rail[1].X, rail[1].Y);

                AddStairBreakLine(document, geometry, textHeight);
                AddStairArrows(document, geometry, textHeight, hasDown);

                document.Anchors.Add(new ViewAnchor
                {
                    Kind = "stair", ElementId = stair.Id,
                    X1 = geometry.X0, Y1 = geometry.Y0, X2 = geometry.X1, Y2 = geometry.Y1
                });
            }
        }

        /// <summary>剖断线：画在第一跑"踏面刚超过 1.2m 剖切高度"的那一级上（两条 45° 细线）。</summary>
        private static void AddStairBreakLine(ViewDocument document, StairGeometry geometry, double textHeight)
        {
            const double cutHeight = 1200d;
            var steps = Math.Max(1, geometry.StepsPerFlight);
            var index = (int)Math.Floor(cutHeight / Math.Max(1d, geometry.Riser));
            if (index >= steps) return;                    // 一跑还没走到剖切高度（层高很低时）
            if (index < 1) index = 1;
            var flight = geometry.Flights[0];
            var offset = Math.Max(150d, textHeight);
            // 45°斜线：沿梯段方向的偏移量 = 梯段宽的一半，看起来就是"斜着划一刀"
            var span = geometry.AlongX ? flight.Y1 - flight.Y0 : flight.X1 - flight.X0;
            var slant = span / 2d;
            var run = index * geometry.Going;
            var startS = run - slant / 2d;
            var endS = run + slant / 2d;
            for (var line = 0; line < 2; line++)
            {
                var shift = line * offset;
                PointModel a, b;
                if (geometry.AlongX)
                {
                    a = new PointModel(geometry.X0 + startS + shift, flight.Y0);
                    b = new PointModel(geometry.X0 + endS + shift, flight.Y1);
                }
                else
                {
                    a = new PointModel(flight.X0, geometry.Y0 + startS + shift);
                    b = new PointModel(flight.X1, geometry.Y0 + endS + shift);
                }
                AddLine(document, ViewLayers.Stair, a.X, a.Y, b.X, b.Y);
            }
        }

        /// <summary>上下行箭头：沿上行路径走一条折线 + 箭头（"上"），非底层再画反向的一条（"下"）。</summary>
        private static void AddStairArrows(ViewDocument document, StairGeometry geometry, double textHeight, bool hasDown)
        {
            var path = (geometry.UpPath ?? new List<PointModel>()).Where(p => p != null).ToList();
            if (path.Count < 2) return;
            var arrows = Math.Max(120d, textHeight * 0.8d);

            // 上：起步点 → 平台 → 到达点，箭头画在末端
            for (var index = 0; index + 1 < path.Count; index++)
                AddLine(document, ViewLayers.Stair, path[index].X, path[index].Y, path[index + 1].X, path[index + 1].Y);
            AddArrowHead(document, path[path.Count - 2], path[path.Count - 1], arrows);
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.Stair, Text = "上", Height = textHeight,
                X = path[0].X - textHeight / 2d, Y = path[0].Y - textHeight * 0.35d
            });

            if (!hasDown) return;
            // 下：从到达点反向回来（画在旁边的偏移线上，免得与"上"叠在一起）
            var offset = Math.Max(200d, textHeight * 1.2d);
            var reverse = new List<PointModel>();
            foreach (var point in path)
                reverse.Add(geometry.AlongX
                    ? new PointModel(point.X, point.Y + offset)
                    : new PointModel(point.X + offset, point.Y));
            for (var index = 0; index + 1 < reverse.Count; index++)
                AddLine(document, ViewLayers.Stair, reverse[index].X, reverse[index].Y,
                    reverse[index + 1].X, reverse[index + 1].Y);
            AddArrowHead(document, reverse[1], reverse[0], arrows);
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.Stair, Text = "下", Height = textHeight,
                X = reverse[reverse.Count - 1].X - textHeight / 2d,
                Y = reverse[reverse.Count - 1].Y - textHeight * 0.35d
            });
        }

        /// <summary>在 <paramref name="tip"/> 处画一个 V 形箭头，方向由 <paramref name="from"/> → tip 决定。</summary>
        private static void AddArrowHead(ViewDocument document, PointModel from, PointModel tip, double size)
        {
            if (from == null || tip == null) return;
            var dx = tip.X - from.X;
            var dy = tip.Y - from.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6d) return;
            var ux = dx / length;
            var uy = dy / length;
            // 两条 30° 的翼
            var cos = Math.Cos(30d * Math.PI / 180d);
            var sin = Math.Sin(30d * Math.PI / 180d);
            var leftX = tip.X - size * (ux * cos - uy * sin);
            var leftY = tip.Y - size * (uy * cos + ux * sin);
            var rightX = tip.X - size * (ux * cos + uy * sin);
            var rightY = tip.Y - size * (uy * cos - ux * sin);
            AddLine(document, ViewLayers.Stair, tip.X, tip.Y, leftX, leftY);
            AddLine(document, ViewLayers.Stair, tip.X, tip.Y, rightX, rightY);
        }

        /// <summary>一道墙上挂着的洞口（按沿墙定位排序）。</summary>
        private static IEnumerable<OpeningModel> OpeningsOnWall(WallModel wall, IEnumerable<OpeningModel> openings)
        {
            return (openings ?? Enumerable.Empty<OpeningModel>())
                .Where(o => o != null && string.Equals(o.HostWallId ?? string.Empty, wall.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => o.Offset);
        }

        private static double WallLength(WallModel wall)
        {
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>洞口沿墙轴线的起止距离（到墙起点）。</summary>
        private static double[] OpeningEdges(WallModel wall, OpeningModel opening)
        {
            var length = WallLength(wall);
            var half = Math.Max(0d, opening.Width) / 2d;
            return new[] { Math.Max(0d, opening.Offset - half), Math.Min(length, opening.Offset + half) };
        }

        /// <summary>沿墙轴线距离 → 平面坐标。</summary>
        private static PointModel PlanPoint(WallModel wall, double distance)
        {
            var length = WallLength(wall);
            if (length < 1d) return new PointModel(wall.X1, wall.Y1);
            var ux = (wall.X2 - wall.X1) / length;
            var uy = (wall.Y2 - wall.Y1) / length;
            return new PointModel(wall.X1 + ux * distance, wall.Y1 + uy * distance);
        }

        /// <summary>
        /// 墙在 [from, to] 这一段的两条面线；<paramref name="jambsOnly"/> = 只画两端封口（洞口处）。
        /// </summary>
        private static void AddWallFaces(ViewDocument document, WallModel wall, double from, double to, bool jambsOnly = false)
        {
            var half = (wall.Thickness > 0.5d ? wall.Thickness : 200d) / 2d;
            var axisStart = PlanPoint(wall, from);
            var axisEnd = PlanPoint(wall, to);
            var start = WallReferenceGeometry.BodyPoint(wall, axisStart.X, axisStart.Y);
            var end = WallReferenceGeometry.BodyPoint(wall, axisEnd.X, axisEnd.Y);
            var length = WallLength(wall);
            if (length < 1d) return;
            var nx = -(wall.Y2 - wall.Y1) / length * half;
            var ny = (wall.X2 - wall.X1) / length * half;

            if (!jambsOnly)
            {
                AddLine(document, ViewLayers.Cut, start.X + nx, start.Y + ny, end.X + nx, end.Y + ny);
                AddLine(document, ViewLayers.Cut, start.X - nx, start.Y - ny, end.X - nx, end.Y - ny);
            }
            AddLine(document, ViewLayers.Cut, start.X + nx, start.Y + ny, start.X - nx, start.Y - ny);   // 封口
            AddLine(document, ViewLayers.Cut, end.X + nx, end.Y + ny, end.X - nx, end.Y - ny);
        }

        public static List<ViewLine> CreatePlanDetailSymbols(BuildingModelDocument model, string storeyId)
        {
            var document = new ViewDocument();
            var walls = model.Walls.Where(w => w.StoreyId == storeyId).ToDictionary(w => w.Id);
            foreach (var opening in model.Openings)
                if (walls.TryGetValue(opening.HostWallId, out var wall))
                    AddOpeningPlanSymbol(document, wall, opening);
            foreach (var column in model.Columns.Where(c => c.StoreyId == storeyId))
                AddRect(document, ViewLayers.Cut, column.X - Math.Max(1d, column.Width) / 2d,
                    column.Y - Math.Max(1d, column.Depth) / 2d, column.X + Math.Max(1d, column.Width) / 2d,
                    column.Y + Math.Max(1d, column.Depth) / 2d);
            return document.Lines;
        }

        /// <summary>平面门窗图例：窗 = 两条玻璃线；门 = 一条扇线 + 90° 开启弧（弧用短线拟合）。</summary>
        private static void AddOpeningPlanSymbol(ViewDocument document, WallModel wall, OpeningModel opening)
        {
            var half = (wall.Thickness > 0.5d ? wall.Thickness : 200d) / 2d;
            var length = WallLength(wall);
            if (length < 1d) return;
            var edges = OpeningEdges(wall, opening);
            var width = edges[1] - edges[0];
            if (width < 1d) return;
            var ux = (wall.X2 - wall.X1) / length;
            var uy = (wall.Y2 - wall.Y1) / length;
            var nx = -uy;
            var ny = ux;
            var isDoor = opening.HasSwingLeaf();
            var axisStart = PlanPoint(wall, edges[0]);
            var axisEnd = PlanPoint(wall, edges[1]);
            var start = WallReferenceGeometry.BodyPoint(wall, axisStart.X, axisStart.Y);
            var end = WallReferenceGeometry.BodyPoint(wall, axisEnd.X, axisEnd.Y);

            if (!isDoor)
            {
                // 窗：两条玻璃线（在墙厚内侧偏移一点，看起来像窗）
                var inset = Math.Min(half * 0.35d, 60d);
                AddLine(document, ViewLayers.Opening,
                    start.X + nx * inset, start.Y + ny * inset, end.X + nx * inset, end.Y + ny * inset);
                AddLine(document, ViewLayers.Opening,
                    start.X - nx * inset, start.Y - ny * inset, end.X - nx * inset, end.Y - ny * inset);
                return;
            }

            // 门：扇线从起点侧门垛沿墙法线出去（长度 = 洞口宽），再画 90° 开启弧到另一端
            var leafX = start.X + nx * width;
            var leafY = start.Y + ny * width;
            AddLine(document, ViewLayers.Opening, start.X, start.Y, leafX, leafY);
            const int steps = 8;
            var previousX = leafX;
            var previousY = leafY;
            for (var step = 1; step <= steps; step++)
            {
                var angle = Math.PI / 2d * step / steps;
                // 从"扇线方向"扫到"洞口方向"：以起点为圆心
                var dx = (nx * Math.Cos(angle) + ux * Math.Sin(angle)) * width;
                var dy = (ny * Math.Cos(angle) + uy * Math.Sin(angle)) * width;
                var pointX = start.X + dx;
                var pointY = start.Y + dy;
                AddLine(document, ViewLayers.Opening, previousX, previousY, pointX, pointY);
                previousX = pointX;
                previousY = pointY;
            }
        }

        /// <summary>平面里的洞口编号：写在洞口正下方（居中）。</summary>
        private static void AddPlanOpeningLabel(ViewDocument document, WallModel wall, OpeningModel opening, int scale)
        {
            var code = (opening.Code ?? string.Empty).Trim();
            if (code.Length == 0) return;
            var height = Math.Max(150d, Math.Max(1, scale) * 2d);
            var first = PlanPoint(wall, opening.Offset - opening.Width / 2d);
            var second = PlanPoint(wall, opening.Offset + opening.Width / 2d);
            var estimated = height * 0.62d * code.Length;
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.Opening,
                Text = code,
                X = (first.X + second.X) / 2d - estimated / 2d,
                Y = Math.Min(first.Y, second.Y) - height * 1.2d,
                Height = height
            });
        }

        /// <summary>平面图的建筑范围（含墙厚外皮），返回 [minX, maxX, minY, maxY]。</summary>
        private static double[] PlanBounds(List<WallModel> walls)
        {
            if (walls == null || walls.Count == 0) return new[] { 0d, 0d, 0d, 0d };
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var wall in walls)
            {
                var half = (wall.Thickness > 0.5d ? wall.Thickness : 200d) / 2d;
                var a = WallReferenceGeometry.BodyPoint(wall, wall.X1, wall.Y1);
                var b = WallReferenceGeometry.BodyPoint(wall, wall.X2, wall.Y2);
                minX = Math.Min(minX, Math.Min(a.X, b.X) - half);
                maxX = Math.Max(maxX, Math.Max(a.X, b.X) + half);
                minY = Math.Min(minY, Math.Min(a.Y, b.Y) - half);
                maxY = Math.Max(maxY, Math.Max(a.Y, b.Y) + half);
            }
            return new[] { minX, maxX, minY, maxY };
        }

        /// <summary>
        /// 平面外围尺寸（建筑制图的三道）：内层洞口定位、中层轴线尺寸（有轴网时）、外层总尺寸。
        /// 左侧同理（竖向）。
        /// </summary>
        private static void AddPlanDimensions(ViewDocument document, ViewDefinitionModel view,
            List<WallModel> walls, List<OpeningModel> openings, BuildingModelDocument model)
        {
            if (walls.Count == 0) return;
            // 尺寸链从建筑外皮起算（与立面图的总长/总宽一致：外墙外皮到外皮）
            var bounds = PlanBounds(walls);
            var minX = bounds[0];
            var maxX = bounds[1];
            var minY = bounds[2];
            var maxY = bounds[3];

            var openingXs = new List<double> { minX, maxX };
            var openingYs = new List<double> { minY, maxY };
            foreach (var opening in openings)
            {
                var wall = walls.FirstOrDefault(w => w != null
                    && string.Equals(w.Id ?? string.Empty, opening.HostWallId ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                if (wall == null) continue;
                var edges = OpeningEdges(wall, opening);
                var a = PlanPoint(wall, edges[0]);
                var b = PlanPoint(wall, edges[1]);
                openingXs.Add(a.X); openingXs.Add(b.X);
                openingYs.Add(a.Y); openingYs.Add(b.Y);
            }

            var axes = BuildingAxisLayout.Resolve(model);
            var axisXs = axes.Where(a => a.Vertical).Select(a => a.Position).ToList();
            var axisYs = axes.Where(a => !a.Vertical).Select(a => a.Position).ToList();

            AddPlanChain(document, openingXs, minY, minY - 1200d, false, "洞口定位（横向）", true);
            if (axisXs.Count > 0)
                AddPlanChain(document, new List<double> { minX, maxX }.Concat(axisXs).ToList(), minY, minY - 2000d, false, "轴线（横向）", false);
            document.Dimensions.Add(new ViewDimension
            {
                Layer = ViewLayers.Dimension, Vertical = false,
                From = minX, To = maxX, AnchorPosition = minY,
                LinePosition = minY - (axisXs.Count > 0 ? 2800d : 2000d), Note = "总长"
            });

            AddPlanChain(document, openingYs, minX, minX - 1200d, true, "洞口定位（竖向）", true);
            if (axisYs.Count > 0)
                AddPlanChain(document, new List<double> { minY, maxY }.Concat(axisYs).ToList(), minX, minX - 2000d, true, "轴线（竖向）", false);
            document.Dimensions.Add(new ViewDimension
            {
                Layer = ViewLayers.Dimension, Vertical = true,
                From = minY, To = maxY, AnchorPosition = minX,
                LinePosition = minX - (axisYs.Count > 0 ? 2800d : 2000d), Note = "总宽"
            });
            _ = view;
        }

        /// <summary>把一串定位值连成连续尺寸链（排序去重后逐段出一条尺寸）。</summary>
        private static void AddPlanChain(ViewDocument document, List<double> values, double anchor, double linePosition,
            bool vertical, string note, bool keepDuplicatesAsIs)
        {
            var points = values.Where(IsFinite).Distinct().OrderBy(v => v).ToList();
            _ = keepDuplicatesAsIs;
            for (var i = 0; i + 1 < points.Count; i++)
            {
                if (points[i + 1] - points[i] < 1d) continue;
                document.Dimensions.Add(new ViewDimension
                {
                    Layer = ViewLayers.Dimension, Vertical = vertical,
                    From = points[i], To = points[i + 1],
                    AnchorPosition = anchor, LinePosition = linePosition, Note = note
                });
            }
        }

        private static void AddLine(ViewDocument document, string layer, double x1, double y1, double x2, double y2)
        {
            document.Lines.Add(new ViewLine { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
        }

        private static void AddRect(ViewDocument document, string layer, double x1, double y1, double x2, double y2)
        {
            AddLine(document, layer, x1, y1, x2, y1);
            AddLine(document, layer, x2, y1, x2, y2);
            AddLine(document, layer, x2, y2, x1, y2);
            AddLine(document, layer, x1, y2, x1, y1);
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
            List<OpeningDetail> openingDetails, OpeningTypeLibraryDocument openingLibrary, int scale,
            ViewDocument document)
        {
            var warnings = document.Warnings;
            var simplifiedDetail = scale >= 100;      // 1:100 及更小按简化画法（见 §5.5 比例分级）
            var dx = wall.X2 - wall.X1;
            var dy = wall.Y2 - wall.Y1;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1d) return;
            var ux = dx / length;
            var uy = dy / length;
            var half = (wall.Thickness > 0.5d ? wall.Thickness : 200d) / 2d;
            var nx = -uy * half;
            var ny = ux * half;
            var bodyStart = WallReferenceGeometry.BodyPoint(wall, wall.X1, wall.Y1);
            var bodyEnd = WallReferenceGeometry.BodyPoint(wall, wall.X2, wall.Y2);
            var zBase = model.BaseElevationOf(wall);
            var zTop = zBase + model.HeightOf(wall);

            var corners = new List<PointModel>
            {
                new PointModel(bodyStart.X + nx, bodyStart.Y + ny),
                new PointModel(bodyEnd.X + nx, bodyEnd.Y + ny),
                new PointModel(bodyEnd.X - nx, bodyEnd.Y - ny),
                new PointModel(bodyStart.X - nx, bodyStart.Y - ny)
            };

            // 斜墙：正交投影的轮廓不再是矩形，P0 用包围盒近似并提示
            var oblique = Math.Abs(ux * frame.Rx + uy * frame.Ry) > 0.02d;
            if (oblique && !warnings.Contains(ObliqueWarning)) warnings.Add(ObliqueWarning);

            var projA = frame.P(wall.X1, wall.Y1);
            var projB = frame.P(wall.X2, wall.Y2);
            // The wall has thickness: a cut can intersect its face even when
            // neither centreline endpoint lies on the cutting plane.
            var cornerDepths = corners.Select(point => frame.P(point.X, point.Y)).ToArray();
            var crossesCut = cornerDepths.Min() <= cutProj + Epsilon
                && cornerDepths.Max() >= cutProj - Epsilon;

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
                // 可点选锚点 + 洞口编号：预览里点一下就认得出是哪一樘，落图后也是门窗表联动的依据
                document.Anchors.Add(new ViewAnchor
                {
                    Kind = "opening", ElementId = opening.Id,
                    X1 = ou0, Y1 = oz0, X2 = ou1, Y2 = oz1
                });
                AddOpeningLabel(document, opening, ou0, ou1, oz0, oz1, scale);

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

        /// <summary>
        /// 洞口编号（C1518 / M0921…）写在洞口旁边：有窗台的写在洞口下方，落地门写在洞口上方
        ///（下方是墙脚/地面，写了压线）。字高按出图比例取（1:100 → 200mm ≈ 图上 2mm）。
        /// </summary>
        private static void AddOpeningLabel(ViewDocument document, OpeningModel opening,
            double u0, double u1, double z0, double z1, int scale)
        {
            var code = (opening.Code ?? string.Empty).Trim();
            if (code.Length == 0) return;
            var height = Math.Max(150d, Math.Max(1, scale) * 2d);
            var estimatedWidth = height * 0.62d * code.Length;          // 仅用于居中估算
            var x = (u0 + u1) / 2d - estimatedWidth / 2d;
            var y = opening.Sill >= 500d ? z0 - height * 1.6d : z1 + height * 0.6d;
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.Opening,
                Text = code,
                X = x,
                Y = y,
                Height = height
            });
        }

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
            var zBase = model.BaseElevationOf(column);
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

        private static void AddSlab(BuildingModelDocument model, SlabModel slab, Frame frame, bool isSection, double cutProj, double viewDepth,
            List<Rect> rects, List<Rect> cutRects, List<string> warnings)
        {
            var outline = slab.Outline ?? new List<PointModel>();
            if (outline.Count < 3) return;
            var zTop = model.TopElevationOf(slab);
            var zBase = zTop - (slab.Thickness > 0.5d ? slab.Thickness : 120d);

            double[] ps, us;
            ToPlane(outline, frame, out ps, out us);
            var projMin = ps.Min();
            var projMax = ps.Max();

            if (isSection)
            {
                if (projMin <= cutProj + Epsilon && projMax >= cutProj - Epsilon)
                {
                    var spans = new List<double[]>();
                    foreach (var triangle in SlabGeometry.Build(slab).Triangles)
                    {
                        double[] trianglePs, triangleUs;
                        ToPlane(triangle, frame, out trianglePs, out triangleUs);
                        double u0, u1;
                        if (TryClipAtP(trianglePs, triangleUs, cutProj, out u0, out u1)
                            && u1 - u0 > Epsilon) spans.Add(new[] { u0, u1 });
                    }
                    var merged = new List<double[]>();
                    foreach (var span in spans.OrderBy(s => s[0]))
                    {
                        if (merged.Count == 0 || span[0] > merged[merged.Count - 1][1] + Epsilon)
                            merged.Add(span);
                        else merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], span[1]);
                    }
                    foreach (var span in merged)
                    {
                        var cut = new Rect { U0 = span[0], U1 = span[1], Z0 = zBase, Z1 = zTop,
                            Depth = double.MaxValue - 1d, Layer = ViewLayers.Cut, IsCut = true };
                        rects.Add(cut); cutRects.Add(cut);
                    }
                    return;
                }
                if (projMin < cutProj - Epsilon) return;
                if (viewDepth > 0.5d && projMin > cutProj + viewDepth) return;
            }

            var body = Silhouette(outline, zBase, zTop, frame, ViewLayers.Elevation);
            if (body.HasValue) rects.Add(body.Value);
        }

        /// <summary>
        /// 立面/剖面里的楼梯：**剖面方向与梯段方向一致**时画锯齿（踏面 + 踢面）+ 梯段斜板 + 平台 + 栏杆；
        /// 方向垂直时（横着剖到楼梯）画成"一级一级摞起来的水平线"。
        ///
        /// 这几条线直接进视图（不走矩形遮挡那套）：楼梯在建筑内部，前面是剖切面之后的墙，
        /// 挡住的只是薄薄一层，画在最上层反而更像制图习惯里的"看线"。
        /// 剖切面在楼梯前面的（整部楼梯被切掉）与超出看线深度的，都不画。
        /// </summary>
        private static void AddStairProfile(BuildingModelDocument model, StairModel stair, Frame frame,
            bool isSection, double cutProj, double viewDepth, List<ViewLine> extraLines)
        {
            var geometry = StairGeometry.Build(model, stair);
            if (geometry == null || geometry.Flights.Count == 0) return;

            // 楼梯在"看的方向"上的范围（拿楼梯间矩形的两个角投一下）
            var cornerA = frame.P(geometry.X0, geometry.Y0);
            var cornerB = frame.P(geometry.X1, geometry.Y1);
            var projMin = Math.Min(cornerA, cornerB);
            var projMax = Math.Max(cornerA, cornerB);
            if (isSection)
            {
                if (projMax < cutProj - Epsilon) return;                              // 整部楼梯在剖切面前面：切掉了
                if (viewDepth > 0.5d && projMin > cutProj + viewDepth) return;         // 比看线深度还远：看不见
            }

            // 剖面方向与梯段方向一致吗？（沿梯段走一趟，看水平坐标 U 变化大不大）
            Func<double, double, double> u = frame.U;
            var first = geometry.Flights[0];
            var uStart = u(first.Start.X, first.Start.Y);
            var uEnd = u(first.End.X, first.End.Y);
            var alongFlight = Math.Abs(uEnd - uStart) > geometry.TreadRun * 0.5d;
            var layer = ViewLayers.Stair;
            const double handrailHeight = 1000d;
            const double treadThickness = 120d;

            foreach (var flight in geometry.Flights)
            {
                var rising = u(flight.End.X, flight.End.Y) > u(flight.Start.X, flight.Start.Y);
                var steps = flight.Steps.OrderBy(step => step.Number).ToList();
                if (steps.Count == 0) continue;

                if (alongFlight)
                {
                    var bottomStart = 0d;
                    var bottomEnd = 0d;
                    for (var index = 0; index < steps.Count; index++)
                    {
                        var step = steps[index];
                        var u0 = u(step.X0, step.Y0);
                        var u1 = u(step.X1, step.Y1);
                        if (u0 > u1) { var swap = u0; u0 = u1; u1 = swap; }
                        AddLine(extraLines, layer, u0, step.TopElevation, u1, step.TopElevation);            // 踏面
                        var riserU = rising ? u1 : u0;
                        AddLine(extraLines, layer, riserU, step.TopElevation - geometry.Riser,
                            riserU, step.TopElevation);                                                      // 踢面
                        var soffit = step.TopElevation - geometry.Riser - treadThickness;
                        if (index == 0) bottomStart = soffit;
                        bottomEnd = soffit;
                    }
                    // 梯段斜板（下面那条斜线）与栏杆
                    var firstLow = u(steps[0].X0, steps[0].Y0);
                    var firstHigh = u(steps[0].X1, steps[0].Y1);
                    var lastLow = u(steps[steps.Count - 1].X0, steps[steps.Count - 1].Y0);
                    var lastHigh = u(steps[steps.Count - 1].X1, steps[steps.Count - 1].Y1);
                    var uFirst = rising ? Math.Min(firstLow, firstHigh) : Math.Max(firstLow, firstHigh);
                    var uLast = rising ? Math.Max(lastLow, lastHigh) : Math.Min(lastLow, lastHigh);
                    AddLine(extraLines, layer, uFirst, bottomStart, uLast, bottomEnd);
                    AddLine(extraLines, layer, uFirst, steps[0].TopElevation + handrailHeight,
                        uLast, steps[steps.Count - 1].TopElevation + handrailHeight);
                }
                else
                {
                    // 横着剖到：一级一条水平线，看上去就是"摞起来的踏步"
                    foreach (var step in steps)
                    {
                        var u0 = u(step.X0, step.Y0);
                        var u1 = u(step.X1, step.Y1);
                        AddLine(extraLines, layer, Math.Min(u0, u1), step.TopElevation,
                            Math.Max(u0, u1), step.TopElevation);
                    }
                    var top = steps[steps.Count - 1].TopElevation + handrailHeight;
                    var a2 = u(steps[0].X0, steps[0].Y0);
                    var b2 = u(steps[0].X1, steps[0].Y1);
                    AddLine(extraLines, layer, Math.Min(a2, b2), top, Math.Max(a2, b2), top);
                }
            }

            // 休息平台（板顶 + 板底 + 两端竖线）与平台栏杆
            var landingA = u(geometry.LandingX0, geometry.LandingY0);
            var landingB = u(geometry.LandingX1, geometry.LandingY1);
            var lu0 = Math.Min(landingA, landingB);
            var lu1 = Math.Max(landingA, landingB);
            var landTop = geometry.LandingElevation;
            var landBottom = landTop - geometry.LandingThickness;
            AddLine(extraLines, layer, lu0, landTop, lu1, landTop);
            AddLine(extraLines, layer, lu0, landBottom, lu1, landBottom);
            AddLine(extraLines, layer, lu0, landBottom, lu0, landTop);
            AddLine(extraLines, layer, lu1, landBottom, lu1, landTop);
            AddLine(extraLines, layer, lu0, landTop + handrailHeight, lu1, landTop + handrailHeight);
        }
        /// <summary>
        /// 轴测图：把三维体量按轴测（或透视）投出来，只画**可见的轮廓线**。
        ///
        /// 走的是三维预览那一整条链路（<see cref="BuildingVolumeBuilder"/> → <see cref="VolumeRenderer"/>）：
        /// 背面剔除、藏在墙里的面丢掉、逐段消隐都在里面，所以落图出来的线和程序里看到的是同一份几何。
        /// 落图后是普通视图（线 + 图名），CAD 侧不用特殊处理。
        /// </summary>
        public static ViewDocument ProjectAxonometric(BuildingModelDocument model, ViewDefinitionModel view)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (view == null) throw new ArgumentNullException(nameof(view));
            var document = new ViewDocument
            {
                Id = view.Id,
                Title = view.Title,
                Kind = ViewKind.Axonometric,
                Scale = Math.Max(1, view.Scale)
            };
            var volume = BuildingVolumeBuilder.Build(model, null);
            if (volume.Faces.Count == 0)
            {
                document.Warnings.Add("模型里还没有可以出轴测图的构件（墙/柱/楼板/楼梯/屋面都为空）。");
                AddTitle(document, view);
                Normalize(document);
                return document;
            }
            var camera = new VolumeCamera
            {
                AzimuthDegrees = view.AzimuthDegrees, ElevationDegrees = view.ElevationDegrees,
                Zoom = 1d, Perspective = view.Perspective
            };
            var faces = VolumeRenderer.Project(volume, camera);
            foreach (var face in faces)
            {
                foreach (var segment in face.Edges ?? new List<List<PointModel>>())
                {
                    if (segment == null || segment.Count < 2) continue;
                    AddLine(document, ViewLayers.Axonometric,
                        segment[0].X, segment[0].Y, segment[1].X, segment[1].Y);
                }
            }
            document.Warnings.Add("轴测图：体量 " + volume.Faces.Count + " 面，可见 " + faces.Count
                + " 面，方位 " + Math.Round(camera.AzimuthDegrees) + "°、仰角 " + Math.Round(camera.ElevationDegrees)
                + "°（改角度重算即可换一个方向）。");
            AddTitle(document, view);
            Normalize(document);
            return document;
        }

        /// <summary>
        /// 立面/剖面里的坡屋面（双坡）：
        ///
        /// - **视线与屋脊平行**（站在山墙这一头看，或剖面顺着屋脊切）→ 看到**三角**轮廓：
        ///   檐口一条横线 + 两条坡线交于屋脊；
        /// - **视线与屋脊垂直**（正对坡面看）→ 看到**坡面**：檐口线 + 屋脊线两条横线（外面套一个矩形轮廓）；
        /// - 剖面时若剖切面在屋面范围内，再补一条"剖到的屋面"横线（该处屋面标高）。
        /// </summary>
        private static void AddRoofProfile(BuildingModelDocument model, RoofModel roof, Frame frame,
            bool isSection, double cutProj, double viewDepth, List<Rect> rects, List<ViewLine> extraLines)
        {
            var geometry = RoofGeometry.Build(model, roof);
            if (geometry == null || !geometry.IsValid) return;

            var eave = geometry.EaveElevation;
            var ridge = geometry.RidgeElevation;
            var bottom = eave - geometry.Thickness;

            // 视线是不是顺着屋脊（Vx/Vy 与屋脊方向同向）
            var alongRidgeView = geometry.AlongX ? Math.Abs(frame.Vx) > 0.5d : Math.Abs(frame.Vy) > 0.5d;
            var u0 = Math.Min(frame.U(geometry.X0, geometry.Y0), frame.U(geometry.X1, geometry.Y1));
            var u1 = Math.Max(frame.U(geometry.X0, geometry.Y0), frame.U(geometry.X1, geometry.Y1));
            // 屋脊在视图水平轴上的位置（坡面跨度方向的中点）
            var ridgeU = frame.U(geometry.RidgeStart.X, geometry.RidgeStart.Y);

            if (alongRidgeView)
            {
                // 山墙这头看：三角形（檐口横线 + 两条坡线）
                AddLine(extraLines, ViewLayers.Roof, u0, eave, u1, eave);
                AddLine(extraLines, ViewLayers.Roof, u0, eave, ridgeU, ridge);
                AddLine(extraLines, ViewLayers.Roof, u1, eave, ridgeU, ridge);
                // 檐口厚度（挑檐板边）
                if (Math.Abs(bottom - eave) > 1d)
                {
                    AddLine(extraLines, ViewLayers.Roof, u0, bottom, u1, bottom);
                    AddLine(extraLines, ViewLayers.Roof, u0, bottom, u0, eave);
                    AddLine(extraLines, ViewLayers.Roof, u1, bottom, u1, eave);
                }
            }
            else
            {
                // 正对坡面看：檐口线 + 屋脊线 + 两侧竖边（用矩形那套做遮挡，才能被前面的墙挡住）
                var body = new Rect
                {
                    U0 = u0, U1 = u1, Z0 = bottom, Z1 = ridge,
                    Depth = frame.Depth((geometry.X0 + geometry.X1) / 2d, (geometry.Y0 + geometry.Y1) / 2d),
                    Layer = ViewLayers.Roof
                };
                rects.Add(body);
                AddLine(extraLines, ViewLayers.Roof, u0, eave, u1, eave);
                AddLine(extraLines, ViewLayers.Roof, u0, ridge, u1, ridge);
            }

            if (!isSection) return;
            // 剖面：剖切面切到屋面时，补一条"这里屋面有多高"的横线
            var p0 = frame.P(geometry.X0, geometry.Y0);
            var p1 = frame.P(geometry.X1, geometry.Y1);
            var projMin = Math.Min(p0, p1);
            var projMax = Math.Max(p0, p1);
            if (projMax < cutProj - Epsilon) return;
            if (viewDepth > 0.5d && projMin > cutProj + viewDepth) return;
            if (cutProj < projMin - Epsilon || cutProj > projMax + Epsilon) return;
            // 切点离哪条檐口近，就算那边的坡高（双坡在剖切面上是单坡的一段）
            var distanceToEave = 0d;
            if (geometry.AlongX) distanceToEave = frame.Vy >= 0d ? cutProj - geometry.Y0 : geometry.Y1 - cutProj;
            else distanceToEave = frame.Vx >= 0d ? cutProj - geometry.X0 : geometry.X1 - cutProj;
            distanceToEave = Math.Max(0d, Math.Min(geometry.HalfSpan, distanceToEave));
            var cutZ = eave + distanceToEave * Math.Tan(geometry.PitchDegrees * Math.PI / 180d);
            AddLine(extraLines, ViewLayers.Roof, u0, cutZ, u1, cutZ);
        }

        /// <summary>往现成的线表里加一条线（楼梯轮廓直接进视图时用）。</summary>
        private static void AddLine(List<ViewLine> lines, string layer, double x1, double y1, double x2, double y2)
        {
            if (lines == null) return;
            lines.Add(new ViewLine { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
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
            // 尺寸链画在建筑外围（下方/左侧），图名要落到最外一道尺寸线之外，别压在尺寸上
            foreach (var dimension in document.Dimensions)
            {
                if (dimension == null || dimension.Vertical) continue;
                minZ = Math.Min(minZ, dimension.LinePosition);
            }
            foreach (var dimension in document.Dimensions)
            {
                if (dimension == null || !dimension.Vertical) continue;
                minU = Math.Min(minU, dimension.LinePosition);
            }
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
            // 锚点（图上元素 ↔ 模型构件）也在视图坐标里，必须一起平移 ——
            // 否则预览点选会按"没平移的位置"去命中，点到的地方和看到的窗对不上。
            foreach (var anchor in document.Anchors)
            {
                if (anchor == null) continue;
                anchor.X1 -= uMin; anchor.X2 -= uMin;
                anchor.Y1 -= zMin; anchor.Y2 -= zMin;
            }
            // 尺寸也要跟着平移：竖直尺寸量的是 Z（From/To）、界线与尺寸线在 X 上；
            // 水平尺寸反过来。漏了这一步，落图后尺寸会整体偏掉一个视图原点。
            foreach (var dimension in document.Dimensions)
            {
                if (dimension == null) continue;
                if (dimension.Vertical)
                {
                    dimension.From -= zMin;
                    dimension.To -= zMin;
                    dimension.AnchorPosition -= uMin;
                    dimension.LinePosition -= uMin;
                }
                else
                {
                    dimension.From -= uMin;
                    dimension.To -= uMin;
                    dimension.AnchorPosition -= zMin;
                    dimension.LinePosition -= zMin;
                }
            }
            foreach (var hatch in document.Hatches)
                foreach (var point in hatch.Boundary) { point.X -= uMin; point.Y -= zMin; }
            foreach (var circle in document.Circles)
            {
                if (circle == null) continue;
                circle.X -= uMin;
                circle.Y -= zMin;
            }
            foreach (var text in document.Texts) { text.X -= uMin; text.Y -= zMin; }
        }
    }
}
