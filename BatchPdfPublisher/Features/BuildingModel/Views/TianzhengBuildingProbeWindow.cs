using System;
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
    public sealed class TianzhengBuildingProbeWindow : Window
    {
        private readonly CadBuildingProbeDocument _report;
        private readonly TextBlock _identity = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly DataGrid _fields = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false,
            CanUserAddRows = false, CanUserDeleteRows = false, EnableRowVirtualization = true,
            EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = Brushes.White, Foreground = Brushes.Black, MinHeight = 100 };

        public TianzhengBuildingProbeWindow(CadBuildingProbeDocument report)
        {
            _report = report;
            Title = "天正墙门窗 · 只读字段核验"; Width = 1060; Height = 680;
            MinWidth = 560; MinHeight = 360; FontSize = 14;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 251));
            UseLayoutRounding = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new Grid { Margin = new Thickness(20) };
            foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
                root.RowDefinitions.Add(new RowDefinition { Height = height });
            root.Children.Add(new TextBlock { Text = "天正墙门窗字段核验", FontSize = 24, FontWeight = FontWeights.SemiBold });
            var help = new TextBlock { Margin = new Thickness(0, 8, 0, 16), TextWrapping = TextWrapping.Wrap,
                Text = "选择左侧对象核对原始值。读取成功不代表含义已确认；本阶段不导入模型、不修改源图。单位、门窗类别及宿主需要对照天正属性核验。" };
            Grid.SetRow(help, 1); root.Children.Add(help);
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240), MinWidth = 130 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 2); root.Children.Add(body);
            var entities = new ListBox { ItemsSource = report.Entities, DisplayMemberPath = "Caption",
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(entities, ScrollBarVisibility.Auto);
            body.Children.Add(entities);
            var right = new Grid(); right.RowDefinitions.Add(new RowDefinition
                { Height = new GridLength(0.38, GridUnitType.Star), MinHeight = 50, MaxHeight = 145 });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _identity.Margin = new Thickness(0, 0, 0, 12);
            right.Children.Add(new ScrollViewer { Content = _identity, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            AddColumn("属性", "Name", 140); AddColumn("状态", "Status", 150);
            AddColumn("原始值", "Text", 190); AddColumn("读取原因", "Error", 240);
            Grid.SetRow(_fields, 1); right.Children.Add(_fields); Grid.SetColumn(right, 2); body.Children.Add(right);
            entities.SelectionChanged += (sender, args) => ShowEntity(entities.SelectedItem as CadBuildingProbeEntity);
            if (report.Entities.Count > 0) entities.SelectedIndex = 0;
            var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var state = report.DbmodBefore.HasValue && report.DbmodAfter.HasValue
                ? (report.DbmodBefore == report.DbmodAfter ? "DBMOD 未变化" : "DBMOD 有变化，请检查源图状态") : "DBMOD 未能核对";
            footer.Children.Add(new TextBlock { Text = report.Entities.Count + " 个对象 · " + state,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var export = new Button { Content = "导出核验 JSON", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 8, 0) };
            export.Click += (sender, args) => Export(); actions.Children.Add(export);
            var close = new Button { Content = "关闭", Padding = new Thickness(16, 8, 16, 8), IsCancel = true };
            close.Click += (sender, args) => Close(); actions.Children.Add(close);
            Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer);
            Content = root;
            Loaded += (sender, args) =>
            {
                var area = SystemParameters.WorkArea;
                Width = Math.Min(Width, area.Width - 32); Height = Math.Min(Height, area.Height - 32);
            };
        }

        private void AddColumn(string title, string property, double width)
        {
            _fields.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width });
        }

        private void ShowEntity(CadBuildingProbeEntity entity)
        {
            if (entity == null) return;
            _fields.ItemsSource = entity.Fields;
            _identity.Text = entity.Caption + "\n图层：" + entity.Layer + " · COM：" + entity.ComType
                + "\n" + entity.CurveStatus + "\n起点 " + Format(entity.CurveStart) + " → 终点 " + Format(entity.CurveEnd)
                + "\n左右宽度推导候选（单位/方向待核验）：总厚 " + Number(entity.CandidateThickness)
                + "，中心偏移 " + Number(entity.CandidateAxisOffset)
                + (string.IsNullOrWhiteSpace(entity.Note) ? "" : "\n" + entity.Note);
        }

        private static string Number(double? value) => value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "—";
        private static string Format(CadProbePoint point) => point == null ? "—"
            : "(" + Number(point.X) + ", " + Number(point.Y) + ", " + Number(point.Z) + ")";

        private void Export()
        {
            var dialog = new SaveFileDialog { Title = "保存天正字段核验记录", Filter = "核验记录 (*.cadprobe.json)|*.cadprobe.json",
                FileName = "天正墙门窗核验-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".cadprobe.json", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            try { CadBuildingProbeFile.Save(dialog.FileName, _report); MessageBox.Show(this, "核验记录已保存：\n" + dialog.FileName); }
            catch (Exception exception) { MessageBox.Show(this, "保存失败，原文件未替换：\n" + exception.Message); }
        }
    }
}
