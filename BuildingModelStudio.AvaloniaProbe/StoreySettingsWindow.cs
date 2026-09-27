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
    public List<StoreyModel> ResultStoreys { get; private set; } = new();

    public StoreySettingsWindow(BuildingModelDocument model)
    {
        Title = "楼层设置"; Width = 720; Height = 490;
        MinWidth = 700; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#151B23"));
        Foreground = Brushes.White;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(20), RowSpacing = 12 };
        root.Children.Add(new TextBlock { Text = "楼层设置", FontSize = 22, FontWeight = FontWeight.Bold });
        var help = new TextBlock { Text = "编辑楼层名称、结构标高和层高（毫米）。墙高为 0 时随所属楼层层高。",
            TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(help, 1); root.Children.Add(help);
        var header = NewRow();
        AddText(header, 0, "楼层 ID"); AddText(header, 1, "名称");
        AddText(header, 2, "标高 mm"); AddText(header, 3, "层高 mm");
        _rows.Children.Add(header);
        foreach (var storey in model.Storeys.OrderBy(s => s.Elevation)) AddStorey(storey);
        var scroller = new ScrollViewer { Content = _rows };
        Grid.SetRow(scroller, 2); root.Children.Add(scroller);
        Grid.SetRow(_error, 3); root.Children.Add(_error);
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
        };
        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = "应用楼层" };
        save.Click += (_, _) => { if (Collect()) Close(true); };
        actions.Children.Add(add); actions.Children.Add(cancel); actions.Children.Add(save);
        Grid.SetRow(actions, 4); root.Children.Add(actions);
        Content = root;
    }

    private void AddStorey(StoreyModel storey)
    {
        var row = NewRow();
        AddText(row, 0, storey.Id);
        var name = AddInput(row, 1, storey.Name);
        var elevation = AddInput(row, 2, storey.Elevation.ToString("0.##", CultureInfo.CurrentCulture));
        var height = AddInput(row, 3, storey.Height.ToString("0.##", CultureInfo.CurrentCulture));
        _entries.Add((storey.Id, name, elevation, height));
        _rows.Children.Add(row);
    }

    private bool Collect()
    {
        var result = new List<StoreyModel>();
        foreach (var entry in _entries)
        {
            if (string.IsNullOrWhiteSpace(entry.name.Text)
                || !TryNumber(entry.elevation.Text, out var elevation)
                || !TryNumber(entry.height.Text, out var height) || height <= 0d)
            { _error.Text = "请填写名称、有效标高以及大于 0 的层高。"; return false; }
            result.Add(new StoreyModel { Id = entry.id, Name = entry.name.Text.Trim(),
                Elevation = elevation, Height = height });
        }
        ResultStoreys = result;
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
