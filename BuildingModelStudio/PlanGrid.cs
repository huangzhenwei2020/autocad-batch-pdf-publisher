using System;
using System.Collections.Generic;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>屏幕网格线：位置已经换算成像素，画的时候不需要再算。</summary>
    internal struct PlanGridLine
    {
        /// <summary>true = 竖线（X 方向）。</summary>
        public bool Vertical;
        /// <summary>true = 亮线（每 5 条一根）。</summary>
        public bool Major;
        /// <summary>屏幕坐标（竖线取 X，横线取 Y）。</summary>
        public float Screen;
    }

    /// <summary>
    /// 平面画布的轴网计算（纯逻辑，不碰 GDI+，可单独自检）。
    ///
    /// 这个类只为一件事存在：**永不产生无限循环，也永不把 ∞ / NaN 交给 GDI+**。
    ///
    /// 2026-09-26 的崩溃就出在这里。原来的写法是：
    ///     for (var y = 0d; ToScreen(0, y).Y &lt; Height + 200; y += step)
    /// 屏幕 Y 随模型 Y **增大而减小**，所以这个条件从第一笔起就永远成立，
    /// 循环一路把 y 加到坐标变成 ±∞ 再传给 Graphics.DrawLine，
    /// GDI+ 抛 System.OverflowException，画布区域整块画不出来（界面上是个红叉）。
    ///
    /// 现在把可见范围一次性反算出来，再按步长取整点，循环次数有硬上限，
    /// 任何非有限输入直接返回空表 —— 缩放到极端、偏移到天边、比例变 NaN 都只会"不画网格"。
    /// </summary>
    internal static class PlanGrid
    {
        /// <summary>基准网格 1000mm。</summary>
        public const double BaseStep = 1000d;
        /// <summary>每 5 条画一根亮线。</summary>
        public const double MajorEvery = 5d;
        /// <summary>两条网格线在屏幕上的最小间距（像素），太密就放大步长。</summary>
        public const double MinScreenGap = 12d;
        /// <summary>单轴网格线数量硬上限（安全阀）。</summary>
        public const int MaxLinesPerAxis = 400;
        /// <summary>步长放大次数上限（5^64 早就超出 double 有效范围，纯粹防死循环）。</summary>
        private const int MaxStepGrowth = 64;
        /// <summary>交给 GDI+ 的屏幕坐标绝对值上限。</summary>
        public const float MaxScreenCoordinate = 1e8f;

        /// <summary>
        /// 算出当前视图下要画的网格线。任何输入异常（非有限、比例为 0）都返回空表。
        /// </summary>
        public static List<PlanGridLine> Compute(double width, double height, double scale, double offsetX, double offsetY)
        {
            var lines = new List<PlanGridLine>();
            if (!IsFinite(width) || !IsFinite(height) || width <= 0d || height <= 0d) return lines;
            if (!IsFinite(scale) || scale <= 0d) return lines;
            if (!IsFinite(offsetX) || !IsFinite(offsetY)) return lines;

            var step = BaseStep;
            var growth = 0;
            while (step * scale < MinScreenGap && growth++ < MaxStepGrowth) step *= MajorEvery;
            if (!IsFinite(step) || step <= 0d) return lines;

            // 可见区域反算成模型坐标（屏幕左边/右边、下边/上边）
            var left = (0d - offsetX) / scale;
            var right = (width - offsetX) / scale;
            var bottom = (offsetY - height) / scale;
            var top = offsetY / scale;
            if (!IsFinite(left) || !IsFinite(right) || !IsFinite(bottom) || !IsFinite(top)) return lines;

            // 可见范围大得离谱时（比例异常小）继续放大步长，而不是靠循环次数硬扛
            growth = 0;
            while ((right - left) / step > MaxLinesPerAxis && growth++ < MaxStepGrowth) step *= MajorEvery;
            if (!IsFinite(step) || step <= 0d) return lines;

            AddAxis(lines, true, left, right, step, scale, offsetX);
            AddAxis(lines, false, bottom, top, step, scale, offsetY);
            return lines;
        }

        /// <summary>把一段模型范围上的整步长点变成屏幕网格线。</summary>
        private static void AddAxis(List<PlanGridLine> lines, bool vertical, double min, double max,
            double step, double scale, double offset)
        {
            if (!IsFinite(min) || !IsFinite(max) || max < min) return;
            var first = Math.Ceiling(min / step) * step;
            if (!IsFinite(first)) return;
            var majorStep = step * MajorEvery;

            var count = 0;
            var value = first;
            while (value <= max)
            {
                if (count++ >= MaxLinesPerAxis) break;

                var screen = vertical ? offset + value * scale : offset - value * scale;
                if (!IsFinite(screen)) break;
                if (Math.Abs(screen) > MaxScreenCoordinate) continue;

                lines.Add(new PlanGridLine
                {
                    Vertical = vertical,
                    Screen = (float)screen,
                    Major = IsFinite(majorStep) && Math.Abs(Math.IEEERemainder(value, majorStep)) < 0.01d
                });

                // 偏移被搞成天文数字时，value + step 可能"加不动"（浮点精度不够），
                // 这时必须停下来，否则就是一堆重复线 / 死循环。
                var next = value + step;
                if (!IsFinite(next) || next <= value) break;
                value = next;
            }
        }

        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
