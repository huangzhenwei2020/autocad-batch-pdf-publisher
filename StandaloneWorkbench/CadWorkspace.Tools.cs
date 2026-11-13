using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Wanluo.StandaloneWorkbench
{
    public partial class CadWorkspace
    {
        private bool _parameterOpen;
        internal bool SuppressToolDialogs { get; set; }
        internal event Action<CadParameterWindow> ParameterWindowOpened;

        private bool ShowParameters(CadParameterWindow dialog)
        {
            if (_parameterOpen || Client.IsBusy) return false;
            _parameterOpen = true;
            try
            {
                dialog.Owner = Window.GetWindow(this);
                dialog.Loaded += (sender, args) => ParameterWindowOpened?.Invoke(dialog);
                return dialog.ShowDialog() == true;
            }
            finally { _parameterOpen = false; RefreshState(); Viewport.FocusDrawing(); }
        }

        private void CheckToolDialog()
        {
            if (_parameterOpen || SuppressToolDialogs || !IsLoaded || Client.IsBusy) return;
            var request = CadNative.DialogState(Client.Handle);
            if (string.IsNullOrEmpty(request.Kind)) return;
            // Run after the current state refresh so a modal window cannot reenter its update.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_parameterOpen || SuppressToolDialogs || _disposed || Client.IsBusy) return;
                var current = CadNative.DialogState(Client.Handle);
                if (current.DocumentId != request.DocumentId || current.Kind != request.Kind) return;
                try { ShowToolDialog(current); }
                catch (Exception error)
                {
                    Status.Text = error.Message;
                    if (!Client.IsBusy && Client.State.ActiveId == current.DocumentId)
                        Run(() => CadNative.Check(CadNative.wl_dialog_cancel(Client.Handle, current.DocumentId, current.Kind)));
                }
            }));
        }

        internal void ShowToolDialog(CadDialogRequest request)
        {
            var document = request.DocumentId;
            CadParameterWindow dialog;
            switch (request.Kind)
            {
                case "text":
                case "mtext":
                    var multiline = request.Kind == "mtext";
                    dialog = new CadParameterWindow(multiline ? "多行文字" : "单行文字", form =>
                        CadNative.Check(CadNative.wl_text_submit(Client.Handle, document, multiline ? 1 : 0, ((TextBox)form.Inputs["text"]).Text, form.Number("height"), form.Number("rotation"), multiline ? form.Number("width") : 0)));
                    dialog.Text("text", "文字内容", request.Text, multiline);
                    dialog.Numeric("height", "字高 mm", request.Height, !request.Editing);
                    dialog.Numeric("rotation", "旋转角度 °", request.Rotation, !request.Editing);
                    if (multiline) dialog.Numeric("width", "文本框宽度 mm · 0为自动", request.Width, !request.Editing);
                    break;
                case "hatch":
                    dialog = new CadParameterWindow("图案填充", form => CadNative.Check(CadNative.wl_hatch_submit(Client.Handle, document, (string)form.Selected("pattern"), form.Number("scale"), form.Number("rotation"))));
                    dialog.Choice("pattern", "填充图案", request.Choices, request.Pattern);
                    dialog.Numeric("scale", "图案比例", request.Scale);
                    dialog.Numeric("rotation", "图案角度 °", request.Rotation);
                    break;
                case "array":
                    dialog = new CadParameterWindow("阵列", form =>
                    {
                        var polar = form.Index("type") == 1;
                        var parameters = new CadArrayParameters
                        {
                            Type = form.Index("type"), Rows = polar ? 1 : form.Integer("rows"), Columns = polar ? 1 : form.Integer("columns"), Count = polar ? form.Integer("count") : 2,
                            RowSpacing = polar ? 0 : form.Number("rowSpacing"), ColumnSpacing = polar ? 0 : form.Number("columnSpacing"), Rotation = polar ? 0 : form.Number("rotation"),
                            CenterX = polar ? form.Number("centerX") : 0, CenterY = polar ? form.Number("centerY") : 0, FillAngle = polar ? form.Number("fillAngle") : 360, RotateItems = polar && form.Checked("rotate") ? 1 : 0
                        };
                        CadNative.Check(CadNative.wl_array_submit(Client.Handle, document, ref parameters));
                    });
                    dialog.Choice("type", "阵列形式", new[] { "矩形阵列", "环形阵列" }, "矩形阵列");
                    dialog.Numeric("rows", "矩形行数", request.Rows);
                    dialog.Numeric("columns", "矩形列数", request.Columns);
                    dialog.Numeric("rowSpacing", "行间距 mm", request.RowSpacing);
                    dialog.Numeric("columnSpacing", "列间距 mm", request.ColumnSpacing);
                    dialog.Numeric("rotation", "矩形阵列旋转角 °", request.Rotation);
                    dialog.Numeric("centerX", "环形中心 X mm", request.CenterX);
                    dialog.Numeric("centerY", "环形中心 Y mm", request.CenterY);
                    dialog.Numeric("count", "环形数量（含原对象）", request.Count);
                    dialog.Numeric("fillAngle", "环形填充角 °", request.FillAngle);
                    dialog.Toggle("rotate", "环形阵列随角度旋转对象", true);
                    var type = (ComboBox)dialog.Inputs["type"];
                    void EnableArrayFields()
                    {
                        foreach (var key in new[] { "rows", "columns", "rowSpacing", "columnSpacing", "rotation" }) dialog.Inputs[key].IsEnabled = type.SelectedIndex == 0;
                        foreach (var key in new[] { "centerX", "centerY", "count", "fillAngle", "rotate" }) dialog.Inputs[key].IsEnabled = type.SelectedIndex == 1;
                    }
                    type.SelectionChanged += (sender, args) => EnableArrayFields(); EnableArrayFields();
                    break;
                case "blockDefine":
                    dialog = new CadParameterWindow("定义块", form => CadNative.Check(CadNative.wl_block_submit(Client.Handle, document, 1, form.Value("name"), 0)));
                    dialog.Text("name", "块名称", request.Text);
                    break;
                case "blockInsert":
                    if (request.Blocks.Count == 0)
                    {
                        CadNative.Check(CadNative.wl_dialog_cancel(Client.Handle, document, request.Kind));
                        Status.Text = "当前图纸没有可插入的命名块，请先定义块。";
                        return;
                    }
                    dialog = new CadParameterWindow("插入块", form => CadNative.Check(CadNative.wl_block_submit(Client.Handle, document, 0, "", ((CadBlockChoice)form.Selected("block")).Id)));
                    dialog.Choice("block", "选择块，确定后在图中指定插入点", request.Blocks, request.Blocks[0]);
                    break;
                case "image":
                    _parameterOpen = true;
                    try
                    {
                        var image = new OpenFileDialog { Title = "插入图像", Filter = "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.gif" };
                        if (image.ShowDialog(Window.GetWindow(this)) == true) Run(() => CadNative.Check(CadNative.wl_image_submit(Client.Handle, document, image.FileName)));
                        else CadNative.Check(CadNative.wl_dialog_cancel(Client.Handle, document, request.Kind));
                    }
                    finally { _parameterOpen = false; Viewport.FocusDrawing(); }
                    return;
                default: return;
            }
            if (!ShowParameters(dialog) && Client.State.ActiveId == document)
                Run(() => CadNative.Check(CadNative.wl_dialog_cancel(Client.Handle, document, request.Kind)));
        }

        private bool CornerTool(string command)
        {
            var state = CadNative.DialogState(Client.Handle);
            var chamfer = command.Equals("CHAMFER", StringComparison.OrdinalIgnoreCase) || command.Equals("CHA", StringComparison.OrdinalIgnoreCase);
            var dialog = new CadParameterWindow(chamfer ? "倒角参数" : "圆角参数", form =>
            {
                CadNative.Check(CadNative.wl_corner_settings(Client.Handle, chamfer ? state.Radius : form.Number("radius"), chamfer ? form.Number("first") : state.FirstDistance, chamfer ? form.Number("second") : state.SecondDistance));
                Client.Command(command);
            });
            if (chamfer) { dialog.Numeric("first", "第一倒角距离 mm", state.FirstDistance); dialog.Numeric("second", "第二倒角距离 mm", state.SecondDistance); }
            else dialog.Numeric("radius", "圆角半径 mm · 0为连接至交点", state.Radius);
            return ShowParameters(dialog);
        }

        private void TextHeight()
        {
            var state = CadNative.DialogState(Client.Handle);
            var dialog = new CadParameterWindow("默认字高", form => CadNative.Check(CadNative.wl_text_height(Client.Handle, form.Number("height"))));
            dialog.Numeric("height", "字高 mm", state.TextHeight);
            ShowParameters(dialog);
        }
        private void DimensionSettings()
        {
            var state = CadNative.DialogState(Client.Handle);
            var dialog = new CadParameterWindow("新建标注参数", form => CadNative.Check(CadNative.wl_dimension_settings(Client.Handle, form.Number("height"), form.Number("arrow"), form.Number("gap"), form.Number("extension"), form.Number("offset"), form.Integer("precision"), form.Index("terminator"))));
            dialog.Numeric("height", "标注字高 mm", state.DimHeight);
            dialog.Numeric("arrow", "箭头／斜线尺寸 mm", state.DimArrow);
            dialog.Numeric("gap", "文字间距 mm", state.DimGap);
            dialog.Numeric("extension", "尺寸界线超出 mm", state.DimExtension);
            dialog.Numeric("offset", "尺寸界线起点间隙 mm", state.DimOffset);
            dialog.Numeric("precision", "线性小数位数（0–15）", state.DimPrecision);
            var ends = new[] { "实心箭头", "建筑斜线" };
            dialog.Choice("terminator", "端点形式", ends, ends[state.DimTerminator]);
            ShowParameters(dialog);
        }

        private bool LeaderTool(string command)
        {
            var state = CadNative.DialogState(Client.Handle);
            var multiple = new[] { "MLE", "MLEADER", "MULTILEADER" }.Contains(command.ToUpperInvariant());
            var dialog = new CadParameterWindow(multiple ? "多重引线" : "引线", form =>
                CadNative.Check(CadNative.wl_leader_start(Client.Handle, multiple ? 1 : 0, ((TextBox)form.Inputs["text"]).Text, form.Number("height"))));
            dialog.Text("text", "注释内容", "说明", true);
            dialog.Numeric("height", "字高 mm", state.TextHeight);
            return ShowParameters(dialog);
        }
        private bool MLineTool(string command)
        {
            var state = CadNative.DialogState(Client.Handle);
            var dialog = new CadParameterWindow("多线参数", form =>
            {
                CadNative.Check(CadNative.wl_mline_settings(Client.Handle, form.Number("scale"), form.Index("justify")));
                Client.Command(command);
            });
            dialog.Numeric("scale", "多线比例", state.MlineScale);
            var labels = new[] { "上对正", "中心对正", "下对正" };
            dialog.Choice("justify", "对正方式", labels, labels[state.MlineJustify]);
            return ShowParameters(dialog);
        }

        private void NewLayer_Click(object sender, RoutedEventArgs args)
        {
            var dialog = new CadParameterWindow("新建图层", form => CadNative.Check(CadNative.wl_layer_manage(Client.Handle, 0, 0, form.Value("name"), 0, -3)));
            dialog.Text("name", "图层名称", "图层" + Client.State.Layers.Count);
            ShowParameters(dialog);
        }
        private void EditLayer_Click(object sender, RoutedEventArgs args)
        {
            if (!(Layers.SelectedItem is CadLayer layer)) { Status.Text = "请先选择图层。"; return; }
            var weights = new[] { -3, 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };
            var labels = weights.Select(value => value < 0 ? "默认" : (value / 100d).ToString("0.00", CultureInfo.CurrentCulture) + " mm").ToArray();
            var dialog = new CadParameterWindow("修改图层", form =>
            {
                var color = (Color)ColorConverter.ConvertFromString(form.Value("color"));
                var rgb = (color.R << 16) | (color.G << 8) | color.B;
                CadNative.Check(CadNative.wl_layer_manage(Client.Handle, 2, layer.Id, form.Value("name"), rgb, weights[form.Index("weight")]));
            });
            dialog.Text("name", "图层名称", layer.Name);
            dialog.Text("color", "颜色 #RRGGBB", "#" + layer.Rgb.ToString("X6", CultureInfo.InvariantCulture));
            var index = Array.IndexOf(weights, layer.Weight);
            dialog.Choice("weight", "线宽", labels, labels[Math.Max(0, index)]);
            ShowParameters(dialog);
        }
        private void DeleteLayer_Click(object sender, RoutedEventArgs args)
        {
            if (!(Layers.SelectedItem is CadLayer layer)) { Status.Text = "请先选择图层。"; return; }
            if (layer.Id == 0) { Status.Text = "默认0图层需要保留。"; return; }
            if (MessageBox.Show(Window.GetWindow(this), "删除图层“" + layer.Name + "”？该层对象将移至0图层，可用Ctrl+Z撤销。", "删除图层", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Run(() => CadNative.Check(CadNative.wl_layer_manage(Client.Handle, 1, layer.Id, "", 0, -3)));
        }
        private void NewLayout_Click(object sender, RoutedEventArgs args)
        {
            var dialog = new CadParameterWindow("新建布局", form => CadNative.Check(CadNative.wl_layout_manage(Client.Handle, 0, 0, form.Value("name"))));
            dialog.Text("name", "布局名称", "布局" + Client.State.Layouts.Count);
            ShowParameters(dialog);
        }
        private void DeleteLayout_Click(object sender, RoutedEventArgs args)
        {
            if (!(Layouts.SelectedItem is CadLayout layout)) return;
            if (layout.Model) { Status.Text = "请先切换到要删除的图纸布局。"; return; }
            if (MessageBox.Show(Window.GetWindow(this), "删除布局“" + layout.Name + "”及其中的图纸空间对象？可用Ctrl+Z撤销。", "删除布局", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Run(() => CadNative.Check(CadNative.wl_layout_manage(Client.Handle, 1, layout.Id, "")));
        }
    }
}
