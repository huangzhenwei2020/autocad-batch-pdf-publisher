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
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class WallDrawingWindow : Window
{
    internal readonly ListBox Choices=new();
    internal readonly NumericUpDown ThicknessInput=new(){Minimum=10,Maximum=10000,Increment=10,FormatString="0.###"};
    internal readonly NumericUpDown HeightInput=new(){Minimum=10,Maximum=100000,Increment=100,FormatString="0.###"};
    internal readonly CheckBox FollowStorey=new(){Content="随楼层高度"};
    internal readonly ComboBox AxisInput=new(){ItemsSource=new[]{"墙中","左墙面","右墙面","自定义偏移"}};
    internal readonly NumericUpDown OffsetInput=new(){Minimum=-10000,Maximum=10000,Increment=10,FormatString="0.###"};
    internal readonly TextBox MaterialInput=new(){MaxLength=64};
    internal readonly Button Draw=new(){Height=36,MinWidth=120};
    internal readonly WallShapePreview PlanPreview=new(true),VolumePreview=new(false);
    internal readonly TextBlock Status=new(){TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    private readonly double _storeyHeight;
    private readonly TextBlock _heading=new(){FontSize=18,FontWeight=FontWeight.SemiBold};
    private bool _loading;
    private decimal _customHeight;

    internal WallDrawingWindow(WallDrawingChoice choice,double storeyHeight)
    {
        _storeyHeight=storeyHeight;
        Title="墙体";Width=1060;Height=720;MinWidth=780;MinHeight=540;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;FontSize=14;FontFamily=new("Microsoft YaHei UI");
        Background=new SolidColorBrush(Color.Parse("#111A25"));Foreground=Brushes.White;
        var edge=new SolidColorBrush(Color.Parse("#38556E"));
        foreach(var type in new[]{typeof(Button),typeof(TextBox),typeof(ComboBox),typeof(NumericUpDown)})
            Styles.Add(new Style(s=>s.OfType(type)){Setters={new Setter(TemplatedControl.BackgroundProperty,new SolidColorBrush(Color.Parse("#20364A"))),
                new Setter(TemplatedControl.BorderBrushProperty,edge),new Setter(TemplatedControl.MinHeightProperty,34d)}});
        var root=new Grid {RowDefinitions=new("56,*,60"),ColumnDefinitions=new("216,*,300")};
        var title=new TextBlock {Text="墙体",FontSize=20,FontWeight=FontWeight.SemiBold,Margin=new Thickness(18,12),VerticalAlignment=VerticalAlignment.Center};
        Grid.SetColumnSpan(title,3);root.Children.Add(title);
        var types=new DockPanel {Margin=new Thickness(12,0,12,12)};
        var label=new TextBlock {Text="墙体类型",Margin=new Thickness(6,8,0,12)};DockPanel.SetDock(label,Dock.Top);types.Children.Add(label);
        Choices.Background=Brushes.Transparent;Choices.BorderThickness=new Thickness(0);Choices.ItemsSource=new[]{false,true};
        Choices.ItemTemplate=new FuncDataTemplate<bool>((curved,_)=>{
            var preview=new WallShapePreview(false){Height=128};preview.SetChoice(new WallDrawingChoice(Curved:curved),3000);
            var tile=new StackPanel {Spacing=8,Margin=new Thickness(8)};tile.Children.Add(preview);
            tile.Children.Add(new TextBlock {Text=curved?"弧墙":"直墙",FontWeight=FontWeight.SemiBold});return tile;
        });
        types.Children.Add(Choices);Grid.SetRow(types,1);root.Children.Add(types);
        var previews=new Grid {RowDefinitions=new("40,*"),Margin=new Thickness(8,0,12,12)};
        previews.Children.Add(_heading);
        var tabs=new TabControl {ItemsSource=new[]{new TabItem {Header="平面",Content=PlanPreview},new TabItem {Header="三维",Content=VolumePreview}},SelectedIndex=0};
        tabs.Styles.Add(new Style(s=>s.OfType<TabItem>()){Setters={new Setter(TabItem.FontSizeProperty,14d),
            new Setter(TabItem.PaddingProperty,new Thickness(16,6)),new Setter(TabItem.MinHeightProperty,36d)}});
        Grid.SetRow(tabs,1);previews.Children.Add(tabs);Grid.SetColumn(previews,1);Grid.SetRow(previews,1);root.Children.Add(previews);
        var fields=new StackPanel {Spacing=10,Margin=new Thickness(16,8,16,16)};
        fields.Children.Add(new TextBlock {Text="绘制参数",FontWeight=FontWeight.SemiBold,Margin=new Thickness(0,0,0,8)});
        void Field(string text,Control input){fields.Children.Add(new TextBlock {Text=text});input.HorizontalAlignment=HorizontalAlignment.Stretch;fields.Children.Add(input);}
        Field("墙厚 mm",ThicknessInput);fields.Children.Add(FollowStorey);Field("墙高 mm",HeightInput);
        Field("定位线",AxisInput);Field("定位偏移 mm",OffsetInput);Field("材料",MaterialInput);
        var settings=new Border {BorderBrush=edge,BorderThickness=new Thickness(1,0,0,0),Child=new ScrollViewer {
            Content=fields,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled}};
        Grid.SetColumn(settings,2);Grid.SetRow(settings,1);root.Children.Add(settings);
        var footer=new Grid {ColumnDefinitions=new("*,Auto,Auto"),ColumnSpacing=10,Margin=new Thickness(16,10)};footer.Children.Add(Status);
        var cancel=new Button {Content="取消",MinWidth=90,Height=36};cancel.Click+=(_,_)=>Close();Grid.SetColumn(cancel,1);footer.Children.Add(cancel);
        Draw.Content=IconLabel("wall-plan","开始绘制");Draw.Background=new SolidColorBrush(Color.Parse("#078CE0"));
        Draw.Click+=(_,_)=>Confirm();Grid.SetColumn(Draw,2);footer.Children.Add(Draw);
        var bottom=new Border {BorderBrush=edge,BorderThickness=new Thickness(0,1,0,0),Child=footer};Grid.SetRow(bottom,2);Grid.SetColumnSpan(bottom,3);root.Children.Add(bottom);
        _loading=true;
        Choices.SelectedIndex=choice.Curved?1:0;ThicknessInput.Value=(decimal)choice.Thickness;
        FollowStorey.IsChecked=choice.Height==0;HeightInput.Value=(decimal)(choice.Height==0?storeyHeight:choice.Height);
        _customHeight=HeightInput.Value.Value;
        AxisInput.SelectedIndex=choice.AxisOffset.HasValue?3:(int)choice.AxisPlacement;
        OffsetInput.Value=(decimal)(choice.AxisOffset??0);MaterialInput.Text=choice.Material;_loading=false;
        Choices.SelectionChanged+=(_,_)=>UpdateDraft();AxisInput.SelectionChanged+=(_,_)=>UpdateDraft();
        FollowStorey.IsCheckedChanged+=(_,_)=>{
            if(_loading)return;
            _loading=true;
            if(FollowStorey.IsChecked==true){_customHeight=HeightInput.Value??_customHeight;HeightInput.Value=(decimal)_storeyHeight;}
            else HeightInput.Value=_customHeight;
            _loading=false;UpdateDraft();
        };
        foreach(var input in new[]{ThicknessInput,HeightInput,OffsetInput}) {
            input.ValueChanged+=(_,_)=>UpdateDraft();
            input.PropertyChanged+=(_,e)=>{if(e.Property==NumericUpDown.TextProperty)UpdateDraft();};
        }
        MaterialInput.TextChanged+=(_,_)=>UpdateDraft();
        Choices.DoubleTapped+=(_,_)=>Confirm();
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}else if(e.Key==Key.Enter){Confirm();e.Handled=true;}};
        SizeChanged+=(_,_)=>root.ColumnDefinitions[0].Width=new GridLength(Width<950?184:216);
        Content=root;UpdateDraft();
        Opened+=(_,_)=>{
            var screen=Screens.ScreenFromWindow(this);if(screen!=null){Width=Math.Min(Width,Math.Max(MinWidth,screen.WorkingArea.Width/screen.Scaling-32));Height=Math.Min(Height,Math.Max(MinHeight,screen.WorkingArea.Height/screen.Scaling-64));}
            Choices.Focus();
        };
    }
    private static Control IconLabel(string icon,string text)
    {
        using var stream=AssetLoader.Open(new Uri("avares://万落建筑模型/Resources/Icons/"+icon+".png"));
        var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
        row.Children.Add(new Image {Source=new Bitmap(stream),Width=18,Height=18});row.Children.Add(new TextBlock {Text=text});return row;
    }
    internal WallDrawingChoice? Draft()
    {
        if(Choices.SelectedIndex<0||AxisInput.SelectedIndex<0)return null;
        bool Number(NumericUpDown field,out double number)=>double.TryParse(field.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out number)&&double.IsFinite(number);
        if(!Number(ThicknessInput,out var thickness))return null;
        var height=0d;var offset=0d;
        if(FollowStorey.IsChecked!=true&&(!Number(HeightInput,out height)||height<10))return null;
        if(AxisInput.SelectedIndex==3&&!Number(OffsetInput,out offset))return null;
        return new(Choices.SelectedIndex==1,thickness,height,
            AxisInput.SelectedIndex==3?WallAxisPlacement.Center:(WallAxisPlacement)AxisInput.SelectedIndex,
            AxisInput.SelectedIndex==3?offset:null,MaterialInput.Text??"");
    }
    private void UpdateDraft()
    {
        if(_loading)return;
        HeightInput.IsEnabled=FollowStorey.IsChecked!=true;OffsetInput.IsEnabled=AxisInput.SelectedIndex==3;
        var choice=Draft();var error=choice?.Validate()??(choice==null?"请填写有效参数；自定义墙高至少 10 mm。":null);
        Draw.IsEnabled=error==null;
        _heading.Text=Choices.SelectedIndex==1?"弧墙":"直墙";
        Status.Text=error??$"当前楼层高度 {_storeyHeight:0.###} mm";
        PlanPreview.SetChoice(error==null?choice:null,_storeyHeight);VolumePreview.SetChoice(error==null?choice:null,_storeyHeight);
    }
    internal void Confirm()
    {
        var choice=Draft();if(choice==null||choice.Validate()!=null){UpdateDraft();return;}Close(choice);
    }
}

