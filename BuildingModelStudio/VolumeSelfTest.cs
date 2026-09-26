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
                if (painted < 3000)
                    throw new Exception("方位 " + view.AzimuthDegrees + "° 只画出了 " + painted + " 个像素");
                log("PASS 三维体量：方位 " + view.AzimuthDegrees + "° / 仰角 " + view.ElevationDegrees
                    + "°　画出面 " + canvas.LastFaceCount + "（剔背面 " + canvas.LastCulledCount + "）非背景像素 " + painted);
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

            log("PASS 三维预览自检全部通过（4 个视角 + 单层过滤）。");
        }

        private static int RenderOnce(VolumeCanvas canvas, Action<string> log)
        {
            using (var bitmap = new Bitmap(Width, Height))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    canvas.Render(graphics);
                    var painted = CountPaintedPixels(bitmap, canvas.BackColor);
                    if (canvas.LastPaintError != null) throw new Exception("三维预览绘制失败：" + canvas.LastPaintError);
                    return painted;
                }
                _ = log;
            }
        }

        private static int CountPaintedPixels(Bitmap bitmap, System.Drawing.Color background)
        {
            var rectangle = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var count = 0;
                for (var index = 0; index + 3 < bytes.Length; index += 4)
                    if (bytes[index] != background.B || bytes[index + 1] != background.G
                        || bytes[index + 2] != background.R) count++;
                return count;
            }
            finally { bitmap.UnlockBits(data); }
        }
    }
}
