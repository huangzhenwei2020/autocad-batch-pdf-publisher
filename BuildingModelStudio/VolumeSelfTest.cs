using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>
    /// 三维预览自检：把 <see cref="BuildingVolume"/> 用轴测投影真正画到离屏位图上。
    ///
    /// 为什么要自检：三维预览是"体量对不对"的唯一眼睛，
    /// 它悄悄画不出东西（或者抛异常）比画错更糟 —— 用户会以为模型是空的。
    /// 所以这里要求：每个视角都要画出足够多的面与像素，且标注为"被剔除的背面"数量要合理。
    /// </summary>
    internal static class VolumeSelfTest
    {
        private const int Width = 900;
        private const int Height = 640;

        public static void Run(Action<string> log)
        {
            if (log == null) log = text => { };
            var model = SampleModelFactory.CreateTwoStoreyHouse();
            var canvas = new VolumeCanvas { Size = new Size(Width, Height) };
            canvas.SetModel(model);

            var volume = BuildingVolumeBuilder.Build(model, null);
            if (volume.Faces.Count == 0) throw new Exception("样例模型应该能算出体量面。");
            if (Math.Abs(volume.Width - 7440d) > 1d) throw new Exception("体量宽应为 7440，实际 " + volume.Width);
            if (Math.Abs(volume.Depth - 5640d) > 1d) throw new Exception("体量深应为 5640，实际 " + volume.Depth);
            if (Math.Abs(volume.Height - 6900d) > 1d) throw new Exception("体量高应为 6900，实际 " + volume.Height);

            foreach (var view in new[]
            {
                new VolumeCamera { AzimuthDegrees = 35d, ElevationDegrees = 28d },
                new VolumeCamera { AzimuthDegrees = 0d, ElevationDegrees = 0d },     // 正南平视
                new VolumeCamera { AzimuthDegrees = 135d, ElevationDegrees = 60d },  // 俯视
                new VolumeCamera { AzimuthDegrees = 250d, ElevationDegrees = 5d }    // 另一个方向、接近平视
            })
            {
                canvas.Camera.AzimuthDegrees = view.AzimuthDegrees;
                canvas.Camera.ElevationDegrees = view.ElevationDegrees;
                canvas.Camera.Zoom = 1d;
                var painted = RenderOnce(canvas, log);
                if (canvas.LastFaceCount < 10)
                    throw new Exception("方位 " + view.AzimuthDegrees + "° 只画了 " + canvas.LastFaceCount + " 个面");
                if (painted.Painted < 3000)
                    throw new Exception("方位 " + view.AzimuthDegrees + "° 只画出了 " + painted.Painted + " 个像素");
                // 轮廓里面不该有"漏底"的背景洞：那说明有面被误丢了（比画错更糟）。
                // 允许千分之一以内的零星像素：相邻两块之间的抗锯齿缝会留几个半透明点，不是洞。
                if (painted.Holes > painted.Painted / 1000)
                    throw new Exception("方位 " + view.AzimuthDegrees + "° 轮廓里漏了 " + painted.Holes + " 个背景像素");
                log("PASS 三维体量：方位 " + view.AzimuthDegrees + "° / 仰角 " + view.ElevationDegrees
                    + "°　画出面 " + canvas.LastFaceCount + "（隐藏面 " + canvas.LastCulledCount + "）非背景像素 "
                    + painted.Painted + "、漏底 " + painted.Holes);
            }

            // 只看一层：体量应该明显变小（高度只剩一层）
            canvas.OnlyCurrentStorey = true;
            canvas.StoreyId = model.Storeys[0].Id;
            canvas.SetModel(model);
            var firstFloor = BuildingVolumeBuilder.Build(model, model.Storeys[0].Id);
            if (Math.Abs(firstFloor.Height - 3600d) > 1d)
                throw new Exception("只看一层时体量高应为 3600，实际 " + firstFloor.Height);
            if (firstFloor.Faces.Count >= volume.Faces.Count)
                throw new Exception("只看一层时的面数应少于整栋");
            canvas.OnlyCurrentStorey = false;
            canvas.SetModel(model);
            log("PASS 三维体量：只看一层时高 3600、面 " + firstFloor.Faces.Count + " 个（整栋 " + volume.Faces.Count + " 个）");

            // 门窗在三维里要"一眼看出来"：玻璃偏蓝、门扇偏暖色，都要真的画到屏幕上
            canvas.Camera.AzimuthDegrees = 35d;
            canvas.Camera.ElevationDegrees = 28d;
            canvas.Camera.Zoom = 1d;
            var colors = RenderOnce(canvas, log);
            if (colors.Bluish < 200) throw new Exception("玻璃应该画出明显偏蓝的像素，实际 " + colors.Bluish);
            if (colors.Warm < 200) throw new Exception("门扇应该画出明显偏暖的像素，实际 " + colors.Warm);
            log("PASS 三维门窗：玻璃（偏蓝）" + colors.Bluish + " 像素、门扇（偏暖）" + colors.Warm
                + " 像素，面 " + canvas.LastFaceCount + " 个");

            // 透视：同一视角换透视也要画得出来（近大远小）
            canvas.SetPerspective(true);
            var perspective = RenderOnce(canvas, log);
            if (canvas.LastFaceCount < 10 || perspective.Painted < 3000)
                throw new Exception("透视下画得太少：面 " + canvas.LastFaceCount + "、像素 " + perspective.Painted);
            log("PASS 三维透视：面 " + canvas.LastFaceCount + " 个、非背景像素 " + perspective.Painted);

            // 剖切：切掉 1500 以上以后，画出来的东西应更"扁"（投影高度/宽度变小）
            var beforeCut = RenderOnce(canvas, log);
            canvas.SetClip(1500d, true);
            var afterCut = RenderOnce(canvas, log);
            if (canvas.LastFaceCount < 10 || afterCut.Painted < 1500)
                throw new Exception("剖切后画得太少：面 " + canvas.LastFaceCount + "、像素 " + afterCut.Painted);
            var beforeRatio = (double)beforeCut.Height / Math.Max(1, beforeCut.Width);
            var afterRatio = (double)afterCut.Height / Math.Max(1, afterCut.Width);
            if (afterRatio >= beforeRatio)
                throw new Exception("剖切后应更扁平：高宽比 " + Math.Round(afterRatio, 3)
                    + " 应小于 " + Math.Round(beforeRatio, 3));
            log("PASS 三维剖切：切到 1500 mm 以后高宽比 " + Math.Round(beforeRatio, 3)
                + " → " + Math.Round(afterRatio, 3) + "，面 " + beforeCut.Faces + " → " + afterCut.Faces);
            canvas.SetClip(0d, false);

            log("PASS 三维预览自检全部通过（4 个视角 + 单层过滤 + 门窗着色 + 透视 + 剖切）。");
        }

        private static Stats RenderOnce(VolumeCanvas canvas, Action<string> log)
        {
            _ = log;
            using (var bitmap = new Bitmap(Width, Height))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    canvas.Render(graphics);
                    var stats = Measure(bitmap, canvas.BackColor);
                    stats.Faces = canvas.LastFaceCount;
                    if (canvas.LastPaintError != null) throw new Exception("三维预览绘制失败：" + canvas.LastPaintError);
                    return stats;
                }
            }
        }

        /// <summary>一次绘制的结果：画了多少像素、落在哪一块、门窗颜色的像素数、轮廓里漏没漏底。</summary>
        internal sealed class Stats
        {
            public int Painted;
            public int Faces;
            public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue;
            public int Bluish;
            public int Warm;
            /// <summary>轮廓内部夹着的背景像素（"漏底"）——正常应为 0。</summary>
            public int Holes;
            public int Width { get { return MaxX < MinX ? 0 : MaxX - MinX + 1; } }
            public int Height { get { return MaxY < MinY ? 0 : MaxY - MinY + 1; } }
        }

        private static Stats Measure(Bitmap bitmap, System.Drawing.Color background)
        {
            var stats = new Stats();
            var rectangle = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var stride = Math.Abs(data.Stride);
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var first = -1;
                    var last = -1;
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var index = y * stride + x * 4;
                        if (index + 2 >= bytes.Length) continue;
                        int blue = bytes[index], green = bytes[index + 1], red = bytes[index + 2];
                        if (blue == background.B && green == background.G && red == background.R) continue;
                        stats.Painted++;
                        if (first < 0) first = x;
                        last = x;
                        if (x < stats.MinX) stats.MinX = x;
                        if (x > stats.MaxX) stats.MaxX = x;
                        if (y < stats.MinY) stats.MinY = y;
                        if (y > stats.MaxY) stats.MaxY = y;
                        if (blue - red > 60) stats.Bluish++;              // 半透明玻璃（墙是灰蓝，差值只有 ~12）
                        else if (red - blue > 30) stats.Warm++;            // 门扇（暖木色）
                    }
                    // 这一行里"两边都画过、中间却空着"的背景像素 = 漏底（左右两端外面的背景不算）
                    for (var x = first + 1; x >= 0 && x < last; x++)
                    {
                        var index = y * stride + x * 4;
                        if (index + 2 >= bytes.Length) continue;
                        if (bytes[index] == background.B && bytes[index + 1] == background.G
                            && bytes[index + 2] == background.R) stats.Holes++;
                    }
                }
                return stats;
            }
            finally { bitmap.UnlockBits(data); }
        }
    }
}
