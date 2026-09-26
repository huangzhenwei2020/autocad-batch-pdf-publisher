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
            if (view.Kind == ViewKind.Plan) return ProjectPlan(model, view, openingLibrary);

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
                    Note = "总高"
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

            // 墙：洞口把墙断开，两段面线 + 洞口两端的封口
            foreach (var wall in walls)
            {
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

            AddPlanDimensions(document, view, walls, openings);
            AddTitle(document, view);
            Normalize(document);
            return document;
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
            var start = PlanPoint(wall, from);
            var end = PlanPoint(wall, to);
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
            var isDoor = (opening.Kind ?? string.Empty).IndexOf("门", StringComparison.Ordinal) >= 0;
            var start = PlanPoint(wall, edges[0]);
            var end = PlanPoint(wall, edges[1]);

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

        /// <summary>平面外围尺寸：下方横向定位链 + 总长，左侧竖向定位链 + 总宽（都由墙端点与洞口边线取值）。</summary>
        private static void AddPlanDimensions(ViewDocument document, ViewDefinitionModel view,
            List<WallModel> walls, List<OpeningModel> openings)
        {
            if (walls.Count == 0) return;
            var minX = walls.Min(w => Math.Min(w.X1, w.X2)) - 200d;
            var maxX = walls.Max(w => Math.Max(w.X1, w.X2)) + 200d;
            var minY = walls.Min(w => Math.Min(w.Y1, w.Y2)) - 200d;
            var maxY = walls.Max(w => Math.Max(w.Y1, w.Y2)) + 200d;
            var xs = new List<double> { minX, maxX };
            var ys = new List<double> { minY, maxY };
            foreach (var wall in walls)
            {
                xs.Add(wall.X1); xs.Add(wall.X2);
                ys.Add(wall.Y1); ys.Add(wall.Y2);
            }
            foreach (var opening in openings)
            {
                var wall = walls.FirstOrDefault(w => w != null
                    && string.Equals(w.Id ?? string.Empty, opening.HostWallId ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                if (wall == null) continue;
                var edges = OpeningEdges(wall, opening);
                var a = PlanPoint(wall, edges[0]);
                var b = PlanPoint(wall, edges[1]);
                xs.Add(a.X); xs.Add(b.X);
                ys.Add(a.Y); ys.Add(b.Y);
            }
            xs = xs.Where(IsFinite).Distinct().OrderBy(v => v).ToList();
            ys = ys.Where(IsFinite).Distinct().OrderBy(v => v).ToList();
            _ = view;

            for (var i = 0; i + 1 < xs.Count; i++)
            {
                if (xs[i + 1] - xs[i] < 1d) continue;
                document.Dimensions.Add(new ViewDimension
                {
                    Layer = ViewLayers.Dimension, Vertical = false,
                    From = xs[i], To = xs[i + 1], AnchorPosition = minY, LinePosition = minY - 1200d,
                    Note = "定位（横向）"
                });
            }
            document.Dimensions.Add(new ViewDimension
            {
                Layer = ViewLayers.Dimension, Vertical = false,
                From = minX, To = maxX, AnchorPosition = minY, LinePosition = minY - 2000d, Note = "总长"
            });
            for (var i = 0; i + 1 < ys.Count; i++)
            {
                if (ys[i + 1] - ys[i] < 1d) continue;
                document.Dimensions.Add(new ViewDimension
                {
                    Layer = ViewLayers.Dimension, Vertical = true,
                    From = ys[i], To = ys[i + 1], AnchorPosition = minX, LinePosition = minX - 1200d,
                    Note = "定位（竖向）"
                });
            }
            document.Dimensions.Add(new ViewDimension
            {
                Layer = ViewLayers.Dimension, Vertical = true,
                From = minY, To = maxY, AnchorPosition = minX, LinePosition = minX - 2000d, Note = "总宽"
            });
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
            foreach (var text in document.Texts) { text.X -= uMin; text.Y -= zMin; }
        }
    }
}
