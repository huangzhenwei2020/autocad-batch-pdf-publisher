using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>图纸定义：把哪几张视图排到一张多大纸上。</summary>
    public sealed class SheetDefinitionModel
    {
        public string Id { get; set; }
        /// <summary>图号（如 建施-01）。</summary>
        public string Number { get; set; }
        /// <summary>图名（如 一层平面图）。</summary>
        public string Title { get; set; }
        /// <summary>纸张规格名（A4/A3/A2/A1/A0，见 <see cref="SheetComposer.Papers"/>）。</summary>
        public string Paper { get; set; } = "A3";
        /// <summary>横放（true，默认）或竖放。</summary>
        public bool Landscape { get; set; } = true;
        /// <summary>指定要用哪张图框模板（登记时的块名）；空 = 落图时按纸张自动匹配。</summary>
        public string FrameTemplate { get; set; }
        /// <summary>排在这张图上的视图 id（views/*.json 的 id，按顺序排入）。</summary>
        public List<string> ViewIds { get; set; } = new List<string>();
    }

    /// <summary>纸张规格（mm）：GB/T 50001-2017 表 3.1.3 的基本幅面。</summary>
    public sealed class PaperSizeModel
    {
        public string Name { get; set; }
        public double ShortSide { get; set; }
        public double LongSide { get; set; }
    }

    /// <summary>
    /// 图纸排版：把若干张视图按比例摆到一张图纸上，画出图框与标题栏。
    ///
    /// 关键设计：**排版结果本身就是一份 <see cref="ViewDocument"/>（Kind = Sheet，单位 = 图纸毫米）**，
    /// 所以插件落图命令一个字都不用改就能把整张图纸落到 DWG 里，打图时按 1:1 出图即可
    ///（视图内容已经按各自比例缩到纸面尺寸）。
    ///
    /// 尺寸的处理：视图里的尺寸在缩放到纸面后，几何距离不再是真实尺寸，
    /// 所以排版时把它们**转成带显式文字的尺寸**（`ViewDimension.Text` = 模型真实值），
    /// 这样图上读到的数字仍然对，箭头与界线位置也正确。
    /// </summary>
    public static class SheetComposer
    {
        /// <summary>图框留边（mm）：装订边 a=25、其余 c=5（GB/T 50001-2017）。</summary>
        public const double BindingMargin = 25d;
        public const double OtherMargin = 5d;
        /// <summary>标题栏尺寸（mm）：宽 180、高 40（放右下角）。</summary>
        public const double TitleBlockWidth = 180d;
        public const double TitleBlockHeight = 40d;
        /// <summary>视图之间的间距与"图名占位"（mm）。</summary>
        private const double ViewGap = 12d;
        private const double ViewTitleSpace = 8d;

        public static readonly PaperSizeModel[] Papers =
        {
            new PaperSizeModel { Name = "A4", ShortSide = 210d, LongSide = 297d },
            new PaperSizeModel { Name = "A3", ShortSide = 297d, LongSide = 420d },
            new PaperSizeModel { Name = "A2", ShortSide = 420d, LongSide = 594d },
            new PaperSizeModel { Name = "A1", ShortSide = 594d, LongSide = 841d },
            new PaperSizeModel { Name = "A0", ShortSide = 841d, LongSide = 1189d }
        };

        public static PaperSizeModel FindPaper(string name)
        {
            var wanted = (name ?? string.Empty).Trim();
            return Papers.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)) ?? Papers[1];
        }

        /// <summary>纸张的实际宽高（按横放/竖放换算）。</summary>
        public static void PaperSize(SheetDefinitionModel sheet, out double width, out double height)
        {
            var paper = FindPaper(sheet == null ? null : sheet.Paper);
            var landscape = sheet == null || sheet.Landscape;
            width = landscape ? paper.LongSide : paper.ShortSide;
            height = landscape ? paper.ShortSide : paper.LongSide;
        }

        /// <summary>
        /// 排一张图纸。<paramref name="views"/> 是要排上去的视图（按 id 找 <see cref="SheetDefinitionModel.ViewIds"/> 里的顺序）。
        /// 找不到的视图会记一条提示，不影响其它视图。
        /// </summary>
        public static ViewDocument Compose(IEnumerable<ViewDocument> views, SheetDefinitionModel sheet)
        {
            if (sheet == null) throw new ArgumentNullException(nameof(sheet));
            var source = (views ?? Enumerable.Empty<ViewDocument>()).Where(v => v != null).ToList();
            double paperWidth, paperHeight;
            PaperSize(sheet, out paperWidth, out paperHeight);

            var document = new ViewDocument
            {
                Id = string.IsNullOrWhiteSpace(sheet.Id) ? "sheet" : sheet.Id,
                Title = BuildTitle(sheet, paperWidth, paperHeight),
                Kind = ViewKind.Sheet,
                Scale = 1,                                  // 纸面毫米，1:1 出图
                PaperName = FindPaper(sheet.Paper).Name,
                PaperWidth = paperWidth,
                PaperHeight = paperHeight,
                FrameTemplate = sheet.FrameTemplate
            };

            AddBorder(document, paperWidth, paperHeight);
            AddTitleBlock(document, sheet, paperWidth, paperHeight);

            var selected = new List<ViewDocument>();
            foreach (var id in sheet.ViewIds ?? new List<string>())
            {
                var view = source.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
                if (view == null) { document.Warnings.Add("图纸上找不到视图：" + id); continue; }
                selected.Add(view);
            }
            if (selected.Count == 0)
            {
                document.Warnings.Add("这张图纸还没有排任何视图。");
                return document;
            }

            // 内容区：图框内**让出标题栏那一条**（标题栏在右下角，整条让开最稳）。
            // 注意 LayoutCells 用的是"top = 小的 y、bottom = 大的 y"的写法。
            var contentLeft = BindingMargin;
            var contentTop = OtherMargin + TitleBlockHeight + ViewGap;
            var contentRight = paperWidth - OtherMargin;
            var contentBottom = paperHeight - OtherMargin;
            if (contentBottom - contentTop < TitleBlockHeight) contentTop = OtherMargin;   // 纸太小就不让了

            var cells = LayoutCells(selected.Count, contentLeft, contentTop, contentRight, contentBottom);
            for (var index = 0; index < selected.Count; index++)
            {
                var view = selected[index];
                var cell = cells[index];
                PlaceView(document, view, cell, sheet);
            }
            return document;
        }

        private static string BuildTitle(SheetDefinitionModel sheet, double width, double height)
        {
            var number = string.IsNullOrWhiteSpace(sheet.Number) ? string.Empty : sheet.Number + "　";
            var title = string.IsNullOrWhiteSpace(sheet.Title) ? "图纸" : sheet.Title;
            return number + title + "　" + FindPaper(sheet.Paper).Name
                + (sheet.Landscape ? " 横" : " 竖") + "　" + Math.Round(width) + "×" + Math.Round(height) + "（1:1 出图）";
        }

        private static void AddBorder(ViewDocument document, double width, double height)
        {
            // 外框（纸边）与图框（留边：左 25 装订、其余 5）——放在"图纸框"图层上：
            // 落图时如果套用了项目自己的图框模板，插件会跳过这一层，不会出现双层图框。
            AddRect(document, ViewLayers.SheetFrame, 0d, 0d, width, height);
            AddRect(document, ViewLayers.SheetFrame, BindingMargin, OtherMargin, width - OtherMargin, height - OtherMargin);
        }

        private static void AddTitleBlock(ViewDocument document, SheetDefinitionModel sheet, double width, double height)
        {
            var right = width - OtherMargin;
            var bottom = OtherMargin;
            var left = right - TitleBlockWidth;
            var top = bottom + TitleBlockHeight;
            AddRect(document, ViewLayers.SheetFrame, left, bottom, right, top);

            // 标题栏内两道横线，把"图名 / 图号·比例·日期"分开
            AddLine(document, ViewLayers.SheetFrame, left, bottom + 22d, right, bottom + 22d);
            AddLine(document, ViewLayers.SheetFrame, left + 110d, bottom, left + 110d, bottom + 22d);

            var number = string.IsNullOrWhiteSpace(sheet.Number) ? "建施-XX" : sheet.Number;
            var title = string.IsNullOrWhiteSpace(sheet.Title) ? "图纸" : sheet.Title;
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.SheetFrame, Text = title, X = left + 4d, Y = bottom + 26d, Height = 7d
            });
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.SheetFrame, Text = "图号 " + number, X = left + 4d, Y = bottom + 6d, Height = 4.5d
            });
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.SheetFrame, Text = "比例 见图", X = left + 114d, Y = bottom + 6d, Height = 4.5d
            });
            document.Texts.Add(new ViewText
            {
                Layer = ViewLayers.SheetFrame, Text = "万落建筑工具　" + DateTime.Now.ToString("yyyy-MM-dd"),
                X = left + 114d, Y = bottom + 26d, Height = 4d
            });
        }

        /// <summary>把 n 个视图排成尽量方正的格子（列数按长宽比估）。</summary>
        private static List<double[]> LayoutCells(int count, double left, double top, double right, double bottom)
        {
            var width = Math.Max(1d, right - left);
            var height = Math.Max(1d, bottom - top);
            var columns = count <= 1 ? 1 : count <= 4 ? 2 : 3;
            var rows = (int)Math.Ceiling(count / (double)columns);
            var cellWidth = (width - ViewGap * (columns - 1)) / columns;
            var cellHeight = (height - ViewGap * (rows - 1)) / rows;
            var cells = new List<double[]>();
            for (var index = 0; index < count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var cellLeft = left + column * (cellWidth + ViewGap);
                var cellTop = top + row * (cellHeight + ViewGap);
                cells.Add(new[] { cellLeft, cellTop, cellLeft + cellWidth, cellTop + cellHeight });
            }
            return cells;
        }

        /// <summary>
        /// 把一个视图按自己的比例缩到纸面并放进格子（居中，图名留在下方）。
        /// 几何超出格子时记提示（不强行改用户定的比例 —— 出图比例是设计决定）。
        /// </summary>
        private static void PlaceView(ViewDocument document, ViewDocument view, double[] cell, SheetDefinitionModel sheet)
        {
            var scale = 1d / Math.Max(1, view.Scale);                 // 模型 mm → 纸面 mm
            var bounds = GeometryBounds(view);
            if (bounds == null) { document.Warnings.Add(view.Title + " 没有几何，未排入图纸。"); return; }

            var viewWidth = (bounds[2] - bounds[0]) * scale;
            var viewHeight = (bounds[3] - bounds[1]) * scale;
            var cellWidth = cell[2] - cell[0];
            var cellHeight = cell[3] - cell[1] - ViewTitleSpace;
            if (viewWidth > cellWidth + 0.5d || viewHeight > cellHeight + 0.5d)
                document.Warnings.Add(view.Title + " 在 " + (view.Scale >= 1 ? "1:" + view.Scale : "1:1")
                    + " 下超出图纸格（" + Math.Round(viewWidth) + "×" + Math.Round(viewHeight) + " > "
                    + Math.Round(cellWidth) + "×" + Math.Round(cellHeight) + "），建议换大纸或改比例。");

            // 居中：把视图几何的左下角放到格子中心偏左下
            var offsetX = cell[0] + (cellWidth - viewWidth) / 2d - bounds[0] * scale;
            var offsetY = cell[1] + (cellHeight - viewHeight) / 2d - bounds[1] * scale + ViewTitleSpace;

            foreach (var line in view.Lines ?? new List<ViewLine>())
            {
                if (line == null) continue;
                document.Lines.Add(new ViewLine
                {
                    Layer = line.Layer,
                    LineType = line.LineType,
                    X1 = line.X1 * scale + offsetX, Y1 = line.Y1 * scale + offsetY,
                    X2 = line.X2 * scale + offsetX, Y2 = line.Y2 * scale + offsetY
                });
            }
            foreach (var circle in view.Circles ?? new List<ViewCircle>())
            {
                if (circle == null) continue;
                document.Circles.Add(new ViewCircle
                {
                    Layer = circle.Layer,
                    X = circle.X * scale + offsetX, Y = circle.Y * scale + offsetY,
                    Radius = circle.Radius * scale
                });
            }
            foreach (var text in view.Texts ?? new List<ViewText>())
            {
                if (text == null || string.IsNullOrEmpty(text.Text)) continue;
                document.Texts.Add(new ViewText
                {
                    Layer = text.Layer, Text = text.Text,
                    X = text.X * scale + offsetX, Y = text.Y * scale + offsetY,
                    Height = Math.Max(1.8d, text.Height * scale)      // 纸面上不小于 1.8mm，不然印出来看不清
                });
            }
            foreach (var hatch in view.Hatches ?? new List<ViewHatch>())
            {
                if (hatch == null || hatch.Boundary == null || hatch.Boundary.Count < 3) continue;
                document.Hatches.Add(new ViewHatch
                {
                    Layer = hatch.Layer, Pattern = hatch.Pattern, Scale = hatch.Scale, Angle = hatch.Angle,
                    Spacing = Math.Max(0.3d, hatch.Spacing * scale),
                    Boundary = hatch.Boundary.Where(p => p != null)
                        .Select(p => new PointModel(p.X * scale + offsetX, p.Y * scale + offsetY)).ToList()
                });
            }
            // 尺寸：纸面上的几何距离不再等于真实尺寸，所以把真实值写进 Text
            foreach (var dimension in view.Dimensions ?? new List<ViewDimension>())
            {
                if (dimension == null) continue;
                var value = string.IsNullOrWhiteSpace(dimension.Text)
                    ? Math.Round(Math.Abs(dimension.To - dimension.From)).ToString("0")
                    : dimension.Text;
                document.Dimensions.Add(new ViewDimension
                {
                    Layer = dimension.Layer, Vertical = dimension.Vertical,
                    From = dimension.From * scale + (dimension.Vertical ? offsetY : offsetX),
                    To = dimension.To * scale + (dimension.Vertical ? offsetY : offsetX),
                    AnchorPosition = dimension.AnchorPosition * scale + (dimension.Vertical ? offsetX : offsetY),
                    LinePosition = dimension.LinePosition * scale + (dimension.Vertical ? offsetX : offsetY),
                    Text = value,
                    Note = dimension.Note
                });
            }

            // 视图自带图名与比例（投影时就带了），这里不再重复标
            _ = sheet;
        }

        /// <summary>视图几何（线 + 圆 + 填充）在视图坐标里的包围盒：minX、minY、maxX、maxY。</summary>
        private static double[] GeometryBounds(ViewDocument view)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var found = false;
            foreach (var line in view.Lines ?? new List<ViewLine>())
            {
                if (line == null) continue;
                Include(Math.Min(line.X1, line.X2), Math.Min(line.Y1, line.Y2));
                Include(Math.Max(line.X1, line.X2), Math.Max(line.Y1, line.Y2));
            }
            foreach (var circle in view.Circles ?? new List<ViewCircle>())
            {
                if (circle == null) continue;
                var radius = Math.Abs(circle.Radius);
                Include(circle.X - radius, circle.Y - radius);
                Include(circle.X + radius, circle.Y + radius);
            }
            foreach (var hatch in view.Hatches ?? new List<ViewHatch>())
                foreach (var point in hatch == null ? new List<PointModel>() : hatch.Boundary ?? new List<PointModel>())
                {
                    if (point == null) continue;
                    Include(point.X, point.Y);
                }
            if (!found) return null;
            return new[] { minX, minY, maxX, maxY };

            void Include(double x, double y)
            {
                if (!IsFinite(x) || !IsFinite(y)) return;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                found = true;
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

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
