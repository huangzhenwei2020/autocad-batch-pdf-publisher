using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using BatchPdfPublisher.Views;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Services
{
    internal sealed class DimensionEditService : IDisposable
    {
        private static DimensionEditService _current;
        private readonly Document _document;
        private readonly DimensionEditPanel _panel;
        private ObjectId _seed;
        private CadDimensionChain _chain;
        private string _queued;
        private Action _pendingWrite;
        private bool _continueAfterWrite;
        private bool _busy, _disposed;
        private int _gripNode = -1;
        private double _markerWidth;
        private readonly List<Entity> _markers = new List<Entity>();
        private readonly IntegerCollection _viewports = new IntegerCollection();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr parent, IntPtr child);
        [System.Runtime.InteropServices.DllImport("accore.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = "?AcadGetCurrentDwgView@@YAPEAUHWND__@@XZ")]
        private static extern IntPtr CurrentDrawingView();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativePoint { internal int X, Y; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }
        internal static void Close() { if (_current != null) _current.Dispose(); }
        private DimensionEditService(Document doc)
        {
            _document = doc; _panel = new DimensionEditPanel(Queue);
            _panel.FormClosed += (s, e) => Dispose();
            _document.Editor.PointMonitor += Monitor;
            _document.CommandEnded += CommandFinished;
            _document.CommandCancelled += CommandFinished;
            _document.CommandFailed += CommandFinished;
            Application.PreTranslateMessage += Message;
            Application.DocumentManager.DocumentToBeDestroyed += Closing;
            Application.DocumentManager.DocumentActivated += Activated;
        }
        internal static void Show(Document doc)
        {
            if (doc == null) return;
            if (_current != null && !_current._disposed && ReferenceEquals(_current._document, doc)) { _current._panel.Show(); _current._panel.Activate(); return; }
            if (_current != null) _current.Dispose();
            _current = new DimensionEditService(doc);
            _current._panel.Status.Text = "请选择一串尺寸，或新建。\n在当前 CAD 图形里直接操作。";
            Application.ShowModelessDialog(_current._panel);
            try
            {
                var picked = doc.Editor.SelectImplied();
                if (picked.Status == PromptStatus.OK && picked.Value.Count == 1)
                {
                    var id = picked.Value.GetObjectIds()[0];
                    _current._chain = CadDimensionChain.Load(doc.Database, id); _current._seed = id; _current.SafeRefresh(true);
                }
            }
            catch (Exception ex) { _current._panel.Status.Text = "请点“选择尺寸串”：" + ex.Message; }
        }
        private bool Active { get { return !_disposed && ReferenceEquals(Application.DocumentManager.MdiActiveDocument, _document); } }
        private void Queue(string action)
        {
            if (!Active) { _panel.Status.Text = "已暂停：请切回来源 CAD 图纸。"; return; }
            if (_busy || _queued != null || _pendingWrite != null || !string.IsNullOrEmpty(_document.CommandInProgress)) { _panel.Status.Text = "请先完成 CAD 当前操作；不会中断或发送 Esc。"; return; }
            _queued = action; _document.SendStringToExecute("WLDIMACT ", false, false, false);
        }
        internal static void RunQueued()
        {
            var service = _current;
            if (service == null || !service.Active || service._queued == null) return;
            var action = service._queued; service._queued = null; service.Run(action);
        }
        private void QueueWrite(Action write, bool continueAdding)
        {
            _pendingWrite = write; _continueAfterWrite = continueAdding;
            _document.SendStringToExecute("WLDIMCOMMIT ", false, false, false);
        }
        internal static void RunPendingWrite()
        {
            var service = _current;
            if (service == null || !service.Active || service._pendingWrite == null) return;
            var write = service._pendingWrite; var more = service._continueAfterWrite;
            service._pendingWrite = null; service._continueAfterWrite = false; service._busy = true;
            try
            {
                write();
                if (more) { service._queued = "add"; service._document.SendStringToExecute("WLDIMACT ", false, false, false); }
            }
            catch (Exception ex) { service._document.Editor.WriteMessage("\n尺寸未修改：" + ex.Message); service._panel.Status.Text = ex.Message; }
            finally { service._busy = false; if (!service._disposed) service.SafeRefresh(true); }
        }
        private void Run(string action)
        {
            if (!Active) return;
            _busy = true; ClearMarkers();
            try
            {
                if (action == "select") { Select(); return; }
                if (action == "new") { New(); return; }
                if (_seed.IsNull || !_seed.IsValid || _seed.IsErased) { Select(); if (_chain == null) return; }
                _chain = CadDimensionChain.Load(_document.Database, _seed);
                if (action == "add")
                {
                    if (Active && !_seed.IsNull)
                    {
                        var result = _document.Editor.GetPoint("\n捕捉增补界点（串内拆分，串外延长；Esc 完成）：");
                        if (result.Status != PromptStatus.OK) return;
                        var t = _chain.T(result.Value.TransformBy(_document.Editor.CurrentUserCoordinateSystem));
                        if (_chain.Stations.Any(x => Math.Abs(x - t) <= _chain.Epsilon))
                        {
                            _document.Editor.WriteMessage("\n该界点已存在，没有新增零尺寸。");
                            _queued = "add"; _document.SendStringToExecute("WLDIMACT ", false, false, false);
                        }
                        else Commit("add", t, 0);
                    }
                }
                else if (action == "line") Drag(false, -1);
                else if (action == "node")
                {
                    var index = _gripNode; _gripNode = -1;
                    if (index < 0) { var result = _document.Editor.GetPoint("\n选择要移动的尺寸界点："); if (result.Status != PromptStatus.OK) return; index = DimensionChainGeometry.Nearest(_chain.Stations, _chain.T(result.Value.TransformBy(_document.Editor.CurrentUserCoordinateSystem))); }
                    Drag(true, index);
                }
                else if (action == "length" || action == "delete")
                {
                    var pick = _document.Editor.GetEntity("\n选择当前尺寸串中的一段："); if (pick.Status != PromptStatus.OK) return;
                    var index = _chain.Segments.FindIndex(s => s.Id == pick.ObjectId); if (index < 0) throw new ArgumentException("请选当前高亮串内的一段。");
                    if (action == "delete") Commit("delete", 0, index);
                    else
                    {
                        var segment = _chain.Segments[index]; var options = new PromptDoubleOptions("\n输入实际段长（CAD 单位；固定起点，0 合并终点，不改被测物体）：") { AllowNegative = false, AllowZero = true, DefaultValue = segment.B - segment.A, UseDefaultValue = true };
                        var result = _document.Editor.GetDouble(options); if (result.Status != PromptStatus.OK) return;
                        var node = DimensionChainGeometry.Nearest(_chain.Stations, segment.B);
                        Commit("node", segment.A + result.Value, node);
                    }
                }
                else if (action == "merge")
                {
                    var pick = _document.Editor.GetPoint("\n选择要合并的内部界点（保留总跨度）："); if (pick.Status != PromptStatus.OK) return;
                    Commit("merge", 0, DimensionChainGeometry.Nearest(_chain.Stations, _chain.T(pick.Value.TransformBy(_document.Editor.CurrentUserCoordinateSystem))));
                }
                else if (action == "release") Commit("release", 0, 0);
            }
            catch (Exception ex) { _document.Editor.WriteMessage("\n尺寸未修改：" + ex.Message); _panel.Status.Text = ex.Message; }
            finally { _busy = false; if (!_disposed) SafeRefresh(true); }
        }
        private void Select()
        {
            var options = new PromptEntityOptions("\n选择原生线性尺寸（共线相接候选将整串高亮）：");
            options.SetRejectMessage("\n请选择尺寸对象。"); options.AddAllowedClass(typeof(Dimension), false);
            var result = _document.Editor.GetEntity(options); if (result.Status != PromptStatus.OK) return;
            _chain = CadDimensionChain.Load(_document.Database, result.ObjectId); _seed = result.ObjectId;
            _document.Editor.SetImpliedSelection(_chain.Segments.Select(s => s.Id).ToArray());
        }
        private void Commit(string action, double value, int node)
        {
            if (!Active) throw new ArgumentException("来源图纸已暂停／关闭，未提交。");
            var chain = _chain; var clearLiteral = _panel.ClearLiteral.Checked;
            // Prompts/previews have NoUndoMarker; only an accepted edit enters
            // this separate ordinary command, so Esc creates no empty undo step.
            QueueWrite(() =>
            {
                _seed = chain.Apply(_document.Database, action, value, node, clearLiteral);
                if (action == "release") _seed = ObjectId.Null;
                _chain = _seed.IsNull ? null : CadDimensionChain.Load(_document.Database, _seed);
                _document.Editor.Regen();
            }, action == "add");
        }
        private void New()
        {
            var ed = _document.Editor; var ucs = ed.CurrentUserCoordinateSystem;
            var direction = ed.GetKeywords(new PromptKeywordOptions("\n测量方向 [水平(H)/竖向(V)/沿两点(A)] <A>：", "H V A") { AllowNone = true });
            if (direction.Status != PromptStatus.OK && direction.Status != PromptStatus.None) return;
            var start = ed.GetPoint("\n新建：捕捉第一个被测点："); if (start.Status != PromptStatus.OK) return;
            var end = ed.GetPoint(new PromptPointOptions("\n捕捉第二个被测点（确定测量方向）：") { BasePoint = start.Value, UseBasePoint = true }); if (end.Status != PromptStatus.OK) return;
            var a = start.Value.TransformBy(ucs); var b = end.Value.TransformBy(ucs);
            if (a.DistanceTo(b) < 1e-10) throw new ArgumentException("两个被测点重合。");
            var line = ed.GetPoint("\n指定尺寸线位置："); if (line.Status != PromptStatus.OK) return;
            var n = Vector3d.ZAxis.TransformBy(ucs).GetNormal();
            if (Math.Abs((b - a).DotProduct(n)) > 1e-7) throw new ArgumentException("首批只支持当前 UCS 平面内的被测点。");
            QueueWrite(() =>
            {
            using (var tx = _document.Database.TransactionManager.StartTransaction())
            {
                var layer = (LayerTableRecord)tx.GetObject(_document.Database.Clayer, OpenMode.ForRead);
                if (layer.IsLocked) throw new ArgumentException("当前图层已锁定。");
                var space = (BlockTableRecord)tx.GetObject(_document.Database.CurrentSpaceId, OpenMode.ForWrite);
                Dimension dimension;
                if (direction.StringResult == "H" || direction.StringResult == "V")
                {
                    var measuring = (direction.StringResult == "H" ? Vector3d.XAxis : Vector3d.YAxis).TransformBy(ucs);
                    if (Math.Abs((b - a).DotProduct(measuring)) < 1e-9) throw new ArgumentException("该方向的实际测量长度为零，未创建尺寸。");
                    var u = (direction.StringResult == "H" ? Vector3d.XAxis : Vector3d.YAxis).TransformBy(ucs).TransformBy(Matrix3d.WorldToPlane(n));
                    dimension = new RotatedDimension(Math.Atan2(u.Y, u.X), a, b, line.Value.TransformBy(ucs), "", _document.Database.Dimstyle);
                }
                else dimension = new AlignedDimension(a, b, line.Value.TransformBy(ucs), "", _document.Database.Dimstyle);
                dimension.Normal = n; dimension.LayerId = _document.Database.Clayer;
                space.AppendEntity(dimension); tx.AddNewlyCreatedDBObject(dimension, true); CadDimensionChain.Tag(dimension, tx, Guid.NewGuid().ToString("N")); _seed = dimension.ObjectId; tx.Commit();
            }
            _chain = CadDimensionChain.Load(_document.Database, _seed); ed.Regen();
            }, true);
        }
        private void Drag(bool node, int index)
        {
            using (var jig = new DimensionChainJig(_document.Database, _chain, node, index))
            {
                var result = _document.Editor.Drag(jig);
                if (result.Status == PromptStatus.OK) Commit(node ? "node" : "line", jig.Value, index);
            }
        }
        private void Monitor(object sender, PointMonitorEventArgs e)
        {
            try
            {
                if (!Active || _busy || _chain == null) return;
                using (var view = _document.Editor.GetCurrentView()) if (Math.Abs(view.Width - _markerWidth) > Math.Max(1e-8, _markerWidth * .02)) DrawMarkers();
            } catch { /* Never propagate through AutoCAD's native point callback. */ }
        }
        private double PixelDistance(System.Windows.Point pointer, Point3d point)
        { return (pointer - _document.Editor.PointToScreen(point, 0)).Length; }
        private Point3d MoveGrip { get { var s = _chain.Stations; return _chain.OnLine((s[0] + s[s.Length - 1]) / 2) + _chain.V * MarkerSize() * 3; } }
        private double MarkerSize() { using (var view = _document.Editor.GetCurrentView()) return view.Width / 180; }
        private void Message(object sender, PreTranslateMessageEventArgs e)
        {
            try
            {
            if (e.Message.message != 0x201 && e.Message.message != 0x203 || !_panel.IsHandleCreated || _panel.IsDisposed) return;
            if (e.Message.hwnd == _panel.Handle || IsChild(_panel.Handle, e.Message.hwnd)) return;
            if (!Active || _busy || _queued != null || _pendingWrite != null || _chain == null || !string.IsNullOrEmpty(_document.CommandInProgress)) return;
            // Hit-test this exact click, not a delayed PointMonitor result.
            // PointToScreen uses drawing-view DIP coordinates, not document or
            // desktop pixels. Map through the actual drawing view and its size.
            var viewWindow = CurrentDrawingView();
            if (viewWindow == IntPtr.Zero || e.Message.hwnd != viewWindow && !IsChild(viewWindow, e.Message.hwnd)) return;
            var packed = e.Message.lParam.ToInt64();
            var pixel = new NativePoint { X = unchecked((short)(packed & 0xffff)), Y = unchecked((short)((packed >> 16) & 0xffff)) };
            NativeRect rect;
            if (!ClientToScreen(e.Message.hwnd, ref pixel) || !ScreenToClient(viewWindow, ref pixel) || !GetClientRect(viewWindow, out rect) || rect.Right <= 0 || rect.Bottom <= 0) return;
            var size = _document.GraphicsManager.DeviceIndependentDisplaySize;
            var pointer = new System.Windows.Point(pixel.X * size.Width / rect.Right, pixel.Y * size.Height / rect.Bottom);
                if (e.Message.message == 0x201)
                {
                    if (PixelDistance(pointer, MoveGrip) <= 9) { e.Handled = true; Queue("line"); return; }
                    var stations = _chain.Stations;
                    for (var i = 0; i < stations.Length; i++) if (PixelDistance(pointer, _chain.OnLine(stations[i])) <= 8) { _gripNode = i; e.Handled = true; Queue("node"); return; }
                }
                else
                {
                    var stations = _chain.Stations;
                    var a = _document.Editor.PointToScreen(_chain.OnLine(stations[0]), 0);
                    var b = _document.Editor.PointToScreen(_chain.OnLine(stations[stations.Length - 1]), 0);
                    var axis = b - a; var delta = pointer - a;
                    var t = axis.LengthSquared > 1e-10 ? System.Windows.Vector.Multiply(delta, axis) / axis.LengthSquared : -1;
                    var inLine = t >= 0 && t <= 1 && (pointer - (a + axis * t)).Length <= 10;
                    if (inLine || _chain.Segments.Any(s => PixelDistance(pointer, s.Text) <= 24)) { e.Handled = true; Queue("add"); }
                }
            }
            catch { /* Native/default double-click remains available when hit-test cannot be proven. */ }
        }
        private void Refresh(bool select = false)
        {
            ClearMarkers(); if (!Active) return;
            if (!_seed.IsNull && _seed.IsValid && !_seed.IsErased)
            {
                try { _chain = CadDimensionChain.Load(_document.Database, _seed); } catch (Exception ex) { _chain = null; _panel.Status.Text = ex.Message; return; }
            }
            else _chain = null;
            if (_chain == null) { _panel.Status.Text = "未选尺寸串；可选择或新建。"; return; }
            var stations = _chain.Stations;
            _panel.Status.Text = "当前串 " + _chain.Segments.Count + " 段 · 跨度 " + (stations[stations.Length - 1] - stations[0]).ToString("0.######") + "\n单位：" + _document.Database.Insunits + "（输入采用 CAD 单位）";
            if (select) _document.Editor.SetImpliedSelection(_chain.Segments.Select(s => s.Id).ToArray());
            DrawMarkers();
        }
        private void DrawMarkers()
        {
            ClearMarkers(); using (var view = _document.Editor.GetCurrentView()) _markerWidth = view.Width;
            foreach (var t in _chain.Stations) Marker(_chain.OnLine(t), false); Marker(MoveGrip, true);
        }
        private void Marker(Point3d p, bool diamond)
        {
            var r = MarkerSize() * .38;
            var line = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(diamond
                ? new[] { p + _chain.U * r, p + _chain.V * r, p - _chain.U * r, p - _chain.V * r }
                : new[] { p + (_chain.U + _chain.V) * r, p + (_chain.U - _chain.V) * r, p - (_chain.U + _chain.V) * r, p - (_chain.U - _chain.V) * r }), true) { ColorIndex = (short)(diamond ? 2 : 4) };
            try { TransientManager.CurrentTransientManager.AddTransient(line, TransientDrawingMode.DirectShortTerm, 130, _viewports); _markers.Add(line); }
            catch { line.Dispose(); }
        }
        private void ClearMarkers()
        {
            foreach (var marker in _markers) { try { TransientManager.CurrentTransientManager.EraseTransient(marker, _viewports); } catch { } marker.Dispose(); } _markers.Clear();
        }
        private void Closing(object sender, DocumentCollectionEventArgs e) { try { if (ReferenceEquals(e.Document, _document)) Dispose(); } catch { } }
        private void Activated(object sender, DocumentCollectionEventArgs e)
        { try { if (_disposed) return; ClearMarkers(); if (Active) SafeRefresh(); else _panel.Status.Text = "已暂停：请切回来源 CAD 图纸。"; } catch { } }
        private void CommandFinished(object sender, CommandEventArgs e) { try { if (!_busy && !_disposed && Active) SafeRefresh(); } catch { } }
        private void SafeRefresh(bool select = false)
        { try { Refresh(select); } catch (Exception ex) { ClearMarkers(); _chain = null; if (!_panel.IsDisposed) _panel.Status.Text = "请重新选择尺寸串：" + ex.Message; } }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; ClearMarkers(); _chain = null;
            _document.Editor.PointMonitor -= Monitor; Application.PreTranslateMessage -= Message; Application.DocumentManager.DocumentToBeDestroyed -= Closing;
            _document.CommandEnded -= CommandFinished; _document.CommandCancelled -= CommandFinished; _document.CommandFailed -= CommandFinished;
            Application.DocumentManager.DocumentActivated -= Activated;
            if (!_panel.IsDisposed) _panel.Close(); if (ReferenceEquals(_current, this)) _current = null;
        }
    }
    internal sealed class DimensionChainJig : DrawJig, IDisposable
    {
        private readonly CadDimensionChain _chain;
        private readonly bool _node;
        private readonly int _index;
        private readonly List<Dimension> _copies = new List<Dimension>();
        internal double Value;
        internal DimensionChainJig(Database db, CadDimensionChain chain, bool node, int index)
        {
            _chain = chain; _node = node; _index = index; Value = node ? chain.Stations[index] : chain.Offset;
            using (var tx = db.TransactionManager.StartTransaction()) foreach (var segment in chain.Segments) _copies.Add((Dimension)((Dimension)tx.GetObject(segment.Id, OpenMode.ForRead)).Clone());
        }
        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var p = prompts.AcquirePoint(new JigPromptPointOptions(_node ? "\n指定界点新位置（相邻重合自动去零）：" : "\n指定整串尺寸线位置：") { UserInputControls = UserInputControls.Accept3dCoordinates });
            if (p.Status != PromptStatus.OK) return SamplerStatus.Cancel;
            var value = _node ? _chain.T(p.Value) : (p.Value - _chain.Origin).DotProduct(_chain.V);
            if (_node) { var nodes = _chain.Stations; value = Math.Max(_index == 0 ? double.MinValue : nodes[_index - 1], Math.Min(_index == nodes.Length - 1 ? double.MaxValue : nodes[_index + 1], value)); }
            if (Math.Abs(value - Value) <= _chain.Epsilon) return SamplerStatus.NoChange;
            Value = value; return SamplerStatus.OK;
        }
        protected override bool WorldDraw(WorldDraw draw)
        {
            for (var i = 0; i < _copies.Count; i++)
            {
                var s = _chain.Segments[i]; var d = _copies[i];
                if (!_node) { var delta = _chain.V * (Value - _chain.Offset); CadDimensionChain.Line(d, s.Line + delta); if (s.TextMoved) d.TextPosition = s.Text + delta; }
                else
                {
                    var a = s.First; var b = s.Second; var station = _chain.Stations[_index];
                    if (Math.Abs(s.A - station) <= _chain.Epsilon) a += _chain.U * (Value - s.A);
                    if (Math.Abs(s.B - station) <= _chain.Epsilon) b += _chain.U * (Value - s.B);
                    if (a.DistanceTo(b) <= _chain.Epsilon) continue;
                    CadDimensionChain.Points(d, s.Reversed ? b : a, s.Reversed ? a : b);
                }
                d.ColorIndex = 4; draw.Geometry.Draw(d);
            }
            return true;
        }
        public void Dispose() { foreach (var copy in _copies) copy.Dispose(); }
    }
}
