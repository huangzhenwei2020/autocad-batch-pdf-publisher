using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class OpeningPlacementWindow : Window
{
    internal readonly List<OpeningPlacementEntry> Entries;
    internal readonly ListBox Choices=new();
    internal readonly TextBox Search=new(){PlaceholderText="搜索编号或类型"};
    internal readonly ComboBox Category=new(){ItemsSource=new[]{"全部","门","窗"},SelectedIndex=0,Width=90};
    internal readonly ComboBox SourceFilter=new(){ItemsSource=new[]{"全部来源","项目","模板","内置"},SelectedIndex=0,Width=110};
    internal readonly Button Place=new(){Height=32,MinWidth=100,IsEnabled=false};
    internal readonly OpeningSymbolPreview PlanPreview=new(true),ElevationPreview=new(false);
    internal readonly TextBox CodeInput=new(){MaxLength=64},WidthInput=new(),HeightInput=new(),SillInput=new(),ThresholdInput=new(),AngleInput=new();
    internal readonly CheckBox OpenIn3DInput=new(){Content="三维显示开启",MinHeight=32};
    internal readonly ComboBox AnglePresets=new(){ItemsSource=new[]{"90°","45°","30°","15°"},PlaceholderText="自定义",Height=32};
    internal readonly Button SaveTemplate=new(){Height=32,MinWidth=112};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,MaxHeight=44};
    private readonly BuildingModelDocument _model;
    private readonly Func<OpeningTypeModel,string?>? _saveTemplate;
    private bool _loading;

    internal OpeningPlacementWindow(BuildingModelDocument model,string? selectedCode=null,string? category=null,Func<OpeningTypeModel,string?>? saveTemplate=null)
    {
        _model=model;_saveTemplate=saveTemplate;
        Entries=OpeningPlacementCatalog.Create(model);
        Title="门窗";Width=900;Height=700;MinWidth=720;MinHeight=560;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#111A25"));Foreground=Brushes.White;FontSize=13;
        var border=new SolidColorBrush(Color.Parse("#38556E"));
        foreach(var control in new[]{typeof(Button),typeof(TextBox),typeof(ComboBox)}) {
            Styles.Add(new Style(s=>s.OfType(control)){Setters={
                new Setter(TemplatedControl.BackgroundProperty,new SolidColorBrush(Color.Parse("#20364A"))),
                new Setter(TemplatedControl.BorderBrushProperty,border),new Setter(TemplatedControl.MinHeightProperty,32d)}});
        }
        var root=new Grid {RowDefinitions=new("36,40,*,44"),RowSpacing=8,Margin=new Thickness(12)};
        var header=new Grid {ColumnDefinitions=new("*,Auto")};
        header.Children.Add(new TextBlock {Text="门窗",FontSize=20,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        var project=new TextBlock {Text=model.Name,MaxWidth=380,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Foreground=Brushes.LightGray};
        Grid.SetColumn(project,1);header.Children.Add(project);root.Children.Add(header);
        var filters=new Grid {ColumnDefinitions=new("*,90,110"),ColumnSpacing=8};filters.Children.Add(Search);
        Grid.SetColumn(Category,1);filters.Children.Add(Category);Grid.SetColumn(SourceFilter,2);filters.Children.Add(SourceFilter);Grid.SetRow(filters,1);root.Children.Add(filters);
        var body=new Grid {ColumnDefinitions=new("*,280"),ColumnSpacing=12};Grid.SetRow(body,2);root.Children.Add(body);
        Choices.ItemTemplate=new FuncDataTemplate<OpeningPlacementEntry>((entry,_)=> {
            if(entry==null)return new Border();
            var row=new Grid {ColumnDefinitions=new("112,*,104"),MinHeight=46,ColumnSpacing=8};
            row.Children.Add(new TextBlock {Text=entry.Type.Code,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis});
            var title=new StackPanel {Spacing=2};title.Children.Add(new TextBlock {Text=entry.Name,TextTrimming=TextTrimming.CharacterEllipsis});
            title.Children.Add(new TextBlock {Text=entry.Source,FontSize=11,Foreground=Brushes.LightGray});Grid.SetColumn(title,1);row.Children.Add(title);
            var size=new TextBlock {Text=$"{entry.Type.Width:0.#} × {entry.Type.Height:0.#}",FontSize=12,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
            Grid.SetColumn(size,2);row.Children.Add(size);return row;
        });
        Choices.Background=Background;Choices.BorderBrush=border;Choices.BorderThickness=new Thickness(1);body.Children.Add(Choices);
        var settings=new StackPanel {Spacing=4};
        var previewPair=new Grid {ColumnDefinitions=new("*,*"),ColumnSpacing=8};
        void Preview(string title,OpeningSymbolPreview canvas,int column) {
            var pane=new StackPanel {Spacing=4};pane.Children.Add(new TextBlock {Text=title});canvas.Height=100;pane.Children.Add(canvas);
            Grid.SetColumn(pane,column);previewPair.Children.Add(pane);
        }
        Preview("平面",PlanPreview,0);Preview("立面",ElevationPreview,1);settings.Children.Add(previewPair);
        Control Field(string label,Control input) {
            var field=new StackPanel {Spacing=2};field.Children.Add(new TextBlock {Text=label,Foreground=Brushes.LightGray});
            input.Height=32;field.Children.Add(input);ToolTip.SetTip(input,label);return field;
        }
        void Pair(string a,Control x,string b,Control y) {
            var row=new Grid {ColumnDefinitions=new("*,*"),ColumnSpacing=8};row.Children.Add(Field(a,x));
            var second=Field(b,y);Grid.SetColumn(second,1);row.Children.Add(second);settings.Children.Add(row);
        }
        settings.Children.Add(Field("门窗编号",CodeInput));Pair("洞口宽 mm",WidthInput,"洞口高 mm",HeightInput);
        Pair("窗台高 mm",SillInput,"门槛高 mm",ThresholdInput);Pair("平面开启角度 °",AngleInput,"角度预设",AnglePresets);
        ToolTip.SetTip(ThresholdInput,"0 表示无门槛；相对洞口底的高度");settings.Children.Add(OpenIn3DInput);
        var settingsScroll=new ScrollViewer {Content=settings,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        Grid.SetColumn(settingsScroll,1);body.Children.Add(settingsScroll);
        var footer=new Grid {ColumnDefinitions=new("*,Auto,Auto,Auto"),ColumnSpacing=8};Grid.SetRow(footer,3);root.Children.Add(footer);footer.Children.Add(_status);
        SaveTemplate.Content=IconLabel("save","保存模板");SaveTemplate.Click+=(_,_)=>SaveCurrentTemplate();
        ToolTip.SetTip(SaveTemplate,"保存当前参数为项目门窗模板");Grid.SetColumn(SaveTemplate,1);footer.Children.Add(SaveTemplate);
        Place.Content=IconLabel("check","放置");Place.Background=new SolidColorBrush(Color.Parse("#1685D5"));ToolTip.SetTip(Place,"放置所选门窗");
        var cancel=new Button {Content=IconLabel("square-x","取消"),Height=32,MinWidth=90};cancel.Click+=(_,_)=>Close();
        Grid.SetColumn(cancel,2);footer.Children.Add(cancel);Grid.SetColumn(Place,3);footer.Children.Add(Place);
        Place.Click+=(_,_)=>Confirm();Choices.DoubleTapped+=(_,_)=>Confirm();
        Choices.SelectionChanged+=(_,_)=>ShowSelection();Search.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty)Filter();};
        Category.SelectionChanged+=(_,_)=>Filter();SourceFilter.SelectionChanged+=(_,_)=>Filter();
        foreach(var field in new[]{CodeInput,WidthInput,HeightInput,SillInput,ThresholdInput,AngleInput})field.PropertyChanged+=(_,e)=> {
            if(!_loading&&e.Property==TextBox.TextProperty)UpdateDraft();
        };
        AnglePresets.SelectionChanged+=(_,_)=>{if(!_loading&&AnglePresets.SelectedIndex>=0)AngleInput.Text=new[]{90,45,30,15}[AnglePresets.SelectedIndex].ToString(CultureInfo.InvariantCulture);};
        OpenIn3DInput.IsCheckedChanged+=(_,_)=>{if(!_loading)UpdateDraft();};
        KeyDown+=(_,e)=> {if(e.Key==Key.Escape){Close();e.Handled=true;}else if(e.Key==Key.Enter){Confirm();e.Handled=true;}};
        if(category!=null)Category.SelectedItem=category;
        Filter();Choices.SelectedItem=Choices.Items.OfType<OpeningPlacementEntry>().FirstOrDefault(e=>string.Equals(e.Type.Code,selectedCode,StringComparison.OrdinalIgnoreCase))
            ??Choices.Items.OfType<OpeningPlacementEntry>().FirstOrDefault();
        Content=root;Opened+=(_,_)=>Search.Focus();
    }
    private static Control IconLabel(string icon,string text){
        using var stream=AssetLoader.Open(new Uri("avares://万落建筑模型/Resources/Icons/"+icon+".png"));
        var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=6,VerticalAlignment=VerticalAlignment.Center};
        row.Children.Add(new Image {Source=new Bitmap(stream),Width=16,Height=16});row.Children.Add(new TextBlock {Text=text,VerticalAlignment=VerticalAlignment.Center});return row;
    }
    private void Filter(){
        var selected=Choices.SelectedItem as OpeningPlacementEntry;var query=Search.Text?.Trim()??"";
        var filtered=Entries.Where(e=>(Category.SelectedIndex==0||e.IsDoor==(Category.SelectedIndex==1))
            &&(SourceFilter.SelectedIndex==0||e.Source==SourceFilter.SelectedItem?.ToString())
            &&e.SearchText.Contains(query,StringComparison.OrdinalIgnoreCase)).ToList();
        Choices.ItemsSource=filtered;Choices.SelectedItem=selected!=null&&filtered.Contains(selected)?selected:filtered.FirstOrDefault();
        _status.Text=filtered.Count==0?"没有匹配的门窗":$"{filtered.Count} 项";ShowSelection();
    }
    private void ShowSelection(){
        var type=(Choices.SelectedItem as OpeningPlacementEntry)?.Type;
        _loading=true;
        try {
            CodeInput.Text=type?.Code??"";
            var door=(type?.Kind??"").Contains("门");SillInput.IsEnabled=!door;ThresholdInput.IsEnabled=door;
            WidthInput.Text=Number(type?.Width);HeightInput.Text=Number(type?.Height);SillInput.Text=Number(type==null?null:door?0:type.Sill);
            ThresholdInput.Text=Number(type==null?null:door?type.ThresholdHeight:0);AngleInput.Text=Number(type?.PlanOpenAngle);
            var swing=type!=null&&OpeningPlanGeometry.HasSwingDoor(OpeningPlacementChoice.FromType(type).CreateOpening("preview",type.Width/2),type);
            AngleInput.IsEnabled=swing;AnglePresets.IsEnabled=swing;OpenIn3DInput.IsEnabled=swing;
            AnglePresets.SelectedIndex=Array.IndexOf(new[]{90d,45,30,15},type?.PlanOpenAngle??90);OpenIn3DInput.IsChecked=swing&&type?.DefaultOpenIn3D==true;
        } catch(Exception ex){Status("门窗参数无效："+ex.Message);}
        finally {_loading=false;}
        UpdateDraft();
    }
    private static string Number(double? value)=>value?.ToString("0.###",CultureInfo.InvariantCulture)??"";
    private void Status(string text){_status.Text=text;ToolTip.SetTip(_status,text);}
    private OpeningPlacementChoice? Draft(){
        if(Choices.SelectedItem is not OpeningPlacementEntry entry)return null;
        var code=(CodeInput.Text??"").Trim();
        if(code.Length==0||code.Length>64||code.Any(char.IsControl))throw new InvalidOperationException("请输入有效门窗编号。");
        var numbers=new List<double>();
        foreach(var field in new[]{WidthInput,HeightInput,SillInput,ThresholdInput,AngleInput}) {
            if(!double.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))throw new InvalidOperationException("请输入有效的尺寸和角度数值。");
            numbers.Add(value);
        }
        if(numbers[4]<0||numbers[4]>180)throw new InvalidOperationException("平面开启角度应为 0～180°。");
        var type=OpeningConstruction.Copy(entry.Type);OpeningConstruction.ResizeType(type,numbers[0],numbers[1]);type.Code=code;
        type.Sill=numbers[2];type.ThresholdHeight=numbers[3];type.PlanOpenAngle=numbers[4];type.DefaultOpenIn3D=OpenIn3DInput.IsChecked==true;
        var choice=new OpeningPlacementChoice(type,numbers[2],numbers[3],numbers[4],type.DefaultOpenIn3D);
        var error=OpeningConstruction.Validate(choice.CreateOpening("preview",type.Width/2),type);
        if(error!=null)throw new InvalidOperationException(error);return choice;
    }
    private void UpdateDraft(){
        Place.IsEnabled=false;SaveTemplate.IsEnabled=false;PlanPreview.SetType(null);ElevationPreview.SetType(null);
        try {
            var choice=Draft();if(choice==null)return;
            var opening=choice.CreateOpening("preview",choice.Type.Width/2);
            PlanPreview.SetOpening(opening,choice.Type);ElevationPreview.SetOpening(opening,choice.Type);SaveTemplate.IsEnabled=_saveTemplate!=null;
            var existing=_model.OpeningTypes.FirstOrDefault(t=>string.Equals(t.Code?.Trim(),choice.Type.Code,StringComparison.OrdinalIgnoreCase));
            if(existing!=null&&!OpeningConstruction.SameConstruction(existing,choice.Type)){Status("该编号已有不同做法，请修改编号后放置。");return;}
            Place.IsEnabled=true;Status($"{Choices.ItemCount} 项");
        } catch(Exception ex){Status(ex.Message);}
    }
    internal void SaveCurrentTemplate(){
        if(!SaveTemplate.IsEnabled||_saveTemplate==null)return;
        try {
            var choice=Draft();if(choice==null)return;
            var error=_saveTemplate(choice.Type);if(error!=null){Status(error);return;}
            Entries.RemoveAll(e=>e.Source=="模板"&&string.Equals(e.Type.Code,choice.Type.Code,StringComparison.OrdinalIgnoreCase));
            var entry=new OpeningPlacementEntry(OpeningConstruction.Copy(choice.Type),string.IsNullOrWhiteSpace(choice.Type.Remarks)?choice.Type.Kind:choice.Type.Remarks,"模板");Entries.Add(entry);
            Search.Text="";SourceFilter.SelectedItem="模板";Filter();Choices.SelectedItem=entry;Status("已保存模板 "+choice.Type.Code);
        } catch(Exception ex){Status(ex.Message);}
    }
    internal void Confirm(){
        if(!Place.IsEnabled)return;
        try{var choice=Draft();if(choice!=null)Close(choice);}catch(Exception ex){Status(ex.Message);}
    }
}

