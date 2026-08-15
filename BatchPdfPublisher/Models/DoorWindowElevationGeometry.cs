using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BatchPdfPublisher.Models
{
    public enum DoorWindowLineRole { Hole, Frame, Mullion, Opening, Material }

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
            if (item.Width <= 0 || item.Height <= 0) throw new InvalidOperationException("门窗洞口尺寸无效。");
            var gap = item.HasInstallationGap ? Math.Max(0d, item.InstallationGap) : 0d;
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
            AddMaterialSymbols(result, cells, item);
            AddOpeningSymbols(result, item);
            return result;
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
                        cells.Add(new DoorWindowCell(left + cell.Left, bottom + cell.Bottom, left + cell.Right, bottom + cell.Top) { Opening = cell.Opening, Material = string.IsNullOrWhiteSpace(cell.Material) ? "无" : cell.Material, IsDoor = cell.IsDoor, IsDeleted = false });
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
            foreach (var cell in cells) if (string.IsNullOrWhiteSpace(cell.Material)) cell.Material = "无";
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
                Add(cell.Left, cell.Bottom, cell.Left, cell.Top, OmitSharedCenter(index, true, cell.Left, cell.Bottom, cell.Top), false);
                Add(cell.Right, cell.Bottom, cell.Right, cell.Top, OmitSharedCenter(index, true, cell.Right, cell.Bottom, cell.Top), false);
                Add(cell.Left, cell.Bottom, cell.Right, cell.Bottom, OmitSharedCenter(index, false, cell.Bottom, cell.Left, cell.Right), false);
                Add(cell.Left, cell.Top, cell.Right, cell.Top, OmitSharedCenter(index, false, cell.Top, cell.Left, cell.Right), false);
            }

            bool OmitSharedCenter(int owner, bool vertical, double coordinate, double start, double end)
            {
                var found = false; var ownerOperable = IsOperable(cells[owner].Opening);
                for (var otherIndex = 0; otherIndex < cells.Count; otherIndex++)
                {
                    if (otherIndex == owner) continue; var other = cells[otherIndex];
                    var adjacent = vertical
                        ? (Math.Abs(other.Left - coordinate) < tolerance || Math.Abs(other.Right - coordinate) < tolerance) && Math.Min(end, other.Top) - Math.Max(start, other.Bottom) > tolerance
                        : (Math.Abs(other.Bottom - coordinate) < tolerance || Math.Abs(other.Top - coordinate) < tolerance) && Math.Min(end, other.Right) - Math.Max(start, other.Left) > tolerance;
                    if (!adjacent) continue; found = true;
                    if (ownerOperable || IsOperable(other.Opening)) return false;
                }
                return found; // 只有相邻两侧均为固定格时，省略中心线。
            }
            void Add(double x1, double y1, double x2, double y2, bool shared, bool omit)
            {
                if (shared || omit) return; // 固定格之间省略中心线；任一侧可开启时保留中心线。
                var forward = x1.ToString("0.###") + ":" + y1.ToString("0.###") + ":" + x2.ToString("0.###") + ":" + y2.ToString("0.###");
                var reverse = x2.ToString("0.###") + ":" + y2.ToString("0.###") + ":" + x1.ToString("0.###") + ":" + y1.ToString("0.###");
                if (keys.Contains(forward) || keys.Contains(reverse)) return; keys.Add(forward);
                geometry.Lines.Add(new DoorWindowLineSegment(x1, y1, x2, y2, DoorWindowLineRole.Frame));
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
                var leftInset = leftNeighbor == null ? outerWidth : SharedInset(cell, leftNeighbor, mullionWidth);
                var rightInset = rightNeighbor == null ? outerWidth : SharedInset(cell, rightNeighbor, mullionWidth);
                var bottomInset = bottomNeighbor == null ? outerWidth : SharedInset(cell, bottomNeighbor, mullionWidth);
                var topInset = topNeighbor == null ? outerWidth : SharedInset(cell, topNeighbor, mullionWidth);
                leftInset = Math.Min(Math.Max(0d, leftInset), width * .45d); rightInset = Math.Min(Math.Max(0d, rightInset), width * .45d);
                bottomInset = Math.Min(Math.Max(0d, bottomInset), height * .45d); topInset = Math.Min(Math.Max(0d, topInset), height * .45d);
                var nDoorBottom = cell.IsDoor && bottomNeighbor == null && string.Equals(item.DoorFrameType, "N型", StringComparison.Ordinal);
                var l = cell.Left + leftInset; var r = cell.Right - rightInset; var b = nDoorBottom ? cell.Bottom : cell.Bottom + bottomInset; var t = cell.Top - topInset;
                if (bottomNeighbor != null && mullionWidth > 0d || bottomNeighbor == null && outerWidth > 0d)
                    if (!nDoorBottom) Add(l, b, r, b);
                if (rightNeighbor != null && mullionWidth > 0d || rightNeighbor == null && outerWidth > 0d) Add(r, b, r, t);
                if (topNeighbor != null && mullionWidth > 0d || topNeighbor == null && outerWidth > 0d) Add(r, t, l, t);
                if (leftNeighbor != null && mullionWidth > 0d || leftNeighbor == null && outerWidth > 0d) Add(l, t, l, b);
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
            double SharedInset(DoorWindowCell first, DoorWindowCell second, double normalWidth) { return IsOperable(first.Opening) || IsOperable(second.Opening) ? normalWidth : normalWidth / 2d; }
            void Add(double x1, double y1, double x2, double y2)
            {
                var key = x1.ToString("0.###") + ":" + y1.ToString("0.###") + ":" + x2.ToString("0.###") + ":" + y2.ToString("0.###");
                if (keys.Add(key)) geometry.Lines.Add(new DoorWindowLineSegment(x1, y1, x2, y2, DoorWindowLineRole.Mullion));
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
                var l = cell.Left + (leftNeighbor == null ? outerWidth : Shared(cell, leftNeighbor)) + extra;
                var r = cell.Right - (rightNeighbor == null ? outerWidth : Shared(cell, rightNeighbor)) - extra;
                var b = cell.Bottom + (bottomNeighbor == null ? outerWidth : Shared(cell, bottomNeighbor)) + extra;
                var t = cell.Top - (topNeighbor == null ? outerWidth : Shared(cell, topNeighbor)) - extra;
                if (r <= l || t <= b) continue;
                if (material == "玻璃")
                {
                    AddRectangle(geometry.Lines, l, b, r, t, DoorWindowLineRole.Material);
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
            double Shared(DoorWindowCell first, DoorWindowCell second) { return IsOperable(first.Opening) || IsOperable(second.Opening) ? mullionWidth : mullionWidth / 2d; }
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
            double Shared(DoorWindowCell other) { return IsOperable(cell.Opening) || IsOperable(other.Opening) ? mullion : mullion / 2d; }
            var leftNeighbor = Neighbor(true, cell.Left); var rightNeighbor = Neighbor(true, cell.Right);
            var bottomNeighbor = Neighbor(false, cell.Bottom); var topNeighbor = Neighbor(false, cell.Top);
            var leftInset = leftNeighbor == null ? outer : Shared(leftNeighbor);
            var rightInset = rightNeighbor == null ? outer : Shared(rightNeighbor);
            var bottomInset = bottomNeighbor == null ? outer : Shared(bottomNeighbor);
            var topInset = topNeighbor == null ? outer : Shared(topNeighbor);
            if (cell.IsDoor && bottomNeighbor == null && string.Equals(item.DoorFrameType, "N型", StringComparison.Ordinal)) bottomInset = 0d;
            var clearance = Math.Min(6d, Math.Min(cell.Right - cell.Left, cell.Top - cell.Bottom) * .01d);
            var left = cell.Left + leftInset + clearance; var right = cell.Right - rightInset - clearance;
            var bottom = cell.Bottom + bottomInset + clearance; var top = cell.Top - topInset - clearance;
            if (right <= left || top <= bottom) return cell;
            return new DoorWindowCell(left, bottom, right, top) { Opening = cell.Opening, Material = cell.Material, IsDoor = cell.IsDoor };
        }

        private static void ApplyOpeningMode(DoorWindowElevationGeometry geometry, DoorWindowCell cell, string value)
        {
            var mode = (value ?? string.Empty).Trim();
            if (mode == "" || mode == "未设置" || mode == "固定") return;
            if (mode == "左平开") AddSideHung(geometry.Lines, cell, true);
            else if (mode == "右平开") AddSideHung(geometry.Lines, cell, false);
            else if (mode == "上悬") AddHung(geometry.Lines, cell, true);
            else if (mode == "下悬") AddHung(geometry.Lines, cell, false);
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
            if (string.Equals(item.ElevationType, "门", StringComparison.Ordinal) || string.Equals(item.ElevationType, "防火门", StringComparison.Ordinal)) { foreach (var cell in cells) cell.IsDoor = true; return; }
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
