using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wanluo.StandaloneWorkbench
{
    internal sealed class CadParameterWindow : Window
    {
        private readonly StackPanel _fields = new StackPanel();
        private readonly TextBlock _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Salmon, Margin = new Thickness(0, 12, 0, 8) };
        internal readonly Dictionary<string, Control> Inputs = new Dictionary<string, Control>();
        internal Button ApplyButton { get; }

        internal CadParameterWindow(string title, Action<CadParameterWindow> apply)
        {
            Title = title; Width = 470; SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 60);
            MinWidth = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(28, 37, 47)); Foreground = Brushes.White;
            var body = new DockPanel { Margin = new Thickness(18) };
            body.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 15) });
            DockPanel.SetDock(body.Children[0], Dock.Top);
            var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Children.Add(_error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            ApplyButton = new Button { Content = "确定", IsDefault = true, MinWidth = 84, Padding = new Thickness(12, 7, 12, 7) };
            ApplyButton.Click += (sender, args) =>
            {
                try { apply(this); DialogResult = true; }
                catch (Exception error) { _error.Text = error.Message; }
            };
            buttons.Children.Add(ApplyButton);
            buttons.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 78, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) });
            bottom.Children.Add(buttons); body.Children.Add(bottom);
            body.Children.Add(new ScrollViewer { Content = _fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height - 230) });
            Content = body;
            Loaded += (sender, args) => Inputs.Values.FirstOrDefault()?.Focus();
        }

        internal void Text(string key, string label, string value, bool multiline = false, bool enabled = true)
        {
            var box = new TextBox { Text = value ?? "", MinHeight = multiline ? 100 : 30, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden, Cursor = Cursors.IBeam, IsEnabled = enabled };
            Add(key, label, box);
        }
        internal void Numeric(string key, string label, double value, bool enabled = true) => Text(key, label, value.ToString("0.########", CultureInfo.CurrentCulture), enabled: enabled);
        internal void NumericPair(string firstKey, string firstLabel, double firstValue, string secondKey, string secondLabel, double secondValue)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var fields = new[] { (firstKey, firstLabel, firstValue), (secondKey, secondLabel, secondValue) };
            for (var index = 0; index < fields.Length; ++index)
            {
                var label = new TextBlock { Text = fields[index].Item2, Margin = new Thickness(index == 0 ? 0 : 8, 9, 0, 5) };
                var input = new TextBox { Text = fields[index].Item3.ToString("0.########", CultureInfo.CurrentCulture), MinHeight = 30, Margin = new Thickness(index == 0 ? 0 : 8, 0, 0, 0), Cursor = Cursors.IBeam };
                Inputs.Add(fields[index].Item1, input);
                Grid.SetColumn(label, index); Grid.SetColumn(input, index); Grid.SetRow(input, 1);
                grid.Children.Add(label); grid.Children.Add(input);
            }
            _fields.Children.Add(grid);
        }
        internal void Note(string text) => _fields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 });
        internal void Field(string key, string label, Control input) => Add(key, label, input);
        internal void File(string key, string label, string value)
        {
            Text(key, label, value);
            var browse = new Button { Content = "选择字体文件…", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            browse.Click += (sender, args) =>
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = "字体文件|*.ttf;*.ttc;*.otf;*.shx", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts) };
                if (picker.ShowDialog(this) == true) ((TextBox)Inputs[key]).Text = picker.FileName;
            };
            browse.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding("IsEnabled") { Source = Inputs[key] });
            _fields.Children.Add(browse);
        }
        internal void Choice(string key, string label, System.Collections.IEnumerable values, object selected)
        {
            var box = new ComboBox { ItemsSource = values, SelectedItem = selected, MinHeight = 30 };
            if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
            Add(key, label, box);
        }
        internal void Toggle(string key, string label, bool value) => Add(key, "", new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 6) });
        private void Add(string key, string label, Control input)
        {
            Inputs.Add(key, input);
            if (label.Length > 0) _fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 9, 0, 5) });
            _fields.Children.Add(input);
        }
        internal string Value(string key) => ((TextBox)Inputs[key]).Text.Trim();
        internal double Number(string key)
        {
            if (!double.TryParse(Value(key), NumberStyles.Float, CultureInfo.CurrentCulture, out var result) || !double.IsFinite(result)) throw new InvalidOperationException("请输入有效数值。");
            return result;
        }
        internal int Integer(string key)
        {
            var number = Number(key);
            if (number != Math.Truncate(number) || number < 0 || number > int.MaxValue) throw new InvalidOperationException("数量应为有效整数。");
            return (int)number;
        }
        internal int Index(string key) => ((ComboBox)Inputs[key]).SelectedIndex;
        internal object Selected(string key) => ((ComboBox)Inputs[key]).SelectedItem;
        internal bool Checked(string key) => ((CheckBox)Inputs[key]).IsChecked == true;
    }
}
