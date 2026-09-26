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
    /// 画布自检：不弹窗口，把平面画布**真正画到离屏位图上**，走的就是 OnPaint 那条路。
    ///
    /// 这个自检来自一次真实崩溃（2026-09-26，界面上画布整块变成红叉、弹"未经处理的异常"）：
    /// PlanCanvas.DrawGrid 的横向网格循环条件写反，y 一路加到 ±∞ 再交给 GDI+，
    /// 抛 System.OverflowException。当时的比例是 1px≈26.7mm。
    ///
    /// 所以这里固定三件事：
    ///   1. 正常视图能画完，网格数量合理、坐标都在屏幕内；
    ///   2. 极端视图（极小/极大比例、天边偏移、NaN/∞）不挂、不抛；
    ///   3. 坏模型（NaN/∞ 坐标）与坏 Graphics 都只能被兜底拦下，绝不能把程序带走。
    /// </summary>
    internal static class CanvasSelfTest
    {
        private const int CanvasWidth = 880;
        private const int CanvasHeight = 620;
        /// <summary>崩溃时状态栏上的比例：1px ≈ 26.7mm。</summary>
        private const double CrashingScale = 1d / 26.7d;

        public static void Run(Action<string> log)
        {
            if (log == null) log = text => { };
            var model = SampleModelFactory.CreateTwoStoreyHouse();
            var canvas = new PlanCanvas { Size = new Size(CanvasWidth, CanvasHeight) };
            canvas.Model = model;
            canvas.StoreyId = model.Storeys[0].Id;      // 不设楼层的话画布只会画网格
            if (canvas.Model == null) throw new InvalidOperationException("画布没有装载模型。");

            // 1) 模型装载后的默认视图（设置 Model 时会做一次 ZoomExtents）
            RenderOnce(canvas, "默认视图", log, expectPainted: true);

            // 2) 崩溃当时的比例：网格要画满可见区域、数量合理、坐标都落在屏幕里
            canvas.SetViewport(CrashingScale, 120d, 520d);
            CheckGrid(CrashingScale, 120d, 520d, log);
            RenderOnce(canvas, "正常视图（1px≈26.7mm）", log, expectPainted: true);

            // 3) 极端视图：以前这里会死循环或把 ∞ 交给 GDI+，现在必须只是"画不出网格"而已
            var extremes = new List<double[]>
            {
                new[] { 0.002d, 60d, 600d },                       // 缩到最小比例
                new[] { 2d, 0d, 0d },                              // 放到最大比例
                new[] { 0.002d, -5e6d, 5e6d },                     // 平移到很远
                new[] { 0.002d, 1e300d, -1e300d },                 // 平移到天文数字
                new[] { 1e-9d, 0d, 0d },                           // 比例小到离谱
                new[] { 1e9d, 0d, 0d },                            // 比例大到离谱
                new[] { double.NaN, 0d, 0d },                      // 比例变成 NaN
                new[] { CrashingScale, double.NaN, 0d },           // 偏移 NaN
                new[] { CrashingScale, 0d, double.NegativeInfinity },
                new[] { CrashingScale, double.PositiveInfinity, 0d },
                new[] { 0d, 0d, 0d },                              // 比例为 0
                new[] { -0.05d, 0d, 0d }                           // 负比例
            };
            foreach (var extreme in extremes)
            {
                canvas.SetViewport(extreme[0], extreme[1], extreme[2]);
                using (var surface = new Offscreen(CanvasWidth, CanvasHeight))
                    canvas.SafeRender(surface.Graphics);     // 兜底路径：允许画不出，但不许抛
                if (canvas.LastPaintError != null)
                    throw new Exception("极端视图仍会让绘制抛异常：比例 " + extreme[0]
                        + "、偏移 (" + extreme[1] + ", " + extreme[2] + ") → " + canvas.LastPaintError);
                var label = "比例 " + extreme[0] + "、偏移 (" + extreme[1] + ", " + extreme[2] + ")";
                var lines = PlanGrid.Compute(CanvasWidth, CanvasHeight, extreme[0], extreme[1], extreme[2]);
                if (lines.Count > PlanGrid.MaxLinesPerAxis * 2)
                    throw new Exception("极端视图下网格线没有封顶：" + lines.Count + "（" + label + "）");
                if (lines.Any(l => float.IsNaN(l.Screen) || float.IsInfinity(l.Screen)))
                    throw new Exception("极端视图下网格线坐标不是有限值（" + label + "）");
                log("PASS 极端视图不挂、不抛、网格有界：" + label + "（网格 " + lines.Count + " 条）");
            }

            // 4) 坏模型：NaN / ∞ 坐标的建筑构件，只能被跳过，不许影响整块画布
            canvas.Model = CreateBrokenModel();
            canvas.StoreyId = canvas.Model.Storeys[0].Id;
            if (!PlanGrid.IsFinite(canvas.ViewScale))
                throw new Exception("坏模型把视图比例带成了非有限值：" + canvas.ViewScale);
            RenderOnce(canvas, "坏模型（NaN/∞ 坐标）", log, expectPainted: true);

            // 5) 连 Graphics 都是坏的（已释放）时，兜底必须拦下来而不是把异常抛给 WinForms
            canvas.Model = model;
            canvas.SetViewport(CrashingScale, 120d, 520d);
            using (var surface = new Offscreen(CanvasWidth, CanvasHeight))
            {
                surface.Graphics.Dispose();
                canvas.SafeRender(surface.Graphics);
            }
            if (canvas.LastPaintError == null)
                throw new Exception("坏 Graphics 没有被兜底拦下。");
            log("PASS 坏 Graphics 被兜底拦下：" + canvas.LastPaintError);

            log("PASS 画布自检全部通过（正常/极端/坏数据/坏 Graphics）。");
        }

        /// <summary>正常比例下网格的硬性要求：铺满可见区域、条数合理、都在屏幕内。</summary>
        private static void CheckGrid(double scale, double offsetX, double offsetY, Action<string> log)
        {
            var lines = PlanGrid.Compute(CanvasWidth, CanvasHeight, scale, offsetX, offsetY);
            var vertical = lines.Count(l => l.Vertical);
            var horizontal = lines.Count(l => !l.Vertical);
            if (vertical < 2 || horizontal < 2)
                throw new Exception("正常视图下网格太稀：" + vertical + " 竖 / " + horizontal + " 横");
            if (lines.Count > 200)
                throw new Exception("正常视图下网格线异常多：" + lines.Count + " 条");
            foreach (var line in lines)
            {
                var limit = line.Vertical ? CanvasWidth : CanvasHeight;
                if (line.Screen < -1f || line.Screen > limit + 1f)
                    throw new Exception("网格线落在可见区域外：" + line.Screen + "（范围 0–" + limit + "）");
            }
            if (!lines.Any(l => l.Major))
                throw new Exception("每 5 条的亮线一根都没有。");
            log("PASS 网格铺满可见区域：" + vertical + " 竖 / " + horizontal + " 横（亮线 "
                + lines.Count(l => l.Major) + " 条，1px≈" + Math.Round(1d / scale, 1) + "mm）");
        }

        private static void RenderOnce(PlanCanvas canvas, string label, Action<string> log, bool expectPainted)
        {
            int painted;
            using (var surface = new Offscreen(CanvasWidth, CanvasHeight))
            {
                canvas.Render(surface.Graphics);       // 真实绘制路径：出问题就直接失败，不藏
                painted = CountPaintedPixels(surface.Bitmap, canvas.BackColor);
            }
            if (canvas.LastPaintError != null)
                throw new Exception(label + " 绘制被兜底救下（说明绘制路径仍然会抛）：" + canvas.LastPaintError);
            if (expectPainted && painted < 2000)
                throw new Exception(label + " 只画出了 " + painted + " 个像素，平面图没画出来。");
            log("PASS 绘制 " + label + "（非背景像素 " + painted + "）");
        }

        /// <summary>数一下有多少像素不是背景色 —— 用来证明"真的把平面图画出来了"，不是只画了网格。</summary>
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

        /// <summary>刻意做一份坏数据模型：坐标是 NaN / ∞，看绘制会不会被带崩。</summary>
        private static BuildingModelDocument CreateBrokenModel()
        {
            var model = SampleModelFactory.CreateTwoStoreyHouse();
            var storeyId = model.Storeys[0].Id;
            var goodWallId = model.Walls[0].Id;

            model.Walls.Add(new WallModel
            {
                Id = "W-nan", StoreyId = storeyId,
                X1 = double.NaN, Y1 = 0d, X2 = 1e300d, Y2 = 1e300d, Thickness = 240d
            });
            model.Walls.Add(new WallModel
            {
                Id = "W-inf", StoreyId = storeyId,
                X1 = double.PositiveInfinity, Y1 = 0d, X2 = 0d, Y2 = double.NegativeInfinity, Thickness = 240d
            });
            model.Columns.Add(new ColumnModel
            {
                Id = "K-bad", StoreyId = storeyId,
                X = double.NaN, Y = double.PositiveInfinity, Width = 400d, Depth = 400d
            });
            model.Slabs.Add(new SlabModel
            {
                Id = "S-bad", StoreyId = storeyId,
                Outline = new List<PointModel>
                {
                    new PointModel(0d, 0d), new PointModel(double.NaN, 0d), new PointModel(0d, double.NaN)
                }
            });
            model.Openings.Add(new OpeningModel
            {
                Id = "O-bad", HostWallId = goodWallId, Kind = "窗", Code = "C0000",
                Offset = double.NaN, Width = 1500d, Height = 1800d, Sill = 900d
            });
            return model;
        }
    }
}
