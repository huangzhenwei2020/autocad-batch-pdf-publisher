using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using static BatchPdfPublisher.Views.ExternalCadLibraryWindow;

namespace BatchPdfPublisher.Views
{
    internal sealed class ExternalCadCaptureWindow
    {
        private readonly Window _window;
        internal ExternalCadCaptureWindow(string root, ExternalCadAsset asset, string name, string category,
            List<string> categories, int count, Func<Window, Point3d?> pick, Action<string, string> save)
        {
            _window = BppDialog.Create("收录到外部图库", 730, 490);
            var shell = new DockPanel { Margin = new Thickness(12) };
            _window.Content = shell;
            shell.Children.Add(Docked(Label("已选 " + count + " 个对象"), Dock.Top));
            var preview = new ExternalCadView { ShowOrigin = true };
            void Render()
            {
                preview.Parts = ExternalCadPreview.Asset(asset, asset.Base, 0, 0, 1).Where(p => p.Kind != DetailPreviewPrimitiveKind.Box).ToList();
                preview.InvalidateVisual();
            }
            var title = new TextBox { Text = name, MaxLength = 80 };
            var folder = new ComboBox { IsEditable = true, ItemsSource = categories, Text = category };
            var location = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) };
            void ShowLocation() { location.Text = string.IsNullOrWhiteSpace(folder.Text) ? root : root + "\\" + folder.Text; }
            ShowLocation();
            folder.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) => ShowLocation()));
            folder.SelectionChanged += (s, e) => _window.Dispatcher.BeginInvoke(new Action(ShowLocation));

            var status = Label("");
            var footer = new DockPanel();
            footer.Children.Add(MakeButton("保存到图库", () =>
            {
                try { save(title.Text, folder.Text); _window.DialogResult = true; }
                catch (Exception ex) { status.Text = ex.Message; }
            }, Dock.Right, true));
            footer.Children.Add(MakeButton("取消", () => _window.DialogResult = false, Dock.Right));
            footer.Children.Add(status);
            shell.Children.Add(Docked(footer, Dock.Bottom));
            var right = new StackPanel { Width = 285, Margin = new Thickness(12, 0, 0, 0) };
            right.Children.Add(Label("名称")); right.Children.Add(title);
            right.Children.Add(Label("分类（可输入新名称）")); right.Children.Add(folder);
            right.Children.Add(Label("保存位置")); right.Children.Add(location);
            shell.Children.Add(Docked(right, Dock.Right));
            var left = new DockPanel();
            left.Children.Add(MakeButton("重新拾取基点", () =>
            {
                var point = pick(_window);
                if (point.HasValue) { asset.Base = point.Value; Render(); }
            }, Dock.Bottom));
            left.Children.Add(preview);
            shell.Children.Add(left);
            Render();
            _window.Loaded += (s, e) => { title.Focus(); title.SelectAll(); };
        }
        internal bool Show(Window owner) => BppDialog.Show(_window, owner) == true;
    }

    internal sealed class ExternalCadBatchWindow
    {
        private readonly Document _document;
        private readonly Window _window;
        private readonly List<ExternalCadAsset> _assets;
        private readonly List<FrameDefinition> _frames;
        private readonly ListBox _items = new ListBox { AllowDrop = true };
        private readonly ExternalCadView _preview = new ExternalCadView();
        private readonly CheckBox _include = new CheckBox { Content = "插入图框" };
        private readonly ComboBox _source = new ComboBox { ItemsSource = new[] { "全部图框", "公司图框", "项目图框" }, SelectedIndex = 0 };
        private readonly ComboBox _frame = new ComboBox { DisplayMemberPath = "SelectionName" };
        private readonly TextBox _scale = new TextBox { Text = "1:25" };
        private readonly TextBox _gap = new TextBox { Text = "10" };
        private readonly TextBox _frameInset = new TextBox
        {
            Text = ExternalCadSheet.DefaultFrameInset.ToString(CultureInfo.CurrentCulture),
            ToolTip = "在已登记的排版范围内向四边额外留白，按纸面毫米计算；填 0 不额外留白。"
        };
        private readonly CheckBox _paginate = new CheckBox { Content = "超出时自动分页", IsChecked = true };
        private readonly CheckBox _title = new CheckBox { Content = "天正图名" };
        private readonly CheckBox _number = new CheckBox { Content = "图名编号" };
        private readonly TextBox _start = new TextBox { Text = "1" };
        private readonly TextBlock _label = Label("");
        private readonly TextBlock _pageText = Label("");
        private Button _place, _previous, _next;
        private int _page;
        private Point _dragOrigin;
        private int _dragIndex = -1;
        private FrameDefinition _lastFrame;
        private List<DetailPreviewPrimitive> _unscaledFrame = new List<DetailPreviewPrimitive>();
        private readonly DispatcherTimer _layoutTimer;
        private ExternalCadAsset _showAsset;
        internal ExternalCadSheet Plan { get; private set; }
        internal List<DetailPreviewPrimitive> FramePreview { get; private set; } = new List<DetailPreviewPrimitive>();

        internal ExternalCadBatchWindow(Document document, IEnumerable<ExternalCadAsset> assets, bool title, bool number)
        {
            _document = document;
            _assets = assets.ToList();
            _frames = new PublishPlanStore().LoadInsertionFrames().Where(frame => frame != null).ToList();
            _title.IsChecked = title; _number.IsChecked = number; _include.IsChecked = _frames.Count > 0;
            _window = BppDialog.Create("万落建筑工具 · 批量插入与排版", 1320, 820);
            _window.MinWidth = 820;
            var shell = new DockPanel { Margin = new Thickness(10) };
            _window.Content = shell;
            var footer = new DockPanel();
            _place = MakeButton("在 CAD 中放置", () => { if (Plan != null) _window.DialogResult = true; }, Dock.Right, true);
            footer.Children.Add(_place);
            footer.Children.Add(MakeButton("返回图库", () => _window.DialogResult = false, Dock.Right));
            _label.TextWrapping = TextWrapping.Wrap;
            footer.Children.Add(_label);
            shell.Children.Add(Docked(footer, Dock.Bottom));
            var grid = Columns(222, 260);
            shell.Children.Add(grid);

            var left = new DockPanel();
            left.Children.Add(Docked(Label("待插入对象 · 拖动排序"), Dock.Top));
            var sort = new StackPanel { Orientation = Orientation.Horizontal };
            sort.Children.Add(MakeButton("上移", () => Move(_items.SelectedIndex, _items.SelectedIndex - 1)));
            sort.Children.Add(MakeButton("下移", () => Move(_items.SelectedIndex, _items.SelectedIndex + 1)));
            left.Children.Add(Docked(sort, Dock.Bottom)); left.Children.Add(_items);
            grid.Children.Add(left);
            HookDrag();

            var center = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
            center.Children.Add(Docked(Label("排版预览"), Dock.Top));
            var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            nav.Children.Add(_previous = MakeButton("上一张", () => { _page--; PreviewPage(); }));
            nav.Children.Add(_pageText);
            nav.Children.Add(_next = MakeButton("下一张", () => { _page++; PreviewPage(); }));
            center.Children.Add(Docked(nav, Dock.Bottom));
            center.Children.Add(_preview);
            Grid.SetColumn(center, 1); grid.Children.Add(center);

            var right = new StackPanel();
            right.Children.Add(Label("图框与排版"));
            right.Children.Add(_include);
            right.Children.Add(Field("图框来源", _source));
            right.Children.Add(Field("登记图框", _frame));
            right.Children.Add(Field("打印比例", _scale));
            right.Children.Add(Field("对象间距 mm", _gap));
            right.Children.Add(Field("边框留白 mm", _frameInset));
            right.Children.Add(_paginate);
            right.Children.Add(Label("随图插入"));
            right.Children.Add(_title);
            right.Children.Add(_number);
            right.Children.Add(Field("起始编号", _start));
            var scroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, 2); grid.Children.Add(scroll);

            _source.SelectionChanged += (s, e) => FramesChanged();
            _layoutTimer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher);
            _layoutTimer.Tick += (s, e) => Recompute();
            _window.Closed += (s, e) => _layoutTimer.Stop();
            _frame.SelectionChanged += (s, e) => QueueLayout();
            foreach (var box in new[] { _include, _paginate, _title, _number })
            { box.Checked += (s, e) => QueueLayout(); box.Unchecked += (s, e) => QueueLayout(); }
            foreach (var field in new[] { _scale, _gap, _frameInset, _start }) field.TextChanged += (s, e) => QueueLayout();
            _items.ItemsSource = _assets.Select(asset => "⋮⋮  " + asset.Name).ToList();
            FramesChanged();
        }

        internal bool Show(Window owner) => BppDialog.Show(_window, owner) == true;

        private void FramesChanged()
        {
            var frames = _frames.Where(frame => _source.SelectedIndex == 0
                || frame.IsCompanyFrame == (_source.SelectedIndex == 1)).ToList();
            _frame.ItemsSource = frames;
            _frame.SelectedIndex = frames.Count == 0 ? -1 : 0;
            QueueLayout();
        }

        private void QueueLayout()
        {
            _source.IsEnabled = _frame.IsEnabled = _paginate.IsEnabled = _frameInset.IsEnabled = _include.IsChecked == true;
            _start.IsEnabled = _number.IsChecked == true;
            _place.IsEnabled = false;
            _label.Text = "正在更新排版预览…";
            _layoutTimer.Stop();
            _layoutTimer.Interval = TimeSpan.FromMilliseconds(140);
            _layoutTimer.Start();
        }

        private void Recompute()
        {
            _layoutTimer.Stop();
            Plan = null;
            try
            {
                if (!TryScale(_scale.Text, out var scale)) throw new InvalidOperationException("请输入有效打印比例。");
                if (!TryDistance(_gap.Text, out var gap) || gap > 1000)
                    throw new InvalidOperationException("对象间距应为 0～1000 mm。");
                var start = 1;
                if (_start.IsEnabled && (!int.TryParse(_start.Text, out start) || start < 1 || start > 9999999))
                    throw new InvalidOperationException("请输入 1～9999999 的起始编号。");
                var frame = _include.IsChecked == true ? _frame.SelectedItem as FrameDefinition
                    ?? throw new InvalidOperationException("请选择登记图框。") : null;
                var frameInset = 0d;
                if (frame != null && !TryDistance(_frameInset.Text, out frameInset))
                    throw new InvalidOperationException("排版边框留白须为大于或等于 0 的数值。");
                var missing = _assets.FirstOrDefault(asset => asset.Preview == null);
                if (missing != null)
                {
                    _label.Text = "正在读取预览 · " + missing.Name;
                    ExternalCadLibrary.LoadPreview(missing);
                    _layoutTimer.Interval = TimeSpan.FromMilliseconds(35);
                    _layoutTimer.Start();
                    return;
                }
                var plan = ExternalCadSheet.Arrange(_assets, frame, scale, gap, _paginate.IsChecked == true, _title.IsChecked == true,
                    _number.IsChecked == true, start, frameInset);
                if (frame != _lastFrame)
                {
                    _unscaledFrame = ExternalCadPreview.Frame(_document, frame, 1);
                    _lastFrame = frame;
                }
                FramePreview = _unscaledFrame.Select(part => ExternalCadSheet.Shape(part.Kind,
                    part.X1 * scale, part.Y1 * scale, part.X2 * scale, part.Y2 * scale, part.Text)).ToList();
                Plan = plan;
                if (_showAsset != null)
                {
                    _page = plan.Slots[_assets.IndexOf(_showAsset)].Page;
                    _showAsset = null;
                }
                _label.Text = plan.Slots.Count + " 个对象" + (frame == null ? " · 不插入图框" : " · " + plan.PageCount + " 张图框");
            }
            catch (Exception ex) { _label.Text = ex.Message; }
            var selected = _items.SelectedIndex;
            _items.ItemsSource = _assets.Select((asset, index) =>
            {
                var slot = Plan?.Slots[index];
                return "⋮⋮  " + (slot == null ? "" : "第" + (slot.Page + 1) + "张  ") + (slot?.Number == null ? "" : slot.Number + "  ") + asset.Name;
            }).ToList();
            _items.SelectedIndex = selected;
            _place.IsEnabled = Plan != null;
            PreviewPage();
        }

        private static bool TryDistance(string text, out double value) =>
            (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && value >= 0 && !double.IsInfinity(value);

        private void PreviewPage()
        {
            var count = Plan?.PageCount ?? 0;
            _page = Math.Max(0, Math.Min(_page, count - 1));
            _previous.IsEnabled = _page > 0;
            _next.IsEnabled = _page + 1 < count;
            _pageText.Text = count == 0 ? "无可用预览" : "第 " + (_page + 1) + " 张 / 共 " + count + " 张";
            _preview.Parts = Plan == null ? null : ExternalCadPreview.Page(Plan, _page, FramePreview).ToList();
            _preview.Extent = Plan == null ? Rect.Empty : new Rect(0, 0, Plan.PageWidth, Plan.PageHeight);
            _preview.Range = Plan?.Frame == null ? Rect.Empty : new Rect(Plan.Left, Plan.Bottom, Plan.Right - Plan.Left, Plan.Top - Plan.Bottom);
            _preview.InvalidateVisual();
        }

        private void Move(int from, int to)
        {
            if (from < 0 || to < 0 || from >= _assets.Count || to >= _assets.Count || from == to) return;
            var asset = _assets[from]; _assets.RemoveAt(from); _assets.Insert(to, asset);
            var display = _items.ItemsSource.Cast<string>().ToList();
            var entry = display[from];
            display.RemoveAt(from);
            display.Insert(to, entry);
            _items.ItemsSource = display;
            _items.SelectedIndex = to;
            _items.ScrollIntoView(_items.SelectedItem);
            _showAsset = asset;
            QueueLayout();
        }

        private void HookDrag()
        {
            int Hit(object origin)
            {
                var container = ItemsControl.ContainerFromElement(_items, origin as DependencyObject);
                return container == null ? -1 : _items.ItemContainerGenerator.IndexFromContainer(container);
            }
            _items.PreviewMouseLeftButtonDown += (s, e) => { _dragIndex = Hit(e.OriginalSource); _dragOrigin = e.GetPosition(_items); };
            _items.MouseMove += (s, e) =>
            {
                if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed
                    || (e.GetPosition(_items) - _dragOrigin).Length < SystemParameters.MinimumVerticalDragDistance) return;
                var index = _dragIndex;
                _dragIndex = -1;
                DragDrop.DoDragDrop(_items, new DataObject("WanLuoExternalAssetIndex", index), DragDropEffects.Move);
            };
            _items.DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent("WanLuoExternalAssetIndex") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            _items.Drop += (s, e) =>
            {
                if (e.Data.GetData("WanLuoExternalAssetIndex") is int from) Move(from, Hit(e.OriginalSource));
            };
        }
    }

    internal sealed class ExternalCadView : FrameworkElement
    {
        private static readonly Brush Canvas = Brush("#0D1922");
        private static readonly Brush Ink = Brush("#D0E2ED");
        private static readonly Pen Stroke = new Pen(Ink, 1);
        private static readonly Pen Guide = new Pen(Brush("#34B9E8"), 1) { DashStyle = DashStyles.Dash };
        private static readonly Typeface Face = new Typeface("Microsoft YaHei UI");
        internal List<DetailPreviewPrimitive> Parts;
        internal Rect Extent = Rect.Empty, Range = Rect.Empty;
        internal bool ShowOrigin;
        internal string Error;

        protected override void OnRender(DrawingContext context)
        {
            var size = new Rect(RenderSize);
            context.DrawRectangle(Canvas, null, size);
            if (!string.IsNullOrWhiteSpace(Error)) DrawText(context, Error, new Point(10, 10), 12);
            if (Parts == null || (Parts.Count == 0 && Extent.IsEmpty) || RenderSize.Width < 30 || RenderSize.Height < 30) return;
            context.PushClip(new RectangleGeometry(size));
            var bounds = Extent;
            foreach (var part in Parts) { bounds.Union(new Point(part.X1, part.Y1)); bounds.Union(new Point(part.X2, part.Y2)); }
            if (ShowOrigin) bounds.Union(new Point(0, 0));
            var scale = Math.Min((RenderSize.Width - 24) / Math.Max(1e-6, bounds.Width), (RenderSize.Height - 24) / Math.Max(1e-6, bounds.Height));
            Point ToScreen(double x, double y) => new Point(RenderSize.Width / 2 + (x - bounds.Left - bounds.Width / 2) * scale,
                RenderSize.Height / 2 - (y - bounds.Top - bounds.Height / 2) * scale);
            foreach (var part in Parts)
            {
                var a = ToScreen(part.X1, part.Y1);
                var b = ToScreen(part.X2, part.Y2);
                if (part.Kind == DetailPreviewPrimitiveKind.Ellipse) context.DrawEllipse(null, Stroke, a + (b - a) / 2, Math.Abs(b.X - a.X) / 2, Math.Abs(b.Y - a.Y) / 2);
                else if (part.Kind == DetailPreviewPrimitiveKind.Text) DrawText(context, part.Text,
                    new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), Math.Max(6, Math.Min(22, Math.Abs(b.Y - a.Y))));
                else if (part.Kind == DetailPreviewPrimitiveKind.Line) context.DrawLine(Stroke, a, b);
            }
            if (!Range.IsEmpty) context.DrawRectangle(null, Guide, new Rect(ToScreen(Range.Left, Range.Top), ToScreen(Range.Right, Range.Bottom)));
            if (ShowOrigin)
            {
                var p = ToScreen(0, 0);
                context.DrawLine(Guide, p - new Vector(8, 0), p + new Vector(8, 0));
                context.DrawLine(Guide, p - new Vector(0, 8), p + new Vector(0, 8));
            }
            context.Pop();
        }

        private void DrawText(DrawingContext context, string text, Point point, double size)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, Ink,
                VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
        }
    }
}
