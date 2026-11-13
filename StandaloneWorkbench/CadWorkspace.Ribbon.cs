using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;

namespace Wanluo.StandaloneWorkbench
{
    public partial class CadWorkspace
    {
        private static readonly HashSet<string> DrawingCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "L", "LINE", "PL", "PLINE", "C", "CIRCLE", "A", "ARC", "REC", "RECTANG", "EL", "ELLIPSE", "ELA", "ELLIPSEARC", "SP", "SPLINE", "PT", "POINT", "XL", "XLINE", "RAY", "ML", "MLINE", "SO", "SOLID", "WI", "WIPEOUT", "T", "TEXT", "DT", "DTEXT", "MT", "MTEXT", "TB", "TABLE", "H", "HATCH", "I", "INSERT", "IM", "IMAGE", "DIM", "DIMLINEAR", "DLI", "DAL", "DIMALIGNED", "DAN", "DIMANGULAR", "DRA", "DIMRADIUS", "DDI", "DIMDIAMETER", "DJO", "DIMJOGGED", "DAR", "DIMARC", "DOR", "DIMORDINATE", "LE", "LEADER", "MLE", "MLEADER" };
        private readonly List<CadIconButton> _undoButtons = new List<CadIconButton>();
        private readonly List<CadIconButton> _redoButtons = new List<CadIconButton>();
        private readonly ComboBox _currentLayer = new ComboBox { Width = 160, Height = 26, MinHeight = 26, Margin = new Thickness(3), FontSize = 12, DisplayMemberPath = "Name" };
        private object _previousRibbonMode;
        private double _previousInspectorWidth;
        private double _inspectorWidth = 240;
        internal event Action<string> UiActionRequested;
        internal event Action<string> ActiveDrawingChanged;
        private string _activeDrawingLabel;

