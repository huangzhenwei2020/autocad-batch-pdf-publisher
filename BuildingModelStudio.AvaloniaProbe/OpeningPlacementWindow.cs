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

internal sealed partial class OpeningPlacementWindow : Window
{
    internal readonly List<OpeningPlacementEntry> Entries;
    internal readonly ListBox Choices=new();
    internal readonly TextBox Search=new(){PlaceholderText="搜索编号或类型"};
    internal readonly ComboBox Category=new(){ItemsSource=new[]{"全部","门","窗"},SelectedIndex=0,Width=90};
    internal readonly ComboBox SourceFilter=new(){ItemsSource=new[]{"全部来源","公共库","项目","模板","内置"},SelectedIndex=0,Width=140};
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
        Title="构件图库 · 门窗";Width=1440;Height=920;MinWidth=800;MinHeight=600;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#111A25"));Foreground=Brushes.White;FontSize=14;FontFamily=new FontFamily("Microsoft YaHei UI");
        var border=new SolidColorBrush(Color.Parse("#38556E"));
        foreach(var control in new[]{typeof(Button),typeof(TextBox),typeof(ComboBox)}) {
            Styles.Add(new Style(s=>s.OfType(control)){Setters={
                new Setter(TemplatedControl.BackgroundProperty,new SolidColorBrush(Color.Parse("#20364A"))),
                new Setter(TemplatedControl.BorderBrushProperty,border),new Setter(TemplatedControl.MinHeightProperty,32d)}});
        }
        var root=BuildLibraryLayout();
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
        Content=root;Opened+=async (_,_)=>{
            var screen=Screens.ScreenFromWindow(this);if(screen!=null){Width=Math.Max(MinWidth,Math.Min(Width,screen.WorkingArea.Width/screen.Scaling-32));Height=Math.Max(MinHeight,Math.Min(Height,screen.WorkingArea.Height/screen.Scaling-64));}
            Search.Focus();await ReloadLibraryAsync();};
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
            &&e.SearchText.Contains(query,StringComparison.OrdinalIgnoreCase)&&MatchesSubcategory(e))
            .OrderBy(e=>{var order=Array.IndexOf(new[]{"M0921","M1825","M1525","M1221","TLM1525","MLC2424","C1218","C0915","C1518"},e.Type.Code);return order<0?100:order;}).ToList();
        Choices.ItemsSource=filtered;Choices.SelectedItem=selected!=null&&filtered.Contains(selected)?selected:filtered.FirstOrDefault();
        _status.Text=filtered.Count==0?"没有匹配的门窗":$"{filtered.Count} 项";ShowSelection();
    }
    private void ShowSelection(){
        var type=(Choices.SelectedItem as OpeningPlacementEntry)?.Type;
        UpdateSelectionHeading();
        _loading=true;
        try {
            CodeInput.Text=type?.Code??"";
            var door=(type?.Kind??"").Contains("门");SillInput.IsEnabled=!door;ThresholdInput.IsEnabled=door;
            WidthInput.Text=Number(type?.Width);HeightInput.Text=Number(type?.Height);SillInput.Text=Number(type==null?null:door?0:type.Sill);
            ThresholdInput.Text=Number(type==null?null:door?type.ThresholdHeight:0);AngleInput.Text=Number(type?.PlanOpenAngle);
            var swing=type!=null&&OpeningPlanGeometry.HasPlanSwing(OpeningPlacementChoice.FromType(type).CreateOpening("preview",type.Width/2),type);
            var swingDoor=type!=null&&OpeningPlanGeometry.HasSwingDoor(OpeningPlacementChoice.FromType(type).CreateOpening("preview",type.Width/2),type);
            AngleInput.IsEnabled=swing;AnglePresets.IsEnabled=swing;OpenIn3DInput.IsEnabled=swingDoor;
            AnglePresets.SelectedIndex=Array.IndexOf(new[]{90d,45,30,15},type?.PlanOpenAngle??90);OpenIn3DInput.IsChecked=swingDoor&&type?.DefaultOpenIn3D==true;
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
        Place.IsEnabled=false;SaveTemplate.IsEnabled=false;NewAsset.IsEnabled=false;PlanPreview.SetType(null);ElevationPreview.SetType(null);VolumePreview.SetType(null);
        try {
            var choice=Draft();if(choice==null)return;
            var opening=choice.CreateOpening("preview",choice.Type.Width/2);
            PlanPreview.SetOpening(opening,choice.Type);ElevationPreview.SetOpening(opening,choice.Type);VolumePreview.SetType(choice.Type);SaveTemplate.IsEnabled=_saveTemplate!=null;NewAsset.IsEnabled=!_libraryBusy;
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
    private double _width;
    internal void SetType(OpeningTypeModel? type){
        SetOpening(type==null?null:OpeningPlacementChoice.FromType(type).CreateOpening("preview",type.Width/2),type);
    }
    internal void SetOpening(OpeningModel? opening,OpeningTypeModel? type){
        _width=type?.Width??0;
        _lines.Clear();if(type!=null&&opening!=null) {
            if(plan)_lines=OpeningPlanGeometry.Build(opening,type,200);
            else _lines=DoorWindowElevationGeometryBuilder.Build(OpeningElevationAdapter.ToScheduleItem(opening,type,type.Width,type.Height)).Lines
                .Select(l=>new ViewLine {X1=l.X1,Y1=l.Y1,X2=l.X2,Y2=l.Y2,Layer=l.Role==DoorWindowLineRole.Opening?"opening":"frame"}).ToList();
        }
        InvalidateVisual();
    }
    public override void Render(DrawingContext context){
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0F151B")),new Rect(Bounds.Size));
        var gridPen=new Pen(new SolidColorBrush(Color.Parse("#1B2933")),.5);
        for(var x=0d;x<Bounds.Width;x+=24)context.DrawLine(gridPen,new Point(x,0),new Point(x,Bounds.Height));
        for(var y=0d;y<Bounds.Height;y+=24)context.DrawLine(gridPen,new Point(0,y),new Point(Bounds.Width,y));
        if(_lines.Count==0)return;
        var points=_lines.SelectMany(l=>new[]{new Point(l.X1,l.Y1),new Point(l.X2,l.Y2)}).ToList();
        var minX=points.Min(p=>p.X);var maxX=points.Max(p=>p.X);var minY=points.Min(p=>p.Y);var maxY=points.Max(p=>p.Y);
        var scale=Math.Max(.00001,Math.Min(Math.Max(1,Bounds.Width-40)/Math.Max(1,maxX-minX),Math.Max(1,Bounds.Height-72)/Math.Max(1,maxY-minY)));
        Point At(double x,double y)=>new(Bounds.Width/2+(x-(minX+maxX)/2)*scale,(Bounds.Height+28)/2-(y-(minY+maxY)/2)*scale);
        var dimPen=new Pen(Brushes.LightGray,.8);var left=At(0,0).X;var right=At(_width,0).X;
        context.DrawLine(dimPen,new Point(left,30),new Point(right,30));
        foreach(var x in new[]{left,right}){context.DrawLine(dimPen,new Point(x,24),new Point(x,36));context.DrawLine(dimPen,new Point(x-3,33),new Point(x+3,27));}
        var label=new FormattedText(_width.ToString("0.###",CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,Typeface.Default,12,Brushes.LightGray);
        context.DrawText(label,new Point((left+right-label.Width)/2,8));
        if(plan){PlanEditorCanvas.DrawOpeningSymbolLines(context,_lines,At,new Pen(Brushes.LightBlue,1.3));return;}
        foreach(var line in _lines)context.DrawLine(new Pen(line.Layer=="opening"?Brushes.Gold:Brushes.LightBlue,1.3,
            line.LineType=="HIDDEN"?new DashStyle(new[]{4d,3d},0):null),At(line.X1,line.Y1),At(line.X2,line.Y2));
    }
}
