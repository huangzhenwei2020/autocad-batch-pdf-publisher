using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class AxisSettingsWindow : Window
{
    private sealed record Entry(AxisModel Axis, CheckBox Auto, TextBox Main, TextBox Start, TextBox End,
        ComboBox StartState, ComboBox EndState, ComboBox LineState);
    private readonly List<Entry> _entries=new();
    private readonly TextBox _height=new() {Width=65,Height=32},_width=new() {Width=65,Height=32},_diameter=new() {Width=65,Height=32};
    private readonly TextBlock _validation=new() {Foreground=Brushes.OrangeRed};
    public DrawingAnnotationSettings ResultAnnotations {get;private set;}=new();
    public List<AxisModel> ResultAxes {get;private set;}=new();
    internal static readonly string[] EndStates={"显示轴号","隐藏轴号","删除轴号·短线"};
    internal static readonly string[] LineStates={"显示轴线","隐藏轴线","删除轴线"};
    public AxisSettingsWindow(BuildingModelDocument model, string? storeyId = null)
    {
        Title="轴线 / 轴号编辑";Width=1280;Height=700;MinWidth=950;MinHeight=480;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#151B23"));Foreground=new SolidColorBrush(Color.Parse("#E8F1F4"));
        var root=new Grid {Margin=new Thickness(20),RowDefinitions=new("Auto,Auto,Auto,*,Auto"),RowSpacing=12};
        root.Children.Add(new TextBlock {Text="轴线 / 轴号编辑",FontSize=22,FontWeight=FontWeight.Bold});
        var help=new TextBlock {Text=(model.StoreyAxes?.ContainsKey(storeyId??"")==true?"本层独立轴网。":"整栋共用轴网。")+"隐藏保留编号；删除端部轴号后，该端缩至墙外 500 mm；删除整条轴线才重排自动编号。修改可撤销。",TextWrapping=TextWrapping.Wrap};
        Grid.SetRow(help,1);root.Children.Add(help);
        var toolbar=new WrapPanel {Orientation=Orientation.Horizontal};
        ResultAnnotations=DrawingAnnotationSettings.Resolve(model);
        _height.Text=ResultAnnotations.TextHeight.ToString("0.##");_width.Text=ResultAnnotations.WidthFactor.ToString("0.##");
        _diameter.Text=ResultAnnotations.AxisDiameter.ToString("0.##");
        foreach(var field in new[] {("文字高度 mm",_height),("宽度因子",_width),("轴号直径 mm",_diameter)}) {
            toolbar.Children.Add(new TextBlock {Text=field.Item1,Margin=new Thickness(6),VerticalAlignment=VerticalAlignment.Center});
            toolbar.Children.Add(field.Item2);
        }
        foreach(var side in new[] {"左","右","上","下"}) {
            toolbar.Children.Add(new TextBlock {Text=side+"侧",Margin=new Thickness(8),VerticalAlignment=VerticalAlignment.Center});
            foreach(var show in new[] {true,false}) {
                var button=new Button {Content=show?"显示":"隐藏",Margin=new Thickness(2)};
                button.Click+=(_,_)=>SetSide(side,show);toolbar.Children.Add(button);
            }
        }
        var restore=new Button {Content="显示全部轴线",Margin=new Thickness(8,0)};
        restore.Click+=(_,_)=>{foreach(var e in _entries) if(e.LineState.SelectedIndex!=2)e.LineState.SelectedIndex=0;};toolbar.Children.Add(restore);
        Grid.SetRow(toolbar,2);root.Children.Add(toolbar);
        var list=new StackPanel {Spacing=6};var header=Row();
        var captions=new[] {"方向 / 坐标 mm","自动","主轴号","下 / 左轴号","上 / 右轴号","下 / 左端","上 / 右端","整条轴线"};
        for(var i=0;i<captions.Length;i++)Cell(header,i,new TextBlock {Text=captions[i]});list.Children.Add(header);
        var vi=0;var hi=0;
        foreach(var axis in BuildingAxisLayout.Resolve(model,storeyId).OrderBy(a=>a.Vertical?0:1).ThenBy(a=>a.Position)) {
            var expected=axis.Deleted?"":axis.Vertical?(++vi).ToString():PlanEditing.LetterName(hi++);
            var original=(storeyId!=null&&model.StoreyAxes?.ContainsKey(storeyId)==true?model.StoreyAxes[storeyId]:model.Axes).FirstOrDefault(a=>a.Id==axis.Id);
            var automatic=axis.AutomaticNumber ?? (original==null || (string.IsNullOrWhiteSpace(original.Name) || original.Name==expected)
                && string.IsNullOrWhiteSpace(original.StartName)&&string.IsNullOrWhiteSpace(original.EndName));
            var row=Row();Cell(row,0,new TextBlock {Text=(axis.Vertical?"竖轴 X=":"横轴 Y=")+axis.Position.ToString("0.##")});
            var auto=new CheckBox {IsChecked=automatic};Cell(row,1,auto);
            var main=Input(axis.Name);var start=Input(axis.StartName);var end=Input(axis.EndName);
            Cell(row,2,main);Cell(row,3,start);Cell(row,4,end);
            var ss=Choice(EndStates,axis.StartRemoved?2:axis.StartHidden?1:0);
            var es=Choice(EndStates,axis.EndRemoved?2:axis.EndHidden?1:0);
            var ls=Choice(LineStates,axis.Deleted?2:axis.Hidden?1:0);
            Cell(row,5,ss);Cell(row,6,es);Cell(row,7,ls);
            void Sync(){main.IsReadOnly=start.IsReadOnly=end.IsReadOnly=auto.IsChecked==true;row.Opacity=ls.SelectedIndex==2?.55:1;}
            auto.IsCheckedChanged+=(_,_)=>Sync();ls.SelectionChanged+=(_,_)=>Sync();Sync();
            _entries.Add(new(axis,auto,main,start,end,ss,es,ls));list.Children.Add(row);
        }
        var scroll=new ScrollViewer {Content=list,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};
        Grid.SetRow(scroll,3);root.Children.Add(scroll);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=10};
        var cancel=new Button {Content="取消"};cancel.Click+=(_,_)=>Close(false);
        var apply=new Button {Content="应用轴网"};apply.Click+=(_,_)=>{
            if(!ReadAnnotations(out var settings)){_validation.Text="参数无效：轴号直径至少为文字高度的两倍。";return;}
            ResultAnnotations=settings;Collect();Close(true);
        };actions.Children.Add(_validation);actions.Children.Add(cancel);actions.Children.Add(apply);
        Grid.SetRow(actions,4);root.Children.Add(actions);Content=root;
    }
    internal void SetSide(string side,bool visible)
    {
        foreach(var e in _entries) {
            if(e.Axis.Vertical!=(side=="上"||side=="下"))continue;
            var state=side=="下"||side=="左"?e.StartState:e.EndState;
            if(state.SelectedIndex!=2)state.SelectedIndex=visible?0:1;
        }
    }
    internal void Collect()
    {
        ResultAxes=_entries.Select(e=>new AxisModel {
            Id=e.Axis.Id,Vertical=e.Axis.Vertical,Position=e.Axis.Position,ExtentStart=e.Axis.ExtentStart,ExtentEnd=e.Axis.ExtentEnd,
            AutomaticNumber=e.Auto.IsChecked==true,Name=e.Auto.IsChecked==true?null:e.Main.Text?.Trim(),
            StartName=e.Auto.IsChecked==true?null:e.Start.Text?.Trim(),EndName=e.Auto.IsChecked==true?null:e.End.Text?.Trim(),
            StartHidden=e.StartState.SelectedIndex==1,EndHidden=e.EndState.SelectedIndex==1,
            StartRemoved=e.StartState.SelectedIndex==2,EndRemoved=e.EndState.SelectedIndex==2,
            Hidden=e.LineState.SelectedIndex==1,Deleted=e.LineState.SelectedIndex==2 }).ToList();
    }
    private bool ReadAnnotations(out DrawingAnnotationSettings settings) {
        settings=new();
        if(!double.TryParse(_height.Text,out var height)||!double.TryParse(_width.Text,out var width)||!double.TryParse(_diameter.Text,out var diameter))return false;
        settings.TextHeight=height;settings.WidthFactor=width;settings.AxisDiameter=diameter;
        return DrawingAnnotationSettings.Valid(settings);
    }
    private static Grid Row()=>new() {ColumnDefinitions=new("165,50,85,100,100,165,165,135"),ColumnSpacing=8,MinHeight=38};
    private static void Cell(Grid row,int column,Control control){control.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(control,column);row.Children.Add(control);}
    private static TextBox Input(string? value)=>new() {Text=value,MaxLength=24,Background=new SolidColorBrush(Color.Parse("#202D3B"))};
    private static ComboBox Choice(string[] values,int selected)=>new() {ItemsSource=values,SelectedIndex=selected,HorizontalAlignment=HorizontalAlignment.Stretch};
}
