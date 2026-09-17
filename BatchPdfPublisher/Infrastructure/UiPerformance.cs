using System;
using System.Reflection;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    /// <summary>
    /// WinForms 重绘性能的统一开关。
    ///
    /// 背景：这些窗口是"一个 Form 上挂一堆 Dock/Anchor 的子控件 + 若干个 DataGridView"，
    /// 而 WinForms 默认**不开双缓冲**。表现就是拖动窗口边框改大小、或拉动表格滚动条时
    /// 画面一闪一闪、感觉发滞——因为每一次重绘都是直接画到屏幕上，先擦背景再画内容，
    /// 中间状态被看见。数据量越大、自定义绘制越多，越明显。
    ///
    /// `Control.DoubleBuffered` 是 protected 的，普通控件从外部改不了，所以这里用反射
    /// 打开它。这是 WinForms 里的标准做法；只在本窗口构建时调用一次，不在绘制热路径上。
    /// DataGridView 另有 <see cref="BufferedDataGridView"/> 直接继承，不用反射。
    /// </summary>
    internal static class UiPerformance
    {
        private static readonly PropertyInfo DoubleBufferedProperty =
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>给任意控件打开双缓冲；失败就算了，只是慢一点，不该影响功能。</summary>
        public static T Buffered<T>(this T control) where T : Control
        {
            if (control == null || DoubleBufferedProperty == null) return control;
            try { DoubleBufferedProperty.SetValue(control, true, null); }
            catch { }
            return control;
        }

        /// <summary>
        /// 递归给窗口里所有控件打开双缓冲。
        ///
        /// 在窗口 Load 时统一走一遍，而不是要求每个窗口自己在 new 表格时记得开——
        /// 插件里有近三十个窗口、十几处 DataGridView，靠人记必然有漏的。
        /// </summary>
        public static void BufferedTree(Control root)
        {
            if (root == null) return;
            try
            {
                foreach (Control child in root.Controls)
                {
                    child.Buffered();
                    if (child.Controls.Count > 0) BufferedTree(child);
                }
            }
            catch { }
        }
    }
}