        private void BuildRibbon()
        {
            CommonTools.Children.Add(Group("绘图", DrawButtons()));
            CommonTools.Children.Add(Group("修改", Tool("move", "移动", "M", "MOVE"), Tool("rotate-ccw", "旋转", "RO", "ROTATE"), Tool("copy", "复制", "CO", "COPY"), Tool("flip-horizontal-2", "镜像", "MI", "MIRROR"), Tool("trash", "删除", "Del", "@delete")));
            CommonTools.Children.Add(Group("选择与视图", Tool("mouse-pointer-2", "全选", "Ctrl+A", "@all"), Tool("maximize", "全图", "ZF", "@fit")));
            _currentLayer.SelectionChanged += (sender, args) => { if (!_updating && _currentLayer.SelectedItem is CadLayer layer) ApplyLayer(layer, true); };
            _currentLayer.Style = (Style)FindResource("CadComboBox");
            _currentLayer.SetBinding(ToolTipProperty, new Binding("SelectedItem.Name") { Source = _currentLayer });
            var layerTools = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            layerTools.Children.Add(_currentLayer);
            var layerActions = new StackPanel { Orientation = Orientation.Horizontal };
            var layersButton = ActionButton("layers", "图层", () => ShowInspector(1));
            var propertiesButton = ActionButton("panel-right", "属性", () => ShowInspector(0));
            layersButton.Style = propertiesButton.Style = (Style)FindResource(typeof(CadIconButton));
            layerActions.Children.Add(layersButton);
            layerActions.Children.Add(propertiesButton);
            layerTools.Children.Add(layerActions);
            CommonTools.Children.Add(Group("当前图层", layerTools));
            var historyTools = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var undo = Tool("undo-2", "撤销", "Ctrl+Z", "@undo");
            var redo = Tool("redo-2", "重做", "Ctrl+Y", "@redo");
            undo.Style = redo.Style = (Style)FindResource(typeof(CadIconButton));
            historyTools.Children.Add(undo); historyTools.Children.Add(redo);
            var historyGroup = Group("编辑记录", historyTools);
            CommonTools.Children.Add(historyGroup);
            SizeChanged += (sender, args) => historyGroup.Visibility = args.NewSize.Width < 1100 ? Visibility.Collapsed : Visibility.Visible;
            DrawTools.Children.Add(Group("基础图形", DrawButtons()));
            DrawTools.Children.Add(Group("曲线与辅助线", Tool("ellipse", "椭圆", "EL", "ELLIPSE"), Tool("ellipse", "椭圆弧", "ELA", "ELLIPSEARC"), Tool("spline", "样条", "SP", "SPLINE"), Tool("point", "点", "PT", "POINT"), Tool("slash", "构造线", "XL", "XLINE"), Tool("ray", "射线", "", "RAY")));
            DrawTools.Children.Add(Group("边界与填充", Tool("columns-2", "多线", "ML", "MLINE"), Tool("hatch", "填充", "H", "HATCH"), Tool("square", "面域", "REG", "REGION"), Tool("square", "实心面", "SO", "SOLID"), Tool("square", "覆盖", "WI", "WIPEOUT")));
            DrawTools.Children.Add(Group("绘图辅助", Tool("grid-3x3", "捕捉", "F3", "@snap"), Tool("columns-2", "正交", "F8", "@ortho"), Tool("waypoints", "极轴", "F10", "@polar")));
            ModifyTools.Children.Add(Group("位置与尺寸", Tool("move", "移动", "M", "MOVE"), Tool("rotate-ccw", "旋转", "RO", "ROTATE"), Tool("copy", "复制", "CO", "COPY"), Tool("flip-horizontal-2", "镜像", "MI", "MIRROR"), Tool("scaling", "缩放", "SC", "SCALE")));
            ModifyTools.Children.Add(Group("边界修改", Tool("offset", "偏移", "O", "OFFSET"), Tool("scissors", "修剪", "TR", "TRIM"), Tool("extend", "延伸", "EX", "EXTEND"), Tool("break", "打断", "BR", "BREAK"), Tool("corner-down-right", "圆角", "F", "FILLET"), Tool("chamfer", "倒角", "CHA", "CHAMFER")));
            ModifyTools.Children.Add(Group("组合编辑", Tool("scaling", "拉伸", "S", "STRETCH"), Tool("array", "阵列", "AR", "ARRAY"), Tool("expand", "分解", "X", "EXPLODE"), Tool("trash", "删除", "Del", "@delete")));
            AnnotationTools.Children.Add(Group("文字与表格", Tool("text", "单行文字", "DT", "TEXT"), Tool("mtext", "多行文字", "MT", "MTEXT"), Tool("table", "表格", "TB", "TABLE"), ActionButton("text", "默认字高", TextHeight)));
            AnnotationTools.Children.Add(Group("尺寸标注", Tool("dimension", "线性标注", "DLI", "DIMLINEAR"), Tool("dimension", "对齐标注", "DAL", "DIMALIGNED"), Tool("angular", "角度标注", "DAN", "DIMANGULAR"), Tool("radius", "半径标注", "DRA", "DIMRADIUS"), Tool("radius", "直径标注", "DDI", "DIMDIAMETER")));
            AnnotationTools.Children.Add(Group("其他标注", Tool("radius", "折弯标注", "DJO", "DIMJOGGED"), Tool("angular", "弧长标注", "DAR", "DIMARC"), Tool("dimension", "坐标标注", "DOR", "DIMORDINATE"), Tool("ray", "引线", "LE", "LEADER"), Tool("ray", "多重引线", "MLE", "MLEADER"), Tool("dimension", "面积查询", "AA", "AREA")));
            AnnotationTools.Children.Add(Group("标注设置", ActionButton("dimension", "标注参数", DimensionSettings)));
            InsertTools.Children.Add(Group("块与图像", Tool("block", "定义块", "B", "BLOCK"), Tool("block", "插入块", "I", "INSERT"), Tool("image", "图像", "IM", "IMAGE"), Tool("expand", "分解", "X", "EXPLODE")));
            ModifyTools.Children.Add(Group("撤销与重做", Tool("undo-2", "撤销", "Ctrl+Z", "@undo"), Tool("redo-2", "重做", "Ctrl+Y", "@redo")));
            ViewTools.Children.Add(Group("视图操作", Tool("maximize", "全图", "ZF", "@fit"), Tool("zoom-in", "缩放", "Z", "ZOOM"), Tool("hand", "平移", "PAN", "PAN")));
            ViewTools.Children.Add(Group("工作面板", ActionButton("panel-right", "属性", () => ShowInspector(0)), ActionButton("layers", "图层", () => ShowInspector(1)), ActionButton("panel-left", "项目", () => UiActionRequested?.Invoke("project")), ActionButton("terminal", "命令记录", ToggleHistory)));
            ViewTools.Children.Add(Group("画布空间", ActionButton("expand", "清屏", () => UiActionRequested?.Invoke("clean"))));
            ViewTools.Children.Add(Group("图纸布局", ActionButton("file-plus", "新建布局", () => NewLayout_Click(this, new RoutedEventArgs())), ActionButton("trash", "删除布局", () => DeleteLayout_Click(this, new RoutedEventArgs()))));
            FileTools.Children.Add(Group("图纸文件", ActionButton("file-plus", "新建", () => NewDrawing()), ActionButton("folder-open", "打开", async () => await PickDrawingAsync()), ActionButton("save", "保存", async () => await SaveWithDialogAsync(false)), ActionButton("save-all", "另存为", async () => await SaveWithDialogAsync(true)), ActionButton("x", "关闭", async () => await CloseCurrentAsync())));
            FileTools.Children.Add(Group("项目与模型", ActionButton("panel-left", "项目", () => UiActionRequested?.Invoke("project")), ActionButton("box", "模型楼层", () => UiActionRequested?.Invoke("models")), ActionButton("app-window", "AutoCAD", () => UiActionRequested?.Invoke("cad"))));
        }

