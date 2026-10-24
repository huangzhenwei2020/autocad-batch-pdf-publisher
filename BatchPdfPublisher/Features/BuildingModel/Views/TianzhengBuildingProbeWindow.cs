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
        private readonly DiagnosticPreview _preview = new DiagnosticPreview();
        private readonly ComboBox _reference = new ComboBox { MinWidth = 120,
            ItemsSource = new[] { "未指定", "阳台", "地面散水", "屋面泛水", "女儿墙", "栏杆" } };
        private CadBuildingProbeEntity _selected;
        private readonly DataGrid _fields = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false,
            CanUserAddRows = false, CanUserDeleteRows = false, EnableRowVirtualization = true,
            EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = Brushes.White, Foreground = Brushes.Black, MinHeight = 100 };

        public TianzhengBuildingProbeWindow(CadBuildingProbeDocument report)
        {
            _report = report;
            Title = "天正构件 · 只读核验"; Width = 1060; Height = 680;
            MinWidth = 560; MinHeight = 360; FontSize = 14;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 251));
            UseLayoutRounding = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new Grid { Margin = new Thickness(20) };
            foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
                root.RowDefinitions.Add(new RowDefinition { Height = height });
            root.Children.Add(new TextBlock { Text = "天正构件核验", FontSize = 24, FontWeight = FontWeights.SemiBold });
            var help = new TextBlock { Margin = new Thickness(0, 8, 0, 16), TextWrapping = TextWrapping.Wrap,
                Text = "CAD " + (report.CadVersion ?? "未知") + " · INSUNITS " + (report.DrawingUnits ?? "未知")
                    + " · " + (report.TianzhengEnvironment ?? "天正版本待核对") };
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
            var identityHost = new ScrollViewer { Content = _identity, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            right.Children.Add(identityHost);
            AddColumn("属性", "Name", 140); AddColumn("状态", "Status", 150);
            AddColumn("原始值", "Text", 190); AddColumn("读取原因", "Error", 240);
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "原始字段", Content = _fields });
            var geometry = new DockPanel();
            var tools = new WrapPanel { Margin = new Thickness(6) };
            tools.Children.Add(new TextBlock { Text = "对照类别", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            tools.Children.Add(_reference);
            var projection = new ComboBox { ItemsSource = new[] { "平面 XY", "正面 XZ", "侧面 YZ" }, SelectedIndex = 0,
                MinWidth = 100, Margin = new Thickness(12, 0, 0, 0) };
            projection.SelectionChanged += (s, e) => { _preview.Projection = projection.SelectedIndex; _preview.InvalidateVisual(); };
            tools.Children.Add(projection); DockPanel.SetDock(tools, Dock.Top); geometry.Children.Add(tools);
            geometry.Children.Add(_preview);
            tabs.Items.Add(new TabItem { Header = "原始几何", Content = geometry });
            tabs.SelectionChanged += (s, e) =>
            {
                var fieldsVisible = tabs.SelectedIndex == 0;
                identityHost.Visibility = fieldsVisible ? Visibility.Visible : Visibility.Collapsed;
                right.RowDefinitions[0].MinHeight = fieldsVisible ? 50 : 0;
                right.RowDefinitions[0].Height = fieldsVisible ? new GridLength(.38, GridUnitType.Star) : new GridLength(0);
            };
            _reference.SelectionChanged += (s, e) => { if (_selected != null) _selected.ReferenceKind = _reference.SelectedIndex <= 0 ? null : (string)_reference.SelectedItem; };
            Grid.SetRow(tabs, 1); right.Children.Add(tabs); Grid.SetColumn(right, 2); body.Children.Add(right);
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
            _selected = null;
            _reference.SelectedIndex = Math.Max(0, Array.IndexOf((string[])_reference.ItemsSource, entity.ReferenceKind));
            _selected = entity; _preview.Entity = entity; _preview.InvalidateVisual();
            _fields.ItemsSource = entity.Fields;
            _identity.Text = entity.Caption + "\nRX：" + entity.RxClassName + " · 图层：" + entity.Layer + " · COM：" + entity.ComType
                + "\n" + entity.CurveStatus + "\n起点 " + Format(entity.CurveStart) + " → 终点 " + Format(entity.CurveEnd)
                + (entity.CandidateThickness.HasValue ? "\n墙厚候选 " + Number(entity.CandidateThickness) + "，中心偏移 " + Number(entity.CandidateAxisOffset) : "")
                + "\n" + (entity.GeometryStatus ?? "未采集原始几何")
                + " · " + (entity.GeometryPaths?.Count ?? 0) + " 条 · 未支持 " + entity.UnsupportedGeometry
                + "\n语义：待核验"
                + (string.IsNullOrWhiteSpace(entity.Note) ? "" : "\n" + entity.Note);
        }

        private static string Number(double? value) => value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "—";
        private static string Format(CadProbePoint point) => point == null ? "—"
            : "(" + Number(point.X) + ", " + Number(point.Y) + ", " + Number(point.Z) + ")";

        private void Export()
        {
            var dialog = new SaveFileDialog { Title = "保存天正字段核验记录", Filter = "核验记录 (*.cadprobe.json)|*.cadprobe.json",
                FileName = "天正构件核验-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".cadprobe.json", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            try { CadBuildingProbeFile.Save(dialog.FileName, _report); MessageBox.Show(this, "核验记录已保存：\n" + dialog.FileName); }
            catch (Exception exception) { MessageBox.Show(this, "保存失败，原文件未替换：\n" + exception.Message); }
        }

        private sealed class DiagnosticPreview : FrameworkElement
        {
            public CadBuildingProbeEntity Entity;
            public int Projection;
            private Point Project(CadProbePoint p) => Projection == 1 ? new Point(p.X, p.Z)
                : Projection == 2 ? new Point(p.Y, p.Z) : new Point(p.X, p.Y);
            protected override void OnRender(DrawingContext dc)
            {
                base.OnRender(dc);
                var area = new Rect(RenderSize); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 31, 40)), null, area);
                var paths = (Entity?.GeometryPaths ?? new System.Collections.Generic.List<CadProbeGeometryPath>())
                    .Where(p => p?.PreviewPoints != null && p.PreviewPoints.Count > 1)
                    .Take(2048).Select(p => new { p.Closed, Points = p.PreviewPoints.Take(16384)
                        .Where(q => q != null && Finite(q.X) && Finite(q.Y) && Finite(q.Z)).Select(Project).ToArray() }).ToArray();
                var points = paths.SelectMany(p => p.Points).ToArray();
                if (points.Length == 0 || ActualWidth < 32 || ActualHeight < 32) return;
                var x = points.Min(p => p.X); var y = points.Min(p => p.Y);
                var width = points.Max(p => p.X) - x; var height = points.Max(p => p.Y) - y;
                if (!Finite(width) || !Finite(height)) return;
                var scale = Math.Min((ActualWidth - 32) / Math.Max(width, 1), (ActualHeight - 32) / Math.Max(height, 1));
                Point Screen(Point p) => new Point((ActualWidth - width * scale) / 2 + (p.X - x) * scale,
                    (ActualHeight + height * scale) / 2 - (p.Y - y) * scale);
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(158, 209, 233)), 1.2);
                dc.PushClip(new RectangleGeometry(area));
                foreach (var path in paths)
                {
                    if (path.Points.Length < 2) continue;
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        context.BeginFigure(Screen(path.Points[0]), false, path.Closed);
                        foreach (var point in path.Points.Skip(1)) context.LineTo(Screen(point), true, false);
                    }
                    geometry.Freeze(); dc.DrawGeometry(null, pen, geometry);
                }
                dc.Pop();
            }
            private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
