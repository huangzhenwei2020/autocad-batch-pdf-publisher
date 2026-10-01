using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private readonly TextBlock _propertyHeading = new()
    { Text = "属性", FontSize = 17, FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 40, 0) };
    private readonly Grid _slabFooter = new()
    { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(12, 8), IsVisible = false };
    private Action? _showSlabProperties;
    private Control? _slabPropertyMark;
    private readonly TextBlock _slabPropertyCode = new()
    { IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 36, 0),
        Foreground = new SolidColorBrush(Color.Parse("#9CCFFF")) };
    private SlabModel? _slabDraft;
    private string? _slabContourTarget;
    private string? _slabOpeningId;
    private ComboBox? _slabFloor;
    private TextBox? _slabCode, _holeName;
    private NumericUpDown? _slabThickness, _slabOffset, _holeWidth, _holeLength;
    private TextBox? _slabTop, _slabBottom;
    private StackPanel? _holeRows;
    private StackPanel? _holeFields;

    private static string Mm(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static SlabModel CopySlab(SlabModel slab)
    {
        var copy = BuildingModelJson.FromJson(BuildingModelJson.ToJson(
            new BuildingModelDocument { Slabs = new() { slab } })).Slabs[0];
        copy.Openings ??= new();
        return copy;
    }

    private void BeginSlabTool(PlanTool tool)
    {
        if (tool != PlanTool.Slab && _session.Model.Slabs.All(s => s.Id != _selectedId))
        { _status.Text = "请先选择楼板。"; return; }
        if (tool != PlanTool.Slab && !ReadSlabDraft(out _)) return;
        _slabContourTarget = tool == PlanTool.Slab ? null : _selectedId;
        _workspaces.SelectedIndex = 1;
        _showSlabProperties?.Invoke();
        SetPlanTool(tool);
        _status.Text = tool == PlanTool.SlabHoleRectangle ? "矩形洞口 RH：指定两个对角点，Esc 取消。"
            : tool == PlanTool.Slab ? "绘制楼板 SL：依次指定轮廓点，Enter 或 C 闭合，Esc 取消。"
            : tool == PlanTool.SlabOutline ? "编辑轮廓 EC：重绘外轮廓，Enter 或 C 闭合，保留现有洞口。"
            : "多边形洞口 PH：依次指定轮廓点，Enter 或 C 闭合，Esc 取消。";
    }

    private bool CommitSlabContour(PlanTool tool, List<PointModel> contour)
    {
        SlabModel draft;
        string? newOpeningId = null;
        if (tool == PlanTool.Slab)
        {
            var floor = (_storeyChooser.SelectedItem as StoreyItem)?.Id;
            var source = _session.Model.FindStorey(floor ?? "");
            floor = string.IsNullOrWhiteSpace(source?.TemplateStoreyId) ? floor : source.TemplateStoreyId;
            draft = new SlabModel { StoreyId = floor ?? "1F", Thickness = 180, TopOffset = 0 };
        }
        else
        {
            if (_slabContourTarget != _selectedId || !ReadSlabDraft(out var read))
            { _status.Text = "楼板选择或参数已变化，请重新启动绘制命令。"; return false; }
            draft = read!;
        }
        if (tool is PlanTool.Slab or PlanTool.SlabOutline) draft.Outline = contour;
        else
        {
            var id = Guid.NewGuid().ToString("N");
            draft.Openings.Add(new SlabOpeningModel
            { Id = id, Name = "洞口 " + (draft.Openings.Count + 1), Outline = contour });
            newOpeningId = id;
        }
        if (!_session.TryUpsertSlab(draft, out var affected, out var error))
        { _status.Text = error; return false; }
        if (newOpeningId != null) _slabOpeningId = newOpeningId;
        _selectedId = affected;
        _slabDraft = null;
        _ = RefreshModelAsync(tool == PlanTool.Slab ? "已绘制楼板" : "已更新楼板及洞口");
        ShowSlabFloor(draft.StoreyId);
        return true;
    }

    private static Button SlabButton(string label, string? icon = null)
    {
        var button = InspectorButton(label);
        button.Height = button.MinHeight = 32;
        button.Padding = new Thickness(8, 0);
        if (icon != null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(CommandIcon(icon, 16));
            if (label.Length > 0) content.Children.Add(new TextBlock
            { Text = label, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
        }
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(button, label.Length > 0 ? label : "删除洞口");
        ToolTip.SetTip(button, label.Length > 0 ? label : "删除洞口");
        return button;
    }

    private static TextBox SlabText(string value, string label, bool readOnly = false)
    {
        var box = new TextBox { Text = value, Height = 32, MinHeight = 32,
            Padding = new Thickness(10, 4), FontSize = 15, IsReadOnly = readOnly,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#192D3D")),
            Foreground = new SolidColorBrush(Color.Parse(readOnly ? "#A7C4DD" : "#E3EDF5")),
            BorderBrush = new SolidColorBrush(Color.Parse("#345267")), BorderThickness = new Thickness(1) };
        AutomationProperties.SetName(box, label);
        ToolTip.SetTip(box, label);
        return box;
    }

    private static Grid SlabRow(string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*"), Height = 32 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#BBCDDB")), FontSize = 15 });
        Grid.SetColumn(control, 1); row.Children.Add(control);
        return row;
    }

    private static NumericUpDown SlabNumber(double value, string label)
    {
        var fits = decimal.TryParse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number);
        var input = new NumericUpDown { Value = fits ? number : null, Increment = 1, FormatString = "0.###",
            Height = 32, MinHeight = 32, FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse("#192D3D")),
            BorderBrush = new SolidColorBrush(Color.Parse("#345267")), BorderThickness = new Thickness(1) };
        input.Classes.Add("slab-number");
        if (!fits) input.Text = value.ToString("R", CultureInfo.InvariantCulture);
        AutomationProperties.SetName(input, label); ToolTip.SetTip(input, label);
        return input;
    }

    private StackPanel SlabSection(string title)
    {
        var header = new Grid { RowDefinitions = new RowDefinitions("1,31"), Height = 32 };
        header.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#314859")) });
        var content = new StackPanel { Spacing = 8 };
        var button = SlabButton(title, "chevron-down");
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.FontWeight = FontWeight.SemiBold; button.FontSize = 16;
        button.BorderThickness = new Thickness(0); button.Background = Brushes.Transparent;
        button.Padding = new Thickness(0);
        button.Click += (_, _) =>
        {
            content.IsVisible = !content.IsVisible;
            ((StackPanel)button.Content!).Children[0] = CommandIcon(content.IsVisible ? "chevron-down" : "chevron-right", 16);
        };
        Grid.SetRow(button, 1); header.Children.Add(button);
        _properties.Children.Add(header);
        _properties.Children.Add(content);
        return content;
    }

    private void BuildSlabProperties(SlabModel slab)
    {
        _slabDraft = CopySlab(slab);
        if (string.IsNullOrWhiteSpace(_slabDraft.Code))
        {
            var number = 1;
            while (_session.Model.Slabs.Any(s => s.Code == "S-" + number)) number++;
            _slabDraft.Code = "S-" + number;
        }
        _properties.Margin = new Thickness(12, 0, 12, 8);
        _properties.Spacing = 8;
        _propertyHeading.Text = "楼板属性";
        _slabPropertyCode.Text = _slabDraft.Code; _slabPropertyCode.IsVisible = true;
        if (_slabPropertyMark != null) _slabPropertyMark.IsVisible = true;
        var basic = SlabSection("基本参数");
        var floors = _session.Model.Storeys.Where(s => string.IsNullOrWhiteSpace(s.TemplateStoreyId))
            .Select(s => new StoreyItem { Id = s.Id, Name = s.Name + "（标高 " + Mm(s.Elevation) + " mm）" }).ToList();
        _slabFloor = new ComboBox { ItemsSource = floors,
            SelectedItem = floors.FirstOrDefault(s => s.Id == slab.StoreyId),
            Height = 32, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse("#192D3D")),
            BorderBrush = new SolidColorBrush(Color.Parse("#345267")), BorderThickness = new Thickness(1),
            FontSize = 14 };
        AutomationProperties.SetName(_slabFloor, "楼板所属楼层");
        basic.Children.Add(SlabRow("所属楼层", _slabFloor));
        _slabCode = SlabText(_slabDraft.Code, "楼板编号");
        _slabThickness = SlabNumber(slab.Thickness, "板厚 mm");
        var storey = _session.Model.FindStorey(slab.StoreyId);
        _slabOffset = SlabNumber(slab.TopOffset ?? slab.TopElevation - (storey?.Elevation ?? 0), "板顶偏移 mm");
        basic.Children.Add(SlabRow("楼板编号", _slabCode));
        basic.Children.Add(SlabRow("板厚 mm", _slabThickness));
        basic.Children.Add(SlabRow("板顶偏移 mm", _slabOffset));
        _slabTop = SlabText("", "板顶标高", true);
        _slabBottom = SlabText("", "板底标高", true);
        basic.Children.Add(SlabRow("板顶标高", _slabTop));
        basic.Children.Add(SlabRow("板底标高", _slabBottom));
        _slabOffset.ValueChanged += (_, _) => UpdateSlabLevels();
        _slabThickness.ValueChanged += (_, _) => UpdateSlabLevels();
        _slabOffset.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.TextProperty) UpdateSlabLevels(); };
        _slabThickness.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.TextProperty) UpdateSlabLevels(); };
        _slabFloor.SelectionChanged += (_, _) => UpdateSlabLevels();
        UpdateSlabLevels();
        var holes = SlabSection("洞口");
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Height = 32 };
        var rectangle = SlabButton("矩形洞口", "square");
        var polygon = SlabButton("多边形洞口", "pentagon");
        var remove = SlabButton("删除洞口", "trash");
        foreach (var button in new[] { rectangle, polygon, remove })
        { button.FontSize = 11; button.Padding = new Thickness(2, 0); button.BorderThickness = new Thickness(0);
            button.Background = Brushes.Transparent; }
        rectangle.Click += (_, _) => BeginSlabTool(PlanTool.SlabHoleRectangle);
        polygon.Click += (_, _) => BeginSlabTool(PlanTool.SlabHolePolygon);
        remove.Click += (_, _) => DeleteSlabOpening();
        tools.Children.Add(rectangle); Grid.SetColumn(polygon, 1); tools.Children.Add(polygon);
        Grid.SetColumn(remove, 2); tools.Children.Add(remove);
        holes.Children.Add(tools);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,32"), Height = 28,
            Background = new SolidColorBrush(Color.Parse("#1D3344")) };
        header.Children.Add(new TextBlock { Text = "名称", Margin = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center });
        var size = new TextBlock { Text = "尺寸 mm", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(size, 1); header.Children.Add(size);
        AddHoleColumnLines(header);
        _holeRows = new StackPanel { Spacing = 0 };
        _holeRows.Children.Add(header);
        holes.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#345267")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = _holeRows });
        var parameters = SlabSection("洞口参数");
        _holeFields = new StackPanel { Spacing = 8 };
        parameters.Children.Add(_holeFields);
        if (!_slabDraft.Openings.Any(o => o.Id == _slabOpeningId)) _slabOpeningId = _slabDraft.Openings.FirstOrDefault()?.Id;
        BuildHoleRows();
        BuildHoleFields();
        _slabFooter.Children.Clear();
        _slabFooter.IsVisible = true;
        var cancel = SlabButton("取消"); cancel.Margin = new Thickness(0, 0, 4, 0);
        cancel.Click += (_, _) => { CancelActiveCommand(); RefreshProperties(); };
        var apply = SlabButton("应用"); apply.Margin = new Thickness(4, 0, 0, 0);
        apply.Background = new SolidColorBrush(Color.Parse("#128FFF"));
        apply.Click += async (_, _) =>
        {
            if (!ReadSlabDraft(out var draft)) return;
            if (!_session.TryUpsertSlab(draft!, out var id, out var error)) { _status.Text = error; return; }
            _selectedId = id; _slabDraft = null;
            await RefreshModelAsync("已应用楼板及洞口参数");
            ShowSlabFloor(draft!.StoreyId);
        };
        _slabFooter.Children.Add(cancel); Grid.SetColumn(apply, 1); _slabFooter.Children.Add(apply);
    }

    private void UpdateSlabLevels()
    {
        if (_slabTop == null || _slabBottom == null) return;
        if (!TryNumber(_slabOffset?.Text, out var offset) || !TryNumber(_slabThickness?.Text, out var thickness))
        { _slabTop.Text = _slabBottom.Text = "—"; return; }
        var floor = _session.Model.FindStorey((_slabFloor?.SelectedItem as StoreyItem)?.Id ?? "");
        var top = (floor?.Elevation ?? 0) + offset;
        _slabTop.Text = Mm(top) + " mm"; _slabBottom.Text = Mm(top - thickness) + " mm";
    }

    private void BuildHoleRows()
    {
        if (_holeRows == null || _slabDraft == null) return;
        while (_holeRows.Children.Count > 1) _holeRows.Children.RemoveAt(1);
        foreach (var hole in _slabDraft.Openings)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,32"), Height = 32,
                Background = new SolidColorBrush(Color.Parse(hole.Id == _slabOpeningId ? "#145F9C" : "#111E29")) };
            var selected = hole.Id == _slabOpeningId;
            var pick = SlabButton(hole.Name ?? "洞口"); pick.BorderThickness = new Thickness(selected ? 4 : 0, 0, 0, 0);
            pick.BorderBrush = new SolidColorBrush(Color.Parse("#38D4FF"));
            pick.Background = Brushes.Transparent; pick.HorizontalContentAlignment = HorizontalAlignment.Left;
            pick.Click += (_, _) =>
            {
                if (!ReadSlabDraft(out var draft)) return;
                _slabDraft = draft; _slabOpeningId = hole.Id;
                BuildHoleRows(); BuildHoleFields();
            };
            row.Children.Add(pick);
            var label = new TextBlock { Text = Mm(hole.Outline.Max(p => p.X) - hole.Outline.Min(p => p.X))
                + " × " + Mm(hole.Outline.Max(p => p.Y) - hole.Outline.Min(p => p.Y)), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            var trash = SlabButton("", "trash"); trash.Padding = new Thickness(4, 0);
            trash.Background = Brushes.Transparent; trash.BorderThickness = new Thickness(0);
            trash.Click += (_, _) => { if (!ReadSlabDraft(out var draft)) return;
                _slabDraft = draft; _slabOpeningId = hole.Id; DeleteSlabOpening(); };
            Grid.SetColumn(trash, 2); row.Children.Add(trash);
            _holeRows.Children.Add(row);
            AddHoleColumnLines(row);
        }
        if (_slabDraft.Openings.Count == 0) _holeRows.Children.Add(new TextBlock
        { Text = "无洞口", Margin = new Thickness(10, 8) });
    }

    private static void AddHoleColumnLines(Grid row)
    {
        for (var column = 0; column < 2; column++)
        {
            var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.Parse("#345267")) };
            Grid.SetColumn(line, column); row.Children.Add(line);
        }
    }

    private void BuildHoleFields()
    {
        _holeFields?.Children.Clear();
        _holeName = null; _holeWidth = _holeLength = null;
        var hole = _slabDraft?.Openings.FirstOrDefault(o => o.Id == _slabOpeningId);
        if (hole == null || _holeFields == null) return;
        _holeName = SlabText(hole.Name ?? "", "洞口名称");
        _holeWidth = SlabNumber(hole.Outline.Max(p => p.X) - hole.Outline.Min(p => p.X), "洞口宽 mm");
        _holeLength = SlabNumber(hole.Outline.Max(p => p.Y) - hole.Outline.Min(p => p.Y), "洞口长 mm");
        _holeFields.Children.Add(SlabRow("名称", _holeName));
        _holeFields.Children.Add(SlabRow("宽 mm", _holeWidth));
        _holeFields.Children.Add(SlabRow("长 mm", _holeLength));
        ToolTip.SetTip(_holeWidth, "以洞口左下角为基点调整 X 方向尺寸，多边形按比例缩放");
        ToolTip.SetTip(_holeLength, "以洞口左下角为基点调整 Y 方向尺寸，多边形按比例缩放");
    }

    private bool ReadSlabDraft(out SlabModel? draft)
    {
        draft = null;
        if (_slabDraft == null) { _status.Text = "请先选择楼板。"; return false; }
        var candidate = CopySlab(_slabDraft);
        if (!TryNumber(_slabThickness?.Text, out var thickness) || thickness <= 0.5
            || !TryNumber(_slabOffset?.Text, out var offset))
        { _status.Text = "板厚必须大于 0.5 mm，板顶偏移必须是有效数字。"; return false; }
        candidate.Thickness = thickness; candidate.TopOffset = offset;
        candidate.Code = _slabCode?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(candidate.Code)) { _status.Text = "楼板编号不能为空。"; return false; }
        candidate.StoreyId = (_slabFloor?.SelectedItem as StoreyItem)?.Id ?? candidate.StoreyId;
        var hole = candidate.Openings.FirstOrDefault(o => o.Id == _slabOpeningId);
        if (hole != null && _holeName != null)
        {
            if (!TryNumber(_holeWidth?.Text, out var width) || !TryNumber(_holeLength?.Text, out var length)
                || width <= 0.5 || length <= 0.5 || string.IsNullOrWhiteSpace(_holeName.Text))
            { _status.Text = "洞口名称和宽、长必须有效。"; return false; }
            var x = hole.Outline.Min(p => p.X); var y = hole.Outline.Min(p => p.Y);
            var oldWidth = hole.Outline.Max(p => p.X) - x; var oldLength = hole.Outline.Max(p => p.Y) - y;
            foreach (var point in hole.Outline)
            { point.X = x + (point.X - x) * width / oldWidth; point.Y = y + (point.Y - y) * length / oldLength; }
            hole.Name = _holeName.Text.Trim();
        }
        var error = SlabGeometry.Validate(candidate);
        if (error != null) { _status.Text = error; return false; }
        draft = candidate;
        return true;
    }

    private void DeleteSlabOpening()
    {
        if (_slabOpeningId == null) { _status.Text = "请先选择洞口。"; return; }
        if (!ReadSlabDraft(out var draft)) return;
        draft!.Openings.RemoveAll(o => o.Id == _slabOpeningId);
        _slabDraft = draft; _slabOpeningId = draft.Openings.FirstOrDefault()?.Id;
        BuildHoleRows(); BuildHoleFields();
        _status.Text = "洞口已从编辑草稿移除，点击应用写入，取消恢复。";
    }

    private void SelectSlabOpening(string id, string opening)
    {
        if (id == _selectedId && _slabDraft != null)
        {
            if (!ReadSlabDraft(out var draft)) return;
            _slabDraft = draft;
        }
        else SelectById(id);
        _slabOpeningId = opening;
        BuildHoleRows(); BuildHoleFields();
        _showSlabProperties?.Invoke();
    }

    private void ShowSlabFloor(string sourceId)
    {
        var currentId = (_storeyChooser.SelectedItem as StoreyItem)?.Id;
        var current = _session.Model.FindStorey(currentId ?? "");
        if ((current?.TemplateStoreyId ?? currentId) == sourceId) return;
        _storeyChooser.SelectedItem = (_storeyChooser.ItemsSource as IEnumerable<StoreyItem>)?
            .FirstOrDefault(s => s.Id == sourceId);
    }

    private async Task<bool> RunSlabSmokeCheckAsync()
    {
        var savedModel = _session.Model;
        var savedSelection = _selectedId;
        var savedFile = _savedJson;
        try
        {
            var model = SampleModelFactory.CreateEmptyModel("楼板界面回归");
            _session = new BuildingModelEditSession(model);
            await RefreshModelAsync("开始楼板界面回归");
            ExecuteCommand("SL");
            foreach (var p in new[] { new PointModel(0, 0), new PointModel(6000, 0),
                new PointModel(6000, 5000), new PointModel(0, 5000) }) _planCanvas.AddContourPoint(p);
            if (!_planCanvas.CompleteContour()) throw new InvalidOperationException("轮廓未完成");
            await Task.Delay(200);
            var id = _session.Model.Slabs.Single().Id;
            SelectById(id);
            ExecuteCommand("RH");
            _planCanvas.AddContourPoint(new PointModel(1000, 1000));
            _planCanvas.AddContourPoint(new PointModel(2500, 3000));
            await Task.Delay(200);
            if (_session.Model.Slabs.Single().Openings.Count != 1) throw new InvalidOperationException("矩形洞口未写入");
            ExecuteCommand("PH");
            foreach (var p in new[] { new PointModel(3500, 1000), new PointModel(4500, 1000),
                new PointModel(4500, 3000), new PointModel(4000, 3500), new PointModel(3500, 3000) })
                _planCanvas.AddContourPoint(p);
            if (!_planCanvas.CompleteContour()) throw new InvalidOperationException("多边形洞口未写入");
            await Task.Delay(200);
            var before = _session.Revision;
            ExecuteCommand("RH");
            _planCanvas.AddContourPoint(new PointModel(-500, 0));
            _planCanvas.AddContourPoint(new PointModel(500, 500));
            if (_session.Revision != before) throw new InvalidOperationException("越界洞口产生提交");
            CancelActiveCommand();
            SelectById(id);
            _slabThickness!.Value = 240; _slabOffset!.Value = 50;
            if (_slabTop!.Text != Mm(_session.Model.FindStorey(_session.Model.Slabs.Single().StoreyId).Elevation + 50) + " mm")
                throw new InvalidOperationException("板顶标高没有实时更新");
            _holeName!.Text = "楼梯井"; _holeWidth!.Value = 900;
            var apply = _slabFooter.Children.OfType<Button>().Last();
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            var slab = _session.Model.Slabs.Single();
            if (slab.Thickness != 240 || slab.TopOffset != 50) throw new InvalidOperationException("应用丢失板参数");
            if (Math.Abs(slab.Openings.Last().Outline.Max(p => p.X) - slab.Openings.Last().Outline.Min(p => p.X) - 900) > .001)
                throw new InvalidOperationException("洞口尺寸未应用");
            ExecuteCommand("DH");
            if (_session.Model.Slabs.Single().Openings.Count != 2 || _slabDraft!.Openings.Count != 1)
                throw new InvalidOperationException("删除草稿越过应用事务");
            _slabFooter.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_slabDraft!.Openings.Count != 2) throw new InvalidOperationException("取消没有还原洞口");
            ExecuteCommand("DH");
            _slabFooter.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            if (_session.Model.Slabs.Single().Openings.Count != 1) throw new InvalidOperationException("删除洞口未应用");
            await UndoModelAsync(); await RedoModelAsync();
            ExecuteCommand("CO"); ExecuteCommand("@7000,0");
            await Task.Delay(200);
            if (_session.Model.Slabs.Count != 2 || _session.Model.Slabs.Last().Openings.Count != 1)
                throw new InvalidOperationException("复制没有携带洞口");
            foreach (var field in new Control[] { _slabFloor!, _slabCode!, _slabThickness!, _slabOffset!,
                _slabTop!, _slabBottom!, _holeName!, _holeWidth!, _holeLength! }.Where(c => c != null))
            {
                if (Math.Abs(field.Bounds.Height - 32) > .1 || field.Bounds.Width < 90)
                    throw new InvalidOperationException("控件高度不一致或编辑区不足：" + AutomationProperties.GetName(field));
            }
            foreach (var button in _slabFooter.Children.OfType<Button>())
                if (Math.Abs(button.Bounds.Height - 32) > .1 || button.Bounds.Width < 100)
                    throw new InvalidOperationException("固定底部按钮不完整");
            var spinner = _slabThickness!.GetVisualDescendants().OfType<ButtonSpinner>().Single();
            var increment = spinner.GetVisualDescendants().OfType<RepeatButton>()
                .Single(b => b.Name == "PART_IncreaseButton" && b.IsEffectivelyVisible && b.Bounds.Height > 0);
            var oldThickness = _slabThickness.Value;
            increment.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_slabThickness.Value != oldThickness + 1) throw new InvalidOperationException("数字上箭头没有接入参数");
            var scroll = _properties.GetVisualAncestors().OfType<ScrollViewer>().First();
            if (scroll.Extent.Height > scroll.Viewport.Height + 1)
            {
                var bar = scroll.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Orientation.Vertical
                    && b.IsEffectivelyVisible && b.Bounds.Height > 100 && b.Maximum > 0);
                var track = bar.GetVisualDescendants().OfType<Track>().Single(t => t.IsEffectivelyVisible && t.Bounds.Height > 0);
                track.Value = track.Maximum;
                await Task.Delay(30);
                if (scroll.Offset.Y < 1) throw new InvalidOperationException("胶囊滑块没有同步到正文滚动");
                bar.Value = 0;
                (track.IncreaseButton as RepeatButton)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(30);
                if (scroll.Offset.Y < 1) throw new InvalidOperationException("点击滚动路径没有翻页");
                scroll.Offset = new Vector(0, 0);
            }
            Console.WriteLine("SLAB_UI_CHECK_OK polygon rectangle invalid parameters cancel delete copy undo redo");
            return true;
        }
        catch (Exception ex) { Console.Error.WriteLine("SLAB_UI_CHECK_FAILED " + ex); return false; }
        finally
        {
            _session = new BuildingModelEditSession(savedModel);
            _savedJson = savedFile;
            _selectedId = savedSelection;
            _slabDraft = null;
            await RefreshModelAsync("楼板界面回归结束");
            _workspaces.SelectedIndex = 1;
            _planCanvas.Fit();
        }
    }
}
