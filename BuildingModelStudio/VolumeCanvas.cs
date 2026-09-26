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
        private BuildingVolume _projectedVolume;
        private VolumeCamera _projectedCamera;
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
        internal int ProjectionCount { get; private set; }
        internal string LastPaintError { get { return _lastPaintError; } }
        internal VolumeCamera Camera { get { return _camera; } }
        /// <summary>调试用：只画这些种类的面（空 = 全画）。</summary>
        internal HashSet<string> KindFilter { get; set; }

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
            ProjectIfNeeded();
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

        /// <summary>透视开关（默认轴测：平行投影，与建筑制图观感一致）。</summary>
        internal void SetPerspective(bool perspective)
        {
            _camera.Perspective = perspective;
            _autoFit = true;
            _camera.Zoom = 1d;
            Invalidate();
            RaiseStatus();
        }

        /// <summary>水平剖切：clipZ &lt;= 0 或 keepAbove 都不勾时关掉；否则只留剖切面一侧。</summary>
        internal void SetClip(double clipZ, bool enabled)
        {
            _camera.ClipZ = enabled ? Math.Max(0d, clipZ) : 0d;
            Invalidate();
            RaiseStatus();
        }

        internal bool PerspectiveOn { get { return _camera.Perspective; } }
        internal double ClipZValue { get { return _camera.ClipZ; } }

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
                + _faces.Count + "（剔除背面 " + LastCulledCount + "）"
                + (_camera.Perspective ? "　透视" : "　轴测")
                + (_camera.ClipZ > 0.5d ? "　剖切 " + Math.Round(_camera.ClipZ) + " mm" : string.Empty)
                + "　左键拖动旋转 / 滚轮缩放 / Ctrl+A 复位");
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

            var faces = ProjectIfNeeded();
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
            using (var glassEdge = new Pen(Color.FromArgb(150, 190, 214), 1f))
            {
                foreach (var face in faces)                     // 已经"从远到近"排好：画家算法消隐
                {
                    if (KindFilter != null && KindFilter.Count > 0 && !KindFilter.Contains(face.Kind ?? string.Empty)) continue;
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
                    // 共面合并：与邻面贴在一起的那段边不画（一排墙段看上去就是一整片墙）
                    var pen = face.Kind == "glass" ? glassEdge : edge;
                    foreach (var segment in face.Edges)
                    {
                        if (segment == null || segment.Count < 2) continue;
                        var x1 = offsetX + segment[0].X * scale;
                        var y1 = offsetY - segment[0].Y * scale;
                        var x2 = offsetX + segment[1].X * scale;
                        var y2 = offsetY - segment[1].Y * scale;
                        if (!DrawGuard.IsFinite(x1) || !DrawGuard.IsFinite(y1)
                            || !DrawGuard.IsFinite(x2) || !DrawGuard.IsFinite(y2)) continue;
                        g.DrawLine(pen, (float)DrawGuard.Clamp(x1), (float)DrawGuard.Clamp(y1),
                            (float)DrawGuard.Clamp(x2), (float)DrawGuard.Clamp(y2));
                    }
                    LastFaceCount++;
                }
            }

            if (_autoFit) _camera.Zoom = 1d;                    // 适应后把相机缩放归位，滚轮再改
        }

        private List<VolumeFace2D> ProjectIfNeeded()
        {
            if (!ReferenceEquals(_volume, _projectedVolume) || _projectedCamera == null
                || _projectedCamera.AzimuthDegrees != _camera.AzimuthDegrees
                || _projectedCamera.ElevationDegrees != _camera.ElevationDegrees
                || _projectedCamera.Perspective != _camera.Perspective
                || _projectedCamera.FieldOfViewDegrees != _camera.FieldOfViewDegrees
                || _projectedCamera.ClipZ != _camera.ClipZ
                || _projectedCamera.ClipKeepAbove != _camera.ClipKeepAbove)
            {
                _faces = VolumeRenderer.Project(_volume, _camera);
                _projectedVolume = _volume;
                _projectedCamera = _camera.Clone();
                ProjectionCount++;
            }
            return _faces;
        }

        /// <summary>
        /// 面颜色：按构件种类给基色，再乘明暗；顶面最亮。
        /// 门窗是"补一层"的重点：玻璃半透明（能看见后面的墙/家具），门扇偏暖色，窗框最深，
        /// 这样一眼就能分清哪儿是窗、哪儿是门。
        /// </summary>
        internal static Color FaceColor(VolumeFace2D face)
        {
            var kind = face.Kind ?? string.Empty;
            var shade = Math.Max(0.25d, Math.Min(1d, face.Shade + (face.IsUp ? 0.12d : 0d)));
            if (kind == "glass")
            {
                // 半透明淡蓝：画家算法从远到近画，先画的墙会透出来
                var alpha = (int)Math.Round(70d + 70d * shade);
                return Color.FromArgb(Math.Max(40, Math.Min(170, alpha)),
                    (int)Math.Round(176d * shade + 30d), (int)Math.Round(212d * shade + 30d), 236);
            }
            var baseColor = kind == "slab" ? Color.FromArgb(150, 158, 170)
                : kind == "column" ? Color.FromArgb(126, 134, 146)
                : kind == "frame" ? Color.FromArgb(96, 104, 116)
                : kind == "door" ? Color.FromArgb(176, 138, 96)
                : kind == "stair" ? Color.FromArgb(158, 160, 166)
                : kind == "roof" ? Color.FromArgb(186, 118, 92)
                : Color.FromArgb(168, 176, 188);
            return Color.FromArgb(
                (int)Math.Round(baseColor.R * shade),
                (int)Math.Round(baseColor.G * shade),
                (int)Math.Round(baseColor.B * shade));
        }
    }
}
