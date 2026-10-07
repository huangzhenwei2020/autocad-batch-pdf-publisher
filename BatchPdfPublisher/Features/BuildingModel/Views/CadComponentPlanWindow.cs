using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadComponentPlanSettings
    {
        public string BlockHandle, Code, Category, Axis;
        public double X, Y, Z, Width, Height, MillimetresPerCadUnit;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(BlockHandle)) throw new InvalidDataException("请先选择平面块。");
            if (Axis != "X" && Axis != "Y") throw new InvalidDataException("方向只能选择 X 或 Y。");
            if (Category != "Door" && Category != "Window") throw new InvalidDataException("门窗类别无效。");
            if (double.IsNaN(X) || double.IsInfinity(X) || double.IsNaN(Y) || double.IsInfinity(Y)
                || double.IsNaN(Z) || double.IsInfinity(Z)) throw new InvalidDataException("基点坐标必须是有效数字。");
            new ComponentPlanFrame(0, 0, 0, Axis == "X" ? 1 : 0, Axis == "Y" ? 1 : 0, MillimetresPerCadUnit);
            ComponentPlanSymbols.Validate(new ComponentPlanSymbol { Code = Code, Name = Code, Category = Category,
                Width = Width, Height = Height, Primitives = { new ComponentPlanPrimitive { Kind = "Line", X2 = 1 } } });
        }
    }

    public sealed class CadComponentPlanBlock
    {
        public string Handle, Name;
    }

    public sealed class CadComponentPlanWindow : Window
    {
        private readonly TabControl _tabs = new TabControl();
        private readonly TextBlock _status = new TextBlock { Height = 44, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Func<Window, CadComponentPlanBlock> _pickBlock;
        private readonly Func<Window, double[]> _pickBase;
        private readonly Func<Window, CadComponentPlanSettings, string> _export;

        public CadComponentPlanWindow(string category, Func<Window, CadComponentPlanBlock> pickBlock,
            Func<Window, double[]> pickBase, Func<Window, CadComponentPlanSettings, string> export, string actionCaption = "导出平面")
        {
            _pickBlock = pickBlock; _pickBase = pickBase; _export = export;
            NameScope.SetNameScope(this, new NameScope());
            Title = "图库 · CAD 平面"; Width = 660; Height = 720; MinWidth = 520; MinHeight = 690;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14; UseLayoutRounding = true;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 251));
            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock { Text = "图库 / CAD 平面", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
            foreach (var kind in new[] { "Door", "Window" }) {
                var tab = new TabItem { Header = kind == "Door" ? "门" : "窗", Padding = new Thickness(26, 8, 26, 8),
                    Content = BuildEditor(kind) };
                _tabs.Items.Add(tab);
            }
            _tabs.SelectedIndex = category == "Door" ? 0 : 1;
            _tabs.SelectionChanged += (s, e) => { if (e.Source == _tabs) _status.Text = ""; };
            Grid.SetRow(_tabs, 1); root.Children.Add(_tabs);
            Grid.SetRow(_status, 2); root.Children.Add(_status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var exportButton = Button(actionCaption, "Export", () => Run(() => {
                var settings = ReadSettings();
                var result = _export?.Invoke(this, settings);
                if (result != null) { _status.Foreground = Brushes.DarkGreen; _status.Text = result; }
            }));
            actions.Children.Add(exportButton);
            var close = Button("关闭", "Close", Close); close.IsCancel = true; actions.Children.Add(close);
            Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;
        }

        private Grid BuildEditor(string kind)
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < 9; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            var block = Input(kind + "Block", "未选择"); block.IsReadOnly = true; block.Tag = null;
            var blockRow = new Grid(); blockRow.ColumnDefinitions.Add(new ColumnDefinition());
            blockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); blockRow.Children.Add(block);
            var choose = Button("选择块", kind + "PickBlock", () => Run(() => {
                var selection = _pickBlock?.Invoke(this); if (selection == null) return;
                block.Text = selection.Name + " · " + selection.Handle; block.Tag = selection.Handle;
            })); Grid.SetColumn(choose, 1); blockRow.Children.Add(choose); Row(grid, 0, "平面块", blockRow);
            Row(grid, 1, "门窗编号", Input(kind + "Code", kind == "Door" ? "M0921" : "C1216"));
            var x = Input(kind + "X", "0"); var y = Input(kind + "Y", "0"); var z = Input(kind + "Z", "0");
            var coords = new Grid();
            coords.ColumnDefinitions.Add(new ColumnDefinition());
            coords.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            coords.Children.Add(x);
            var pick = Button("拾取", kind + "PickBase", () => Run(() => {
                var point = _pickBase?.Invoke(this); if (point == null) return;
                if (point.Length != 3) throw new InvalidDataException("基点坐标无效。");
                x.Text = point[0].ToString("G17", CultureInfo.InvariantCulture);
                y.Text = point[1].ToString("G17", CultureInfo.InvariantCulture);
                z.Text = point[2].ToString("G17", CultureInfo.InvariantCulture);
            })); Grid.SetColumn(pick, 1); coords.Children.Add(pick); Row(grid, 2, "基点 X（CAD 坐标）", coords);
            Row(grid, 3, "基点 Y（CAD 坐标）", y);
            Row(grid, 4, "基点 Z（CAD 坐标）", z);
            var axes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var axis in new[] { "X", "Y" }) {
                var radio = new RadioButton { Content = axis, GroupName = kind + "Axis", Height = 36, MinWidth = 80,
                    VerticalContentAlignment = VerticalAlignment.Center, IsChecked = axis == "X" };
                RegisterName(kind + axis + "Axis", radio); axes.Children.Add(radio);
            }
            Row(grid, 5, "沿墙方向（CAD）", axes);
            Row(grid, 6, "洞口宽 mm", Input(kind + "Width", kind == "Door" ? "900" : "1200"));
            Row(grid, 7, "洞口高 mm", Input(kind + "Height", kind == "Door" ? "2100" : "1600"));
            Row(grid, 8, "1 CAD 单位 = mm", Input(kind + "Units", "1"));
            return grid;
        }
        public void SetInitialPlan(ComponentPlanSymbol plan)
        {
            ComponentPlanSymbols.Validate(plan);
            var kind=plan.Category;_tabs.SelectedIndex=kind=="Door"?0:1;
            ((TextBox)FindName(kind+"Code")).Text=plan.Code;
            ((TextBox)FindName(kind+"Width")).Text=plan.Width.ToString("G17",CultureInfo.InvariantCulture);
            ((TextBox)FindName(kind+"Height")).Text=plan.Height.ToString("G17",CultureInfo.InvariantCulture);
        }

        public CadComponentPlanSettings ReadSettings()
        {
            var kind = _tabs.SelectedIndex == 0 ? "Door" : "Window";
            var result = new CadComponentPlanSettings { Category = kind, BlockHandle = (string)((TextBox)FindName(kind + "Block")).Tag,
                Code = ((TextBox)FindName(kind + "Code")).Text.Trim(),
                Axis = ((RadioButton)FindName(kind + "XAxis")).IsChecked == true ? "X" : "Y",
                X = Number(kind + "X", "基点 X"), Y = Number(kind + "Y", "基点 Y"), Z = Number(kind + "Z", "基点 Z"),
                Width = Number(kind + "Width", "洞口宽"), Height = Number(kind + "Height", "洞口高"), MillimetresPerCadUnit = Number(kind + "Units", "单位倍率") };
            result.Validate(); return result;
        }
        private double Number(string name, string label)
        {
            double value;
            if (!double.TryParse(((TextBox)FindName(name)).Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidDataException(label + "必须是有效数字。");
            return value;
        }
        private TextBox Input(string name, string text)
        {
            var input = new TextBox { Text = text, Height = 36, Padding = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RegisterName(name, input); return input;
        }
        private Button Button(string text, string name, Action action)
        {
            var button = new Button { Content = text, Height = 36, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            RegisterName(name, button); button.Click += (s, e) => action(); return button;
        }
        private static void Row(Grid grid, int row, string label, FrameworkElement control)
        {
            var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(title, row); grid.Children.Add(title); Grid.SetRow(control, row); Grid.SetColumn(control, 1); grid.Children.Add(control);
        }
        private void Run(Action action)
        {
            try { _status.Text = ""; action(); }
            catch (Exception ex) { _status.Foreground = Brushes.Firebrick; _status.Text = ex.Message; }
            _status.ToolTip = _status.Text;
        }
    }
}
