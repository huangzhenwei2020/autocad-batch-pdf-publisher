using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>
    /// 立面/剖面预览的自检：把 <see cref="ViewDocument"/> 真正画到离屏位图上（走 OnPaint 同一条路）。
    ///
    /// 为什么值得单独自检：预览是"不开 CAD 也能看立面"的唯一眼睛，
    /// 它悄悄画不出来（或者抛异常）比画错更糟 —— 用户会以为视图本身是空的。
    /// 所以这里钉住：每一张默认视图都要**画够线条**、极端视图变换不挂、坏数据只跳过自己。
    /// </summary>
    internal static class ViewPreviewSelfTest
    {
        private const int Width = 900;
        private const int Height = 640;

        public static void Run(Action<string> log)
        {
            if (log == null) log = text => { };
            var model = SampleModelFactory.CreateTwoStoreyHouse();
            var library = SampleModelFactory.CreateDemoOpeningLibrary();
            var canvas = new ViewPreviewCanvas { Size = new Size(Width, Height) };

            // 1) 五张默认视图：每张都要真的画出来，而且线条一条不少
            foreach (var definition in SampleModelFactory.CreateDefaultViews(model.Name))
            {
                var view = OrthographicProjector.Project(model, definition, library);
                var painted = RenderOnce(canvas, view, definition.Title, log);
                if (canvas.LastLineCount != view.Lines.Count)
                    throw new Exception(definition.Title + " 只画了 " + canvas.LastLineCount + " / " + view.Lines.Count + " 条线");
                if (canvas.LastTextCount != view.Texts.Count)
                    throw new Exception(definition.Title + " 只画了 " + canvas.LastTextCount + " / " + view.Texts.Count + " 个文字");
                if (painted < 2000) throw new Exception(definition.Title + " 只画出了 " + painted + " 个像素");
                log("PASS 预览 " + definition.Title + "：线 " + canvas.LastLineCount + "（门窗 "
                    + view.Lines.Count(l => l.Layer == ViewLayers.Opening) + "）、文字 " + canvas.LastTextCount
                    + "、填充 " + canvas.LastHatchCount + "，非背景像素 " + painted);
            }

            // 2) 点选：点洞口正中应选中它；点空白处应取消；重算后仍保持选中同一个构件
            var southView = OrthographicProjector.Project(model, SampleModelFactory.CreateDefaultViews(model.Name)[0], library);
            canvas.View = southView;
            using (var surface = new Offscreen(Width, Height)) canvas.Render(surface.Graphics);   // 先画一次，视图变换才定下来
            var anchor = southView.Anchors.FirstOrDefault(a => a != null);
            if (anchor == null) throw new Exception("南立面没有门窗锚点，预览无法点选。");
            var center = canvas.ModelToScreenForTest((anchor.X1 + anchor.X2) / 2d, (anchor.Y1 + anchor.Y2) / 2d);
            canvas.SimulateClick(center);
            if (canvas.SelectedAnchor == null || canvas.SelectedAnchor.ElementId != anchor.ElementId)
                throw new Exception("点洞口正中应选中 " + anchor.ElementId + "，实际 "
                    + (canvas.SelectedAnchor == null ? "没选中" : canvas.SelectedAnchor.ElementId));
            canvas.SimulateClick(new Point(Width - 5, 5));      // 右上角空白处
            if (canvas.SelectedAnchor != null) throw new Exception("点空白处应取消选中。");
            canvas.SelectElement(anchor.ElementId);
            canvas.View = OrthographicProjector.Project(model, SampleModelFactory.CreateDefaultViews(model.Name)[0], library);
            if (canvas.SelectedAnchor == null) throw new Exception("重算视图后应仍保持选中同一个构件。");
            using (var surface = new Offscreen(Width, Height)) canvas.Render(surface.Graphics);
            log("PASS 预览点选：点中洞口 → 选中 " + anchor.ElementId + "；点空白取消；重算后仍选中同一樘");

            // 3) 极端视图变换：不许挂、不许抛（平面画布崩过一次，预览不能重蹈覆辙）
            var south = OrthographicProjector.Project(model, SampleModelFactory.CreateDefaultViews(model.Name)[0], library);
            var extremes = new List<double[]>
            {
                new[] { 0.002d, 60d, 600d },
                new[] { 2d, 0d, 0d },
                new[] { 1e-9d, 0d, 0d },
                new[] { 1e9d, 0d, 0d },
                new[] { double.NaN, 0d, 0d },
                new[] { 0.05d, double.NaN, 0d },
                new[] { 0.05d, 0d, double.NegativeInfinity },
                new[] { 0d, 0d, 0d },
                new[] { -0.05d, 0d, 0d },
                new[] { 0.05d, 1e300d, -1e300d }
            };
            foreach (var extreme in extremes)
            {
                canvas.SetViewport(extreme[0], extreme[1], extreme[2]);
                using (var surface = new Offscreen(Width, Height))
                    canvas.SafeRender(surface.Graphics);
                if (canvas.LastPaintError != null)
                    throw new Exception("极端视图变换仍会让预览抛异常：" + canvas.LastPaintError);
            }
            log("PASS 预览极端视图变换 " + extremes.Count + " 种：不挂、不抛");

            // 3) 坏视图数据：NaN / ∞ 坐标、超大文字、越界填充，只能被跳过自己
            canvas.View = BrokenView();
            var brokenPainted = RenderOnce(canvas, null, "坏数据视图", log);
            if (brokenPainted < 200) throw new Exception("坏数据视图应保留能画的那部分，实际只有 " + brokenPainted + " 个像素");
            canvas.View = south;

            // 4) 坏 Graphics（已释放）也必须被兜底拦下
            using (var surface = new Offscreen(Width, Height))
            {
                surface.Graphics.Dispose();
                canvas.SafeRender(surface.Graphics);
            }
            if (canvas.LastPaintError == null) throw new Exception("坏 Graphics 没有被兜底拦下。");
            using (var empty = new Offscreen(Width, Height))
            {
                canvas.View = null;
                canvas.SafeRender(empty.Graphics);      // 没有视图时只画提示，同样不许抛
            }

            log("PASS 立面预览自检全部通过（5 张视图 / 极端变换 / 坏数据 / 坏 Graphics）。");
        }

        private static int RenderOnce(ViewPreviewCanvas canvas, ViewDocument view, string label, Action<string> log)
        {
            int painted;
            using (var surface = new Offscreen(Width, Height))
            {
                if (view != null) canvas.View = view;
                canvas.Render(surface.Graphics);        // 真实绘制路径：出问题就直接失败，不藏
                painted = CountPaintedPixels(surface.Bitmap, canvas.BackColor);
            }
            if (canvas.LastPaintError != null)
                throw new Exception(label + " 预览被兜底救下（说明绘制路径仍然会抛）：" + canvas.LastPaintError);
            return painted;
        }

        /// <summary>数一下有多少像素不是背景色 —— 证明"真的把图画出来了"，不是只画了背景。</summary>
        private static int CountPaintedPixels(Bitmap bitmap, Color background)
        {
            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var count = 0;
                for (var index = 0; index + 3 < bytes.Length; index += 4)
                {
                    if (bytes[index] != background.B || bytes[index + 1] != background.G
                        || bytes[index + 2] != background.R) count++;
                }
                return count;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>刻意做一份坏视图：NaN / ∞ 坐标、天文数字、越界填充，看绘制会不会被带崩。</summary>
        private static ViewDocument BrokenView()
        {
            var view = new ViewDocument { Id = "broken", Title = "坏数据视图", Scale = 100 };
            view.Lines.Add(new ViewLine { Layer = ViewLayers.Elevation, X1 = 0d, Y1 = 0d, X2 = 3000d, Y2 = 0d });
            view.Lines.Add(new ViewLine { Layer = ViewLayers.Elevation, X1 = 3000d, Y1 = 0d, X2 = 3000d, Y2 = 2000d });
            view.Lines.Add(new ViewLine { Layer = ViewLayers.Opening, X1 = 0d, Y1 = 0d, X2 = double.PositiveInfinity, Y2 = 0d });
            view.Lines.Add(new ViewLine { Layer = ViewLayers.Cut, X1 = double.NaN, Y1 = 0d, X2 = 0d, Y2 = double.NaN });
            view.Lines.Add(new ViewLine { Layer = ViewLayers.Elevation, X1 = 1e300d, Y1 = 0d, X2 = 1e300d, Y2 = 1e300d });
            view.Lines.Add(null);
            view.Texts.Add(new ViewText { Layer = ViewLayers.LevelText, Text = "±0.000", X = 100d, Y = 200d, Height = 300d });
            view.Texts.Add(new ViewText { Layer = ViewLayers.LevelText, Text = "坏文字", X = double.NaN, Y = 0d, Height = 300d });
            view.Texts.Add(new ViewText { Layer = ViewLayers.LevelText, Text = "超大文字", X = 1200d, Y = 200d, Height = 1e12d });
            view.Hatches.Add(new ViewHatch
            {
                Layer = ViewLayers.CutHatch, Pattern = "ANSI31", Spacing = 75d, Angle = 45d,
                Boundary = new List<PointModel> { new PointModel(0d, 0d), new PointModel(500d, 0d), new PointModel(500d, 1500d), new PointModel(0d, 1500d) }
            });
            view.Hatches.Add(new ViewHatch
            {
                Layer = ViewLayers.CutHatch, Pattern = "ANSI31", Spacing = 0d, Angle = 45d,
                Boundary = new List<PointModel> { new PointModel(double.NaN, 0d), new PointModel(0d, 0d), new PointModel(0d, 0d) }
            });
            return view;
        }

        /// <summary>离屏画布：位图 + Graphics 一起管，自检不会漏 GDI 对象。</summary>
        private sealed class Offscreen : IDisposable
        {
            public Offscreen(int width, int height)
            {
                Bitmap = new Bitmap(width, height);
                Graphics = Graphics.FromImage(Bitmap);
                Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            }

            public Bitmap Bitmap { get; private set; }
            public Graphics Graphics { get; private set; }

            public void Dispose()
            {
                Graphics.Dispose();
                Bitmap.Dispose();
            }
        }
    }
}
