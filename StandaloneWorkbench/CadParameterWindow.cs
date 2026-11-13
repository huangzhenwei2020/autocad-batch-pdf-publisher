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
            body.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height - 230) });
            Content = body;
            Loaded += (sender, args) => Inputs.Values.FirstOrDefault()?.Focus();
        }

        internal void Text(string key, string label, string value, bool multiline = false, bool enabled = true)
        {
            var box = new TextBox { Text = value ?? "", MinHeight = multiline ? 100 : 30, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden, Cursor = Cursors.IBeam, IsEnabled = enabled };
            Add(key, label, box);
        }
        internal void Numeric(string key, string label, double value, bool enabled = true) => Text(key, label, value.ToString("0.########", CultureInfo.CurrentCulture), enabled: enabled);
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
