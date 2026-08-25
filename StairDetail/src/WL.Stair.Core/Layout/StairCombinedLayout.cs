using System;
using System.Collections.Generic;
using System.Linq;

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
    }

    public sealed class StairLayoutSlot
    {
        public StairLayoutItem Item { get; set; }
        public int Page { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
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
    /// Grid selection follows the proven detail-layout rule: first minimise
    /// pages, then maximise useful capacity and finally prefer the grid whose
    /// natural aspect ratio is closest to the registered content range.
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
            var grid = FindBestGrid(items, contentWidth, contentHeight, Math.Max(0.0, options.ItemGap));
            if (grid == null)
                throw new InvalidOperationException("楼梯平面或剖面无法放入当前纸张，请使用更大图框或更小出图比例。");

            plan.Columns = grid.Columns;
            plan.Rows = grid.Rows;
            var extraWidth = Math.Max(0.0, contentWidth - grid.ColumnWidths.Sum()) / grid.Columns;
            var extraHeight = Math.Max(0.0, contentHeight - grid.RowHeights.Sum()) / grid.Rows;
            foreach (var width in grid.ColumnWidths) plan.ColumnWidths.Add(width + extraWidth);
            foreach (var height in grid.RowHeights) plan.RowHeights.Add(height + extraHeight);

            var perPage = plan.Columns * plan.Rows;
            for (var index = 0; index < items.Count; index++)
            {
                var pageIndex = index % perPage;
                var row = pageIndex / plan.Columns;
                var column = pageIndex % plan.Columns;
                var cellWidth = plan.ColumnWidths[column];
                var cellHeight = plan.RowHeights[row];
                var cellX = plan.ContentLeft + plan.ColumnWidths.Take(column).Sum();
                var cellY = plan.ContentTop - plan.RowHeights.Take(row + 1).Sum();
                var item = items[index];
                plan.Slots.Add(new StairLayoutSlot
                {
                    Item = item,
                    Page = index / perPage,
                    Row = row,
                    Column = column,
                    X = cellX + (cellWidth - item.Width) / 2.0,
                    Y = cellY + (cellHeight - item.Height) / 2.0,
                    Width = item.Width,
                    Height = item.Height,
                    CellX = cellX,
                    CellY = cellY,
                    CellWidth = cellWidth,
                    CellHeight = cellHeight
                });
            }
            plan.PageCount = (int)Math.Ceiling((double)items.Count / perPage);
            return plan;
        }

        private static GridCandidate FindBestGrid(
            IList<StairLayoutItem> items,
            double contentWidth,
            double contentHeight,
            double gap)
        {
            GridCandidate best = null;
            var targetAspect = contentWidth / Math.Max(1.0, contentHeight);
            for (var columns = 1; columns <= items.Count; columns++)
            {
                for (var rows = 1; rows <= items.Count; rows++)
                {
                    var capacity = columns * rows;
                    var widths = new double[columns];
                    var heights = new double[rows];
                    for (var index = 0; index < items.Count; index++)
                    {
                        var position = index % capacity;
                        var column = position % columns;
                        var row = position / columns;
                        widths[column] = Math.Max(widths[column], items[index].Width + gap);
                        heights[row] = Math.Max(heights[row], items[index].Height + gap);
                    }
                    var requiredWidth = widths.Sum();
                    var requiredHeight = heights.Sum();
                    if (requiredWidth > contentWidth + 0.01 || requiredHeight > contentHeight + 0.01)
                        continue;
                    var pages = (int)Math.Ceiling((double)items.Count / capacity);
                    var usefulCapacity = Math.Min(items.Count, capacity);
                    var emptyCells = pages * capacity - items.Count;
                    var naturalAspect = requiredWidth / Math.Max(1.0, requiredHeight);
                    var aspectError = Math.Abs(Math.Log(Math.Max(0.000001, naturalAspect / targetAspect)));
                    var candidate = new GridCandidate
                    {
                        Columns = columns,
                        Rows = rows,
                        Pages = pages,
                        Capacity = usefulCapacity,
                        EmptyCells = emptyCells,
                        AspectError = aspectError,
                        ColumnWidths = widths,
                        RowHeights = heights
                    };
                    if (best == null || candidate.Pages < best.Pages
                        || (candidate.Pages == best.Pages && candidate.Capacity > best.Capacity)
                        || (candidate.Pages == best.Pages && candidate.Capacity == best.Capacity
                            && candidate.EmptyCells < best.EmptyCells)
                        || (candidate.Pages == best.Pages && candidate.Capacity == best.Capacity
                            && candidate.EmptyCells == best.EmptyCells && candidate.AspectError < best.AspectError))
                        best = candidate;
                }
            }
            return best;
        }

        private sealed class GridCandidate
        {
            public int Columns;
            public int Rows;
            public int Pages;
            public int Capacity;
            public int EmptyCells;
            public double AspectError;
            public double[] ColumnWidths;
            public double[] RowHeights;
        }
    }
}
