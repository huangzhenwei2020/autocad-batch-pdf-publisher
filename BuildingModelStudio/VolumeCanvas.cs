using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>
    /// 三维预览画布（P4 的第一块）：把 <see cref="BuildingVolume"/> 用**自研的轴测投影**画出来。
    ///
    /// 为什么不用 WebView2 + three.js：这里要的就是"看一眼体块关系"，
    /// 自研 GDI+ 轴测图不依赖任何运行时与外部 js（发布体积、离线可用、还能离屏出图核对），
    /// 而且三维体量本来就要算（P4/P5 用），投影逻辑放在 Shared 里可单元测试。
    ///
    /// 交互：左键拖动 = 绕建筑转（方位角/仰角），滚轮 = 缩放，Ctrl+A = 复位适应。
    /// </summary>
    internal sealed class VolumeCanvas : Control
    {
        private readonly VolumeCamera _camera = new VolumeCamera();
        private BuildingVolume _volume;
        private List<VolumeFace2D> _faces = new List<VolumeFace2D>();
        private bool _autoFit = true;
        private bool _dragging;
        private Point _lastMouse;
        private string _lastPaintError;

        public event Action<string> StatusChanged;
        /// <summary>楼层过滤：空 = 全部楼层。</summary>
        public string StoreyId { get; set; }
        /// <summary>只画这一层（勾选"只看当前楼层"）。</summary>
        public bool OnlyCurrentStorey { get; set; }
        /// <summary>当前模型（设置后重算体量）。</summary>
        private BuildingModelDocument _model;

        public VolumeCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(24, 26, 30);
            Cursor = Cursors.Hand;
        }

        internal int LastFaceCount { get; private set; }
        internal int LastCulledCount { get; private set; }
        internal string LastPaintError { get { return _lastPaintError; } }
        internal VolumeCamera Camera { get { return _camera; } }

        /// <summary>设置模型（会重算体量并适应视图）。</summary>
        public void SetModel(BuildingModelDocument model)
        {
            _model = model;
            Rebuild();
            ZoomExtents();
        }

        /// <summary>重算体量（模型改了以后调用）。</summary>
        public void Rebuild()
        {
            _volume = BuildingModelBuilder(_model);
            _faces = VolumeRenderer.Project(_volume, _camera);
            if (_autoFit) { /* 画的时候按视口适应 */ }
            Invalidate();
            RaiseStatus();
        }

        private BuildingVolume BuildingModelBuilder(BuildingModelDocument model)
        {
            return BuildingVolumeBuilder.Build(model, OnlyCurrentStorey ? StoreyId : null);
        }

        public void ZoomExtents()
        {
            _autoFit = true;
            _camera.Zoom = 1d;
            Invalidate();
            RaiseStatus();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_autoFit) Invalidate();
        }

        private void RaiseStatus()
        {
            if (_volume == null)
            {
                StatusChanged?.Invoke("三维预览：还没有模型");
                return;
            }
            StatusChanged?.Invoke("三维轴测　方位 " + Math.Round(_camera.AzimuthDegrees) + "°　仰角 "
                + Math.Round(_camera.ElevationDegrees) + "°　体量 " + Math.Round(_volume.Width) + "×"
                + Math.Round(_volume.Depth) + "×" + Math.Round(_volume.Height) + " mm　面 "
                + _faces.Count + "（剔除背面 " + LastCulledCount + "）　左键拖动旋转 / 滚轮缩放 / Ctrl+A 复位");
        }

        // ───────────────────────── 鼠标 ─────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _lastMouse = e.Location;
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            var dx = e.X - _lastMouse.X;
            var dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;
            _camera.AzimuthDegrees = (_camera.AzimuthDegrees - dx * 0.6d) % 360d;
            _camera.ElevationDegrees = Math.Max(-5d, Math.Min(88d, _camera.ElevationDegrees + dy * 0.5d));
            _faces = VolumeRenderer.Project(_volume, _camera);
            Invalidate();
            RaiseStatus();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _autoFit = false;
            _camera.Zoom = Math.Max(0.2d, Math.Min(6d, _camera.Zoom * (e.Delta > 0 ? 1.12d : 1d / 1.12d)));
            Invalidate();
            RaiseStatus();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.A && (ModifierKeys & Keys.Control) == Keys.Control) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.A) { ZoomExtents(); }
        }

        // ───────────────────────── 绘制 ─────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                Render(e.Graphics);
                _lastPaintError = null;
            }
            catch (Exception exception)
            {
                _lastPaintError = exception.GetType().Name + "：" + exception.Message;
                try { e.Graphics.Clear(BackColor); } catch { }
            }
        }

        internal void Render(Graphics g)
        {
            if (g == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            LastFaceCount = 0;
            LastCulledCount = 0;

            if (_volume == null || _volume.Faces.Count == 0)
            {
                using (var font = new Font("Microsoft YaHei UI", 10f))
                using (var brush = new SolidBrush(Color.FromArgb(150, 156, 166)))
                    g.DrawString("还没有体量：先在「平面草图」里画墙/柱/楼板，再切到这一页看三维。", font, brush, 16, 16);
                return;
            }

            var faces = VolumeRenderer.Project(_volume, _camera);
            _faces = faces;
            LastCulledCount = _volume.Faces.Count - faces.Count;
            if (faces.Count == 0) return;

            double scale, offsetX, offsetY;
            // 相机缩放叠加在"适应视口"的基础上
            VolumeRenderer.FitToView(faces, Width, Height, out scale, out offsetX, out offsetY);
            scale *= Math.Max(0.05d, _camera.Zoom);
            offsetX = Width / 2d - (Width / 2d - offsetX) * Math.Max(0.05d, _camera.Zoom);
            offsetY = Height / 2d - (Height / 2d - offsetY) * Math.Max(0.05d, _camera.Zoom);
            if (!DrawGuard.IsFinite(scale) || scale <= 0d) return;

            using (var edge = new Pen(Color.FromArgb(70, 78, 88), 1f))
            {
                foreach (var face in faces)                     // 已经"从远到近"排好：画家算法消隐
                {
                    var points = new PointF[face.Points.Count];
                    var valid = true;
                    for (var index = 0; index < face.Points.Count; index++)
                    {
                        var x = offsetX + face.Points[index].X * scale;
                        var y = offsetY - face.Points[index].Y * scale;
                        if (!DrawGuard.IsFinite(x) || !DrawGuard.IsFinite(y)) { valid = false; break; }
                        points[index] = new PointF((float)DrawGuard.Clamp(x), (float)DrawGuard.Clamp(y));
                    }
                    if (!valid || points.Length < 3) continue;
                    using (var brush = new SolidBrush(FaceColor(face)))
                        g.FillPolygon(brush, points);
                    g.DrawPolygon(edge, points);
                    LastFaceCount++;
                }
            }

            if (_autoFit) _camera.Zoom = 1d;                    // 适应后把相机缩放归位，滚轮再改
        }

        /// <summary>面颜色：按构件种类给基色，再乘明暗；顶面最亮。</summary>
        internal static Color FaceColor(VolumeFace2D face)
        {
            var baseColor = face.Kind == "slab" ? Color.FromArgb(150, 158, 170)
                : face.Kind == "column" ? Color.FromArgb(126, 134, 146)
                : Color.FromArgb(168, 176, 188);
            var shade = Math.Max(0.25d, Math.Min(1d, face.Shade + (face.IsUp ? 0.12d : 0d)));
            return Color.FromArgb(
                (int)Math.Round(baseColor.R * shade),
                (int)Math.Round(baseColor.G * shade),
                (int)Math.Round(baseColor.B * shade));
        }
    }
}
