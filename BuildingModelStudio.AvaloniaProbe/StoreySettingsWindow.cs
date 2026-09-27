using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class StoreySettingsWindow : Window
{
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly List<(string id, TextBox name, TextBox elevation, TextBox height)> _entries = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.OrangeRed };
    private readonly TextBox _datum = new();
    private readonly string _datumId;
    public List<StoreyModel> ResultStoreys { get; private set; } = new();

    public StoreySettingsWindow(BuildingModelDocument model)
    {
        Title = "楼层设置"; Width = 720; Height = 540;
        MinWidth = 700; MinHeight = 360;
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
        datumRow.Children.Add(new TextBlock { Text = $"{anchor.Name}基准标高 mm",
            VerticalAlignment = VerticalAlignment.Center });
        datumRow.Children.Add(_datum);
        Grid.SetRow(datumRow, 2); root.Children.Add(datumRow);
        var header = NewRow();
        AddText(header, 0, "楼层 ID"); AddText(header, 1, "名称");
        AddText(header, 2, "自动标高 mm"); AddText(header, 3, "层高 mm");
        _rows.Children.Add(header);
        foreach (var storey in model.Storeys.OrderBy(s => s.Elevation)) AddStorey(storey);
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
            RecalculatePreview();
        };
        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = "应用楼层" };
        save.Click += (_, _) => { if (Collect()) Close(true); };
        actions.Children.Add(addBasement); actions.Children.Add(add);
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
        if (basement)
        { _entries.Insert(0, (storey.Id, name, elevation, height)); _rows.Children.Insert(1, row); }
        else
        { _entries.Add((storey.Id, name, elevation, height)); _rows.Children.Add(row); }
    }

    private void RecalculatePreview()
    {
        if (_entries.Count == 0 || !TryNumber(_datum.Text, out var datum)) return;
        var input = new List<StoreyModel>();
        foreach (var entry in _entries)
        {
            if (!TryNumber(entry.height.Text, out var height) || height <= 0d) return;
            input.Add(new StoreyModel { Id = entry.id, Name = entry.name.Text ?? entry.id,
                Height = height });
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
            input.Add(new StoreyModel { Id = entry.id, Name = entry.name.Text.Trim(), Height = height });
        }
        ResultStoreys = StoreyElevationLayout.Resolve(input, _datumId, datum);
        return true;
    }

    private static Grid NewRow() => new() { ColumnDefinitions = new ColumnDefinitions("100,180,160,160"),
        ColumnSpacing = 8, MinHeight = 40 };

    private static void AddText(Grid row, int column, string value)
    {
        var label = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, column); row.Children.Add(label);
    }

    private static TextBox AddInput(Grid row, int column, string? value)
    {
        var input = new TextBox { Text = value, MinWidth = 130,
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