        private FrameworkElement[] DrawButtons() => new FrameworkElement[]
        {
            Tool("slash", "直线", "L", "LINE"), Tool("waypoints", "多段线", "PL", "PLINE"),
            Tool("circle", "圆", "C", "CIRCLE"), Tool("spline", "圆弧", "A", "ARC"), Tool("square", "矩形", "REC", "RECTANG")
        };

        private CadIconButton Tool(string icon, string label, string shortcut, string command)
        {
            var button = new CadIconButton { Icon = icon, Label = label + (shortcut.Length > 0 && shortcut.Length <= 3 ? " " + shortcut : ""), Shortcut = shortcut, Tag = command, ToolTip = label + (shortcut.Length > 0 ? "  " + shortcut : "") + "\n" + command, Style = (Style)FindResource("CadRibbonButton") };
            button.Width = double.NaN;
            button.MinWidth = 56;
            button.Padding = new Thickness(8, 3, 8, 3);
            button.Click += Tool_Click;
            if (command == "@undo") _undoButtons.Add(button);
            if (command == "@redo") _redoButtons.Add(button);
            return button;
        }

        private CadIconButton ActionButton(string icon, string label, Action action)
        {
            var button = new CadIconButton { Icon = icon, Label = label, ToolTip = label, Style = (Style)FindResource("CadRibbonButton") };
            button.Width = double.NaN; button.MinWidth = 56; button.Padding = new Thickness(8, 3, 8, 3);
            button.Click += (sender, args) => action();
            return button;
        }

