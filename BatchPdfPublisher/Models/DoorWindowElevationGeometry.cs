using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BatchPdfPublisher.Models
{
    public enum DoorWindowLineRole { Hole, Frame, Mullion, Opening }

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
    }

    public sealed class DoorWindowElevationGeometry
    {
        public readonly List<DoorWindowLineSegment> Lines = new List<DoorWindowLineSegment>();
        public readonly List<DoorWindowCell> Cells = new List<DoorWindowCell>();
        public double HoleWidth, HoleHeight, FrameLeft, FrameBottom, FrameRight, FrameTop;
    }

    public static class DoorWindowElevationGeometryBuilder
    {
        public static DoorWindowElevationGeometry Build(DoorWindowScheduleItem item)
        {
            if (item == null) throw new ArgumentNullException("item");
            if (item.Width <= 0 || item.Height <= 0) throw new InvalidOperationException("门窗洞口尺寸无效。");
            var gap = Math.Max(0d, item.InstallationGap);
            var left = gap; var bottom = gap; var right = item.Width - gap; var top = item.Height - gap;
            if (right <= left || top <= bottom) throw new InvalidOperationException("安装缝大于门窗洞口尺寸。");

            var result = new DoorWindowElevationGeometry
            {
                HoleWidth = item.Width, HoleHeight = item.Height,
                FrameLeft = left, FrameBottom = bottom, FrameRight = right, FrameTop = top
            };
            AddRectangle(result.Lines, 0, 0, item.Width, item.Height, DoorWindowLineRole.Hole);
            AddRectangle(result.Lines, left, bottom, right, top, DoorWindowLineRole.Frame);

            var cells = CreateCells(item, left, bottom, right, top);
            result.Cells.AddRange(cells);
            AddDividerLines(result, cells, left, bottom, right, top);
            AddOpeningSymbols(result, item);
            return result;
        }

        private static List<DoorWindowCell> CreateCells(DoorWindowScheduleItem item, double left, double bottom, double right, double top)
        {
            var cells = new List<DoorWindowCell>();
            var width = right - left; var height = top - bottom;
            var preset = (item.DivisionPreset ?? string.Empty).Trim();
            if (preset == "自定义")
            {
                var columns = ParseRatios(item.CustomColumnRatios); var rows = ParseRatios(item.CustomRowRatios);
                if (columns.Count == 0 || rows.Count == 0) throw new InvalidOperationException("自定义分格比例无效。");
                var columnTotal = columns.Sum(); var rowTotal = rows.Sum(); var y = bottom;
                foreach (var rowRatio in rows)
                {
                    var nextY = y + height * rowRatio / rowTotal; var x = left;
                    foreach (var columnRatio in columns)
                    {
                        var nextX = x + width * columnRatio / columnTotal; cells.Add(new DoorWindowCell(x, y, nextX, nextY)); x = nextX;
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
            return cells;
        }

        private static void AddDividerLines(DoorWindowElevationGeometry geometry, IList<DoorWindowCell> cells, double left, double bottom, double right, double top)
        {
            var vertical = new HashSet<string>(); var horizontal = new HashSet<string>();
            foreach (var cell in cells)
            {
                if (cell.Left > left + .001 && cell.Left < right - .001 && vertical.Add(cell.Left.ToString("0.###")))
                    geometry.Lines.Add(new DoorWindowLineSegment(cell.Left, bottom, cell.Left, top, DoorWindowLineRole.Mullion));
                if (cell.Right > left + .001 && cell.Right < right - .001 && vertical.Add(cell.Right.ToString("0.###")))
                    geometry.Lines.Add(new DoorWindowLineSegment(cell.Right, bottom, cell.Right, top, DoorWindowLineRole.Mullion));
                if (cell.Bottom > bottom + .001 && cell.Bottom < top - .001 && horizontal.Add(cell.Bottom.ToString("0.###")))
                    geometry.Lines.Add(new DoorWindowLineSegment(left, cell.Bottom, right, cell.Bottom, DoorWindowLineRole.Mullion));
                if (cell.Top > bottom + .001 && cell.Top < top - .001 && horizontal.Add(cell.Top.ToString("0.###")))
                    geometry.Lines.Add(new DoorWindowLineSegment(left, cell.Top, right, cell.Top, DoorWindowLineRole.Mullion));
            }
        }

        private static void AddOpeningSymbols(DoorWindowElevationGeometry geometry, DoorWindowScheduleItem item)
        {
            var modes = (item.CellOpeningModes ?? string.Empty).Split(new[] { '|' }, StringSplitOptions.None);
            if (item.DivisionPreset == "自定义" && modes.Length == geometry.Cells.Count)
            {
                for (var index = 0; index < geometry.Cells.Count; index++) ApplyOpeningMode(geometry, geometry.Cells[index], modes[index]);
                return;
            }
            var mode = (item.OpeningMode ?? string.Empty).Trim();
            if (mode == "" || mode == "未设置" || mode == "固定") return;
            if (mode == "百叶")
            {
                foreach (var cell in geometry.Cells)
                    for (var index = 1; index < 7; index++)
                    {
                        var y = cell.Bottom + (cell.Top - cell.Bottom) * index / 7d;
                        geometry.Lines.Add(new DoorWindowLineSegment(cell.Left, y, cell.Right, y, DoorWindowLineRole.Opening));
                    }
                return;
            }
            if (mode == "双扇平开")
            {
                // Adjacent leaves hinge at the shared mullion.  The previous
                // test used the outside jamb and mirrored every paired symbol.
                foreach (var cell in geometry.Cells) AddSideHung(geometry.Lines, cell, cell.Left >= (geometry.FrameLeft + geometry.FrameRight) / 2d);
                return;
            }
            foreach (var cell in geometry.Cells)
                ApplyOpeningMode(geometry, cell, mode);
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
            else if (mode == "推拉")
            {
                geometry.Lines.Add(new DoorWindowLineSegment(cell.Left, (cell.Bottom + cell.Top) / 2d, cell.Right, (cell.Bottom + cell.Top) / 2d, DoorWindowLineRole.Opening));
                var direction = (cell.Right - cell.Left) * .18d;
                geometry.Lines.Add(new DoorWindowLineSegment(cell.Right - direction, (cell.Bottom + cell.Top) / 2d + direction * .35d, cell.Right, (cell.Bottom + cell.Top) / 2d, DoorWindowLineRole.Opening));
                geometry.Lines.Add(new DoorWindowLineSegment(cell.Right - direction, (cell.Bottom + cell.Top) / 2d - direction * .35d, cell.Right, (cell.Bottom + cell.Top) / 2d, DoorWindowLineRole.Opening));
            }
        }

        public static List<double> ParseRatios(string value)
        {
            var result = new List<double>();
            foreach (var token in (value ?? string.Empty).Split(new[] { ',', '，', ';', '；', ':', '：', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            { double number; if (!double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || number <= 0) return new List<double>(); result.Add(number); }
            return result;
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

        private static void AddRectangle(ICollection<DoorWindowLineSegment> lines, double left, double bottom, double right, double top, DoorWindowLineRole role)
        {
            lines.Add(new DoorWindowLineSegment(left, bottom, right, bottom, role));
            lines.Add(new DoorWindowLineSegment(right, bottom, right, top, role));
            lines.Add(new DoorWindowLineSegment(right, top, left, top, role));
            lines.Add(new DoorWindowLineSegment(left, top, left, bottom, role));
        }
    }
}
