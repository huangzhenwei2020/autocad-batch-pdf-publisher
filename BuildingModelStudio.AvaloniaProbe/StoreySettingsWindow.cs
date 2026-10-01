using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class StoreySettingsWindow : Window
{
    private const string Independent = "独立楼层";
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly List<(string id, Grid row, TextBox name, TextBox elevation,
        TextBox height, ComboBox template, ComboBox kind)> _entries = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.OrangeRed };
    private readonly TextBox _datum = new() { Height = 34, MinHeight = 34 };
    private readonly TextBlock _datumLabel = new();
    private readonly HashSet<string> _occupiedIds;
    private string _datumId;
    public List<StoreyModel> ResultStoreys { get; private set; } = new();

    public StoreySettingsWindow(BuildingModelDocument model)
    {
        _occupiedIds = new HashSet<string>(model.Walls.Select(w => w.StoreyId)
            .Concat(model.Columns.Select(c => c.StoreyId))
            .Concat(model.Slabs.Select(s => s.StoreyId))
            .Concat(model.Stairs.Select(s => s.StoreyId))
            .Concat(model.Roofs.Select(r => r.StoreyId))
            .Concat(model.Rooms.Select(r => r.StoreyId)), StringComparer.OrdinalIgnoreCase);
        Title = "楼层设置"; Width = 960; Height = 540;
        MinWidth = 960; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#151B23"));
        Foreground = Brushes.White;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(20), RowSpacing = 12 };
        root.Children.Add(new TextBlock { Text = "楼层设置", FontSize = 22, FontWeight = FontWeight.Bold });
        var help = new TextBlock { Text = "只需设置一层基准标高及各层层高；地上向上、地下向下自动推算（毫米）。",
            TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(help, 1); root.Children.Add(help);
        var anchor = model.FindStorey("1F") ?? model.Storeys.OrderBy(s => Math.Abs(s.Elevation)).First();
        _datumId = anchor.Id;
        _datum.Text = anchor.Elevation.ToString("0.##", CultureInfo.CurrentCulture);
        _datum.TextChanged += (_, _) => RecalculatePreview();
        var datumRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _datumLabel.Text = $"{anchor.Name}基准标高 mm";
        _datumLabel.VerticalAlignment = VerticalAlignment.Center;
        datumRow.Children.Add(_datumLabel);
        datumRow.Children.Add(_datum);
        Grid.SetRow(datumRow, 2); root.Children.Add(datumRow);
        var header = NewRow();
        AddText(header, 0, "楼层 ID"); AddText(header, 1, "名称");
        AddText(header, 2, "自动标高 mm"); AddText(header, 3, "层高 mm");
        AddText(header, 4, "标准层来源"); AddText(header, 5, "操作");
        AddText(header, 6, "楼层类型");
        _rows.Children.Add(header);
        foreach (var storey in model.Storeys.OrderBy(s => s.Elevation)) AddStorey(storey);
        RefreshTemplateOptions();
        RecalculatePreview();
        var scroller = new ScrollViewer { Content = _rows };
        Grid.SetRow(scroller, 3); root.Children.Add(scroller);
        Grid.SetRow(_error, 4); root.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Spacing = 9 };
        var add = new Button { Content = "新增楼层" };
        add.Click += (_, _) =>
        {
            var next = 1;
            while (_entries.Any(e => string.Equals(e.id, next + "F", StringComparison.OrdinalIgnoreCase))) next++;
            var top = _entries.Select(e => (TryNumber(e.elevation.Text, out var z)
                && TryNumber(e.height.Text, out var h)) ? z + h : 0d).DefaultIfEmpty(0d).Max();
            AddStorey(new StoreyModel { Id = next + "F", Name = next + "层",
                Elevation = top, Height = 3300 });
            RefreshTemplateOptions();
            RecalculatePreview();
        };
        var addBasement = new Button { Content = "新增地下层" };
        addBasement.Click += (_, _) =>
        {
            var next = 1;
            while (_entries.Any(e => string.Equals(e.id, "B" + next,
                StringComparison.OrdinalIgnoreCase))) next++;
            var bottom = _entries.Select(e => TryNumber(e.elevation.Text, out var z) ? z : 0d)
                .DefaultIfEmpty(0d).Min();
            AddStorey(new StoreyModel { Id = "B" + next, Name = "地下" + next + "层",
                Elevation = bottom - 3300d, Height = 3300d }, true);
            RefreshTemplateOptions();
            RecalculatePreview();
        };
        var cancel = new Button { Content = "取消" };
        var addRoof = new Button { Content = "新增屋顶层" };
        addRoof.Click += (_, _) => AddSpecialStorey(StoreyKind.Roof, "RF", "屋顶层");
        var addMachine = new Button { Content = "新增机房层" };
        addMachine.Click += (_, _) => AddSpecialStorey(StoreyKind.MachineRoom, "MR", "机房层");
        cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = "应用楼层" };
        save.Click += (_, _) => { if (Collect()) Close(true); };
        actions.Children.Add(addBasement); actions.Children.Add(add);
        actions.Children.Add(addRoof); actions.Children.Add(addMachine);
        actions.Children.Add(cancel); actions.Children.Add(save);
        Grid.SetRow(actions, 5); root.Children.Add(actions);
        Content = root;
    }

    private void AddStorey(StoreyModel storey, bool basement = false)
    {
        var row = NewRow();
        AddText(row, 0, storey.Id);
        var name = AddInput(row, 1, storey.Name);
        var elevation = AddInput(row, 2, storey.Elevation.ToString("0.##", CultureInfo.CurrentCulture));
        elevation.IsReadOnly = true;
        var height = AddInput(row, 3, storey.Height.ToString("0.##", CultureInfo.CurrentCulture));
        height.TextChanged += (_, _) => RecalculatePreview();
        var template = new ComboBox { Height = 34, MinHeight = 34, Tag = storey.TemplateStoreyId,
            IsEnabled = !_occupiedIds.Contains(storey.Id),
            Background = new SolidColorBrush(Color.Parse("#202D3B")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#496273")) };
        if (!template.IsEnabled) ToolTip.SetTip(template, "该楼层已有独立构件，不能改为标准层引用");
        Grid.SetColumn(template, 4);
        row.Children.Add(template);
        var kind = new ComboBox { ItemsSource = new[] { "普通层", "屋顶层", "机房层" },
            SelectedIndex = (int)storey.Kind, Height = 34, MinHeight = 34,
            Background = new SolidColorBrush(Color.Parse("#202D3B")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#496273")) };
        Grid.SetColumn(kind, 6); row.Children.Add(kind);
        kind.SelectionChanged += (_, _) => RefreshTemplateOptions();
        var remove = new Button { Content = "删除", Height = 34, MinHeight = 34,
            IsEnabled = !_occupiedIds.Contains(storey.Id) };
        if (!remove.IsEnabled) ToolTip.SetTip(remove, "该楼层已有构件，不能直接删除");
        remove.Click += (_, _) => DeleteStorey(storey.Id);
        Grid.SetColumn(remove, 5);
        row.Children.Add(remove);
        if (basement)
        { _entries.Insert(0, (storey.Id, row, name, elevation, height, template, kind)); _rows.Children.Insert(1, row); }
        else
        { _entries.Add((storey.Id, row, name, elevation, height, template, kind)); _rows.Children.Add(row); }
    }

    private void AddSpecialStorey(StoreyKind kind, string prefix, string name)
    {
        var id = prefix;
        for (var n = 2; _entries.Any(e => e.id == id); n++) id = prefix + n;
        var top = _entries.Select(e => TryNumber(e.elevation.Text, out var z)
            && TryNumber(e.height.Text, out var h) ? z + h : 0d).DefaultIfEmpty(0d).Max();
        AddStorey(new StoreyModel { Id = id, Name = name, Kind = kind,
            Elevation = top, Height = 3000d });
        RefreshTemplateOptions();
        RecalculatePreview();
    }

    private void RefreshTemplateOptions()
    {
        foreach (var entry in _entries)
        {
            var preferred = entry.template.SelectedItem as string ?? entry.template.Tag as string;
            var options = new List<string> { Independent };
            if (entry.kind.SelectedIndex == 0)
                options.AddRange(_entries.Where(other => other.id != entry.id
                    && other.kind.SelectedIndex == 0).Select(other => other.id));
            entry.template.IsEnabled = entry.kind.SelectedIndex == 0 && !_occupiedIds.Contains(entry.id);
            entry.template.ItemsSource = options;
            entry.template.SelectedItem = preferred != null && options.Contains(preferred) ? preferred : Independent;
            entry.template.Tag = null;
        }
    }

    private void DeleteStorey(string id)
    {
        var index = _entries.FindIndex(e => string.Equals(e.id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        if (_entries.Count == 1) { _error.Text = "至少保留一个楼层。"; return; }
        if (_occupiedIds.Contains(id)) { _error.Text = "请先移走该楼层的构件。"; return; }
        if (_entries.Any(e => string.Equals(e.template.SelectedItem as string, id,
            StringComparison.OrdinalIgnoreCase)))
        { _error.Text = "该楼层是标准层来源，请先取消其他楼层的引用。"; return; }
        var removed = _entries[index];
        _entries.RemoveAt(index);
        _rows.Children.Remove(removed.row);
        if (string.Equals(_datumId, id, StringComparison.OrdinalIgnoreCase))
        {
            var next = _entries[Math.Min(index, _entries.Count - 1)];
            _datumId = next.id;
            _datumLabel.Text = (next.name.Text ?? next.id) + "基准标高 mm";
            _datum.Text = next.elevation.Text;
        }
        _error.Text = "";
        RefreshTemplateOptions();
        RecalculatePreview();
    }

    private void RecalculatePreview()
    {
        if (_entries.Count == 0 || !TryNumber(_datum.Text, out var datum)) return;
        var input = new List<StoreyModel>();
        foreach (var entry in _entries)
        {
            if (!TryNumber(entry.height.Text, out var height) || height <= 0d) return;
            input.Add(new StoreyModel { Id = entry.id, Name = entry.name.Text ?? entry.id,
                Height = height, Kind = (StoreyKind)entry.kind.SelectedIndex,
                TemplateStoreyId = TemplateId(entry.template) });
        }
        foreach (var (entry, floor) in _entries.Zip(StoreyElevationLayout.Resolve(input, _datumId, datum)))
            entry.elevation.Text = floor.Elevation.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private bool Collect()
    {
        var input = new List<StoreyModel>();
        if (!TryNumber(_datum.Text, out var datum))
        { _error.Text = "一层基准标高无效。"; return false; }
        foreach (var entry in _entries)
        {
            if (string.IsNullOrWhiteSpace(entry.name.Text)
                || !TryNumber(entry.height.Text, out var height) || height <= 0d)
            { _error.Text = "请填写名称以及大于 0 的层高。"; return false; }
            input.Add(new StoreyModel { Id = entry.id, Name = entry.name.Text.Trim(),
                Height = height, Kind = (StoreyKind)entry.kind.SelectedIndex,
                TemplateStoreyId = TemplateId(entry.template) });
        }
        if (input.Any(s => !string.IsNullOrWhiteSpace(s.TemplateStoreyId)
            && (input.FirstOrDefault(source => source.Id == s.TemplateStoreyId)?.TemplateStoreyId != null
                || _occupiedIds.Contains(s.Id))))
        { _error.Text = "标准层只能引用独立楼层；已有独立构件的楼层不能作为引用层。"; return false; }
        ResultStoreys = StoreyElevationLayout.Resolve(input, _datumId, datum);
        return true;
    }

    private static string? TemplateId(ComboBox combo)
    {
        var selected = combo.SelectedItem as string;
        return selected == Independent ? null : selected;
    }

    private static Grid NewRow() => new() { ColumnDefinitions = new ColumnDefinitions("60,*,120,110,120,54,100"),
        ColumnSpacing = 8, MinHeight = 40 };

    private static void AddText(Grid row, int column, string value)
    {
        var label = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, column); row.Children.Add(label);
    }

    private static TextBox AddInput(Grid row, int column, string? value)
    {
        var input = new TextBox { Text = value, Height = 34, MinHeight = 34,
            Background = new SolidColorBrush(Color.Parse("#202D3B")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#496273")) };
        Grid.SetColumn(input, column); row.Children.Add(input);
        return input;
    }

    private static bool TryNumber(string? text, out double value)
        => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value);
}
