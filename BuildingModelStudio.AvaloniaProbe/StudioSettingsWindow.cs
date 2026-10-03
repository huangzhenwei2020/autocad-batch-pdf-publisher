using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;
internal sealed class StudioInteractionSettings
{
    public bool GridVisible {get;set;}
    public double PickboxSize {get;set;}=8;
    public double CrosshairPercent {get;set;}=10;
    public string CrosshairColor {get;set;}="#B3D8E1";
    private static string PathName=>System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WanLuoArchitectureTools","building-model-settings.json");
    public static StudioInteractionSettings Load() {
        try {var value=JsonSerializer.Deserialize<StudioInteractionSettings>(File.ReadAllText(PathName));
            return value!=null&&value.Valid()?value:new();} catch(IOException){return new();}catch(JsonException){return new();}catch(UnauthorizedAccessException){return new();}
    }
    public bool Valid()=>double.IsFinite(PickboxSize)&&PickboxSize>=2&&PickboxSize<=40&&double.IsFinite(CrosshairPercent)&&CrosshairPercent>=1&&CrosshairPercent<=100&&Color.TryParse(CrosshairColor,out _);
    public void Save(){Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);File.WriteAllText(PathName,JsonSerializer.Serialize(this,new JsonSerializerOptions {WriteIndented=true}));}
}
internal sealed class StudioSettingsWindow:Window
{
    public StudioInteractionSettings Interaction {get;private set;}=new();
    public DrawingScaleSettings Scales {get;private set;}=new();
    public bool ApplyExisting {get;private set;}
    public StudioSettingsWindow(StudioInteractionSettings settings,DrawingScaleSettings? scales)
    {
        Title="系统设置";Width=680;Height=680;MinWidth=540;MinHeight=560;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#151B23"));Foreground=new SolidColorBrush(Color.Parse("#E8F1F4"));
        var root=new Grid {Margin=new Thickness(24),RowDefinitions=new("Auto,*,Auto"),RowSpacing=18};
        root.Children.Add(new TextBlock {Text="系统设置",FontSize=23,FontWeight=FontWeight.Bold});
        var panel=new StackPanel {Spacing=12};
        panel.Children.Add(new TextBlock {Text="平面操作",FontSize=17,FontWeight=FontWeight.Bold});
        var grid=new CheckBox {Content="显示背景栅格 · F7 切换",IsChecked=settings.GridVisible};panel.Children.Add(grid);
        TextBox Field(string title,string value) {
            var row=new Grid {ColumnDefinitions=new("*,170")};row.Children.Add(new TextBlock {Text=title,VerticalAlignment=VerticalAlignment.Center});
            var box=new TextBox {Text=value,Background=new SolidColorBrush(Color.Parse("#202D3B"))};Grid.SetColumn(box,1);row.Children.Add(box);panel.Children.Add(row);return box;
        }
        var pick=Field("拾取框大小 · 屏幕像素（2～40）",settings.PickboxSize.ToString());
        var cross=Field("十字光标长度 · 视口百分比（1～100）",settings.CrosshairPercent.ToString());
        var color=Field("光标颜色 · #RRGGBB",settings.CrosshairColor);
        panel.Children.Add(new TextBlock {Text="出图比例 · 1 : N",FontSize=17,FontWeight=FontWeight.Bold,Margin=new Thickness(0,12,0,0)});
        scales??=new();var plan=Field("平面图",scales.Plan.ToString());var elevation=Field("立面图",scales.Elevation.ToString());
        var section=Field("剖面图",scales.Section.ToString());var axon=Field("轴测图",scales.Axonometric.ToString());var opening=Field("门窗立面",scales.OpeningElevation.ToString());
        var existing=new CheckBox {Content="同时更新已有图纸的比例",IsChecked=false};panel.Children.Add(existing);
        panel.Children.Add(new TextBlock {Text="比例控制出图文字与符号的图上尺寸；滚轮只改变视图缩放，不改变构件尺寸。各张图纸仍可单独设置比例。",TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.Parse("#91AABC"))});
        var scroll=new ScrollViewer {Content=panel};Grid.SetRow(scroll,1);root.Children.Add(scroll);
        var footer=new StackPanel {Spacing=10};var error=new TextBlock {Foreground=Brushes.OrangeRed,TextWrapping=TextWrapping.Wrap};footer.Children.Add(error);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button {Content="取消"};cancel.Click+=(_,_)=>Close(false);actions.Children.Add(cancel);
        var apply=new Button {Content="应用设置"};apply.Click+=(_,_)=>{
            if(!double.TryParse(pick.Text,out var p)||!double.TryParse(cross.Text,out var c)
                ||!int.TryParse(plan.Text,out var a)||!int.TryParse(elevation.Text,out var b)||!int.TryParse(section.Text,out var d)
                ||!int.TryParse(axon.Text,out var e)||!int.TryParse(opening.Text,out var f)) {error.Text="请输入有效的数值。";return;}
            Interaction=new() {GridVisible=grid.IsChecked==true,PickboxSize=p,CrosshairPercent=c,CrosshairColor=color.Text??""};
            if(!Interaction.Valid()||new[] {a,b,d,e,f}.Any(n=>n<1||n>10000)){error.Text="检查拾取框、光标长度、颜色和比例的范围。";return;}
            Scales=new() {Plan=a,Elevation=b,Section=d,Axonometric=e,OpeningElevation=f};ApplyExisting=existing.IsChecked==true;Close(true);
        };actions.Children.Add(apply);footer.Children.Add(actions);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
    }
}
