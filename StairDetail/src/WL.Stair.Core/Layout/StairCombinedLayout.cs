using System;
using System.Collections.Generic;
using System.Linq;
using WL.Stair.Core.Domain;

namespace WL.Stair.Core.Layout
{
    /// <summary>
    /// Portable description of one stair-detail view.  The CAD layer keeps
    /// ownership of Tianzheng objects; this class only carries the occupied
    /// model-space rectangle used by preview and later frame insertion.
    /// </summary>
    public sealed class StairLayoutItem
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsSection { get; set; }
        public IList<StairLayoutPreviewLine> PreviewLines { get; set; }
    }

    public sealed class StairLayoutPreviewLine
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public string Color { get; set; }
        public bool Dashed { get; set; }
    }

    public sealed class StairLayoutOptions
    {
        public double PageWidth { get; set; }
        public double PageHeight { get; set; }
        public double LeftMargin { get; set; }
        public double RightMargin { get; set; }
        public double TopMargin { get; set; }
        public double BottomMargin { get; set; }
        public double ItemGap { get; set; }
        public int GridColumns { get; set; }
        public int GridRows { get; set; }
        public IList<double> ColumnRatios { get; set; }
        public IList<double> RowRatios { get; set; }
    }

    public sealed class StairLayoutSlot
    {
        public StairLayoutItem Item { get; set; }
        public int Page { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int ColumnSpan { get; set; }
        public int RowSpan { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double CellX { get; set; }
        public double CellY { get; set; }
        public double CellWidth { get; set; }
        public double CellHeight { get; set; }
    }

    public sealed class StairLayoutPlan
    {
        public int PageCount { get; set; }
        public int Columns { get; set; }
        public int Rows { get; set; }
        public double PageWidth { get; set; }
        public double PageHeight { get; set; }
        public double ContentLeft { get; set; }
        public double ContentRight { get; set; }
        public double ContentBottom { get; set; }
        public double ContentTop { get; set; }
        public IList<double> ColumnWidths { get; } = new List<double>();
        public IList<double> RowHeights { get; } = new List<double>();
        public IList<StairLayoutSlot> Slots { get; } = new List<StairLayoutSlot>();
    }

    /// <summary>
    /// Fixed grids waste most of a sheet when a tall section is mixed with
    /// several short floor plans. This packer uses free rectangles instead:
    /// it first minimises page count, then minimises the occupied envelope.
    /// </summary>
    public static class StairCombinedLayout
    {
        public static StairLayoutPlan Compute(
            IEnumerable<StairLayoutItem> source,
            StairLayoutOptions options)
        {
            var items = (source ?? Enumerable.Empty<StairLayoutItem>())
                .Where(item => item != null && item.Width > 0.01 && item.Height > 0.01)
                .ToList();
            if (items.Count == 0)
                throw new InvalidOperationException("没有可排版的楼梯平面或剖面。");
            if (options == null || options.PageWidth <= 0.01 || options.PageHeight <= 0.01)
                throw new InvalidOperationException("排版纸张尺寸无效。");

            var plan = new StairLayoutPlan
            {
                PageWidth = options.PageWidth,
                PageHeight = options.PageHeight,
                ContentLeft = Math.Max(0.0, options.LeftMargin),
                ContentRight = options.PageWidth - Math.Max(0.0, options.RightMargin),
                ContentBottom = Math.Max(0.0, options.BottomMargin),
                ContentTop = options.PageHeight - Math.Max(0.0, options.TopMargin)
            };
            if (plan.ContentRight <= plan.ContentLeft || plan.ContentTop <= plan.ContentBottom)
                throw new InvalidOperationException("排版范围无效，请检查四周边距。");

            var contentWidth = plan.ContentRight - plan.ContentLeft;
            var contentHeight = plan.ContentTop - plan.ContentBottom;
            var packed = FindBestPacking(items, contentWidth, contentHeight,
                Math.Max(0.0, options.ItemGap));
            if (packed == null)
                throw new InvalidOperationException("楼梯平面或剖面无法放入当前纸张，请使用更大图框或更小出图比例。");
            plan.Columns = packed.Columns;
            plan.Rows = packed.Rows;
            var columnWidths = ResolveSizes(contentWidth, packed.Columns,
                options.GridColumns, options.ColumnRatios);
            var rowHeights = ResolveSizes(contentHeight, packed.Rows,
                options.GridRows, options.RowRatios);
            if (packed.IsGrid && !GridFits(packed, columnWidths, rowHeights))
            {
                columnWidths = ResolveSizes(contentWidth, packed.Columns, 0, null);
                rowHeights = ResolveSizes(contentHeight, packed.Rows, 0, null);
            }
            foreach (var value in columnWidths) plan.ColumnWidths.Add(value);
            foreach (var value in rowHeights) plan.RowHeights.Add(value);
            plan.PageCount = packed.Pages.Count;
            foreach (var page in packed.Pages)
            {
                if (packed.IsGrid)
                {
                    foreach (var value in page.Placements)
                    {
                        var cellX = Sum(columnWidths, 0, value.Column);
                        var cellWidth = Sum(columnWidths, value.Column, value.ColumnSpan);
                        var top = Sum(rowHeights, 0, value.Row);
                        var cellHeight = Sum(rowHeights, value.Row, value.RowSpan);
                        var cellY = contentHeight - top - cellHeight;
                        plan.Slots.Add(new StairLayoutSlot
                        {
                            Item = value.Item,
                            Page = page.Index,
                            Row = value.Row,
                            Column = value.Column,
                            RowSpan = value.RowSpan,
                            ColumnSpan = value.ColumnSpan,
                            X = plan.ContentLeft + cellX + (cellWidth - value.Item.Width) / 2.0,
                            Y = plan.ContentBottom + cellY + (cellHeight - value.Item.Height) / 2.0,
                            Width = value.Item.Width,
                            Height = value.Item.Height,
                            CellX = plan.ContentLeft + cellX,
                            CellY = plan.ContentBottom + cellY,
                            CellWidth = cellWidth,
                            CellHeight = cellHeight
                        });
                    }
                    continue;
                }
                var minX = page.Placements.Min(value => value.X);
                var minY = page.Placements.Min(value => value.Y);
                var maxX = page.Placements.Max(value => value.X + value.Width);
                var maxY = page.Placements.Max(value => value.Y + value.Height);
                var shiftX = (contentWidth - (maxX - minX)) / 2.0 - minX;
                var shiftY = (contentHeight - (maxY - minY)) / 2.0 - minY;
                foreach (var value in page.Placements)
                {
                    plan.Slots.Add(new StairLayoutSlot
                    {
                        Item = value.Item,
                        Page = page.Index,
                        Row = 0,
                        Column = 0,
                        RowSpan = 1,
                        ColumnSpan = 1,
                        X = plan.ContentLeft + value.X + shiftX + value.ContentOffsetX,
                        Y = plan.ContentBottom + value.Y + shiftY + value.ContentOffsetY,
                        Width = value.Item.Width,
                        Height = value.Item.Height,
                        CellX = plan.ContentLeft + value.X + shiftX,
                        CellY = plan.ContentBottom + value.Y + shiftY,
                        CellWidth = value.Width,
                        CellHeight = value.Height
                    });
                }
            }
            var order = items.Select((item, index) => new { item, index })
                .ToDictionary(value => value.item, value => value.index);
            var sorted = plan.Slots.OrderBy(value => order[value.Item]).ToList();
            plan.Slots.Clear();
            foreach (var slot in sorted) plan.Slots.Add(slot);
            return plan;
        }

        public static void ApplyPlacements(StairLayoutPlan plan,
            IEnumerable<StairLayoutPlacementDefinition> source)
        {
            if (plan == null || plan.Columns <= 0 || plan.Rows <= 0
                || plan.ColumnWidths.Count != plan.Columns
                || plan.RowHeights.Count != plan.Rows) return;
            foreach (var placement in (source
                ?? Enumerable.Empty<StairLayoutPlacementDefinition>())
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.Key)))
            {
                var slot = plan.Slots.FirstOrDefault(value => value.Item != null
                    && string.Equals(value.Item.Key, placement.Key,
                        StringComparison.OrdinalIgnoreCase));
                if (slot == null || placement.Page < 0 || placement.Page >= plan.PageCount
                    || placement.Column < 0 || placement.Row < 0
                    || placement.Column + slot.ColumnSpan > plan.Columns
                    || placement.Row + slot.RowSpan > plan.Rows) continue;
                var overlaps = plan.Slots.Where(value => !ReferenceEquals(value, slot)
                    && value.Page == placement.Page
                    && RectanglesOverlap(value.Column, value.Row,
                        value.ColumnSpan, value.RowSpan,
                        placement.Column, placement.Row,
                        slot.ColumnSpan, slot.RowSpan)).ToList();
                if (overlaps.Count == 0)
                {
                    PlaceSlot(plan, slot, placement.Page,
                        placement.Row, placement.Column);
                    continue;
                }
                // Occupied targets retain the familiar swap behaviour when
                // both items own the same cell shape. More complex overlaps
                // are left unchanged to prevent drawings from colliding.
                if (overlaps.Count != 1) continue;
                var other = overlaps[0];
                if (other.ColumnSpan != slot.ColumnSpan
                    || other.RowSpan != slot.RowSpan
                    || other.Column != placement.Column
                    || other.Row != placement.Row) continue;
                var oldPage = slot.Page;
                var oldRow = slot.Row;
                var oldColumn = slot.Column;
                PlaceSlot(plan, slot, placement.Page,
                    placement.Row, placement.Column);
                PlaceSlot(plan, other, oldPage, oldRow, oldColumn);
            }
        }

        private static bool RectanglesOverlap(int firstColumn, int firstRow,
            int firstColumnSpan, int firstRowSpan, int secondColumn, int secondRow,
            int secondColumnSpan, int secondRowSpan)
        {
            return firstColumn < secondColumn + secondColumnSpan
                && secondColumn < firstColumn + firstColumnSpan
                && firstRow < secondRow + secondRowSpan
                && secondRow < firstRow + firstRowSpan;
        }

        private static void PlaceSlot(StairLayoutPlan plan, StairLayoutSlot slot,
            int page, int row, int column)
        {
            var contentHeight = plan.ContentTop - plan.ContentBottom;
            var cellX = Sum(plan.ColumnWidths, 0, column);
            var cellWidth = Sum(plan.ColumnWidths, column, slot.ColumnSpan);
            var top = Sum(plan.RowHeights, 0, row);
            var cellHeight = Sum(plan.RowHeights, row, slot.RowSpan);
            var cellY = contentHeight - top - cellHeight;
            slot.Page = page;
            slot.Row = row;
            slot.Column = column;
            slot.CellX = plan.ContentLeft + cellX;
            slot.CellY = plan.ContentBottom + cellY;
            slot.CellWidth = cellWidth;
            slot.CellHeight = cellHeight;
            slot.X = slot.CellX + (cellWidth - slot.Width) / 2.0;
            slot.Y = slot.CellY + (cellHeight - slot.Height) / 2.0;
        }

        private static IList<double> ResolveSizes(double total, int count,
            int requestedCount, IList<double> ratios)
        {
            var result = new List<double>();
            if (count <= 0) return result;
            if (requestedCount == count && ratios != null && ratios.Count == count
                && ratios.All(value => value > 0.000001))
            {
                var sum = ratios.Sum();
                foreach (var ratio in ratios) result.Add(total * ratio / sum);
                return result;
            }
            for (var index = 0; index < count; index++) result.Add(total / count);
            return result;
        }

        private static double Sum(IList<double> values, int start, int count)
        {
            var result = 0.0;
            for (var index = start; index < start + count && index < values.Count; index++)
                result += values[index];
            return result;
        }

        private static bool GridFits(PackingCandidate packed,
            IList<double> columnWidths, IList<double> rowHeights)
        {
            foreach (var page in packed.Pages)
                foreach (var value in page.Placements)
                    if (Sum(columnWidths, value.Column, value.ColumnSpan) + 0.01 < value.Item.Width
                        || Sum(rowHeights, value.Row, value.RowSpan) + 0.01 < value.Item.Height)
                        return false;
            return true;
        }

        private static PackingCandidate FindBestPacking(
            IList<StairLayoutItem> items,
            double contentWidth,
            double contentHeight,
            double gap)
        {
            foreach (var item in items)
                if (item.Width + gap > contentWidth + 0.01
                    || item.Height + gap > contentHeight + 0.01)
                    return null;

            var orders = new List<IList<StairLayoutItem>>
            {
                items.ToList(),
                items.OrderByDescending(value => value.Width * value.Height).ToList(),
                items.OrderByDescending(value => value.Height).ThenByDescending(value => value.Width).ToList(),
                items.OrderByDescending(value => value.Width).ThenByDescending(value => value.Height).ToList(),
                items.OrderByDescending(value => value.IsSection).ThenByDescending(value => value.Height).ToList(),
                items.OrderBy(value => value.IsSection).ThenByDescending(value => value.Width * value.Height).ToList()
            };
            PackingCandidate best = null;
            foreach (var order in orders)
            {
                var candidates = new[]
                {
                    PackGrid(order, contentWidth, contentHeight, gap),
                    Pack(order, contentWidth, contentHeight, gap)
                };
                foreach (var candidate in candidates.Where(value => value != null))
                    if (best == null || candidate.Pages.Count < best.Pages.Count
                        || (candidate.Pages.Count == best.Pages.Count
                            && candidate.IsGrid && !best.IsGrid)
                        || (candidate.Pages.Count == best.Pages.Count
                            && candidate.IsGrid == best.IsGrid
                            && candidate.OccupiedArea < best.OccupiedArea))
                        best = candidate;
            }
            return best;
        }

        private static PackingCandidate PackGrid(IList<StairLayoutItem> items,
            double width, double height, double gap)
        {
            var ordinary = items.Where(value => !value.IsSection).ToList();
            if (ordinary.Count == 0) return null;
            var sortedWidths = ordinary.Select(value => value.Width + gap).OrderBy(value => value).ToList();
            var sortedHeights = ordinary.Select(value => value.Height + gap).OrderBy(value => value).ToList();
            var baseWidth = sortedWidths[sortedWidths.Count / 2];
            var baseHeight = sortedHeights[sortedHeights.Count / 2];
            var columns = Math.Max(1, (int)Math.Floor(width / Math.Max(1.0, baseWidth)));
            var rows = Math.Max(1, (int)Math.Floor(height / Math.Max(1.0, baseHeight)));
            if (columns * rows < 2) return null;
            var cellWidth = width / columns;
            var cellHeight = height / rows;
            var result = new PackingCandidate
            {
                IsGrid = true,
                Columns = columns,
                Rows = rows
            };
            foreach (var item in items)
            {
                var spanColumns = Math.Max(1, (int)Math.Ceiling((item.Width + gap) / cellWidth));
                var spanRows = Math.Max(1, (int)Math.Ceiling((item.Height + gap) / cellHeight));
                if (spanColumns > columns || spanRows > rows) return null;
                PackingPage targetPage = null;
                int targetColumn = 0, targetRow = 0;
                foreach (var page in result.Pages)
                {
                    if (TryFindGridSpace(page.Grid, columns, rows, spanColumns,
                        spanRows, out targetColumn, out targetRow))
                    { targetPage = page; break; }
                }
                if (targetPage == null)
                {
                    targetPage = new PackingPage
                    {
                        Index = result.Pages.Count,
                        Grid = new bool[columns, rows]
                    };
                    result.Pages.Add(targetPage);
                    if (!TryFindGridSpace(targetPage.Grid, columns, rows,
                        spanColumns, spanRows, out targetColumn, out targetRow)) return null;
                }
                for (var column = targetColumn; column < targetColumn + spanColumns; column++)
                    for (var row = targetRow; row < targetRow + spanRows; row++)
                        targetPage.Grid[column, row] = true;
                var mergedWidth = spanColumns * cellWidth;
                var mergedHeight = spanRows * cellHeight;
                targetPage.Placements.Add(new PackedPlacement
                {
                    Item = item,
                    X = targetColumn * cellWidth,
                    Y = height - (targetRow + spanRows) * cellHeight,
                    Width = mergedWidth,
                    Height = mergedHeight,
                    ContentOffsetX = (mergedWidth - item.Width) / 2.0,
                    ContentOffsetY = (mergedHeight - item.Height) / 2.0,
                    Column = targetColumn,
                    Row = targetRow,
                    ColumnSpan = spanColumns,
                    RowSpan = spanRows
                });
            }
            result.OccupiedArea = result.Pages.Count * width * height;
            return result;
        }

        private static bool TryFindGridSpace(bool[,] grid, int columns, int rows,
            int spanColumns, int spanRows, out int resultColumn, out int resultRow)
        {
            for (var row = 0; row <= rows - spanRows; row++)
                for (var column = 0; column <= columns - spanColumns; column++)
                {
                    var free = true;
                    for (var x = column; x < column + spanColumns && free; x++)
                        for (var y = row; y < row + spanRows; y++)
                            if (grid[x, y]) { free = false; break; }
                    if (!free) continue;
                    resultColumn = column;
                    resultRow = row;
                    return true;
                }
            resultColumn = 0;
            resultRow = 0;
            return false;
        }

        private static PackingCandidate Pack(IList<StairLayoutItem> items,
            double width, double height, double gap)
        {
            var result = new PackingCandidate();
            foreach (var item in items)
            {
                PlacementChoice best = null;
                foreach (var page in result.Pages)
                    foreach (var free in page.Free)
                    {
                        var packedWidth = item.Width + gap;
                        var packedHeight = item.Height + gap;
                        if (packedWidth > free.Width + 0.01 || packedHeight > free.Height + 0.01)
                            continue;
                        var choice = new PlacementChoice
                        {
                            Page = page,
                            Free = free,
                            AreaWaste = free.Width * free.Height - packedWidth * packedHeight,
                            ShortWaste = Math.Min(free.Width - packedWidth, free.Height - packedHeight)
                        };
                        if (best == null || choice.AreaWaste < best.AreaWaste
                            || (Math.Abs(choice.AreaWaste - best.AreaWaste) < 0.01
                                && choice.ShortWaste < best.ShortWaste)) best = choice;
                    }
                if (best == null)
                {
                    var page = new PackingPage { Index = result.Pages.Count };
                    page.Free.Add(new PackedRect { X = 0, Y = 0, Width = width, Height = height });
                    result.Pages.Add(page);
                    best = new PlacementChoice { Page = page, Free = page.Free[0] };
                }
                var placed = new PackedPlacement
                {
                    Item = item,
                    X = best.Free.X,
                    Y = best.Free.Y,
                    Width = item.Width + gap,
                    Height = item.Height + gap,
                    ContentOffsetX = gap / 2.0,
                    ContentOffsetY = gap / 2.0
                };
                best.Page.Placements.Add(placed);
                SplitFreeRectangles(best.Page, placed);
            }
            result.OccupiedArea = result.Pages.Sum(page =>
            {
                var minX = page.Placements.Min(value => value.X);
                var minY = page.Placements.Min(value => value.Y);
                var maxX = page.Placements.Max(value => value.X + value.Width);
                var maxY = page.Placements.Max(value => value.Y + value.Height);
                return (maxX - minX) * (maxY - minY);
            });
            return result;
        }

        private static void SplitFreeRectangles(PackingPage page, PackedPlacement used)
        {
            var next = new List<PackedRect>();
            foreach (var free in page.Free)
            {
                if (used.X >= free.X + free.Width || used.X + used.Width <= free.X
                    || used.Y >= free.Y + free.Height || used.Y + used.Height <= free.Y)
                { next.Add(free); continue; }
                if (used.X > free.X) next.Add(new PackedRect
                    { X = free.X, Y = free.Y, Width = used.X - free.X, Height = free.Height });
                if (used.X + used.Width < free.X + free.Width) next.Add(new PackedRect
                    { X = used.X + used.Width, Y = free.Y,
                        Width = free.X + free.Width - used.X - used.Width, Height = free.Height });
                if (used.Y > free.Y) next.Add(new PackedRect
                    { X = free.X, Y = free.Y, Width = free.Width, Height = used.Y - free.Y });
                if (used.Y + used.Height < free.Y + free.Height) next.Add(new PackedRect
                    { X = free.X, Y = used.Y + used.Height, Width = free.Width,
                        Height = free.Y + free.Height - used.Y - used.Height });
            }
            page.Free.Clear();
            for (var i = 0; i < next.Count; i++)
            {
                if (next[i].Width <= 0.01 || next[i].Height <= 0.01) continue;
                var contained = false;
                for (var j = 0; j < next.Count; j++)
                    if (i != j && next[i].X >= next[j].X - 0.01
                        && next[i].Y >= next[j].Y - 0.01
                        && next[i].X + next[i].Width <= next[j].X + next[j].Width + 0.01
                        && next[i].Y + next[i].Height <= next[j].Y + next[j].Height + 0.01)
                    { contained = true; break; }
                if (!contained) page.Free.Add(next[i]);
            }
        }

        private sealed class PackingCandidate { public readonly List<PackingPage> Pages = new List<PackingPage>(); public double OccupiedArea; public bool IsGrid; public int Columns; public int Rows; }
        private sealed class PackingPage { public int Index; public readonly List<PackedRect> Free = new List<PackedRect>(); public readonly List<PackedPlacement> Placements = new List<PackedPlacement>(); public bool[,] Grid; }
        private class PackedRect { public double X; public double Y; public double Width; public double Height; }
        private sealed class PackedPlacement : PackedRect { public StairLayoutItem Item; public double ContentOffsetX; public double ContentOffsetY; public int Column; public int Row; public int ColumnSpan = 1; public int RowSpan = 1; }
        private sealed class PlacementChoice { public PackingPage Page; public PackedRect Free; public double AreaWaste; public double ShortWaste; }
    }
}
