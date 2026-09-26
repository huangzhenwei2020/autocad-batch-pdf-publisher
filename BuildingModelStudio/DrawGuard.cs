using System;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>
    /// 绘制护栏：所有要交给 GDI+ 的屏幕坐标都必须先过这里。
    ///
    /// 为什么单独成文件：2026-09-26 画布崩过一次 —— 网格循环把坐标算成了 ∞ / 天文数字，
    /// GDI+ 的 Graphics.DrawLine 直接抛 System.OverflowException（界面上画布整块红叉）。
    /// 实测边界：1e9 可以、2e9 就抛；±∞ 与 NaN 一律抛。
    /// 所以规则写在一处：**非有限值不许出这个类，屏幕坐标一律夹到 ±1e8**。
    /// 平面画布（PlanCanvas）、轴网（PlanGrid）、立面预览（ViewPreviewCanvas）共用它。
    /// </summary>
    internal static class DrawGuard
    {
        /// <summary>交给 GDI+ 的屏幕坐标上限（实测 1e9 还能画、2e9 就抛）。</summary>
        public const double MaxScreenCoordinate = 1e8d;

        /// <summary>模型坐标的合理上限（超过它说明数据坏了：文件写错或被改成天文数字）。</summary>
        public const double MaxModelCoordinate = 1e9d;

        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>夹取屏幕坐标：NaN 落到 0，超出上限就贴边（屏幕外的构件反正看不见）。</summary>
        public static double Clamp(double screen)
        {
            if (double.IsNaN(screen)) return 0d;
            if (screen > MaxScreenCoordinate) return MaxScreenCoordinate;
            if (screen < -MaxScreenCoordinate) return -MaxScreenCoordinate;
            return screen;
        }

        /// <summary>能安全交给 GDI+ 的模型坐标：有限、而且不超出合理范围。</summary>
        public static bool Sane(double x, double y)
        {
            return IsFinite(x) && IsFinite(y)
                && Math.Abs(x) <= MaxModelCoordinate && Math.Abs(y) <= MaxModelCoordinate;
        }

        /// <summary>视口能不能用：比例必须是有限的正数，偏移必须有限。</summary>
        public static bool ViewportUsable(double scale, double offsetX, double offsetY)
        {
            return IsFinite(scale) && scale > 0d && IsFinite(offsetX) && IsFinite(offsetY);
        }
    }
}