internal sealed class WallShapePreview(bool plan) : Control
{
    private WallModel? _wall;
    private List<VolumeFace2D> _faces=new();
    internal int FaceCount=>_faces.Count;
    internal void SetChoice(WallDrawingChoice? choice,double height)
    {
        _wall=null;_faces.Clear();
        if(choice!=null){var model=choice.PreviewModel(height);_wall=model.Walls[0];
            if(!plan)_faces=VolumeRenderer.Project(BuildingVolumeBuilder.Build(model),new VolumeCamera {AzimuthDegrees=-45,ElevationDegrees=25},includeHiddenEdges:false);}
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0F151B")),new Rect(Bounds.Size));
        if(_wall==null)return;
        var curve=CurveGeometry.ForWall(_wall);
        var ring=CurveGeometry.WallBoundary(_wall);
        var points=plan?CurveGeometry.Expand(ring):_faces.SelectMany(f=>f.Points).ToList();
        if(points.Count==0)return;
        var x0=points.Min(p=>p.X);var x1=points.Max(p=>p.X);var y0=points.Min(p=>p.Y);var y1=points.Max(p=>p.Y);
        var scale=Math.Min(Math.Max(1,Bounds.Width-48)/Math.Max(1,x1-x0),Math.Max(1,Bounds.Height-64)/Math.Max(1,y1-y0));
        Point At(PointModel p)=>new(Bounds.Width/2+(p.X-(x0+x1)/2)*scale,Bounds.Height/2-(p.Y-(y0+y1)/2)*scale);
        var outline=new Pen(new SolidColorBrush(Color.Parse("#9FC9E8")),1.4);
        if(plan){
            var geometry=new StreamGeometry();using(var path=geometry.Open()){
                path.BeginFigure(At(ring[0]),true);
                for(var i=0;i<ring.Count;i++){
                    var a=ring[i];var b=ring[(i+1)%ring.Count];var c=new CurveGeometry(a,b,a.Bulge);
                    if(c.IsArc)path.ArcTo(At(b),new Size(c.Radius*scale,c.Radius*scale),0,Math.Abs(c.Sweep)>Math.PI,
                        c.Sweep>0?SweepDirection.CounterClockwise:SweepDirection.Clockwise);
                    else path.LineTo(At(b));
                }path.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#38556E")),outline,geometry);
            var axis=new StreamGeometry();using(var path=axis.Open()){
                path.BeginFigure(At(curve.Start),false);
                if(curve.IsArc)path.ArcTo(At(curve.End),new Size(curve.Radius*scale,curve.Radius*scale),0,Math.Abs(curve.Sweep)>Math.PI,
                    curve.Sweep>0?SweepDirection.CounterClockwise:SweepDirection.Clockwise);
                else path.LineTo(At(curve.End));path.EndFigure(false);
            }
            context.DrawGeometry(null,new Pen(Brushes.LightGreen,1,new DashStyle(new[]{5d,4d},0)),axis);
        }else foreach(var face in _faces.Where(f=>f.Visible&&f.Points.Count>=3)){
            var g=new StreamGeometry();using(var path=g.Open()) {path.BeginFigure(At(face.Points[0]),true);foreach(var p in face.Points.Skip(1))path.LineTo(At(p));path.EndFigure(true);}
            var shade=Math.Clamp(face.Shade,.25,1);
            var brush=new SolidColorBrush(Color.FromRgb((byte)(150*shade),(byte)(185*shade),(byte)(205*shade)));
            context.DrawGeometry(brush,new Pen(brush,.65),g);
        }
    }
}
