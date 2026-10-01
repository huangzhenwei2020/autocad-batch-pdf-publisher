using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;
using Microsoft.Win32;

namespace BatchPdfPublisher.Views
{
    public sealed class TianzhengOpeningRegistrationWindow : Window
    {
        private readonly CadBuildingProbeDocument _probe;
        private readonly OpeningTypeLibraryDocument _library;
        private readonly List<CadOpeningRegistrationRow> _rows;
        private readonly IEnumerable<CadOpeningSillEntry> _sillEntries;
        private readonly CadFloorRegistrationContext _floor;
        private readonly Func<Window, CadOpeningRegistrationRow, CadOpeningPlacement> _pickPlacement;
        private readonly DataGrid _table = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false,
            CanUserDeleteRows = false, EnableRowVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column };
        private readonly ComboBox _type = new ComboBox { MinHeight = 34, MaxDropDownHeight = 300 };
        private readonly TextBox _width = new TextBox(), _height = new TextBox(), _sill = new TextBox();
        private readonly CheckBox _confirm = new CheckBox { Content = "已核对本洞口尺寸、编号与底高", Margin = new Thickness(0, 14, 0, 10) };
        private readonly CheckBox _units = new CheckBox { Content = "已核对源图尺寸单位为毫米（mm）" };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        private bool _loading;
        private CadOpeningRegistrationRow Selected => _table.SelectedItem as CadOpeningRegistrationRow;