        private static Border Group(string name, params FrameworkElement[] tools)
        {
            var grid = new Grid { Margin = new Thickness(5, 0, 5, 0) };
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var tool in tools) row.Children.Add(tool);
            grid.Children.Add(row);
            var caption = new TextBlock { Text = name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(207, 217, 229)) };
            Grid.SetRow(caption, 1); grid.Children.Add(caption);
            return new Border { Child = grid, BorderBrush = new SolidColorBrush(Color.FromRgb(49, 60, 75)), BorderThickness = new Thickness(0, 0, 1, 0) };
        }

        internal bool ExecuteCommand(string command, bool explicitTool = false)
        {
            command = command.Trim();
            if (command.Equals("NEW", StringComparison.OrdinalIgnoreCase) || command.Equals("QNEW", StringComparison.OrdinalIgnoreCase))
                return NewDrawing();
            if (Client.IsBusy || _parameterOpen) return false;
            if (explicitTool && !command.StartsWith("@", StringComparison.Ordinal) && Client.State.ActiveId != 0)
                if (!Run(() => Client.Command("@cancel"))) return false;
            if (DrawingCommands.Contains(command) && Client.State.ActiveId == 0 && !NewDrawing()) return false;
            if (Client.State.ActiveId != 0 && !Client.State.Prompt.Contains("对角点") && !Client.State.CursorMode.Equals("point", StringComparison.Ordinal))
            {
                if (new[] { "F", "FILLET", "CHA", "CHAMFER" }.Contains(command.ToUpperInvariant())) return CornerTool(command);
                if (new[] { "LE", "LEADER", "MLE", "MLEADER", "MULTILEADER" }.Contains(command.ToUpperInvariant())) return LeaderTool(command);
                if (new[] { "ML", "MLINE" }.Contains(command.ToUpperInvariant())) return MLineTool(command);
            }
            var applied = Run(() =>
            {
                if (DrawingCommands.Contains(command) && Client.State.ActiveId == 0)
                {
                    Client.New();
                    Status.Text = "新图纸已创建，可以开始绘图。";
                }
                Client.Command(command);
            });
            if (applied) Viewport.FocusDrawing();
            return applied;
        }
        internal void ToggleRibbon()
        {
            EditTools.Tag = Equals(EditTools.Tag, "Collapsed") ? null : "Collapsed";
            RibbonCollapse.Icon = Equals(EditTools.Tag, "Collapsed") ? "chevron-down" : "chevron-up";
        }
        internal void ShowInspector(int tab)
        {
            InspectorTabs.SelectedIndex = tab;
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorColumn.Width = new GridLength(_inspectorWidth);
            InspectorDivider.Width = new GridLength(4);
        }
        internal void ToggleProperties()
        {
            if (InspectorPanel.Visibility == Visibility.Visible && InspectorTabs.SelectedIndex == 0) HideInspector();
            else ShowInspector(0);
        }
        private void NextDocument(bool previous)
        {
            var state = Client.State;
            if (state.Documents.Count < 2 || Client.IsBusy) return;
            var index = state.Documents.FindIndex(item => item.Id == state.ActiveId);
            var next = (index + state.Documents.Count + (previous ? -1 : 1)) % state.Documents.Count;
            Run(() => Client.Activate(state.Documents[next].Id));
        }
        private void HideInspector()
        {
            if (InspectorColumn.ActualWidth > 0) _inspectorWidth = InspectorColumn.ActualWidth;
            InspectorPanel.Visibility = Visibility.Collapsed;
            InspectorColumn.Width = new GridLength(0); InspectorDivider.Width = new GridLength(0);
            Viewport.FocusDrawing();
        }
        internal void SetCleanScreen(bool clean)
        {
            if (clean)
            {
                _previousRibbonMode = EditTools.Tag; _previousInspectorWidth = InspectorColumn.Width.Value;
                EditTools.Tag = "Collapsed"; HideInspector();
            }
            else
            {
                EditTools.Tag = _previousRibbonMode;
                if (_previousInspectorWidth > 0) ShowInspector(InspectorTabs.SelectedIndex);
            }
            RibbonCollapse.Icon = Equals(EditTools.Tag, "Collapsed") ? "chevron-down" : "chevron-up";
        }
        private void ToggleHistory() => History.Visibility = History.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        private void CollapseRibbon_Click(object sender, RoutedEventArgs args) => ToggleRibbon();
        private void History_Click(object sender, RoutedEventArgs args) => ToggleHistory();
        private void HideInspector_Click(object sender, RoutedEventArgs args) => HideInspector();
        private void ToggleInspector_Click(object sender, RoutedEventArgs args)
        {
            var tab = int.Parse((string)((Button)sender).Tag);
            if (InspectorPanel.Visibility == Visibility.Visible && InspectorTabs.SelectedIndex == tab) HideInspector();
            else ShowInspector(tab);
        }
        private async void CloseDocument_Click(object sender, RoutedEventArgs args)
        {
            if (!Client.IsBusy && ((FrameworkElement)sender).DataContext is CadDrawing doc && await ConfirmDrawingAsync(doc)) Run(() => Client.Close(doc.Id));
        }
        internal async Task CloseCurrentAsync()
        {
            if (Client.IsBusy) return;
            var state = Client.State;
            var doc = state.Documents.FirstOrDefault(item => item.Id == state.ActiveId);
            if (doc != null && await ConfirmDrawingAsync(doc)) Run(() => Client.Close(doc.Id));
        }
    }
}