internal sealed class OpeningSymbolPreview(bool plan) : Control
{
    internal IReadOnlyList<ViewLine> Lines=>_lines;
    private List<ViewLine> _lines=new();
    internal void SetType(OpeningTypeModel? type){
        SetOpening(type==null?null:OpeningPlacementChoice.FromType(type).CreateOpening("preview",type.Width/2),type);
    }
    internal void SetOpening(OpeningModel? opening,OpeningTypeModel? type){
        _lines.Clear();if(type!=null&&opening!=null) {
            if(plan)_lines=OpeningPlanGeometry.Build(opening,type,200);
            else _lines=DoorWindowElevationGeometryBuilder.Build(OpeningElevationAdapter.ToScheduleItem(opening,type,type.Width,type.Height)).Lines
                .Select(l=>new ViewLine {X1=l.X1,Y1=l.Y1,X2=l.X2,Y2=l.Y2,Layer=l.Role==DoorWindowLineRole.Opening?"opening":"frame"}).ToList();
        }
        InvalidateVisual();
    }
    public override void Render(DrawingContext context){
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0F151B")),new Rect(Bounds.Size));
        if(_lines.Count==0)return;
        var points=_lines.SelectMany(l=>new[]{new Point(l.X1,l.Y1),new Point(l.X2,l.Y2)}).ToList();
        var minX=points.Min(p=>p.X);var maxX=points.Max(p=>p.X);var minY=points.Min(p=>p.Y);var maxY=points.Max(p=>p.Y);
        var scale=Math.Max(.00001,Math.Min(Math.Max(1,Bounds.Width-32)/Math.Max(1,maxX-minX),Math.Max(1,Bounds.Height-32)/Math.Max(1,maxY-minY)));
        Point At(double x,double y)=>new(Bounds.Width/2+(x-(minX+maxX)/2)*scale,Bounds.Height/2-(y-(minY+maxY)/2)*scale);
        foreach(var line in _lines)context.DrawLine(new Pen(line.Layer=="opening"?Brushes.Gold:Brushes.LightBlue,1.3,
            line.LineType=="HIDDEN"?new DashStyle(new[]{4d,3d},0):null),At(line.X1,line.Y1),At(line.X2,line.Y2));
    }
}
