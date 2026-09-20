using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    /// <summary>
    /// AutoCAD 内所有 WinForms 子窗口的统一 DPI 基类。AutoCAD 决定进程 DPI 模式，
    /// 本基类负责按当前显示器 DPI 缩放控件，并保证窗口不会超出工作区。
    /// </summary>
    public class DpiAwareForm : Form
    {
        private bool _screenBoundsApplied;

        protected DpiAwareForm()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            SizeGripStyle = SizeGripStyle.Show;
            // 这些窗口都是"一个 Form 挂一堆 Dock 子控件"，默认不双缓冲时拖动边框改大小、
            // 拉动表格滚动条都会一闪一闪、感觉发滞。窗口自己开一次，Load 时再把整棵
            // 控件树都开一遍（表格是最吃重绘的），插件里所有窗口一起受益。
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Load += (sender, args) => { UiPerformance.BufferedTree(this); ApplyNativeDarkTheme(this); ApplyScreenBounds(); };
            Shown += (sender, args) => ApplyScreenBounds();
            DpiChanged += (sender, args) => BeginInvoke(new Action(ApplyScreenBounds));
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (Font == null || Font.Size < 8F) Font = new Font("Microsoft YaHei UI", 9F);
        }

        /// <summary>
        /// 句柄创建**之前**给整棵控件树开双缓冲。
        ///
        /// 时机很关键：DataGridView 这类自绘控件随时设 DoubleBuffered 都有效，但
        /// ListView / TreeView 是原生控件，要靠 WinForms 在 OnHandleCreated 里把
        /// LVS_EX_DOUBLEBUFFER / TVS_EX_DOUBLEBUFFER 风格的位设上去——句柄建好之后再设
        /// 就不生效了。所以在 CreateHandle 里先设、再 base。
        /// （Load 时还会再走一遍，兜住那些运行时才加进来的控件。）
        /// </summary>
        protected override void CreateHandle()
        {
            UiPerformance.BufferedTree(this);
            base.CreateHandle();
            ApplyTitleBarTheme();
        }

        private void ApplyTitleBarTheme()
        {
            // Windows 10 1809 uses attribute 19; current Windows uses 20.
            // The call is ignored on older systems.  Applying it once when the
            // handle is created has no resize/scroll performance cost.
            if (BackColor.GetBrightness() >= 0.42F) return;
            var enabled = 1;
            try
            {
                if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string subAppName, string subIdList);

        private static void ApplyNativeDarkTheme(Control root)
        {
            if (root == null || root.IsDisposed || root.FindForm() == null || root.FindForm().BackColor.GetBrightness() >= 0.42F) return;
            foreach (Control child in root.Controls)
            {
                if (child.IsHandleCreated &&
                    (child is ListBox || child is ComboBox || child is DataGridView ||
                     (child is ScrollableControl && ((ScrollableControl)child).AutoScroll)))
                {
                    try { SetWindowTheme(child.Handle, "DarkMode_Explorer", null); }
                    catch (DllNotFoundException) { }
                    catch (EntryPointNotFoundException) { }
                }
                if (child.HasChildren) ApplyNativeDarkTheme(child);
            }
        }

        private void ApplyScreenBounds()
        {
            if (IsDisposed || !IsHandleCreated) return;
            var working = Screen.FromControl(this).WorkingArea;
            var margin = Math.Max(12, DeviceDpi / 8);
            var maximumWidth = Math.Max(320, working.Width - margin * 2);
            var maximumHeight = Math.Max(240, working.Height - margin * 2);

            // 设计尺寸经过 AutoScale 后可能超过小屏幕；仅向下收缩窗口外框，
            // 内部可伸缩布局继续由 Dock/Anchor/TableLayoutPanel 负责。
            if (Width > maximumWidth) Width = maximumWidth;
            if (Height > maximumHeight) Height = maximumHeight;

            var minimumWidth = Math.Min(MinimumSize.Width, maximumWidth);
            var minimumHeight = Math.Min(MinimumSize.Height, maximumHeight);
            if (MinimumSize.Width != minimumWidth || MinimumSize.Height != minimumHeight)
                MinimumSize = new Size(minimumWidth, minimumHeight);

            if (!_screenBoundsApplied || !working.Contains(Bounds))
            {
                Left = Math.Max(working.Left + margin, Math.Min(Left, working.Right - Width - margin));
                Top = Math.Max(working.Top + margin, Math.Min(Top, working.Bottom - Height - margin));
            }
            _screenBoundsApplied = true;
        }
    }
}
