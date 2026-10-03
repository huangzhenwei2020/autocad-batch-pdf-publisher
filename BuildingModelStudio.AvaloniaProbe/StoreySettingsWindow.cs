using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class StoreySettingsWindow : Window
{
    private const string Independent = "独立楼层";
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly List<(string id, Grid row, TextBox name, TextBox elevation,
        TextBox height, ComboBox template, ComboBox kind)> _entries = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.OrangeRed };
    private readonly TextBox _datum = new() { Height = 34, MinHeight = 34 };
    private readonly TextBlock _datumLabel = new();
    private bool _updatingElevations;
    private string _datumId;
    private readonly Dictionary<string,TextBlock> _rangeSummaries = new();
    public List<StoreyModel> ResultStoreys { get; private set; } = new();

    public StoreySettingsWindow(BuildingModelDocument model)
    {
        Title = "楼层设置"; Width = 960; Height = 540;
        MinWidth = 960; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#151B23"));
        Foreground = Brushes.White;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(20), RowSpacing = 12 };
        root.Children.Add(new TextBlock { Text = "楼层设置", FontSize = 22, FontWeight = FontWeight.Bold });
        var help = new TextBlock { Text = "楼层名称可填 4～15层，自动生成 12 个实际楼层，共用一次登记的平面。标高填写范围首层标高，层高为每层高度，后续楼层自动累加。应用后可撤销。",
            TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(help, 1); root.Children.Add(help);
        var anchor = model.FindStorey("1F") ?? model.Storeys.OrderBy(s => Math.Abs(s.Elevation)).First();
        _datumId = anchor.Id;
        _datum.Text = anchor.Elevation.ToString("0.##", CultureInfo.CurrentCulture);
        _datum.TextChanged += (_, _) => RecalculatePreview();
        var datumRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _datumLabel.Text = $"{anchor.Name}基准标高 mm";
        _datumLabel.VerticalAlignment = VerticalAlignment.Center;
        datumRow.Children.Add(_datumLabel);
        datumRow.Children.Add(_datum);
        Grid.SetRow(datumRow, 2); root.Children.Add(datumRow);
        var header = NewRow();
        AddText(header, 0, "楼层 ID"); AddText(header, 1, "名称");
        AddText(header, 2, "首层标高 mm"); AddText(header, 3, "每层层高 mm");
        AddText(header, 4, "层数与标高范围"); AddText(header, 5, "操作");
        AddText(header, 6, "楼层类型");
        _rows.Children.Add(header);
        foreach (var storey in model.Storeys.OrderBy(s => s.Elevation)
            .Where(s=>string.IsNullOrWhiteSpace(s.StandardGroupId) || s.StandardGroupId==s.Id)) AddStorey(storey);
        RefreshTemplateOptions();
        RecalculatePreview();
        var scroller = new ScrollViewer { Content = _rows };
        Grid.SetRow(scroller, 3); root.Children.Add(scroller);
        Grid.SetRow(_error, 4); root.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Spacing = 9 };
        var add = new Button { Content = "新增楼层" };
        add.Click += (_, _) =>
        {
            var next = 1;
            while (_entries.Any(e => e.id==next+"F" || StandardStoreyLayout.TryParseRange(e.name.Text,out var first,out var last) && next>=first && next<=last)) next++;
            var top = _entries.Select(e => (TryNumber(e.elevation.Text, out var z)
                && TryNumber(e.height.Text, out var h)) ? z + h*RepeatCount(e.name.Text) : 0d).DefaultIfEmpty(0d).Max();
            AddStorey(new StoreyModel { Id = next + "F", Name = next + "层",
                Elevation = top, Height = 3300 });
            RefreshTemplateOptions();
            RecalculatePreview();
        };
        var addBasement = new Button { Content = "新增地下层" };
        addBasement.Click += (_, _) =>
        {
            var next = 1;
            while (_entries.Any(e => string.Equals(e.id, "B" + next,
                StringComparison.OrdinalIgnoreCase))) next++;
            var bottom = _entries.Select(e => TryNumber(e.elevation.Text, out var z) ? z : 0d)
                .DefaultIfEmpty(0d).Min();
            AddStorey(new StoreyModel { Id = "B" + next, Name = "地下" + next + "层",
                Elevation = bottom - 3300d, Height = 3300d }, true);
            RefreshTemplateOptions();
            RecalculatePreview();
        };
        var cancel = new Button { Content = "取消" };
        var addStandard = new Button { Content = "新增标准层" };
        addStandard.Click += (_,_) =>
        {
            var next=1;
            while(_entries.Any(e=>e.id==next+"F" || StandardStoreyLayout.TryParseRange(e.name.Text,out var first,out var last) && next>=first && next<=last))next++;
            AddStorey(new StoreyModel { Id=next+"F",Name=next+"～"+(next+1)+"层",Height=3000 });
            RefreshTemplateOptions();RecalculatePreview();
        };
        var addRoof = new Button { Content = "新增屋顶层" };
        addRoof.Click += (_, _) => AddSpecialStorey(StoreyKind.Roof, "RF", "屋顶层");
        var addMachine = new Button { Content = "新增机房层" };
        addMachine.Click += (_, _) => AddSpecialStorey(StoreyKind.MachineRoom, "MR", "机房层");
        cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = "应用楼层" };
        save.Click += (_, _) => { if (Collect()) Close(true); };
        actions.Children.Add(addBasement); actions.Children.Add(add);
        actions.Children.Add(addStandard);
        actions.Children.Add(addRoof); actions.Children.Add(addMachine);
        actions.Children.Add(cancel); actions.Children.Add(save);
        Grid.SetRow(actions, 5); root.Children.Add(actions);
        Content = root;
    }

    private void AddStorey(StoreyModel storey, bool basement = false)
    {
        var row = NewRow();
        AddText(row, 0, storey.Id);
        var name = AddInput(row, 1, storey.StandardFloorRange ?? storey.Name);
        name.TextChanged += (_,_)=>RecalculatePreview();
        var elevation = AddInput(row, 2, storey.Elevation.ToString("0.##", CultureInfo.CurrentCulture));
        elevation.TextChanged += (_, _) => EditElevation(storey.Id);
        var height = AddInput(row, 3, storey.Height.ToString("0.##", CultureInfo.CurrentCulture));
        height.TextChanged += (_, _) => RecalculatePreview();
        var template = new ComboBox { Height = 34, MinHeight = 34, Tag = storey.TemplateStoreyId,
            Background = new SolidColorBrush(Color.Parse("#202D3B")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#496273")) };
        ToolTip.SetTip(template, "改为标准层时使用来源层平面；改回独立楼层时保留当前构件。应用后可撤销。");
        var summary = new TextBlock { VerticalAlignment=VerticalAlignment.Center,FontSize=12,TextWrapping=TextWrapping.Wrap };
        _rangeSummaries[storey.Id]=summary;Grid.SetColumn(summary,4);row.Children.Add(summary);
        var kind = new ComboBox { ItemsSource = new[] { "普通层", "屋顶层", "机房层" },
            SelectedIndex = (int)storey.Kind, Height = 34, MinHeight = 34,
            Background = new SolidColorBrush(Color.Parse("#202D3B")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#496273")) };
        Grid.SetColumn(kind, 6); row.Children.Add(kind);
        kind.SelectionChanged += (_, _) => RefreshTemplateOptions();
        var remove = new Button { Content = "删除", Height = 34, MinHeight = 34 };
        ToolTip.SetTip(remove, "应用后同时删除本层构件，可撤销恢复。");
        remove.Click += (_, _) => DeleteStorey(storey.Id);
        Grid.SetColumn(remove, 5);
        row.Children.Add(remove);
        if (basement)
        { _entries.Insert(0, (storey.Id, row, name, elevation, height, template, kind)); _rows.Children.Insert(1, row); }
        else
        { _entries.Add((storey.Id, row, name, elevation, height, template, kind)); _rows.Children.Add(row); }
    }

    private void AddSpecialStorey(StoreyKind kind, string prefix, string name)
    {
        var id = prefix;
        for (var n = 2; _entries.Any(e => e.id == id); n++) id = prefix + n;
        var top = _entries.Select(e => TryNumber(e.elevation.Text, out var z)
            && TryNumber(e.height.Text, out var h) ? z + h*RepeatCount(e.name.Text) : 0d).DefaultIfEmpty(0d).Max();
        AddStorey(new StoreyModel { Id = id, Name = name, Kind = kind,
            Elevation = top, Height = 3000d });
        RefreshTemplateOptions();
        RecalculatePreview();
    }

    private void RefreshTemplateOptions()
    {
        foreach (var entry in _entries)
        {
            var preferred = entry.template.SelectedItem as string ?? entry.template.Tag as string;
            var options = new List<string> { Independent };
            if (entry.kind.SelectedIndex == 0)
                options.AddRange(_entries.Where(other => other.id != entry.id
                    && other.kind.SelectedIndex == 0).Select(other => other.id));
            entry.template.IsEnabled = entry.kind.SelectedIndex == 0;
            entry.template.ItemsSource = options;
            entry.template.SelectedItem = preferred != null && options.Contains(preferred) ? preferred : Independent;
            entry.template.Tag = null;
        }
    }

    private void DeleteStorey(string id)
    {
        var index = _entries.FindIndex(e => string.Equals(e.id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        if (_entries.Count == 1) { _error.Text = "至少保留一个楼层。"; return; }
        var removed = _entries[index];
        _entries.RemoveAt(index);
        _rows.Children.Remove(removed.row);
        if (string.Equals(_datumId, id, StringComparison.OrdinalIgnoreCase))
        {
            var next = _entries[Math.Min(index, _entries.Count - 1)];
            _datumId = next.id;
            _datumLabel.Text = (next.name.Text ?? next.id) + "基准标高 mm";
            _datum.Text = next.elevation.Text;
        }
        _error.Text = "";
        RefreshTemplateOptions();
        RecalculatePreview();
    }

    private void RecalculatePreview()
    {
        if (_updatingElevations || _entries.Count == 0 || !TryNumber(_datum.Text, out var datum)) return;
        var input = new List<StoreyModel>();
        foreach (var entry in _entries)
        {
            if (!TryNumber(entry.height.Text, out var height) || height <= 0d) return;
            input.Add(new StoreyModel { Id = entry.id, Name = entry.name.Text ?? entry.id,
                Height = height, Kind = (StoreyKind)entry.kind.SelectedIndex,
                TemplateStoreyId = TemplateId(entry.template) });
        }
        _updatingElevations = true;
        try {
            var expanded=StoreyElevationLayout.Resolve(StandardStoreyLayout.ExpandRanges(input),_datumId,datum);
            foreach (var entry in _entries)
            {
                var floor=expanded.Single(s=>s.Id==entry.id);
                entry.elevation.Text = floor.Elevation.ToString("0.##", CultureInfo.CurrentCulture);
                var count=RepeatCount(entry.name.Text);
                _rangeSummaries[entry.id].Text=count==1 ? "1 层" : count+" 层\n"+floor.Elevation.ToString("0.##")+"～"+(floor.Elevation+(count-1)*floor.Height).ToString("0.##")+" mm";
            }
            _error.Text="";
        } catch(ArgumentException ex) { _error.Text=ex.Message;
        } finally { _updatingElevations = false; }
    }

    private void EditElevation(string id)
    {
        if (_updatingElevations) return;
        var index = _entries.FindIndex(e => e.id == id);
        var anchor = _entries.FindIndex(e => e.id == _datumId);
        if (index < 0 || anchor < 0 || !TryNumber(_entries[index].elevation.Text, out var elevation)) return;
        if (index == anchor) { _datum.Text = _entries[index].elevation.Text; RecalculatePreview(); return; }
        var heightIndex = index > anchor ? index - 1 : index;
        var adjacentIndex = index > anchor ? index - 1 : index + 1;
        if (!TryNumber(_entries[adjacentIndex].elevation.Text, out var adjacent)) return;
        var height = (index > anchor ? elevation - adjacent : adjacent - elevation)/RepeatCount(_entries[heightIndex].name.Text);
        if (height <= 0) { _error.Text = "相邻楼层标高应递增，层高必须大于 0。"; return; }
        _entries[heightIndex].height.Text = height.ToString("0.##", CultureInfo.CurrentCulture);
        _error.Text = "";
        RecalculatePreview();
    }

    private bool Collect()
    {
        var input = new List<StoreyModel>();
        if (!TryNumber(_datum.Text, out var datum))
        { _error.Text = "一层基准标高无效。"; return false; }
        foreach (var entry in _entries)
        {
            if (string.IsNullOrWhiteSpace(entry.name.Text)
                || !TryNumber(entry.height.Text, out var height) || height <= 0d
                || !TryNumber(entry.elevation.Text, out _))
            { _error.Text = "请填写名称以及大于 0 的层高。"; return false; }
            input.Add(new StoreyModel { Id = entry.id, Name = entry.name.Text.Trim(),
                Height = height, Kind = (StoreyKind)entry.kind.SelectedIndex,
                TemplateStoreyId = TemplateId(entry.template) });
        }
        if (input.Any(s => !string.IsNullOrWhiteSpace(s.TemplateStoreyId)
            && input.FirstOrDefault(source => source.Id == s.TemplateStoreyId)?.TemplateStoreyId != null))
        { _error.Text = "标准层只能引用独立楼层。"; return false; }
        try { ResultStoreys = StoreyElevationLayout.Resolve(StandardStoreyLayout.ExpandRanges(input), _datumId, datum); }
        catch(ArgumentException ex) { _error.Text=ex.Message;return false; }
        if (_entries.Any(entry => !TryNumber(entry.elevation.Text, out var z)
            || Math.Abs(z - ResultStoreys.Single(s=>s.Id==entry.id).Elevation) > 0.01))
        { _error.Text = "请修正标高，相邻楼层标高应递增。"; return false; }
        return true;
    }

    private static string? TemplateId(ComboBox combo)
    {
        var selected = combo.SelectedItem as string;
        return selected == Independent ? null : selected;
    }

    private static int RepeatCount(string? label) => StandardStoreyLayout.TryParseRange(label,out var first,out var last) ? last-first+1 : 1;

    private static Grid NewRow() => new() { ColumnDefinitions = new ColumnDefinitions("60,*,120,110,140,54,100"),
        ColumnSpacing = 8, MinHeight = 40 };

    private static void AddText(Grid row, int column, string value)
    {
        var label = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, column); row.Children.Add(label);
    }

    private static TextBox AddInput(Grid row, int column, string? value)
    {
        var input = new TextBox { Text = value, Height = 34, MinHeight = 34,
            Background = new SolidColorBrush(Color.Parse("#202D3B")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#496273")) };
        Grid.SetColumn(input, column); row.Children.Add(input);
        return input;
    }

    private static bool TryNumber(string? text, out double value)
        => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value);

    internal static async Task RunEditingCheckAsync(BuildingModelDocument model)
    {
        var original=BuildingModelJson.ToJson(model);
        var dialog=new StoreySettingsWindow(model);
        async Task Flush() { await Task.Delay(80); }
        await Flush();
        var anchorIndex=dialog._entries.FindIndex(e=>e.id==dialog._datumId);
        if(anchorIndex+1>=dialog._entries.Count) throw new InvalidOperationException("楼层检查需至少一个上层。");
        var anchor=dialog._entries[anchorIndex];var upper=dialog._entries[anchorIndex+1];
        if(dialog._entries.Any(e=>e.elevation.IsReadOnly || !e.row.Children.OfType<Button>().Single().IsEnabled)
            || !upper.template.IsEnabled) throw new InvalidOperationException("已有构件楼层仍被锁定");
        TryNumber(anchor.elevation.Text,out var z);TryNumber(anchor.height.Text,out var h);
        upper.elevation.Text=(z+h+200).ToString(CultureInfo.InvariantCulture);await Flush();
        if(!TryNumber(anchor.height.Text,out var resized) || Math.Abs(resized-h-200)>.01 || !dialog.Collect())
            throw new InvalidOperationException("修改标高没有联动相邻层高："+dialog._error.Text);
        anchor.height.Text=(h+400).ToString(CultureInfo.InvariantCulture);await Flush();
        if(!TryNumber(upper.elevation.Text,out var upperZ) || Math.Abs(upperZ-z-h-400)>.01)
            throw new InvalidOperationException("修改层高没有联动上层标高");
        upper.elevation.Text=(z-100).ToString(CultureInfo.InvariantCulture);await Flush();
        if(dialog.Collect())throw new InvalidOperationException("无效楼层标高被接受");
        upper.elevation.Text=(z+h+400).ToString(CultureInfo.InvariantCulture);await Flush();
        upper.template.SelectedItem=anchor.id;await Flush();
        if(!dialog.Collect() || dialog.ResultStoreys.Single(s=>s.Id==upper.id).TemplateStoreyId!=anchor.id)
            throw new InvalidOperationException("已有构件的楼层不能修改标准层来源："+dialog._error.Text);
        dialog.DeleteStorey(anchor.id);await Flush();
        if(!dialog.Collect() || dialog.ResultStoreys.Any(s=>s.Id==anchor.id)
            || dialog.ResultStoreys.Any(s=>s.TemplateStoreyId==anchor.id))
            throw new InvalidOperationException("非空来源楼层删除或取消引用失败："+dialog._error.Text);
        if(BuildingModelJson.ToJson(model)!=original)throw new InvalidOperationException("未应用的楼层设置改写了模型");
        var rangeModel=SampleModelFactory.CreateEmptyModel("标准层范围检查");
        rangeModel.Storeys=Enumerable.Range(1,3).Select(i=>new StoreyModel { Id=i+"F",Name=i+"层",Height=3000 }).ToList();
        rangeModel.Storeys.Add(new StoreyModel { Id="4F",Name="4～15层",Height=3000 });
        rangeModel.Storeys.Add(new StoreyModel { Id="RF",Name="屋顶层",Kind=StoreyKind.Roof,Height=3000 });
        var ranges=new StoreySettingsWindow(rangeModel);await Flush();
        if(!ranges.Collect() || ranges.ResultStoreys.Count!=16 || ranges.ResultStoreys.Single(s=>s.Id=="15F").Elevation!=42000
            || ranges.ResultStoreys.Single(s=>s.Id=="RF").Elevation!=45000 || ranges._entries.Any(e=>e.row.Children.Contains(e.template)))
            throw new InvalidOperationException("标准层范围展开或隐藏来源选择失败："+ranges._error.Text);
        ranges.Show();await Flush();
        var content=(Grid)ranges.Content!;content.Background=ranges.Background;
        content.Measure(new Size(960,540));content.Arrange(new Rect(0,0,960,540));
        using(var image=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(960,540),new Vector(96,96)))
        {
            image.Render(content);
            var folder=System.IO.Path.Combine(Environment.CurrentDirectory,".artifacts","standard-floor-ranges");
            System.IO.Directory.CreateDirectory(folder);image.Save(System.IO.Path.Combine(folder,"storey-settings.png"));
        }
        var range=ranges._entries.Single(e=>e.id=="4F");range.height.Text="3200";await Flush();
        if(!ranges.Collect() || ranges.ResultStoreys.Single(s=>s.Id=="RF").Elevation!=47400)
            throw new InvalidOperationException("标准层范围层高没有联动后续屋顶");
        var restored=new StoreySettingsWindow(new BuildingModelDocument { Storeys=ranges.ResultStoreys });await Flush();
        if(restored._entries.Count!=5 || !restored.Collect() || restored.ResultStoreys.Count!=16)
            throw new InvalidOperationException("重新打开标准层设置没有保留分组");
        range.name.Text="4～2层";await Flush();if(ranges.Collect())throw new InvalidOperationException("无效范围被接受");
        ranges.Close();
        Console.WriteLine("STANDARD_FLOOR_RANGE_UI_OK range-count elevations roof-link collapsed-reopen no-source-selector invalid-range");
        var basement=SampleModelFactory.CreateEmptyModel("地下层检查");
        basement.Storeys.Insert(0,new StoreyModel { Id="B1",Name="地下一层",Elevation=-3300,Height=3300 });
        var underground=new StoreySettingsWindow(basement);await Flush();
        var lower=underground._entries[0];lower.elevation.Text="-4000";await Flush();
        if(!underground.Collect() || underground.ResultStoreys[0].Height!=4000 || underground.ResultStoreys[1].Elevation!=0)
            throw new InvalidOperationException("地下层标高修改没有联动自身层高");
        Console.WriteLine("STOREY_EDITING_UI_OK elevations heights templates delete invalid-input cancel basements");
    }
}
