using System;
using System.Drawing;
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
            Load += (sender, args) => { UiPerformance.BufferedTree(this); ApplyScreenBounds(); };
            Shown += (sender, args) => ApplyScreenBounds();
            DpiChanged += (sender, args) => BeginInvoke(new Action(ApplyScreenBounds));
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (Font == null || Font.Size < 8F) Font = new Font("Microsoft YaHei UI", 9F);
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
