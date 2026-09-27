using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed record MovementResult(double X, double Y, double Angle);

/// <summary>Shows the drag result before committing an object transform.</summary>
internal sealed class MovementInputWindow : Window
{
    public MovementInputWindow(double dx, double dy, double angle)
    {
        var rotating = Math.Abs(angle) > 0.001;
        Title = rotating ? "确认旋转" : "确认移动";
        Width = 430; Height = rotating ? 235 : 300;
        MinWidth = 380; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#151B23"));
        Foreground = Brushes.White;
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = rotating ? "旋转角度" : "移动距离（mm）",
            FontSize = 20, FontWeight = FontWeight.Bold });
        var x = new TextBox { Text = dx.ToString("0.##", CultureInfo.CurrentCulture) };
        var y = new TextBox { Text = dy.ToString("0.##", CultureInfo.CurrentCulture) };
        var a = new TextBox { Text = angle.ToString("0.##", CultureInfo.CurrentCulture) };
        if (rotating) AddField(root, "角度（°）", a);
        else
        {
            AddField(root, "X 方向", x);
            AddField(root, "Y 方向", y);
            root.Children.Add(new TextBlock
            {
                Text = $"鼠标拖动距离 {Math.Sqrt(dx * dx + dy * dy):0.##} mm；可直接输入精确位移。",
                Foreground = new SolidColorBrush(Color.Parse("#A9C9D7"))
            });
        }
        var error = new TextBlock { Foreground = Brushes.OrangeRed };
        root.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Spacing = 9 };
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        cancel.Click += (_, _) => Close(null);
        var confirm = new Button { Content = "确定移动", MinWidth = 90 };
        if (rotating) confirm.Content = "确定旋转";
        confirm.Click += (_, _) =>
        {
            if (!TryNumber(x.Text, out var vx) || !TryNumber(y.Text, out var vy)
                || !TryNumber(a.Text, out var va))
            { error.Text = "请输入有效数字。"; return; }
            Close(new MovementResult(vx, vy, va));
        };
        actions.Children.Add(cancel); actions.Children.Add(confirm);
        root.Children.Add(actions);
        Content = root;
    }

    private static void AddField(StackPanel root, string label, TextBox field)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*") };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(field, 1); row.Children.Add(field);
        root.Children.Add(row);
    }

    private static bool TryNumber(string? text, out double value)
        => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value);
}
