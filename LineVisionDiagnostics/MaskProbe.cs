using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace Wanluo.LineVision.Diagnostics
{
    /// <summary>单独检查二值化：亮度直方图、Otsu 阈值、反转判据是否触发。</summary>
    internal static class MaskProbe
    {
        public static void Run(string path, int requestedThreshold)
        {
            using (var source = new Bitmap(path))
            {
                var width = source.Width; var height = source.Height;
                var total = width * height;
                var histogram = new int[256];
                var data = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    unsafe
                    {
                        var start = (byte*)data.Scan0;
                        for (var y = 0; y < height; y++)
                        {
                            var row = start + y * data.Stride;
                            for (var x = 0; x < width; x++)
                            {
                                var value = (row[x * 3] * 29 + row[x * 3 + 1] * 150 + row[x * 3 + 2] * 77) >> 8;
                                histogram[value]++;
                            }
                        }
                    }
                }
                finally { source.UnlockBits(data); }

                Console.WriteLine("尺寸 " + width + "x" + height + "  总像素 " + total.ToString("N0", CultureInfo.InvariantCulture));
                Console.WriteLine();
                Console.WriteLine("亮度累计分布：");
                var running = 0;
                foreach (var probe in new[] { 0, 32, 64, 96, 128, 160, 192, 224, 240, 250, 254, 255 })
                {
                    // 逐档累计：0 档只含灰度 0，之后每档加上 (上一档, 本档] 的计数。
                    var from = probe == 0 ? 0 : 1;
                    running = 0;
                    for (var value = 0; value <= probe; value++) running += histogram[value];
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  灰度<={0,-3} 累计 {1,9} ({2,6:0.00}%)  本档 {3}",
                        probe, running, 100d * running / total, histogram[probe]));
                }
                long weighted = 0;
                for (var i = 0; i < 256; i++) weighted += (long)i * histogram[i];
                Console.WriteLine();
                Console.WriteLine("平均亮度 = " + (weighted / (double)total).ToString("0.0", CultureInfo.InvariantCulture)
                    + "（0=全黑, 255=全白）");
                Console.WriteLine("纯黑(0)像素 " + histogram[0].ToString("N0", CultureInfo.InvariantCulture)
                    + "，纯白(255)像素 " + histogram[255].ToString("N0", CultureInfo.InvariantCulture));

                // 复算 Otsu（与 BuildBinary 中同一套算法）
                long sum = 0;
                for (var i = 0; i < 256; i++) sum += (long)i * histogram[i];
                long sumBackground = 0; var background = 0; var best = 127; var maximum = -1d;
                for (var threshold = 0; threshold < 255; threshold++)
                {
                    background += histogram[threshold];
                    if (background == 0) continue;
                    var foreground = total - background;
                    if (foreground == 0) break;
                    sumBackground += (long)threshold * histogram[threshold];
                    var meanBackground = sumBackground / (double)background;
                    var meanForeground = (sum - sumBackground) / (double)foreground;
                    var score = (double)background * foreground * (meanBackground - meanForeground) * (meanBackground - meanForeground);
                    if (score > maximum) { maximum = score; best = threshold; }
                }
                var otsu = Math.Max(20, Math.Min(235, best));
                var darkAtOtsu = 0;
                for (var i = 0; i <= otsu; i++) darkAtOtsu += histogram[i];
                Console.WriteLine();
                Console.WriteLine("Otsu 原始值 = " + best + "，钳制后 = " + otsu);
                Console.WriteLine("  Otsu 阈值下暗像素 = " + darkAtOtsu.ToString("N0", CultureInfo.InvariantCulture)
                    + " (" + (100d * darkAtOtsu / total).ToString("0.00", CultureInfo.InvariantCulture) + "%)");
                Console.WriteLine("  反转判据 暗像素 > 65% 触发? " + (darkAtOtsu > total * 0.65d ? "是 ← 掩膜会被整体翻转" : "否"));
                Console.WriteLine();

                foreach (var threshold in new[] { 0, 100, 128, 150, 160, 180, 200, 220 })
                {
                    var effective = threshold > 0 ? Math.Max(1, Math.Min(254, threshold)) : otsu;
                    var count = 0;
                    for (var i = 0; i <= effective; i++) count += histogram[i];
                    var used = count > total * 0.65d ? total - count : count;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  指定阈值 {0,-4} 实际用 {1,-4} 暗像素 {2,9} ({3,6:0.00}%) 反转?{4} 最终墨迹 {5,9}",
                        threshold, effective, count, 100d * count / total,
                        count > total * 0.65d ? "是" : "否", used));
                }
            }
        }
    }
}
