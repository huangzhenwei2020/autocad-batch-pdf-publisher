using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    internal static partial class CadNative
    {
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_layout_update(IntPtr session, ulong document,
            ulong key, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double width, double height, int position, int duplicate);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr wl_viewports(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_viewport_start(IntPtr session, ulong document, ref CadViewportParameters parameters);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_viewport_navigate(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_viewport_clip(IntPtr session, ulong document, ulong viewport, int remove);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_viewport_edit(IntPtr session, ulong document, ulong viewport);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_viewport_inline(IntPtr session, ulong document, ulong viewport);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_viewport_apply(IntPtr session, ulong document, ulong entity,
            ref CadViewportParameters parameters, [In] uint[] frozen, int count);
        internal static List<CadPaperViewport> PaperViewports(IntPtr session)
        {
            var json = Text(wl_viewports(session));
            if (string.IsNullOrEmpty(json)) Check(0);
            return JsonSerializer.Deserialize<List<CadPaperViewport>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CadViewportParameters
    {
        public double X, Y, Width, Height, ModelX, ModelY, Ratio, Twist;
        public int Locked, Fit;
    }
    internal sealed class CadPaperViewport
    {
        public ulong Id { get; set; }
        public int Number { get; set; }
        public bool Selected { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double ModelX { get; set; }
        public double ModelY { get; set; }
        public double ModelZ { get; set; }
        public bool Perspective { get; set; }
        public double Ratio { get; set; }
        public double Twist { get; set; }
        public bool Locked { get; set; }
        public bool Plan { get; set; } = true;
        public bool CanMaximizeEdit { get; set; }
        public bool CanInlineEdit { get; set; }
        public int ClipCount { get; set; }
        public bool ClipEllipse { get; set; }
        public List<uint> Frozen { get; set; } = new List<uint>();
        public override string ToString() => $"视口 {Number} · {Width:0.##} × {Height:0.##} · 1:{Ratio:0.####}";
    }
    public partial class CadWorkspace
    {
        internal void LayoutSettings(bool duplicate = false)
        {
            var state = Client.State;
            var layout = state.Layouts.SingleOrDefault(item => item.Current);
            if (layout == null || layout.Model) { Status.Text = "请先切换到要设置的图纸布局。"; return; }
            var dialog = new CadParameterWindow(duplicate ? "复制图纸布局" : "布局与页面设置", form =>
                CadNative.Check(CadNative.wl_layout_update(Client.Handle, state.ActiveId, layout.Key, form.Value("name"),
                    form.Number("width"), form.Number("height"), form.Integer("position"), duplicate ? 1 : 0)));
            dialog.Text("name", "布局名称", layout.Name + (duplicate ? "_副本" : ""));
            dialog.Numeric("position", "标签顺序（模型后第一张为1）", duplicate ? layout.Id + 1 : layout.Id);
            var papers = new[] { "自定义", "A4 · 297 × 210", "A3 · 420 × 297", "A2 · 594 × 420", "A1 · 841 × 594", "A0 · 1189 × 841" };
            dialog.Choice("paper", "纸张规格", papers, papers[0]);
            dialog.NumericPair("width", "纸张宽 mm", layout.Width, "height", "纸张高 mm", layout.Height);
            var rotate = new Button { Content = "交换宽高", HorizontalAlignment = HorizontalAlignment.Right };
            rotate.Click += (sender, args) =>
            {
                var width = dialog.Value("width");
                ((TextBox)dialog.Inputs["width"]).Text = dialog.Value("height");
                ((TextBox)dialog.Inputs["height"]).Text = width;
            };
            dialog.Field("rotate", "", rotate);
            ((ComboBox)dialog.Inputs["paper"]).SelectionChanged += (sender, args) =>
            {
                var index = dialog.Index("paper");
                if (index == 0) return;
                var sizes = new[] { (297, 210), (420, 297), (594, 420), (841, 594), (1189, 841) };
                ((TextBox)dialog.Inputs["width"]).Text = sizes[index - 1].Item1.ToString(CultureInfo.CurrentCulture);
                ((TextBox)dialog.Inputs["height"]).Text = sizes[index - 1].Item2.ToString(CultureInfo.CurrentCulture);
            };
            dialog.Note(duplicate ? "复制本布局的纸空间图形、视口和页面设置。模型空间共用当前图纸，修改可整体撤销。" : "名称、顺序和页面尺寸一起应用。纸张宽高修改后按毫米设置页面，图形位置保持原坐标。Ctrl+Z 撤销。");
            ShowParameters(dialog);
        }

        internal void PaperViewport(bool edit = false)
        {
            var state = Client.State;
            var layout = state.Layouts.SingleOrDefault(item => item.Current);
            if (layout == null || layout.Model) { Status.Text = "请先选择底部的图纸布局，再创建或修改视口。"; return; }
            var viewports = CadNative.PaperViewports(Client.Handle);
            if (edit && viewports.Count == 0) { Status.Text = "当前布局没有可修改的浮动视口，请先新建矩形视口。"; return; }
            var selected = edit ? viewports.FirstOrDefault(item => item.Selected) ?? viewports[0] : null;
            var dialog = new CadParameterWindow(edit ? "修改布局视口" : "新建矩形视口 · MVIEW", form =>
            {
                var draw = !edit && form.Index("method") == 0;
                var fit = form.Checked("fit");
                var p = new CadViewportParameters
                {
                    X = draw ? 0 : form.Number("x"), Y = draw ? 0 : form.Number("y"), Width = draw ? 1 : form.Number("width"), Height = draw ? 1 : form.Number("height"),
                    ModelX = fit ? 0 : form.Number("modelX"), ModelY = fit ? 0 : form.Number("modelY"), Ratio = fit ? 100 : ReadViewportRatio(((ComboBox)form.Inputs["ratio"]).Text),
                    Twist = form.Number("twist"), Locked = form.Checked("locked") ? 1 : 0, Fit = fit ? 1 : 0
                };
                if (draw)
                    CadNative.Check(CadNative.wl_viewport_start(Client.Handle, state.ActiveId, ref p));
                else
                {
                    var frozen = ((ListBox)form.Inputs["frozen"]).SelectedItems.Cast<CadLayer>().Select(item => item.Id).ToArray();
                    CadNative.Check(CadNative.wl_viewport_apply(Client.Handle, state.ActiveId, edit ? ((CadPaperViewport)form.Selected("viewport")).Id : 0,
                        ref p, frozen, frozen.Length));
                }
            });
            if (edit) dialog.Choice("viewport", "当前布局的视口", viewports, selected);
            else dialog.Choice("method", "创建方式", new[] { "两点绘制矩形边界", "输入纸面位置及尺寸" }, "两点绘制矩形边界");
            dialog.Toggle("locked", "锁定视口平移和缩放", selected?.Locked ?? true);
            dialog.NumericPair("x", "纸面中心 X", selected?.X ?? layout.Width / (2 * layout.UnitToMm), "y", "纸面中心 Y", selected?.Y ?? layout.Height / (2 * layout.UnitToMm));
            dialog.NumericPair("width", "视口宽（图纸单位）", selected?.Width ?? Math.Max(1, layout.Width - 40) / layout.UnitToMm, "height", "视口高（图纸单位）", selected?.Height ?? Math.Max(1, layout.Height - 40) / layout.UnitToMm);
            var ratio = new ComboBox { IsEditable = true, MinHeight = 30, ItemsSource = new[] { "1:1", "1:20", "1:25", "1:50", "1:75", "1:100", "1:150", "1:200", "1:500" } };
            ratio.Text = "1:" + (selected?.Ratio ?? 100).ToString("0.########", CultureInfo.InvariantCulture);
            dialog.Field("ratio", "比例（图纸单位∶模型单位）", ratio);
            dialog.Toggle("fit", "自动适合全部模型范围", !edit);
            dialog.NumericPair("modelX", "模型中心 X", selected?.ModelX ?? 0, "modelY", "模型中心 Y", selected?.ModelY ?? 0);
            dialog.Numeric("twist", "内容旋转角度 °", selected?.Twist ?? 0);
            var layers = new ListBox { SelectionMode = SelectionMode.Multiple, DisplayMemberPath = "Name", ItemsSource = state.Layers, MaxHeight = 120, MinHeight = 65 };
            dialog.Field("frozen", "仅在此视口冻结的图层（单击切换）", layers);
            foreach (var layer in state.Layers.Where(item => selected?.Frozen.Contains(item.Id) == true)) layers.SelectedItems.Add(layer);
            void Enable()
            {
                var draw = !edit && dialog.Index("method") == 0;
                var selectedViewport = edit ? (CadPaperViewport)dialog.Selected("viewport") : null;
                var plan = selectedViewport == null || (selectedViewport.Plan && !selectedViewport.Perspective);
                foreach (var key in new[] { "x", "y", "width", "height" }) dialog.Inputs[key].IsEnabled = !draw;
                dialog.Inputs["ratio"].IsEnabled = !dialog.Checked("fit");
                foreach (var key in new[] { "modelX", "modelY" }) dialog.Inputs[key].IsEnabled = plan && !dialog.Checked("fit");
                dialog.Inputs["twist"].IsEnabled = plan;
                dialog.Inputs["fit"].IsEnabled = plan;
                layers.IsEnabled = !draw;
                dialog.ApplyButton.Content = draw ? "到图纸中绘制" : "应用";
            }
            if (!edit) ((ComboBox)dialog.Inputs["method"]).SelectionChanged += (sender, args) => Enable();
            ((CheckBox)dialog.Inputs["fit"]).Checked += (sender, args) => Enable();
            ((CheckBox)dialog.Inputs["fit"]).Unchecked += (sender, args) => Enable();
            if (edit) ((ComboBox)dialog.Inputs["viewport"]).SelectionChanged += (sender, args) =>
            {
                var item = (CadPaperViewport)dialog.Selected("viewport");
                foreach (var field in new[] { ("x", item.X), ("y", item.Y), ("width", item.Width), ("height", item.Height), ("modelX", item.ModelX), ("modelY", item.ModelY), ("twist", item.Twist) })
                    ((TextBox)dialog.Inputs[field.Item1]).Text = field.Item2.ToString("0.########", CultureInfo.CurrentCulture);
                ratio.Text = "1:" + item.Ratio.ToString("0.########", CultureInfo.InvariantCulture);
                ((CheckBox)dialog.Inputs["fit"]).IsChecked = false;
                ((CheckBox)dialog.Inputs["locked"]).IsChecked = item.Locked;
                layers.SelectedItems.Clear();
                foreach (var layer in state.Layers.Where(layer => item.Frozen.Contains(layer.Id))) layers.SelectedItems.Add(layer);
                Enable();
            };
            dialog.Note("矩形边界裁剪模型内容。纸面中心决定放置位置，模型中心决定显示区域。两次单击绘制边界，Esc 取消草稿，Ctrl+Z 撤销已应用修改。");
            Enable(); ShowParameters(dialog);
        }
        internal static double ReadViewportRatio(string text)
        {
            var parts = text.Trim().Replace('：', ':').Replace('∶', ':').Split(':', '/');
            if (parts.Length > 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var first) || !double.IsFinite(first) || first <= 0)
                throw new InvalidOperationException("请输入有效比例，例如1:50、1:100或100。");
            var ratio = first;
            if (parts.Length == 2)
            {
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var second)) throw new InvalidOperationException("请输入有效比例。");
                ratio = second / first;
            }
            if (!double.IsFinite(ratio) || ratio <= 0) throw new InvalidOperationException("视口比例需要大于0。");
            return ratio;
        }
        private void LayoutSettings_Click(object sender, RoutedEventArgs args) => LayoutSettings();
        internal void NavigateViewport()
        {
            CadNative.Check(CadNative.wl_viewport_navigate(Client.Handle));
            Status.Text = "单击视口内部，用中键平移、滚轮缩放。视口锁定可在属性面板修改。";
            Viewport.FocusDrawing();
        }
        internal void EditViewportModel(bool inlineEdit = false)
        {
            var state = Client.State;
            if (state.EditingViewport != 0)
            {
                Client.Command(state.InlineViewport == inlineEdit ? "VPMIN" : inlineEdit ? "MSPACE" : "VPMAX");
                RefreshState(); Viewport.FocusDrawing(); return;
            }
            void Enter(CadPaperViewport viewport)
            {
                CadNative.Check(inlineEdit ? CadNative.wl_viewport_inline(Client.Handle, state.ActiveId, viewport.Id) :
                    CadNative.wl_viewport_edit(Client.Handle, state.ActiveId, viewport.Id));
                Status.Text = inlineEdit ?
                    (viewport.Plan ? "正在原位编辑模型，双击视口外或输入PSPACE返回纸空间。" :
                        "正在原位编辑正投影视口。可用 UCS VIEW 设置绘图平面，PSPACE 返回纸空间。") :
                    viewport.Plan ? "正在最大化编辑视口模型，点击“返回布局”或输入VPMIN完成。" :
                    "正在最大化编辑正投影视口。可用 UCS VIEW 设置绘图平面，VPMIN 返回布局。";
            }
            var viewports = CadNative.PaperViewports(Client.Handle)
                .Where(item => inlineEdit ? item.CanInlineEdit : item.CanMaximizeEdit).ToArray();
            if (viewports.Length == 0)
            {
                Status.Text = inlineEdit ? "当前布局没有可原位编辑的视口，可用 VPMAX 编辑正投影视口。" :
                    "当前布局没有可最大化编辑的正投影视口。";
                return;
            }
            var selected = viewports.FirstOrDefault(item => item.Selected);
            if (selected != null || viewports.Length == 1)
            {
                Enter(selected ?? viewports[0]);
                RefreshState(); Viewport.FocusDrawing(); return;
            }
            var dialog = new CadParameterWindow(inlineEdit ? "原位视口编辑 · MSPACE" : "最大化视口编辑 · VPMAX", form =>
            {
                Enter((CadPaperViewport)form.Selected("viewport"));
            });
            dialog.Choice("viewport", "要编辑模型的视口", viewports, viewports[0]);
            dialog.Note(inlineEdit ? "保留布局显示，在目标视口内绘制和修改模型。双击视口外、点击“返回布局”或输入PSPACE返回纸空间。" :
                "在最大化画布中绘制和修改模型。完成后点击底部“返回布局”或输入VPMIN，恢复原布局视角及视口比例。");
            ShowParameters(dialog);
        }
        private void ViewportEditing_Click(object sender, RoutedEventArgs args) => Run(() =>
        {
            if (Client.State.EditingViewport != 0) { Client.Command("PSPACE"); Status.Text = "已返回纸空间，模型修改已同步。"; }
            else EditViewportModel(true);
            Viewport.FocusDrawing();
        });

        internal void ClipViewport()
        {
            var state=Client.State;
            if (!state.Layouts.Any(item => item.Current && !item.Model)) { Status.Text="请先切换到图纸布局。"; return; }
            var viewports=CadNative.PaperViewports(Client.Handle);
            if (viewports.Count==0) { Status.Text="当前布局没有可裁剪的视口。"; return; }
            var dialog=new CadParameterWindow("视口边界裁剪 · VPCLIP", form =>
                CadNative.Check(CadNative.wl_viewport_clip(Client.Handle,state.ActiveId,((CadPaperViewport)form.Selected("viewport")).Id,form.Index("method"))));
            dialog.Choice("viewport","要裁剪的视口",viewports,viewports.FirstOrDefault(item => item.Selected) ?? viewports[0]);
            dialog.Choice("method","操作",new[] {"绘制／重画多边形边界","恢复矩形边界","绘制圆形边界","绘制轴向椭圆边界","绘制旋转椭圆边界","拾取闭合多段线","拾取闭合样条曲线"},"绘制／重画多边形边界");
            dialog.Note("多边形指定顶点后按 Enter。圆形指定圆心和圆周点，轴向椭圆指定外包矩形的对角点，旋转椭圆依次指定中心、长轴端点及短轴半径。拾取时单击闭合多段线或样条曲线的边。Backspace 退回，Esc 取消，Ctrl+Z 撤销。");
            ShowParameters(dialog);
        }
        private void CopyLayout_Click(object sender, RoutedEventArgs args) => LayoutSettings(true);
    }
}
