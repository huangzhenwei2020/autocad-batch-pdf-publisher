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

        /// <summary>
        /// 把一条屏幕线段裁到视口内（Liang-Barsky）。返回 false = 整段都在视口外，不用画。
        ///
        /// 为什么必须裁：GDI+ 画**虚线/点划线**时要按线长生成虚线段。
        /// 缩放到很大时（例如 1px=1e-9mm）轴线可能长到 2e8 像素，GDI+ 会生成上亿段虚线直接卡死
        /// —— 2026-09-26 的"轴网自检卡住"就是这个。裁到视口后线长最多一个屏幕对角线，画起来就是瞬时的。
        /// </summary>
        public static bool ClipLine(ref float x1, ref float y1, ref float x2, ref float y2,
            float left, float top, float right, float bottom)
        {
            if (!IsFinite(x1) || !IsFinite(y1) || !IsFinite(x2) || !IsFinite(y2)) return false;
            if (!IsFinite(left) || !IsFinite(top) || !IsFinite(right) || !IsFinite(bottom)) return false;
            var dx = x2 - x1;
            var dy = y2 - y1;
            var enter = 0d;
            var exit = 1d;
            if (!ClipEdge(-dx, x1 - left, ref enter, ref exit)) return false;
            if (!ClipEdge(dx, right - x1, ref enter, ref exit)) return false;
            if (!ClipEdge(-dy, y1 - top, ref enter, ref exit)) return false;
            if (!ClipEdge(dy, bottom - y1, ref enter, ref exit)) return false;
            if (exit <= enter) return false;
            var startX = x1 + enter * dx;
            var startY = y1 + enter * dy;
            var endX = x1 + exit * dx;
            var endY = y1 + exit * dy;
            x1 = (float)startX; y1 = (float)startY;
            x2 = (float)endX; y2 = (float)endY;
            return true;
        }

        private static bool ClipEdge(double p, double q, ref double enter, ref double exit)
        {
            if (Math.Abs(p) < 1e-12d) return q >= 0d;
            var r = q / p;
            if (p < 0d)
            {
                if (r > exit) return false;
                if (r > enter) enter = r;
            }
            else
            {
                if (r < enter) return false;
                if (r < exit) exit = r;
            }
            return true;
        }

        /// <summary>这个矩形（屏幕坐标）跟视口有交集吗（没交集的多边形/圆可以直接跳过）。</summary>
        public static bool IntersectsViewport(float left, float top, float right, float bottom,
            float viewWidth, float viewHeight, float margin = 8f)
        {
            if (!IsFinite(left) || !IsFinite(top) || !IsFinite(right) || !IsFinite(bottom)) return false;
            if (Math.Min(left, right) > viewWidth + margin) return false;
            if (Math.Max(left, right) < -margin) return false;
            if (Math.Min(top, bottom) > viewHeight + margin) return false;
            if (Math.Max(top, bottom) < -margin) return false;
            return true;
        }
    }
}
