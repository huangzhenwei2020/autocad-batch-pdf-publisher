using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

/// <summary>Building-wide axis numbering; empty endpoint fields inherit the main number.</summary>
internal sealed class AxisSettingsWindow : Window
{
    private readonly List<Entry> _entries = new();
    private readonly HashSet<string> _originalIds;
    public List<AxisModel> ResultAxes { get; private set; } = new();

    private sealed record Entry(AxisModel Axis, CheckBox Auto, TextBox Main,
        TextBox Start, TextBox End);

    public AxisSettingsWindow(BuildingModelDocument model)
    {
        Title = "轴号设置";
        Width = 900; Height = 480; MinWidth = 700; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#151B23"));
        Foreground = new SolidColorBrush(Color.Parse("#E8F1F4"));
        _originalIds = model.Axes.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(20), RowSpacing = 12 };
        root.Children.Add(new TextBlock { Text = "整栋共用轴网 · 轴号设置", FontSize = 22,
            FontWeight = Avalonia.Media.FontWeight.Bold });
        var help = new TextBlock { Text = "默认自动编号。关闭“自动”可分别填写两端轴号；竖轴为下 / 上，横轴为左 / 右。两端留空时沿用主轴号。支持字母、数字、-、/、撇号。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Grid.SetRow(help, 1); root.Children.Add(help);

        var list = new StackPanel { Spacing = 5 };
        var header = NewRow();
        AddCell(header, 0, "方向 / 坐标"); AddCell(header, 1, "自动");
        AddCell(header, 2, "主轴号"); AddCell(header, 3, "下 / 左端");
        AddCell(header, 4, "上 / 右端");
        list.Children.Add(header);
        var verticalIndex = 0;
        var horizontalIndex = 0;
        foreach (var axis in BuildingAxisLayout.Resolve(model)
            .OrderBy(a => a.Vertical ? 0 : 1).ThenBy(a => a.Position))
        {
            var automaticName = axis.Vertical ? (++verticalIndex).ToString()
                : PlanEditing.LetterName(horizontalIndex++);
            var original = model.Axes.FirstOrDefault(a => string.Equals(a.Id, axis.Id,
                StringComparison.OrdinalIgnoreCase));
            var manual = original != null && ((!string.IsNullOrWhiteSpace(original.Name)
                    && !string.Equals(original.Name, automaticName, StringComparison.OrdinalIgnoreCase))
                || !string.IsNullOrWhiteSpace(original.StartName)
                || !string.IsNullOrWhiteSpace(original.EndName));
            var row = NewRow();
            AddCell(row, 0, (axis.Vertical ? "竖轴 X=" : "横轴 Y=") + axis.Position.ToString("0.##") + " mm");
            var automatic = new CheckBox { IsChecked = !manual, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(automatic, 1); row.Children.Add(automatic);
            var main = NewInput(original?.Name ?? axis.Name, row, 2);
            var start = NewInput(original?.StartName ?? "", row, 3);
            var end = NewInput(original?.EndName ?? "", row, 4);
            void Sync()
            {
                main.IsReadOnly = start.IsReadOnly = end.IsReadOnly = automatic.IsChecked == true;
            }
            automatic.IsCheckedChanged += (_, _) => Sync();
            Sync();
            _entries.Add(new Entry(axis, automatic, main, start, end));
            list.Children.Add(row);
        }
        var scroller = new ScrollViewer { Content = list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroller, 2); root.Children.Add(scroller);

        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = "取消", MinWidth = 86 };
        cancel.Click += (_, _) => Close(false);
        var apply = new Button { Content = "应用轴号", MinWidth = 100 };
        apply.Click += (_, _) => { Collect(); Close(true); };
        actions.Children.Add(cancel); actions.Children.Add(apply);
        Grid.SetRow(actions, 3); root.Children.Add(actions);
        Content = root;
    }

    private void Collect()
    {
        var axes = new List<AxisModel>();
        foreach (var entry in _entries)
        {
            var axis = entry.Axis;
            var automatic = entry.Auto.IsChecked == true;
            // Imported axes keep their geometry even if numbering is reset to automatic.
            if (automatic && !_originalIds.Contains(axis.Id)) continue;
            axes.Add(new AxisModel
            {
                Id = axis.Id, Vertical = axis.Vertical, Position = axis.Position,
                ExtentStart = axis.ExtentStart, ExtentEnd = axis.ExtentEnd,
                Name = automatic ? null : entry.Main.Text?.Trim(),
                StartName = automatic ? null : entry.Start.Text?.Trim(),
                EndName = automatic ? null : entry.End.Text?.Trim()
            });
        }
        ResultAxes = axes;
    }

    private static Grid NewRow() => new() { ColumnDefinitions = new ColumnDefinitions("180,65,150,150,150"),
        ColumnSpacing = 8, MinHeight = 40 };

    private static void AddCell(Grid row, int column, string value)
    {
        var text = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, column); row.Children.Add(text);
    }

    private static TextBox NewInput(string value, Grid row, int column)
    {
        var input = new TextBox { Text = value, MaxLength = 24, MinWidth = 115,
            Background = new SolidColorBrush(Color.Parse("#202D3B")),
            Foreground = new SolidColorBrush(Color.Parse("#E8F1F4")),
            BorderBrush = new SolidColorBrush(Color.Parse("#496273")) };
        Grid.SetColumn(input, column); row.Children.Add(input);
        return input;
    }
}
