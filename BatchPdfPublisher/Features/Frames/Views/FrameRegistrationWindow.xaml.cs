using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using Forms = System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    public partial class FrameRegistrationWindow : Window
    {
        private const string DoNotRead = "（不读取）";
        private readonly IDictionary<string, string> _attributeValues;
        private readonly FrameDefinition _existing;
        private readonly string _attributeTagSignature;
        private readonly string _definitionSignature;
        private readonly double _referenceAspectRatio;
        private readonly Func<FrameProjectScanReport> _projectScanner;
        private FrameProjectScanReport _projectScan;
        public List<FrameProjectScanIssue> RequestedIssues { get; } = new List<FrameProjectScanIssue>();
        public bool OpenAllRequested { get; private set; }
        public bool PickLayoutRangeRequested => PickLayoutRangeBox.IsChecked == true;

        public FrameDefinition Definition { get; private set; }

        public FrameRegistrationWindow(string blockName, FrameSizeGuess guess, IDictionary<string, string> attributeValues, FrameDefinition existing = null,
            string attributeTagSignature = null, string definitionSignature = null, double referenceAspectRatio = 0d,
            Func<FrameProjectScanReport> projectScanner = null)
        {
            InitializeComponent();
            Loaded += (sender, args) => FitToCurrentScreen();
            _existing = existing;
            _attributeTagSignature = attributeTagSignature;
            _definitionSignature = definitionSignature;
            _referenceAspectRatio = referenceAspectRatio;
            _projectScanner = projectScanner;
            _attributeValues = attributeValues ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            BlockNameBox.Text = blockName;
            var detectedAttributes = _attributeValues.Count == 0
                ? "未检测到属性标签；可在右侧选择“不读取”并手工填写。"
                : "检测到属性：" + string.Join("；", _attributeValues.Select(x => x.Key + " = " + (string.IsNullOrWhiteSpace(x.Value) ? "（空）" : x.Value.Trim())));
            MeasuredSizeText.Text = guess.MeasuredSize + "；建议 " + guess.PaperSize +
                                    (string.IsNullOrWhiteSpace(guess.Extension) ? string.Empty : "+" + guess.Extension) +
                                    "，" + guess.PaperOrientation + "，打印比例 " + guess.PrintScale + "（均可修改）\n" + detectedAttributes;

            var selectedTags = existing == null
                ? Enumerable.Empty<string>()
                : new[] { existing.BuildingAttributeTag, existing.SheetNumberAttributeTag, existing.SheetNameAttributeTag, existing.PrintScaleAttributeTag };
            var tags = new[] { DoNotRead }.Concat(_attributeValues.Keys).Concat(selectedTags)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var box in TagBoxes()) box.ItemsSource = tags;

            SelectComboByText(PaperSizeBox, existing?.PaperSize ?? guess.PaperSize);
            PaperSizeBox_SelectionChanged(null, null);
            SelectComboByText(ExtensionBox, string.IsNullOrWhiteSpace(existing?.Extension ?? guess.Extension) ? "无加长" : existing?.Extension ?? guess.Extension);
            SelectComboByText(OrientationBox, existing?.PaperOrientation ?? guess.PaperOrientation);
            NoteBox.Text = existing?.Note ?? string.Empty;
            PickLayoutRangeBox.IsChecked = existing == null || !FrameLayoutRangeService.HasValidRange(existing);
            LayoutRangeText.Text = "当前登记：" + FrameLayoutRangeService.Describe(existing) + "。范围按图框 1:1 纸面毫米保存，供大样和门窗排版共用。";

            if (existing == null)
            {
                SelectTag(BuildingTagBox, tags, "子项目名称", "楼栋", "BUILDING", "栋号", "SUBPROJECT", "SUBPROJECTNAME");
                SelectTag(SheetNumberTagBox, tags, "图号", "SHEETNO", "SHEET_NO", "DRAWINGNO", "DRAWING_NO");
                SelectTag(SheetNameTagBox, tags, "图名", "图纸名称", "SHEETNAME", "SHEET_NAME", "DRAWINGNAME", "DRAWING_NAME");
                SelectTag(PrintScaleTagBox, tags, "比例", "SCALE", "PRINTSCALE", "PRINT_SCALE");
                FillValueFromSelectedTag(BuildingTagBox, BuildingValueBox);
                FillValueFromSelectedTag(SheetNumberTagBox, SheetNumberValueBox);
                FillValueFromSelectedTag(SheetNameTagBox, SheetNameValueBox);
                FillValueFromSelectedTag(PrintScaleTagBox, PrintScaleValueBox);
                if (string.IsNullOrWhiteSpace(PrintScaleValueBox.Text)) PrintScaleValueBox.Text = guess.PrintScale;
            }
            else
            {
                SelectStoredTag(BuildingTagBox, existing.BuildingAttributeTag);
                SelectStoredTag(SheetNumberTagBox, existing.SheetNumberAttributeTag);
                SelectStoredTag(SheetNameTagBox, existing.SheetNameAttributeTag);
                SelectStoredTag(PrintScaleTagBox, existing.PrintScaleAttributeTag);
                BuildingValueBox.Text = existing.DefaultBuilding ?? string.Empty;
                SheetNumberValueBox.Text = existing.DefaultSheetNumber ?? string.Empty;
                SheetNameValueBox.Text = existing.DefaultSheetName ?? string.Empty;
                PrintScaleValueBox.Text = string.IsNullOrWhiteSpace(existing.DefaultPrintScale) ? guess.PrintScale : existing.DefaultPrintScale;
                Title = "修改图框登记";
            }
        }

        private void FitToCurrentScreen()
        {
            var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var source = PresentationSource.FromVisual(this);
            var fromDevice = source != null && source.CompositionTarget != null
                ? source.CompositionTarget.TransformFromDevice
                : Matrix.Identity;
            var topLeft = fromDevice.Transform(new Point(screen.Left, screen.Top));
            var bottomRight = fromDevice.Transform(new Point(screen.Right, screen.Bottom));
            var workWidth = Math.Max(1d, bottomRight.X - topLeft.X);
            var workHeight = Math.Max(1d, bottomRight.Y - topLeft.Y);
            MaxWidth = Math.Max(520d, workWidth - 32d);
            MaxHeight = Math.Max(420d, workHeight - 32d);
            Width = Math.Min(Width, MaxWidth);
            Height = Math.Min(Height, MaxHeight);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_projectScanner != null && _projectScan == null) RunProjectScan();
            if (_projectScan != null && _projectScan.Issues.Count > 0)
            {
                ShowIssueWindow(_projectScan);
                return;
            }
            if (_projectScan != null && _projectScan.Failures.Count > 0 && MessageBox.Show("有 " + _projectScan.Failures.Count + " 个工程 CAD 未能完成检查：\r\n\r\n" + string.Join("\r\n", _projectScan.Failures.Take(12)) + "\r\n\r\n仍要保存登记吗？", "工程 CAD 未全部检查", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var paper = ItemText(PaperSizeBox);
            if (string.IsNullOrWhiteSpace(paper))
            {
                MessageBox.Show("请选择纸张规格。", "登记图框", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var extension = ItemText(ExtensionBox);
            Definition = new FrameDefinition
            {
                RegistrationId = string.IsNullOrWhiteSpace(_existing?.RegistrationId) ? Guid.NewGuid().ToString("N") : _existing.RegistrationId,
                BlockName = BlockNameBox.Text,
                TemplateRelativePath = _existing?.TemplateRelativePath,
                AttributeTagSignature = _attributeTagSignature ?? _existing?.AttributeTagSignature,
                DefinitionSignature = _definitionSignature ?? _existing?.DefinitionSignature,
                ReferenceAspectRatio = _referenceAspectRatio > 0d ? _referenceAspectRatio : _existing?.ReferenceAspectRatio ?? 0d,
                PaperSize = paper,
                Extension = extension == "无加长" ? string.Empty : extension,
                PaperOrientation = ItemText(OrientationBox),
                Note = NoteBox.Text?.Trim(),
                BuildingAttributeTag = TagOf(BuildingTagBox),
                SheetNumberAttributeTag = TagOf(SheetNumberTagBox),
                SheetNameAttributeTag = TagOf(SheetNameTagBox),
                PrintScaleAttributeTag = TagOf(PrintScaleTagBox),
                DefaultBuilding = BuildingValueBox.Text?.Trim(),
                DefaultSheetNumber = SheetNumberValueBox.Text?.Trim(),
                DefaultSheetName = SheetNameValueBox.Text?.Trim(),
                DefaultPrintScale = PrintScaleValueBox.Text?.Trim(),
                HasLayoutRange = _existing?.HasLayoutRange ?? false,
                LayoutLeftMargin = _existing?.LayoutLeftMargin ?? 0d,
                LayoutRightMargin = _existing?.LayoutRightMargin ?? 0d,
                LayoutTopMargin = _existing?.LayoutTopMargin ?? 0d,
                LayoutBottomMargin = _existing?.LayoutBottomMargin ?? 0d
            };
            DialogResult = true;
        }

        private void ScanProject_Click(object sender, RoutedEventArgs e) => RunProjectScan();

        private void RunProjectScan()
        {
            if (_projectScanner == null) return;
            ProjectScanText.Text = "正在扫描工程 CAD，请稍候……";
            try
            {
                _projectScan = _projectScanner();
                ProjectScanText.Text = _projectScan.Summary;
                if (_projectScan.Issues.Count > 0)
                    ShowIssueWindow(_projectScan);
                else if (_projectScan.Failures.Count > 0)
                    MessageBox.Show("重复 TAG 检查未发现问题，但以下 CAD 读取失败：\r\n\r\n" + string.Join("\r\n", _projectScan.Failures.Take(20)), "工程图框检查未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception exception)
            {
                ProjectScanText.Text = "扫描失败：" + exception.Message;
                MessageBox.Show(ProjectScanText.Text, "工程图框检查", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowIssueWindow(FrameProjectScanReport report)
        {
            var window = new Window { Title = "工程图框检查", Width = 650, Height = 430, MinWidth = 520, MinHeight = 320, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var description = new TextBlock { Text = "发现重复属性 TAG。请选择问题文件进入块编辑器修改属性定义，修改后执行 ATTSYNC。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            root.Children.Add(description);
            var list = new ListBox { ItemsSource = report.Issues, DisplayMemberPath = "DisplayText" };
            if (report.Issues.Count > 0) list.SelectedIndex = 0;
            Grid.SetRow(list, 1); root.Children.Add(list);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var openSelected = new Button { Content = "打开选中并修改", MinWidth = 125, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
            openSelected.Click += (s, e) => RequestIssueAction(list.SelectedItem as FrameProjectScanIssue, report.Issues, false, window);
            var openAll = new Button { Content = "全部打开并修改", MinWidth = 125, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
            openAll.Click += (s, e) => RequestIssueAction(list.SelectedItem as FrameProjectScanIssue, report.Issues, true, window);
            var close = new Button { Content = "关闭", MinWidth = 75, Padding = new Thickness(8, 4, 8, 4), IsCancel = true };
            close.Click += (s, e) => window.Close();
            buttons.Children.Add(openSelected); buttons.Children.Add(openAll); buttons.Children.Add(close);
            Grid.SetRow(buttons, 2); root.Children.Add(buttons);
            window.Content = root;
            window.ShowDialog();
        }

        private void RequestIssueAction(FrameProjectScanIssue selected, IEnumerable<FrameProjectScanIssue> all, bool openAll, Window issueWindow)
        {
            if (selected == null) return;
            RequestedIssues.Clear();
            RequestedIssues.Add(selected);
            if (openAll) RequestedIssues.AddRange(all.Where(x => !ReferenceEquals(x, selected)));
            OpenAllRequested = openAll;
            issueWindow.Close();
            DialogResult = false;
        }

        private void PaperSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ExtensionBox == null) return;
            var paper = ItemText(PaperSizeBox);
            var previous = ItemText(ExtensionBox);
            ExtensionBox.Items.Clear();
            ExtensionBox.Items.Add(new ComboBoxItem { Content = "无加长" });
            foreach (var extension in PaperSizeCatalog.GetSupportedExtensions(paper).Where(x => !string.IsNullOrWhiteSpace(x)))
                ExtensionBox.Items.Add(new ComboBoxItem { Content = extension });
            SelectComboByText(ExtensionBox, string.IsNullOrWhiteSpace(previous) ? "无加长" : previous);
        }

        private void TagBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var box = sender as ComboBox;
            if (box == null) return;
            if (box == BuildingTagBox) FillValueFromSelectedTag(box, BuildingValueBox);
            else if (box == SheetNumberTagBox) FillValueFromSelectedTag(box, SheetNumberValueBox);
            else if (box == SheetNameTagBox) FillValueFromSelectedTag(box, SheetNameValueBox);
            else if (box == PrintScaleTagBox) FillValueFromSelectedTag(box, PrintScaleValueBox);
        }

        private void FillValueFromSelectedTag(ComboBox tagBox, TextBox valueBox)
        {
            var tag = TagOf(tagBox);
            if (!string.IsNullOrWhiteSpace(tag) && _attributeValues.TryGetValue(tag, out var value) && !string.IsNullOrWhiteSpace(value))
                valueBox.Text = value.Trim();
        }

        private IEnumerable<ComboBox> TagBoxes()
        {
            return new[] { BuildingTagBox, SheetNumberTagBox, SheetNameTagBox, PrintScaleTagBox };
        }

        private static string ItemText(ComboBox box)
        {
            return (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? box.Text;
        }

        private static string TagOf(ComboBox box)
        {
            var value = ItemText(box);
            return value == DoNotRead ? string.Empty : value;
        }

        private static void SelectComboByText(ComboBox box, string text)
        {
            foreach (ComboBoxItem item in box.Items)
            {
                if (string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            if (box.IsEditable) box.Text = text;
            else box.SelectedIndex = 0;
        }

        private static void SelectStoredTag(ComboBox box, string tag)
        {
            box.SelectedItem = string.IsNullOrWhiteSpace(tag) ? DoNotRead : tag;
        }

        private static void SelectTag(ComboBox box, IEnumerable<string> tags, params string[] aliases)
        {
            var available = tags.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            foreach (var alias in aliases)
            {
                var exact = available.FirstOrDefault(tag => string.Equals(alias, tag, StringComparison.OrdinalIgnoreCase));
                if (exact != null) { box.SelectedItem = exact; return; }
            }
            foreach (var alias in aliases)
            {
                var normalizedAlias = NormalizeTag(alias);
                var normalized = available.FirstOrDefault(tag => string.Equals(normalizedAlias, NormalizeTag(tag), StringComparison.OrdinalIgnoreCase));
                if (normalized != null) { box.SelectedItem = normalized; return; }
            }
            box.SelectedItem = DoNotRead;
        }

        private static string NormalizeTag(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }
    }
}