        public TianzhengOpeningRegistrationWindow(CadBuildingProbeDocument probe, OpeningTypeLibraryDocument library,
            IEnumerable<CadOpeningSillEntry> sillEntries = null, CadFloorRegistrationContext floor = null,
            Func<Window, CadOpeningRegistrationRow, CadOpeningPlacement> pickPlacement = null)
        {
            _probe = probe; _library = library; _rows = CadOpeningRegistration.FromProbe(probe); _sillEntries = sillEntries;
            _floor = floor;
            _pickPlacement = pickPlacement;
            if (floor != null) _units.Content = "已核对源图单位倍率及换算后的毫米尺寸";
            if (floor != null)
                foreach (var row in _rows) { row.Width *= floor.Alignment.MillimetresPerCadUnit; row.Height *= floor.Alignment.MillimetresPerCadUnit; }
            Title = "天正门窗 · 登记核对"; Width = 1120; Height = 700; MinWidth = 720; MinHeight = 440;
            FontSize = 14; FontFamily = new FontFamily("Microsoft YaHei UI"); UseLayoutRounding = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 251));
            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock { Text = "门窗登记核对 · " + library.ProjectName + "\n"
                + (floor == null ? "" : floor.Storey.Name + " · 标高 " + floor.Storey.Elevation + " mm · 墙 " + floor.WallCandidates.Count
                    + " · 门窗 " + _rows.Count + " · 标准层引用 " + floor.ReferenceStoreys.Count + " 层\n")
                + "CAD 提供洞口宽高；按编号选择已有 MCLM 立面做法。此步保存参数清单，定位和宿主墙随后登记。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
            _table.Columns.Add(new DataGridCheckBoxColumn { Header = "登记", Binding = new Binding("Include"), Width = 50 });
            foreach (var column in new[] { new[] { "对象", "SourceHandle" }, new[] { "编号", "Code" },
                new[] { "宽 mm", "Width" }, new[] { "高 mm", "Height" }, new[] { "状态", "Status" } })
                _table.Columns.Add(new DataGridTextColumn { Header = column[0], Binding = new Binding(column[1]),
                    IsReadOnly = true, Width = column[1] == "Status" ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(85) });
            _table.ItemsSource = _rows; body.Children.Add(_table);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "编号与立面做法", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            _type.ItemsSource = library.Types.Select((t, i) => (i + 1) + " · " + t.Code + " · " + t.Width + "×" + t.Height
                + " · " + t.ElevationType + " · " + t.DivisionPreset).ToList();
            panel.Children.Add(_type);
            panel.Children.Add(new TextBlock { Text = "同编号的历史参数分别列出；按实际做法选择。未找到编号时，先在 MCLM 设置后重新登记。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
            AddInput(panel, "洞口宽度 mm", _width); AddInput(panel, "洞口高度 mm", _height); AddInput(panel, "窗底离地高度 mm（本层楼板至窗底）", _sill);
            panel.Children.Add(_confirm); panel.Children.Add(_status);
            if (floor != null && pickPlacement != null)
            {
                var pick = new Button { Content = "在 CAD 拾取宿主墙与洞口定位", Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 8) };
                pick.Click += (s, e) => PickPlacement(); panel.Children.Add(pick);
                _confirm.Content = "已核对尺寸、编号、底高及已拾取的定位";
            }
            panel.Children.Add(new TextBlock { Text = "可补充宿主墙与洞口两端定位；没有定位的记录仍为待登记。\n天正未提供编号时手动选做法；对象句柄仅作来源身份，不作为门窗编号。",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
            var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(scroll, 2); body.Children.Add(scroll);
            Grid.SetRow(body, 1); root.Children.Add(body);
            var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(_units);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var export = new Button { Content = "保存勾选登记清单", Padding = new Thickness(14, 8, 14, 8) };
            export.Click += (s, e) => Save(); actions.Children.Add(export);
            var close = new Button { Content = "关闭", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            close.Click += (s, e) => Close(); actions.Children.Add(close);
            Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer);
            Content = root;
            _table.SelectionChanged += (s, e) => ShowRow();
            _type.SelectionChanged += (s, e) => ChooseType();
            _width.TextChanged += (s, e) => Edit(); _height.TextChanged += (s, e) => Edit(); _sill.TextChanged += (s, e) => Edit();
            _confirm.Checked += (s, e) => Confirm(); _confirm.Unchecked += (s, e) => Confirm();
            if (_rows.Count > 0) _table.SelectedIndex = 0;
            else _status.Text = "没有选到可登记的天正洞口，请选择 TCH_OPENING 对象。";
            Loaded += (s, e) => { Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32); Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32); };
        }

        private static void AddInput(Panel panel, string title, TextBox box)
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 10, 0, 4) });
            box.MinHeight = 32; box.Padding = new Thickness(6); panel.Children.Add(box);
        }
        private static string Text(double? value) => value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";
        private static double? Value(string text) { double value; return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? (double?)value : null; }
        private void ShowRow()
        {
            _loading = true; var row = Selected;
            _type.SelectedIndex = row != null && row.TypeIndex.HasValue ? row.TypeIndex.Value : -1;
            _width.Text = Text(row?.Width); _height.Text = Text(row?.Height); _sill.Text = Text(row?.Sill);
            _confirm.IsChecked = row != null && row.Confirmed; _loading = false; UpdateStatus();
        }
        private void ChooseType()
        {
            if (_loading || Selected == null || _type.SelectedIndex < 0) return;
            var row = Selected; var type = _library.Types[_type.SelectedIndex];
            row.TypeIndex = _type.SelectedIndex; row.Code = type.Code; row.Confirmed = false;
            string source;
            row.Sill = CadOpeningRegistration.ResolveSill(_sillEntries, type.Code, _type.SelectedIndex, _floor?.Storey.Name, out source);
            row.SillSource = source;
            ShowRow();
        }
        private void Edit()
        {
            if (_loading || Selected == null) return;
            var sill = Value(_sill.Text);
            if (sill != Selected.Sill) Selected.SillSource = "用户输入并确认";
            Selected.Width = Value(_width.Text); Selected.Height = Value(_height.Text); Selected.Sill = sill;
            Selected.Confirmed = false; _confirm.IsChecked = false; UpdateStatus();
        }
        private void Confirm() { if (_loading || Selected == null) return; Selected.Confirmed = _confirm.IsChecked == true; UpdateStatus(); }
        private void PickPlacement()
        {
            if (Selected == null) return;
            try
            {
                var placement = _pickPlacement(this, Selected);
                // Escape at any pick keeps the previous placement and parameter edits.
                if (placement == null) return;
                Selected.Placement = placement; Selected.Confirmed = false;
                ShowRow();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "定位未更新"); }
        }
        private void UpdateStatus()
        {
            if (Selected == null) return;
            var row = Selected; row.Status = CadOpeningRegistration.Validate(row, _library)
                ?? (row.Placement == null ? "参数已确认 · 定位待登记" : "参数与拾取定位已确认");
            _status.Text = row.Status;
            if (!string.IsNullOrWhiteSpace(row.SillSource)) _status.Text += "\n离地高度：" + row.SillSource;
            if (_floor != null && row.Sill.HasValue && !double.IsNaN(row.Sill.Value) && !double.IsInfinity(row.Sill.Value) && row.Sill.Value >= 0)
                _status.Text += "\n窗底标高：" + _floor.Alignment.OpeningBottomElevation(_floor.Storey, row.Sill.Value).ToString("0.###", CultureInfo.InvariantCulture) + " mm";
            if (row.TypeIndex.HasValue) _status.Text += "\n" + CadOpeningRegistration.SizeNote(row, _library.Types[row.TypeIndex.Value]);
            if (row.Placement != null)
            {
                try
                {
                    var location = row.Placement.Revalidate(_floor, row.Width ?? 0);
                    _status.Text += "\n宿主墙：" + location.HostSourceHandle + " · 距墙起点 " + Text(location.DistanceFromWallStart)
                        + " mm\n模型位置：" + Text(location.ModelCenter.X) + ", " + Text(location.ModelCenter.Y) + " mm";
                }
                catch (Exception ex) { row.Status = "定位须重核：" + ex.Message; _status.Text += "\n" + row.Status; }
            }

        }
        private void Save()
        {
            _table.CommitEdit(DataGridEditingUnit.Cell, true); _table.CommitEdit(DataGridEditingUnit.Row, true);
            try
            {
                var document = CadOpeningRegistration.Build(_probe, _library, _rows, _units.IsChecked == true, _floor);
                var dialog = new SaveFileDialog { Title = "保存门窗参数登记清单", Filter = "门窗登记清单 (*.openings-register.json)|*.openings-register.json",
                    FileName = "楼层登记.openings-register.json", AddExtension = true };
                if (dialog.ShowDialog(this) != true) return;
                CadOpeningRegistration.Save(dialog.FileName, document);
                MessageBox.Show(this, "已保存 " + document.Openings.Count + " 个门窗参数。定位和宿主墙待登记，本文件不是 model.json。");
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "登记清单尚未保存"); }
        }
    }
}
