using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Wanluo.StandaloneWorkbench
{
    internal sealed class InputDialog : Window
    {
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _folder = new TextBox();

        public InputDialog(Window owner, string title, string name, string folder, Func<string, string, bool> apply)
        {
            Owner = owner;
            Style = (Style)FindResource(typeof(Window));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Title = title;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            var content = new StackPanel { Margin = new Thickness(24) };
            content.Children.Add(new TextBlock { Text = title, FontSize = 22, Margin = new Thickness(3, 0, 0, 16) });
            content.Children.Add(new TextBlock { Text = "名称", Margin = new Thickness(3) });
            _name.Text = name ?? string.Empty;
            content.Children.Add(_name);
            if (folder != null)
            {
                content.Children.Add(new TextBlock { Text = "项目文件夹", Margin = new Thickness(3, 14, 3, 3) });
                _folder.Text = folder;
                content.Children.Add(_folder);
                var browse = new Button { Content = "选择文件夹", HorizontalAlignment = HorizontalAlignment.Left };
                browse.Click += (sender, args) =>
                {
                    var picker = new OpenFolderDialog { Title = "选择项目文件夹", InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : string.Empty };
                    if (picker.ShowDialog(this) == true) _folder.Text = picker.FolderName;
                };
                content.Children.Add(browse);
            }
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 12, 3, 3) };
            content.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = "取消", IsCancel = true };
            var accept = new Button { Content = "确定", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
            accept.Click += (sender, args) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(_name.Text)) throw new ArgumentException("请输入名称。");
                    if (apply(_name.Text.Trim(), _folder.Text.Trim())) DialogResult = true;
                }
                catch (Exception error) { status.Text = error.Message; }
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(accept);
            content.Children.Add(buttons);
            Content = content;
            Loaded += (sender, args) => { _name.Focus(); _name.SelectAll(); };
        }

        public static string Collision(Window owner, string name)
        {
            var choice = "cancel";
            var dialog = new Window { Owner = owner, Title = "同名模型", Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "模型“" + name + "”已存在", FontSize = 20, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "可以打开已有模型，或备份后创建同名的新模型。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 18) });
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var option in new[] { ("打开已有模型", "open"), ("备份后替换", "replace"), ("取消", "cancel") })
            {
                var button = new Button { Content = option.Item1 };
                button.Click += (sender, args) => { choice = option.Item2; dialog.Close(); };
                buttons.Children.Add(button);
            }
            panel.Children.Add(buttons);
            dialog.Content = panel;
            dialog.ShowDialog();
            return choice;
        }
    }
}
