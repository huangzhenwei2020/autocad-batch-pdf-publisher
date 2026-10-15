using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadModelFloorEditRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Height { get; set; }
        public string Elevation { get; set; }
        public string Source { get; set; }
        public string Kind { get; set; }
        public List<string> Sources { get; set; }
    }

    public sealed class CadModelProjectWindow : Window
    {
        private const string Independent = "独立平面";
        private readonly ObservableCollection<CadModelFloorEditRow> _rows = new ObservableCollection<CadModelFloorEditRow>();
        private readonly DataGrid _grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single };
        private readonly TextBox _name = new TextBox(), _datum = new TextBox();
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        private string _datumId;
        private bool _previewing;
        public string ModelName => _name.Text.Trim();
        public List<StoreyModel> ResultStoreys { get; private set; }

        public CadModelProjectWindow(BuildingModelDocument model, bool creating)
        {
            Title = creating ? "新建建筑模型" : "楼层设置";
            Width = 940; Height = 570; MinWidth = 780; MinHeight = 440;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14; UseLayoutRounding = true;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 251)); WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new Grid { Margin = new Thickness(18) };
            foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                root.RowDefinitions.Add(new RowDefinition { Height = height });
            var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition());
            header.Children.Add(new TextBlock { Text = "模型名称", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            _name.Text = model.Name; _name.IsReadOnly = !creating; _name.Padding = new Thickness(7); Grid.SetColumn(_name, 1); header.Children.Add(_name); root.Children.Add(header);
            var anchor = model.FindStorey("1F") ?? model.Storeys.OrderBy(s => Math.Abs(s.Elevation)).First();
            _datumId = anchor.Id; _datum.Text = anchor.Elevation.ToString("0.##", CultureInfo.InvariantCulture); _datum.Width = 110; _datum.Padding = new Thickness(5);
            var datum = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
            datum.Children.Add(new TextBlock { Text = "基准标高 mm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }); datum.Children.Add(_datum);
            Grid.SetRow(datum, 1); root.Children.Add(datum);
            AddColumn("楼层 ID", "Id", 65, true); AddColumn("名称 / 标准层范围", "Name", 150); AddColumn("每层层高 mm", "Height", 110); AddColumn("首层标高 mm", "Elevation", 110, true);
            AddChoice("平面来源", "Source", null, 130); AddChoice("楼层类型", "Kind", new[] { "普通层", "屋顶层", "机房层" }, 100);
            foreach (var s in model.Storeys.OrderBy(s => s.Elevation).Where(s => string.IsNullOrWhiteSpace(s.StandardGroupId) || s.StandardGroupId == s.Id)) Add(s);
            _grid.ItemsSource = _rows; Grid.SetRow(_grid, 2); root.Children.Add(_grid);
            _grid.CellEditEnding += (s, e) => Dispatcher.BeginInvoke(new Action(Preview));
            _datum.TextChanged += (s, e) => Preview();
            var info = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
            info.Children.Add(new TextBlock { Text = "楼层名称可填 4～15层；该范围共用首层平面，标高按层高累加。删除楼层或改为共用平面会同步处理本层构件。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray }); info.Children.Add(_error);
            Grid.SetRow(info, 3); root.Children.Add(info);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            void Button(string label, Action action, bool cancel = false)
            {
                var button = new Button { Content = label, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(4), IsCancel = cancel };
                button.Click += (s, e) => { try { action(); } catch (Exception ex) { _error.Text = ex.Message; } }; actions.Children.Add(button);
            }
            Button("地下层", () => Append("B", "地下", StoreyKind.Normal, true));
            Button("新增楼层", () => Append("F", "层", StoreyKind.Normal));
            Button("标准层", () => Append("F", "层", StoreyKind.Normal, range: true));
            Button("屋顶层", () => Append("RF", "屋顶层", StoreyKind.Roof));
            Button("机房层", () => Append("MR", "机房层", StoreyKind.MachineRoom));
            Button("删除楼层", () => { CommitCells(); if (_rows.Count == 1) throw new ArgumentException("至少保留一个楼层。");
                if (_grid.SelectedItem is CadModelFloorEditRow row) { _rows.Remove(row); if (row.Id == _datumId) _datumId = _rows[0].Id; Preview(); } });
            Button("取消", () => { DialogResult = false; }, true);
            Button(creating ? "创建模型" : "应用并保存", () => { ResultStoreys = Collect(); if (ModelName.Length == 0) throw new ArgumentException("请输入模型名称。"); DialogResult = true; });
            Grid.SetRow(actions, 4); root.Children.Add(actions); Content = root; Preview();
        }
        private void AddColumn(string title, string property, double width, bool readOnly = false)
        {
            _grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = width, IsReadOnly = readOnly });
        }
        private void AddChoice(string title, string property, string[] options, double width)
        {
            var combo = new FrameworkElementFactory(typeof(ComboBox));
            if (options == null) combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Sources"));
            else combo.SetValue(ItemsControl.ItemsSourceProperty, options);
            combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            _grid.Columns.Add(new DataGridTemplateColumn { Header = title, CellTemplate = new DataTemplate { VisualTree = combo }, Width = width });
        }
        private void Add(StoreyModel s)
        {
            _rows.Add(new CadModelFloorEditRow { Id = s.Id, Name = s.StandardFloorRange ?? s.Name, Height = s.Height.ToString("0.##", CultureInfo.InvariantCulture),
                Elevation = s.Elevation.ToString("0.##", CultureInfo.InvariantCulture), Source = s.TemplateStoreyId ?? Independent,
                Kind = s.Kind == StoreyKind.Roof ? "屋顶层" : s.Kind == StoreyKind.MachineRoom ? "机房层" : "普通层" });
        }
        private void Append(string prefix, string name, StoreyKind kind, bool basement = false, bool range = false)
        {
            CommitCells(); var n = 1;
            while (_rows.Any(r => r.Id == (prefix == "F" ? n + "F" : prefix + (n == 1 && !basement ? "" : n.ToString()))
                || prefix == "F" && StandardStoreyLayout.TryParseRange(r.Name, out var first, out var last) && n >= first && n <= last)) n++;
            var id = prefix == "F" ? n + "F" : prefix + (n == 1 && !basement ? "" : n.ToString());
            var s = new StoreyModel { Id = id, Name = range ? n + "～" + (n + 1) + "层" : basement ? name + n + "层" : prefix == "F" ? n + name : name,
                Height = 3300, Kind = kind };
            Add(s); if (basement) _rows.Move(_rows.Count - 1, 0); Preview();
        }
        private void CommitCells()
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        private List<StoreyModel> Collect()
        {
            CommitCells(); double datum;
            if (!Number(_datum.Text, out datum)) throw new ArgumentException("基准标高无效。");
            var input = new List<StoreyModel>();
            foreach (var row in _rows)
            {
                double height; if (!Number(row.Height, out height) || height <= 0) throw new ArgumentException("层高必须是大于 0 的有效数字。");
                input.Add(new StoreyModel { Id = row.Id, Name = row.Name?.Trim(), Height = height,
                    Kind = row.Kind == "屋顶层" ? StoreyKind.Roof : row.Kind == "机房层" ? StoreyKind.MachineRoom : StoreyKind.Normal,
                    TemplateStoreyId = row.Source == Independent ? null : row.Source });
            }
            var floors = StoreyElevationLayout.Resolve(StandardStoreyLayout.ExpandRanges(input), _datumId, datum);
            BuildingModelProjectWorkflow.ValidateStoreys(floors); return floors;
        }
        private void Preview()
        {
            if (_rows.Count == 0 || _previewing) return;
            _previewing=true;
            try
            {
            foreach (var row in _rows) row.Sources = new[] { Independent }.Concat(_rows.Where(r => r.Id != row.Id && r.Kind == "普通层" && r.Source == Independent).Select(r => r.Id)).ToList();
            try { var floors = Collect(); foreach (var row in _rows) row.Elevation = floors.Single(f => f.Id == row.Id).Elevation.ToString("0.##", CultureInfo.InvariantCulture); _error.Text = ""; }
            catch (Exception ex) { _error.Text = ex.Message; }
            _grid.Items.Refresh();
            }
            finally { _previewing=false; }
        }
        private static bool Number(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
