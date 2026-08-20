using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BatchPdfPublisher.Models
{
    public enum DoorWindowLineRole { Hole, Frame, Mullion, SashFrame, Opening, Material }

    public sealed class DoorWindowLineSegment
    {
        public DoorWindowLineSegment(double x1, double y1, double x2, double y2, DoorWindowLineRole role)
        { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Role = role; }
        public double X1, Y1, X2, Y2;
        public DoorWindowLineRole Role;
    }

    public sealed class DoorWindowCell
    {
        public DoorWindowCell(double left, double bottom, double right, double top)
        { Left = left; Bottom = bottom; Right = right; Top = top; }
        public double Left, Bottom, Right, Top;
        public bool IsDoor;
        public string Opening;
        public string Material = "无";
        public bool IsDeleted;
    }

    public sealed class DoorWindowLayoutCell
    {
        public double Left, Bottom, Right, Top;
        public string Opening;
        public string Material = "无";
        public bool IsDoor;
        public bool IsDeleted;
    }

    public sealed class DoorWindowElevationGeometry
    {
        public readonly List<DoorWindowLineSegment> Lines = new List<DoorWindowLineSegment>();
        public readonly List<DoorWindowCell> Cells = new List<DoorWindowCell>();
        public double HoleWidth, HoleHeight, FrameLeft, FrameBottom, FrameRight, FrameTop;
        /// <summary>凸窗转折面在主立面左右的投影范围；非凸窗均为 0。</summary>
        public double BayLeftExtent, BayRightExtent;
    }

    public static class DoorWindowElevationGeometryBuilder
    {
        private sealed class BoundaryEdge
        {
            public double X1, Y1, X2, Y2, NormalX, NormalY;
            public BoundaryEdge(double x1, double y1, double x2, double y2, double normalX, double normalY) { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; NormalX = normalX; NormalY = normalY; }
        }

        public static DoorWindowElevationGeometry Build(DoorWindowScheduleItem item)
        {
            if (item == null) throw new ArgumentNullException("item");
            // NaN/Infinity 会让 <=0 判断恒为 false，必须显式拦截，否则几何坐标
            // 携带 NaN 一路传染到 GDI+ 绘制，抛出"参数无效"。
            if (double.IsNaN(item.Width) || double.IsInfinity(item.Width)
                || double.IsNaN(item.Height) || double.IsInfinity(item.Height)
                || item.Width <= 0 || item.Height <= 0) throw new InvalidOperationException("门窗洞口尺寸无效。");
            var gap = item.HasInstallationGap ? (double.IsNaN(item.InstallationGap) || double.IsInfinity(item.InstallationGap) ? 0d : Math.Max(0d, item.InstallationGap)) : 0d;
            var left = gap; var bottom = gap; var right = item.Width - gap; var top = item.Height - gap;
            if (right <= left || top <= bottom) throw new InvalidOperationException("安装缝大于门窗洞口尺寸。");

            var result = new DoorWindowElevationGeometry
            {
                HoleWidth = item.Width, HoleHeight = item.Height,
                FrameLeft = left, FrameBottom = bottom, FrameRight = right, FrameTop = top
            };
            var cells = CreateCells(item, left, bottom, right, top);
            foreach (var cell in cells) if (string.IsNullOrWhiteSpace(cell.Opening)) cell.Opening = item.OpeningMode;
            MarkDoorCells(item, cells, left, right);
            result.Cells.AddRange(cells);
            if (item.HasInstallationGap && gap > 0d) result.Lines.AddRange(BuildInstallationGapOutline(cells, gap));
            AddCellBoundaryLines(result, cells, item);
            AddProfileWidthLines(result, cells, item.HasOuterFrame ? Math.Max(0d, item.OuterFrameWidth) : 0d, item.HasMullion ? Math.Max(0d, item.MullionWidth) : 0d, item);
            AddDoorFrameLines(result, cells, item);
            AddMaterialSymbols(result, cells, item);
            AddOpeningSymbols(result, item);
            AddArchTopLine(result, item);
            AddBayWindowReturnFaces(result, item);
            return result;
        }

        /// <summary>
        /// 凸窗的左右转折面采用三面展开表达：左右面与中间窗面同高、按深度展开为平面宽度，
        /// 仅以竖向分隔线区分三面，不绘制斜线或透视收口。转折面可各自设为墙或窗。
        /// </summary>
        private static void AddBayWindowReturnFaces(DoorWindowElevationGeometry geometry, DoorWindowScheduleItem item)
        {
            if (geometry == null || item == null || !string.Equals(item.ElevationType, "凸窗", StringComparison.Ordinal)) return;
            var leftDepth = NormalizeBayDepth(item.BayLeftDepth);
            var rightDepth = NormalizeBayDepth(item.BayRightDepth);
            var leftIsWindow = string.Equals((item.BayLeftSide ?? "墙").Trim(), "窗", StringComparison.Ordinal);
            var rightIsWindow = string.Equals((item.BayRightSide ?? "墙").Trim(), "窗", StringComparison.Ordinal);
            // 展开面按实际深度 1:1 展开，不做透视压缩。
            geometry.BayLeftExtent = leftDepth;
            geometry.BayRightExtent = rightDepth;
            AddReturn(true, geometry.BayLeftExtent, leftIsWindow);
            AddReturn(false, geometry.BayRightExtent, rightIsWindow);

            void AddReturn(bool onLeft, double depth, bool isWindow)
            {
                if (depth <= .01d) return;
                var x = onLeft ? 0d : geometry.HoleWidth;
                var outerX = onLeft ? x - depth : x + depth;
                var bottom = 0d; var top = geometry.HoleHeight;
                // 三面展开：转折面为完整矩形，x 位置就是与主窗面的分隔线。
                geometry.Lines.Add(new DoorWindowLineSegment(outerX, bottom, outerX, top, DoorWindowLineRole.Frame));
                geometry.Lines.Add(new DoorWindowLineSegment(outerX, bottom, x, bottom, DoorWindowLineRole.Frame));
                geometry.Lines.Add(new DoorWindowLineSegment(outerX, top, x, top, DoorWindowLineRole.Frame));
                if (isWindow)
                {
                    var gap = item.HasInstallationGap ? Math.Max(0d, item.InstallationGap) : 0d;
                    var clearHeight = Math.Max(1d, item.Height - gap * 2d);
                    var layoutValue = onLeft ? item.BayLeftCellLayout : item.BayRightCellLayout;
                    var layout = ParseCellLayout(layoutValue);
                    if (layout.Count == 0)
                        layout.Add(new DoorWindowLayoutCell { Left = 0d, Bottom = 0d, Right = depth, Top = clearHeight, Opening = "固定", Material = "玻璃" });
                    NormalizeBayLayout(layout, depth, clearHeight);
                    var sideItem = new DoorWindowScheduleItem
                    {
                        Width = depth, Height = clearHeight, ElevationType = "普通窗", DivisionPreset = "自定义", OpeningMode = "自定义",
                        HasInstallationGap = false, InstallationGap = 0d, HasOuterFrame = item.HasOuterFrame, OuterFrameWidth = item.OuterFrameWidth,
                        HasMullion = item.HasMullion, MullionWidth = item.MullionWidth, Material = "玻璃",
                        CustomCellLayout = SerializeCellLayout(layout), CellOpeningModes = string.Join("|", layout.Select(cell => cell.Opening ?? "固定"))
                    };
                    var sideGeometry = Build(sideItem);
                    var offsetX = onLeft ? -depth : geometry.HoleWidth;
                    foreach (var line in sideGeometry.Lines.Where(line => line.Role != DoorWindowLineRole.Hole))
                        geometry.Lines.Add(new DoorWindowLineSegment(line.X1 + offsetX, line.Y1 + gap, line.X2 + offsetX, line.Y2 + gap, line.Role));
                    // 侧窗格也纳入整体门窗的尺寸边界，生成标注时与正面一起连续标注。
                    foreach (var cell in sideGeometry.Cells)
                        geometry.Cells.Add(new DoorWindowCell(cell.Left + offsetX, cell.Bottom + gap, cell.Right + offsetX, cell.Top + gap)
                        { Opening = cell.Opening, Material = cell.Material, IsDoor = cell.IsDoor, IsDeleted = cell.IsDeleted });
                }
                else
                {
                    // 墙面同样是展开面，仅以短横线表示实体墙，不画透视斜线。
                    var y1 = bottom + (top - bottom) * .33d; var y2 = bottom + (top - bottom) * .67d;
                    geometry.Lines.Add(new DoorWindowLineSegment(outerX, y1, x, y1, DoorWindowLineRole.Material));
                    geometry.Lines.Add(new DoorWindowLineSegment(outerX, y2, x, y2, DoorWindowLineRole.Material));
                }
            }
        }

        private static void NormalizeBayLayout(IList<DoorWindowLayoutCell> cells, double width, double height)
        {
            if (cells == null || cells.Count == 0) return;
            var oldWidth = cells.Max(x => x.Right); var oldHeight = cells.Max(x => x.Top);
            if (oldWidth <= 0d || oldHeight <= 0d) return;
            var sx = width / oldWidth; var sy = height / oldHeight;
            foreach (var cell in cells) { cell.Left *= sx; cell.Right *= sx; cell.Bottom *= sy; cell.Top *= sy; }
        }

        private static double NormalizeBayDepth(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d) return 600d;
            return Math.Min(5000d, value);
        }

        /// <summary>拱形窗：在顶部亮子内画一条圆弧分格线，表示拱形轮廓（折线逼近，便于预览与 CAD 统一绘制）。</summary>
        private static void AddArchTopLine(DoorWindowElevationGeometry geometry, DoorWindowScheduleItem item)
        {
            if (item == null || geometry == null) return;
            var isArch = string.Equals(item.ElevationType, "拱形窗", StringComparison.Ordinal) || (item.DivisionPreset ?? string.Empty) == "拱形亮子";
            if (!isArch) return;
            var topCell = geometry.Cells.OrderByDescending(x => x.Top).FirstOrDefault();
            if (topCell == null) return;
            var left = topCell.Left; var right = topCell.Right; var bottom = topCell.Bottom; var top = topCell.Top;
            var centerX = (left + right) / 2d; var width = right - left;
            var radius = Math.Min(width / 2d, Math.Max(0d, top - bottom));
            if (radius <= 0.5d) return;
            const int segments = 16;
            for (var index = 0; index < segments; index++)
            {
                var angle1 = Math.PI - Math.PI * index / segments; var angle2 = Math.PI - Math.PI * (index + 1) / segments;
                var x1 = centerX + Math.Cos(angle1) * radius; var y1 = bottom + Math.Sin(angle1) * radius;
                var x2 = centerX + Math.Cos(angle2) * radius; var y2 = bottom + Math.Sin(angle2) * radius;
                geometry.Lines.Add(new DoorWindowLineSegment(x1, y1, x2, y2, DoorWindowLineRole.Mullion));
            }
        }

        public static List<DoorWindowLineSegment> BuildInstallationGapOutline(IList<DoorWindowCell> cells, double gap)
        {
            const double tolerance = .001d; var result = new List<DoorWindowLineSegment>();
            if (cells == null || cells.Count == 0 || gap <= 0d) return result;
            var xs = DistinctCoordinates(cells.SelectMany(x => new[] { x.Left, x.Right }));
            var ys = DistinctCoordinates(cells.SelectMany(x => new[] { x.Bottom, x.Top }));
            if (xs.Count < 2 || ys.Count < 2) return result;
            var occupied = new bool[xs.Count - 1, ys.Count - 1];
            for (var x = 0; x < xs.Count - 1; x++)
                for (var y = 0; y < ys.Count - 1; y++)
                {
                    var cx = (xs[x] + xs[x + 1]) / 2d; var cy = (ys[y] + ys[y + 1]) / 2d;
                    occupied[x, y] = cells.Any(cell => cx > cell.Left - tolerance && cx < cell.Right + tolerance && cy > cell.Bottom - tolerance && cy < cell.Top + tolerance);
                }
            var edges = new List<BoundaryEdge>();
            for (var x = 0; x < xs.Count - 1; x++)
                for (var y = 0; y < ys.Count - 1; y++)
                {
                    if (!occupied[x, y]) continue;
                    if (y == 0 || !occupied[x, y - 1]) edges.Add(new BoundaryEdge(xs[x], ys[y], xs[x + 1], ys[y], 0, -1));
                    if (x == xs.Count - 2 || !occupied[x + 1, y]) edges.Add(new BoundaryEdge(xs[x + 1], ys[y], xs[x + 1], ys[y + 1], 1, 0));
                    if (y == ys.Count - 2 || !occupied[x, y + 1]) edges.Add(new BoundaryEdge(xs[x + 1], ys[y + 1], xs[x], ys[y + 1], 0, 1));
                    if (x == 0 || !occupied[x - 1, y]) edges.Add(new BoundaryEdge(xs[x], ys[y + 1], xs[x], ys[y], -1, 0));
                }
            while (edges.Count > 0)
            {
                var loop = new List<BoundaryEdge> { edges[0] }; edges.RemoveAt(0);
                while (!Near(loop[loop.Count - 1].X2, loop[loop.Count - 1].Y2, loop[0].X1, loop[0].Y1) && edges.Count > 0)
                {
                    var next = edges.FindIndex(edge => Near(loop[loop.Count - 1].X2, loop[loop.Count - 1].Y2, edge.X1, edge.Y1));
                    if (next < 0) break; loop.Add(edges[next]); edges.RemoveAt(next);
                }
                if (loop.Count == 0) continue;
                var vertices = new List<double[]>();
                for (var index = 0; index < loop.Count; index++)
                {
                    var current = loop[index]; var previous = loop[(index + loop.Count - 1) % loop.Count];
                    var sameNormal = Math.Abs(previous.NormalX - current.NormalX) < tolerance && Math.Abs(previous.NormalY - current.NormalY) < tolerance;
                    var offsetX = sameNormal ? current.NormalX * gap : (previous.NormalX + current.NormalX) * gap;
                    var offsetY = sameNormal ? current.NormalY * gap : (previous.NormalY + current.NormalY) * gap;
                    vertices.Add(new[] { current.X1 + offsetX, current.Y1 + offsetY });
                }
                for (var index = 0; index < vertices.Count; index++)
                {
                    var next = vertices[(index + 1) % vertices.Count]; var current = vertices[index];
                    if (!Near(current[0], current[1], next[0], next[1])) result.Add(new DoorWindowLineSegment(current[0], current[1], next[0], next[1], DoorWindowLineRole.Hole));
                }
            }
            return result;

            List<double> DistinctCoordinates(IEnumerable<double> values)
            {
                var ordered = values.OrderBy(x => x).ToList(); var distinct = new List<double>();
                foreach (var value in ordered) if (distinct.Count == 0 || Math.Abs(value - distinct[distinct.Count - 1]) > tolerance) distinct.Add(value);
                return distinct;
            }
            bool Near(double x1, double y1, double x2, double y2) { return Math.Abs(x1 - x2) <= tolerance && Math.Abs(y1 - y2) <= tolerance; }
        }

        private static List<DoorWindowCell> CreateCells(DoorWindowScheduleItem item, double left, double bottom, double right, double top)
        {
            var cells = new List<DoorWindowCell>();
            var width = right - left; var height = top - bottom;
            var preset = (item.DivisionPreset ?? string.Empty).Trim();
            if (preset == "自定义")
            {
                var layout = ParseCellLayout(item.CustomCellLayout);
                if (layout.Count > 0)
                {
                    ValidateCellLayout(layout, width, height);
                    foreach (var cell in layout.Where(x => !x.IsDeleted))
                        cells.Add(new DoorWindowCell(left + cell.Left, bottom + cell.Bottom, left + cell.Right, bottom + cell.Top) { Opening = cell.Opening, Material = string.IsNullOrWhiteSpace(cell.Material) ? (string.IsNullOrWhiteSpace(item.Material) ? "无" : item.Material) : cell.Material, IsDoor = cell.IsDoor, IsDeleted = false });
                    if (cells.Count == 0) throw new InvalidOperationException("至少要保留一个门窗面板。");
                    return cells;
                }
                var columns = ParseRatios(item.CustomColumnRatios); var rows = ParseRatios(item.CustomRowRatios);
                if (columns.Count == 0 || rows.Count == 0) throw new InvalidOperationException("自定义分格比例无效。");
                var columnSizes = ResolveActualSizes(item.CustomColumnWidths, columns, width, "列宽");
                var rowSizes = ResolveActualSizes(item.CustomRowHeights, rows, height, "行高");
                var y = bottom;
                foreach (var rowSize in rowSizes)
                {
                    var nextY = y + rowSize; var x = left;
                    foreach (var columnSize in columnSizes)
                    {
                        var nextX = x + columnSize; cells.Add(new DoorWindowCell(x, y, nextX, nextY)); x = nextX;
                    }
                    y = nextY;
                }
                foreach (var cell in cells) cell.Material = string.IsNullOrWhiteSpace(item.Material) ? "无" : item.Material;
                return cells;
            }
            switch (preset)
            {
                case "双扇等分":
                    cells.Add(new DoorWindowCell(left, bottom, left + width / 2d, top));
                    cells.Add(new DoorWindowCell(left + width / 2d, bottom, right, top));
                    break;
                case "三扇等分":
                    for (var index = 0; index < 3; index++) cells.Add(new DoorWindowCell(left + width * index / 3d, bottom, left + width * (index + 1) / 3d, top));
                    break;
                case "四扇等分":
                    for (var index = 0; index < 4; index++) cells.Add(new DoorWindowCell(left + width * index / 4d, bottom, left + width * (index + 1) / 4d, top));
                    break;
                case "五扇等分":
                    for (var index = 0; index < 5; index++) cells.Add(new DoorWindowCell(left + width * index / 5d, bottom, left + width * (index + 1) / 5d, top));
                    break;
                case "拱形亮子":
                    // 下方矩形分格 + 顶部拱形亮子（亮子内横向一至两扇）。
                    var archBottom = bottom + height * .68d;
                    if (width > 1500d)
                    {
                        cells.Add(new DoorWindowCell(left, bottom, left + width / 2d, archBottom));
                        cells.Add(new DoorWindowCell(left + width / 2d, bottom, right, archBottom));
                        cells.Add(new DoorWindowCell(left, archBottom, right, top));
                    }
                    else
                    {
                        cells.Add(new DoorWindowCell(left, bottom, right, archBottom));
                        cells.Add(new DoorWindowCell(left, archBottom, right, top));
                    }
                    break;
                case "上亮":
                    cells.Add(new DoorWindowCell(left, bottom, right, bottom + height * .72d));
                    cells.Add(new DoorWindowCell(left, bottom + height * .72d, right, top));
                    break;
                case "侧亮":
                    cells.Add(new DoorWindowCell(left, bottom, left + width * .68d, top));
                    cells.Add(new DoorWindowCell(left + width * .68d, bottom, right, top));
                    break;
                case "上亮+侧亮":
                    var splitX = left + width * .68d; var splitY = bottom + height * .72d;
                    cells.Add(new DoorWindowCell(left, bottom, splitX, splitY));
                    cells.Add(new DoorWindowCell(splitX, bottom, right, splitY));
                    cells.Add(new DoorWindowCell(left, splitY, right, top));
                    break;
                case "门联窗":
                    var doorX = left + width * .58d;
                    cells.Add(new DoorWindowCell(left, bottom, doorX, top));
                    cells.Add(new DoorWindowCell(doorX, bottom, right, top));
                    break;
                default:
                    cells.Add(new DoorWindowCell(left, bottom, right, top));
                    break;
            }
            foreach (var cell in cells) cell.Material = string.IsNullOrWhiteSpace(item.Material) ? "无" : item.Material;
            return cells;
        }

        private static void AddDividerLines(DoorWindowElevationGeometry geometry, IList<DoorWindowCell> cells, double left, double bottom, double right, double top)
        {
            var vertical = new HashSet<string>(); var horizontal = new HashSet<string>();
            foreach (var cell in cells)
            {
                AddVertical(cell.Left, cell.Bottom, cell.Top); AddVertical(cell.Right, cell.Bottom, cell.Top);
                AddHorizontal(cell.Bottom, cell.Left, cell.Right); AddHorizontal(cell.Top, cell.Left, cell.Right);
            }

            void AddVertical(double x, double start, double end)
            {
                if (x <= left + .001 || x >= right - .001) return;
                var key = x.ToString("0.###") + ":" + start.ToString("0.###") + ":" + end.ToString("0.###");
                if (vertical.Add(key)) geometry.Lines.Add(new DoorWindowLineSegment(x, start, x, end, DoorWindowLineRole.Mullion));
            }
            void AddHorizontal(double y, double start, double end)
            {
                if (y <= bottom + .001 || y >= top - .001) return;
                var key = y.ToString("0.###") + ":" + start.ToString("0.###") + ":" + end.ToString("0.###");
                if (horizontal.Add(key)) geometry.Lines.Add(new DoorWindowLineSegment(start, y, end, y, DoorWindowLineRole.Mullion));
            }
        }

        private static void AddCellBoundaryLines(DoorWindowElevationGeometry geometry, IList<DoorWindowCell> cells, DoorWindowScheduleItem item)
        {
            const double tolerance = .05d; var keys = new HashSet<string>();
            for (var index = 0; index < cells.Count; index++)
            {
                var cell = cells[index];
                Add(cell.Left, cell.Bottom, cell.Left, cell.Top, SharedNeighbor(index, true, cell.Left, cell.Bottom, cell.Top), true, cell, index);
                Add(cell.Right, cell.Bottom, cell.Right, cell.Top, SharedNeighbor(index, true, cell.Right, cell.Bottom, cell.Top), true, cell, index);
                Add(cell.Left, cell.Bottom, cell.Right, cell.Bottom, SharedNeighbor(index, false, cell.Bottom, cell.Left, cell.Right), false, cell, index);
                Add(cell.Left, cell.Top, cell.Right, cell.Top, SharedNeighbor(index, false, cell.Top, cell.Left, cell.Right), false, cell, index);
            }

            DoorWindowCell SharedNeighbor(int owner, bool vertical, double coordinate, double start, double end)
            {
                for (var otherIndex = 0; otherIndex < cells.Count; otherIndex++)
                {
                    if (otherIndex == owner) continue; var other = cells[otherIndex];
                    var adjacent = vertical
                        ? (Math.Abs(other.Left - coordinate) < tolerance || Math.Abs(other.Right - coordinate) < tolerance) && Math.Min(end, other.Top) - Math.Max(start, other.Bottom) > tolerance
                        : (Math.Abs(other.Bottom - coordinate) < tolerance || Math.Abs(other.Top - coordinate) < tolerance) && Math.Min(end, other.Right) - Math.Max(start, other.Left) > tolerance;
                    if (adjacent) return other;
                }
                return null;
            }
            void Add(double x1, double y1, double x2, double y2, DoorWindowCell neighbor, bool vertical, DoorWindowCell owner, int ownerIndex)
            {
                if (neighbor != null)
                {
                    // 相邻门扇或可开启窗扇取消实体分隔框，仅保留一根中心实线；上悬、下悬仍按普通分隔框处理。
                    if (!item.HasMullion || vertical && MergeDoorDivider(owner, neighbor))
                    {
                        if (item.HasMullion && vertical) TrimMergedDivider(ownerIndex, owner, ref y1, ref y2);
                        if (vertical ? y2 <= y1 + tolerance : x2 <= x1 + tolerance) return;
                        var divider = x1.ToString("0.###") + ":" + y1.ToString("0.###") + ":" + x2.ToString("0.###") + ":" + y2.ToString("0.###") + ":D";
                        if (keys.Add(divider)) geometry.Lines.Add(new DoorWindowLineSegment(x1, y1, x2, y2, DoorWindowLineRole.Frame));
                    }
                    return;
                }
                var forward = x1.ToString("0.###") + ":" + y1.ToString("0.###") + ":" + x2.ToString("0.###") + ":" + y2.ToString("0.###");
                var reverse = x2.ToString("0.###") + ":" + y2.ToString("0.###") + ":" + x1.ToString("0.###") + ":" + y1.ToString("0.###");
                if (keys.Contains(forward) || keys.Contains(reverse)) return; keys.Add(forward);
                geometry.Lines.Add(new DoorWindowLineSegment(x1, y1, x2, y2, DoorWindowLineRole.Frame));
            }
            void TrimMergedDivider(int ownerIndex, DoorWindowCell owner, ref double start, ref double end)
            {
                var mullion = Math.Max(0d, item.MullionWidth);
                var outer = item.HasOuterFrame ? Math.Max(0d, item.OuterFrameWidth) : 0d;
                var bottomNeighbor = SharedNeighbor(ownerIndex, false, owner.Bottom, owner.Left, owner.Right);
                var topNeighbor = SharedNeighbor(ownerIndex, false, owner.Top, owner.Left, owner.Right);
                start += bottomNeighbor != null ? mullion / 2d : IsNShapedDoor(item, owner) ? 0d : outer;
                end -= topNeighbor != null ? mullion / 2d : outer;
            }
        }

        private static void AddProfileWidthLines(DoorWindowElevationGeometry geometry, IList<DoorWindowCell> cells, double outerWidth, double mullionWidth, DoorWindowScheduleItem item)
        {
            const double tolerance = .05d; var keys = new HashSet<string>();
            foreach (var cell in cells)
            {
                var width = cell.Right - cell.Left; var height = cell.Top - cell.Bottom;
                var leftNeighbor = Neighbor(cell, true, cell.Left); var rightNeighbor = Neighbor(cell, true, cell.Right);
                var bottomNeighbor = Neighbor(cell, false, cell.Bottom); var topNeighbor = Neighbor(cell, false, cell.Top);
                var nShapedBottom = bottomNeighbor == null && IsNShapedDoor(item, cell);
                var leftInset = leftNeighbor == null ? outerWidth : SharedInset(cell, leftNeighbor, mullionWidth, true);
                var rightInset = rightNeighbor == null ? outerWidth : SharedInset(cell, rightNeighbor, mullionWidth, true);
                var bottomInset = nShapedBottom ? 0d : bottomNeighbor == null ? outerWidth : SharedInset(cell, bottomNeighbor, mullionWidth);
                var topInset = topNeighbor == null ? outerWidth : SharedInset(cell, topNeighbor, mullionWidth);
                leftInset = Math.Min(Math.Max(0d, leftInset), width * .45d); rightInset = Math.Min(Math.Max(0d, rightInset), width * .45d);
                bottomInset = Math.Min(Math.Max(0d, bottomInset), height * .45d); topInset = Math.Min(Math.Max(0d, topInset), height * .45d);
                var l = cell.Left + leftInset; var r = cell.Right - rightInset; var b = cell.Bottom + bottomInset; var t = cell.Top - topInset;
                if (!nShapedBottom && (bottomNeighbor != null && mullionWidth > 0d || bottomNeighbor == null && outerWidth > 0d))
                    Add(l, b, r, b);
                if (rightNeighbor != null && mullionWidth > 0d && !MergeDoorDivider(cell, rightNeighbor) || rightNeighbor == null && outerWidth > 0d) Add(r, b, r, t);
                if (topNeighbor != null && mullionWidth > 0d || topNeighbor == null && outerWidth > 0d) Add(r, t, l, t);
                if (leftNeighbor != null && mullionWidth > 0d && !MergeDoorDivider(cell, leftNeighbor) || leftNeighbor == null && outerWidth > 0d) Add(l, t, l, b);
            }
            DoorWindowCell Neighbor(DoorWindowCell owner, bool vertical, double coordinate)
            {
                foreach (var other in cells.Where(x => !ReferenceEquals(x, owner)))
                {
                    if (vertical && (Math.Abs(other.Left - coordinate) < tolerance || Math.Abs(other.Right - coordinate) < tolerance) && Math.Min(owner.Top, other.Top) - Math.Max(owner.Bottom, other.Bottom) > tolerance) return other;
                    if (!vertical && (Math.Abs(other.Bottom - coordinate) < tolerance || Math.Abs(other.Top - coordinate) < tolerance) && Math.Min(owner.Right, other.Right) - Math.Max(owner.Left, other.Left) > tolerance) return other;
                }
                return null;
            }
            // 先按固定框计算每格净空；开启扇的独立边框在固定框内部另行扣减。
            double SharedInset(DoorWindowCell owner, DoorWindowCell neighbor, double normalWidth, bool vertical = false) { return vertical && MergeDoorDivider(owner, neighbor) ? 0d : normalWidth / 2d; }
            void Add(double x1, double y1, double x2, double y2)
            {
                var key = x1.ToString("0.###") + ":" + y1.ToString("0.###") + ":" + x2.ToString("0.###") + ":" + y2.ToString("0.###");
                if (keys.Add(key)) geometry.Lines.Add(new DoorWindowLineSegment(x1, y1, x2, y2, DoorWindowLineRole.Mullion));
            }
        }

        private static void AddDoorFrameLines(DoorWindowElevationGeometry geometry, IList<DoorWindowCell> cells, DoorWindowScheduleItem item)
        {
            var width = Math.Max(0d, item == null ? 0d : item.DoorFrameWidth);
            if (width <= 0d) return;
            foreach (var cell in cells.Where(x => IsOperable(x.Opening) && !x.IsDeleted))
            {
                var frame = FixedFrameArea(geometry, item, cell);
                var left = frame.Left + width; var right = frame.Right - width;
                var bottom = frame.Bottom + width; var top = frame.Top - width;
                if (right <= left || top <= bottom) continue;
                geometry.Lines.Add(new DoorWindowLineSegment(left, bottom, right, bottom, DoorWindowLineRole.SashFrame));
                geometry.Lines.Add(new DoorWindowLineSegment(right, bottom, right, top, DoorWindowLineRole.SashFrame));
                geometry.Lines.Add(new DoorWindowLineSegment(right, top, left, top, DoorWindowLineRole.SashFrame));
                geometry.Lines.Add(new DoorWindowLineSegment(left, top, left, bottom, DoorWindowLineRole.SashFrame));
            }
        }

        private static void AddMaterialSymbols(DoorWindowElevationGeometry geometry, IList<DoorWindowCell> cells, DoorWindowScheduleItem item)
        {
            const double tolerance = .05d; var outerWidth = item.HasOuterFrame ? Math.Max(0d, item.OuterFrameWidth) : 0d; var mullionWidth = item.HasMullion ? Math.Max(0d, item.MullionWidth) : 0d;
            foreach (var cell in cells)
            {
                var material = string.IsNullOrWhiteSpace(cell.Material) ? "无" : cell.Material;
                if (material == "无") continue;
                var extra = Math.Min(12d, Math.Min(cell.Right - cell.Left, cell.Top - cell.Bottom) * .04d);
                var leftNeighbor = Neighbor(cell, true, cell.Left); var rightNeighbor = Neighbor(cell, true, cell.Right); var bottomNeighbor = Neighbor(cell, false, cell.Bottom); var topNeighbor = Neighbor(cell, false, cell.Top);
                var l = cell.Left + (leftNeighbor == null ? outerWidth : Shared(cell, leftNeighbor, true)) + extra;
                var r = cell.Right - (rightNeighbor == null ? outerWidth : Shared(cell, rightNeighbor, true)) - extra;
                var b = cell.Bottom + (bottomNeighbor == null ? outerWidth : Shared(cell, bottomNeighbor, false)) + extra;
                var t = cell.Top - (topNeighbor == null ? outerWidth : Shared(cell, topNeighbor, false)) - extra;
                if (r <= l || t <= b) continue;
                if (material == "玻璃")
                {
                    var cx = (l + r) / 2d; var cy = (b + t) / 2d; var mark = Math.Min(r - l, t - b) * .09d;
                    for (var index = -1; index <= 1; index++) geometry.Lines.Add(new DoorWindowLineSegment(cx - mark, cy - mark * .45d + index * mark * .55d, cx + mark, cy + mark * .45d + index * mark * .55d, DoorWindowLineRole.Material));
                }
                else if (material == "百叶")
                    for (var index = 1; index < 8; index++) { var y = b + (t - b) * index / 8d; geometry.Lines.Add(new DoorWindowLineSegment(l, y, r, y, DoorWindowLineRole.Material)); }
                else if (material == "实板")
                { geometry.Lines.Add(new DoorWindowLineSegment(l, b, r, t, DoorWindowLineRole.Material)); geometry.Lines.Add(new DoorWindowLineSegment(l, t, r, b, DoorWindowLineRole.Material)); }
            }
            DoorWindowCell Neighbor(DoorWindowCell owner, bool vertical, double coordinate)
            {
                foreach (var other in cells.Where(x => !ReferenceEquals(x, owner)))
                {
                    if (vertical && (Math.Abs(other.Left - coordinate) < tolerance || Math.Abs(other.Right - coordinate) < tolerance) && Math.Min(owner.Top, other.Top) - Math.Max(owner.Bottom, other.Bottom) > tolerance) return other;
                    if (!vertical && (Math.Abs(other.Bottom - coordinate) < tolerance || Math.Abs(other.Top - coordinate) < tolerance) && Math.Min(owner.Right, other.Right) - Math.Max(owner.Left, other.Left) > tolerance) return other;
                }
                return null;
            }
            double Shared(DoorWindowCell owner, DoorWindowCell neighbor, bool vertical)
            {
                return vertical && MergeDoorDivider(owner, neighbor) ? 0d : mullionWidth / 2d;
            }
        }

        private static bool IsOperable(string opening)
        {
            var value = (opening ?? string.Empty).Trim();
            return value != "" && value != "固定" && value != "未设置" && value != "百叶";
        }

        private static void AddOpeningSymbols(DoorWindowElevationGeometry geometry, DoorWindowScheduleItem item)
        {
            var modes = (item.CellOpeningModes ?? string.Empty).Split(new[] { '|' }, StringSplitOptions.None);
            if (item.DivisionPreset == "自定义" && geometry.Cells.Any(x => !string.IsNullOrWhiteSpace(x.Opening)))
            {
                foreach (var cell in geometry.Cells) ApplyOpeningMode(geometry, OpeningArea(geometry, item, cell), cell.Opening);
                return;
            }
            if (item.DivisionPreset == "自定义" && modes.Length == geometry.Cells.Count)
            {
                for (var index = 0; index < geometry.Cells.Count; index++) ApplyOpeningMode(geometry, OpeningArea(geometry, item, geometry.Cells[index]), modes[index]);
                return;
            }
            var mode = (item.OpeningMode ?? string.Empty).Trim();
            if (mode == "" || mode == "未设置" || mode == "固定") return;
            if (mode == "百叶")
            {
                foreach (var source in geometry.Cells)
                {
                    var cell = OpeningArea(geometry, item, source);
                    for (var index = 1; index < 7; index++)
                    {
                        var y = cell.Bottom + (cell.Top - cell.Bottom) * index / 7d;
                        geometry.Lines.Add(new DoorWindowLineSegment(cell.Left, y, cell.Right, y, DoorWindowLineRole.Opening));
                    }
                }
                return;
            }
            if (mode == "双扇平开")
            {
                // Adjacent leaves hinge at the shared mullion.  The previous
                // test used the outside jamb and mirrored every paired symbol.
                foreach (var source in geometry.Cells)
                {
                    var cell = OpeningArea(geometry, item, source);
                    AddSideHung(geometry.Lines, cell, cell.Left >= (geometry.FrameLeft + geometry.FrameRight) / 2d);
                }
                return;
            }
            foreach (var cell in geometry.Cells)
                ApplyOpeningMode(geometry, OpeningArea(geometry, item, cell), mode);
        }

        private static DoorWindowCell OpeningArea(DoorWindowElevationGeometry geometry, DoorWindowScheduleItem item, DoorWindowCell cell)
        {
            var fixedArea = FixedFrameArea(geometry, item, cell);
            var doorFrame = IsOperable(cell.Opening) ? Math.Max(0d, item.DoorFrameWidth) : 0d;
            var clearance = Math.Min(6d, Math.Min(cell.Right - cell.Left, cell.Top - cell.Bottom) * .01d);
            var left = fixedArea.Left + doorFrame + clearance; var right = fixedArea.Right - doorFrame - clearance;
            var bottom = fixedArea.Bottom + doorFrame + clearance; var top = fixedArea.Top - doorFrame - clearance;
            if (right <= left || top <= bottom) return cell;
            return new DoorWindowCell(left, bottom, right, top) { Opening = cell.Opening, Material = cell.Material, IsDoor = cell.IsDoor };
        }

        private static DoorWindowCell FixedFrameArea(DoorWindowElevationGeometry geometry, DoorWindowScheduleItem item, DoorWindowCell cell)
        {
            const double tolerance = .05d;
            var outer = item.HasOuterFrame ? Math.Max(0d, item.OuterFrameWidth) : 0d;
            var mullion = item.HasMullion ? Math.Max(0d, item.MullionWidth) : 0d;
            DoorWindowCell Neighbor(bool vertical, double coordinate)
            {
                foreach (var other in geometry.Cells.Where(x => !ReferenceEquals(x, cell)))
                {
                    if (vertical && (Math.Abs(other.Left - coordinate) < tolerance || Math.Abs(other.Right - coordinate) < tolerance) && Math.Min(cell.Top, other.Top) - Math.Max(cell.Bottom, other.Bottom) > tolerance) return other;
                    if (!vertical && (Math.Abs(other.Bottom - coordinate) < tolerance || Math.Abs(other.Top - coordinate) < tolerance) && Math.Min(cell.Right, other.Right) - Math.Max(cell.Left, other.Left) > tolerance) return other;
                }
                return null;
            }
            var leftNeighbor = Neighbor(true, cell.Left); var rightNeighbor = Neighbor(true, cell.Right);
            var bottomNeighbor = Neighbor(false, cell.Bottom); var topNeighbor = Neighbor(false, cell.Top);
            var left = cell.Left + (leftNeighbor == null ? outer : MergeDoorDivider(cell, leftNeighbor) ? 0d : mullion / 2d);
            var right = cell.Right - (rightNeighbor == null ? outer : MergeDoorDivider(cell, rightNeighbor) ? 0d : mullion / 2d);
            var bottom = cell.Bottom + (bottomNeighbor == null && IsNShapedDoor(item, cell) ? 0d : bottomNeighbor == null ? outer : mullion / 2d);
            var top = cell.Top - (topNeighbor == null ? outer : mullion / 2d);
            return new DoorWindowCell(left, bottom, right, top) { Opening = cell.Opening, IsDoor = cell.IsDoor };
        }

        private static bool IsNShapedDoor(DoorWindowScheduleItem item, DoorWindowCell cell)
        {
            return item != null && cell != null && cell.IsDoor && string.Equals(item.DoorFrameType, "N型", StringComparison.Ordinal);
        }

        private static bool MergeDoorDivider(DoorWindowCell first, DoorWindowCell second)
        {
            return first != null && second != null
                && (first.IsDoor && second.IsDoor || IsOperable(first.Opening) && IsOperable(second.Opening))
                && !IsSuspended(first.Opening) && !IsSuspended(second.Opening);
        }

        private static bool IsSuspended(string opening)
        {
            var value = (opening ?? string.Empty).Trim();
            return value == "上悬" || value == "下悬";
        }

        private static void ApplyOpeningMode(DoorWindowElevationGeometry geometry, DoorWindowCell cell, string value)
        {
            var mode = (value ?? string.Empty).Trim();
            if (mode == "" || mode == "未设置" || mode == "固定") return;
            if (mode == "左平开") AddSideHung(geometry.Lines, cell, true);
            else if (mode == "右平开") AddSideHung(geometry.Lines, cell, false);
            else if (mode == "上悬") AddHung(geometry.Lines, cell, true);
            else if (mode == "下悬") AddHung(geometry.Lines, cell, false);
            else if (mode == "中悬")
            {
                // 中悬窗：窗扇绕水平中轴旋转，画成顶边铰接 + 中部轴线的双三角示意。
                var middleY = (cell.Bottom + cell.Top) / 2d;
                geometry.Lines.Add(new DoorWindowLineSegment(cell.Left, middleY, cell.Right, middleY, DoorWindowLineRole.Opening));
                geometry.Lines.Add(new DoorWindowLineSegment(cell.Left, middleY, (cell.Left + cell.Right) / 2d, cell.Top, DoorWindowLineRole.Opening));
                geometry.Lines.Add(new DoorWindowLineSegment(cell.Right, middleY, (cell.Left + cell.Right) / 2d, cell.Top, DoorWindowLineRole.Opening));
                geometry.Lines.Add(new DoorWindowLineSegment(cell.Left, middleY, (cell.Left + cell.Right) / 2d, cell.Bottom, DoorWindowLineRole.Opening));
                geometry.Lines.Add(new DoorWindowLineSegment(cell.Right, middleY, (cell.Left + cell.Right) / 2d, cell.Bottom, DoorWindowLineRole.Opening));
            }
            else if (mode == "百叶")
                for (var index = 1; index < 7; index++) { var y = cell.Bottom + (cell.Top - cell.Bottom) * index / 7d; geometry.Lines.Add(new DoorWindowLineSegment(cell.Left, y, cell.Right, y, DoorWindowLineRole.Opening)); }
            else if (mode == "推拉" || mode == "右推拉")
            {
                AddSlidingArrow(geometry.Lines, cell, true);
            }
            else if (mode == "左推拉") AddSlidingArrow(geometry.Lines, cell, false);
            else if (mode == "双向推拉") AddSlidingArrow(geometry.Lines, cell, (cell.Left + cell.Right) / 2d <= (geometry.FrameLeft + geometry.FrameRight) / 2d);
        }

        public static List<double> ParseRatios(string value)
        {
            var result = new List<double>();
            foreach (var token in (value ?? string.Empty).Split(new[] { ',', '，', ';', '；', ':', '：', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            { double number; if (!double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || number <= 0) return new List<double>(); result.Add(number); }
            return result;
        }

        public static List<double> ResolveActualSizes(string actualValue, IList<double> ratios, double total, string label)
        {
            var actual = ParseRatios(actualValue);
            if (!string.IsNullOrWhiteSpace(actualValue) && actual.Count == 0)
                throw new InvalidOperationException((label ?? "尺寸") + "只能填写大于 0 的毫米数值，并用逗号分隔。");
            if (actual.Count > 0)
            {
                if (actual.Count != ratios.Count) throw new InvalidOperationException((label ?? "尺寸") + "数量与分格数量不一致。");
                var difference = Math.Abs(actual.Sum() - total);
                if (difference > Math.Max(0.5d, total * 0.0001d))
                    throw new InvalidOperationException((label ?? "尺寸") + "合计应为 " + total.ToString("0.##") + " mm，当前为 " + actual.Sum().ToString("0.##") + " mm。");
                return actual;
            }
            var ratioTotal = ratios.Sum();
            if (ratioTotal <= 0) throw new InvalidOperationException((label ?? "尺寸") + "比例无效。");
            var result = ratios.Select(x => total * x / ratioTotal).ToList();
            if (result.Count > 0) result[result.Count - 1] += total - result.Sum();
            return result;
        }

        public static List<DoorWindowLayoutCell> ParseCellLayout(string value)
        {
            var result = new List<DoorWindowLayoutCell>();
            foreach (var record in (value ?? string.Empty).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = record.Split(','); if (parts.Length < 6) return new List<DoorWindowLayoutCell>();
                double left, bottom, right, top;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bottom) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out right) || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out top)) return new List<DoorWindowLayoutCell>();
                result.Add(new DoorWindowLayoutCell { Left = left, Bottom = bottom, Right = right, Top = top, Opening = parts[4], IsDoor = parts[5] == "1", IsDeleted = parts.Length > 6 && parts[6] == "1", Material = parts.Length > 7 && !string.IsNullOrWhiteSpace(parts[7]) ? parts[7] : "无" });
            }
            return result;
        }

        public static string SerializeCellLayout(IEnumerable<DoorWindowLayoutCell> cells)
        {
            return string.Join("|", (cells ?? Enumerable.Empty<DoorWindowLayoutCell>()).Select(x =>
                x.Left.ToString("0.###", CultureInfo.InvariantCulture) + "," + x.Bottom.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                x.Right.ToString("0.###", CultureInfo.InvariantCulture) + "," + x.Top.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                (string.IsNullOrWhiteSpace(x.Opening) ? "固定" : x.Opening.Replace(",", string.Empty).Replace("|", string.Empty)) + "," + (x.IsDoor ? "1" : "0") + "," + (x.IsDeleted ? "1" : "0") + "," + (string.IsNullOrWhiteSpace(x.Material) ? "无" : x.Material.Replace(",", string.Empty).Replace("|", string.Empty))));
        }

        public static void ValidateCellLayout(IList<DoorWindowLayoutCell> cells, double width, double height)
        {
            if (cells == null || cells.Count == 0 || cells.All(x => x.IsDeleted)) throw new InvalidOperationException("至少要保留一个门窗面板。");
            const double tolerance = 0.05d;
            var active = cells.Where(x => !x.IsDeleted).ToList();
            foreach (var cell in active)
            {
                if (cell.Left < -tolerance || cell.Bottom < -tolerance || cell.Right > width + tolerance || cell.Top > height + tolerance || cell.Right - cell.Left < 1d || cell.Top - cell.Bottom < 1d)
                    throw new InvalidOperationException("存在超出窗框或尺寸过小的分格。");
            }
            for (var first = 0; first < active.Count; first++) for (var second = first + 1; second < active.Count; second++)
            {
                var overlapWidth = Math.Min(active[first].Right, active[second].Right) - Math.Max(active[first].Left, active[second].Left);
                var overlapHeight = Math.Min(active[first].Top, active[second].Top) - Math.Max(active[first].Bottom, active[second].Bottom);
                if (overlapWidth > tolerance && overlapHeight > tolerance) throw new InvalidOperationException("分格之间存在重叠。");
            }
        }

        private static void MarkDoorCells(DoorWindowScheduleItem item, IList<DoorWindowCell> cells, double left, double right)
        {
            if (item == null || cells == null || cells.Count == 0 || cells.Any(x => x.IsDoor)) return;
            if ((item.ElevationType ?? string.Empty).Contains("门") && !string.Equals(item.ElevationType, "门联窗", StringComparison.Ordinal)) { foreach (var cell in cells) cell.IsDoor = true; return; }
            if (!string.Equals(item.ElevationType, "门联窗", StringComparison.Ordinal)) return;
            var columns = cells.Select(x => new { x.Left, x.Right }).Distinct().OrderBy(x => x.Left).ToList();
            if (columns.Count == 0) return;
            var placement = string.IsNullOrWhiteSpace(item.DoorPlacement) ? "靠左" : item.DoorPlacement;
            var distance = Math.Max(0d, item.DoorEdgeDistance);
            var target = placement == "靠右" ? right - distance : placement == "居中" ? (left + right) / 2d : left + distance;
            var selected = placement == "靠右"
                ? columns.OrderBy(x => Math.Abs(x.Right - target)).First()
                : placement == "居中" ? columns.OrderBy(x => Math.Abs((x.Left + x.Right) / 2d - target)).First()
                : columns.OrderBy(x => Math.Abs(x.Left - target)).First();
            foreach (var cell in cells) if (Math.Abs(cell.Left - selected.Left) < 0.01d && Math.Abs(cell.Right - selected.Right) < 0.01d) cell.IsDoor = true;
        }

        private static void AddSideHung(ICollection<DoorWindowLineSegment> lines, DoorWindowCell cell, bool hingeLeft)
        {
            var hingeX = hingeLeft ? cell.Left : cell.Right; var freeX = hingeLeft ? cell.Right : cell.Left; var middleY = (cell.Bottom + cell.Top) / 2d;
            lines.Add(new DoorWindowLineSegment(hingeX, cell.Bottom, freeX, middleY, DoorWindowLineRole.Opening));
            lines.Add(new DoorWindowLineSegment(freeX, middleY, hingeX, cell.Top, DoorWindowLineRole.Opening));
        }

        private static void AddHung(ICollection<DoorWindowLineSegment> lines, DoorWindowCell cell, bool hingeTop)
        {
            var hingeY = hingeTop ? cell.Top : cell.Bottom; var freeY = hingeTop ? cell.Bottom : cell.Top; var middleX = (cell.Left + cell.Right) / 2d;
            lines.Add(new DoorWindowLineSegment(cell.Left, hingeY, middleX, freeY, DoorWindowLineRole.Opening));
            lines.Add(new DoorWindowLineSegment(middleX, freeY, cell.Right, hingeY, DoorWindowLineRole.Opening));
        }

        private static void AddSlidingArrow(ICollection<DoorWindowLineSegment> lines, DoorWindowCell cell, bool pointsRight, double verticalOffsetFactor = 0d)
        {
            var width = cell.Right - cell.Left; var height = cell.Top - cell.Bottom; var y = (cell.Bottom + cell.Top) / 2d + height * verticalOffsetFactor;
            var start = pointsRight ? cell.Left + width * .18d : cell.Right - width * .18d; var end = pointsRight ? cell.Right - width * .18d : cell.Left + width * .18d;
            var head = Math.Min(width, height) * .10d; var sign = pointsRight ? -1d : 1d;
            lines.Add(new DoorWindowLineSegment(start, y, end, y, DoorWindowLineRole.Opening));
            lines.Add(new DoorWindowLineSegment(end + sign * head, y + head * .45d, end, y, DoorWindowLineRole.Opening));
            lines.Add(new DoorWindowLineSegment(end + sign * head, y - head * .45d, end, y, DoorWindowLineRole.Opening));
        }

        private static void AddRectangle(ICollection<DoorWindowLineSegment> lines, double left, double bottom, double right, double top, DoorWindowLineRole role)
        {
            lines.Add(new DoorWindowLineSegment(left, bottom, right, bottom, role));
            lines.Add(new DoorWindowLineSegment(right, bottom, right, top, role));
            lines.Add(new DoorWindowLineSegment(right, top, left, top, role));
            lines.Add(new DoorWindowLineSegment(left, top, left, bottom, role));
        }
    }
}
