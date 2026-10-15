using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private string? _underlayPath;
    private long _underlayStamp = -1;
    private bool _underlayLoading;
    private Button? _underlaySettings;
    private readonly CheckBox _underlayShow = new() { Content="CAD 底图", IsChecked=true, VerticalAlignment=VerticalAlignment.Center };
    private readonly CheckBox _underlayCapture = new() { Content="捕捉", IsChecked=true, VerticalAlignment=VerticalAlignment.Center };
    private StackPanel BuildUnderlayControls()
    {
        var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
        row.Children.Add(_underlayShow);row.Children.Add(_underlayCapture);
        _underlayShow.IsCheckedChanged+=(_,_)=>{_planCanvas.UnderlayVisible=_underlayShow.IsChecked==true;_planCanvas.InvalidateVisual();};
        _underlayCapture.IsCheckedChanged+=(_,_)=>{_planCanvas.UnderlaySnapEnabled=_underlayCapture.IsChecked==true;_planCanvas.InvalidateVisual();};
        _underlaySettings=new Button {Content=CommandIcon("layers",16),Width=32,Height=32,IsEnabled=false};
        ToolTip.SetTip(_underlaySettings,"参照底图图层与淡化");
        _underlaySettings.Click+=async (_,_)=>await OpenUnderlaySettingsAsync();row.Children.Add(_underlaySettings);
        var refresh=new Button {Content=CommandIcon("rotate-ccw",16),Width=32,Height=32};
        ToolTip.SetTip(refresh,"重新读取已登记的参照底图");
        refresh.Click+=async(_,_)=>await ReloadUnderlaysAsync(true);row.Children.Add(refresh);
        return row;
    }
    private async Task ReloadUnderlaysAsync(bool force=false)
    {
        var path=_filePath;
        if(path==null)
        {
            _planCanvas.SetUnderlays(new());
            _underlayPath=null;_underlayStamp=-1;
            _planCanvas.HiddenUnderlayLayers.Clear();
            if(_underlaySettings!=null)_underlaySettings.IsEnabled=false;
            return;
        }
        if(_underlayLoading)return;
        var file=CadFloorPlanRegistry.FilePath(path);
        var stamp=File.Exists(file)?File.GetLastWriteTimeUtc(file).Ticks:0;
        if(!force&&path==_underlayPath&&stamp==_underlayStamp)return;
        if(path!=_underlayPath)
        {
            _planCanvas.SetUnderlays(new());
            _planCanvas.HiddenUnderlayLayers.Clear();
            if(_underlaySettings!=null)_underlaySettings.IsEnabled=false;
        }
        _underlayLoading=true;
        try
        {
            var indices=await Task.Run(()=> {
                if(!File.Exists(file))return new Dictionary<string,CadUnderlayIndex>(StringComparer.OrdinalIgnoreCase);
                if(new FileInfo(file).Length>128*1024*1024)throw new InvalidDataException("登记底图文件过大，请缩小各层登记范围。");
                var registry=CadFloorPlanRegistry.Load(path);
                return registry.Floors.Where(c=>c.Underlay!=null).ToDictionary(c=>c.Floor.Storey.Id,c=>new CadUnderlayIndex(c),StringComparer.OrdinalIgnoreCase);
            });
            if(path!=_filePath)return;
            if(path!=_underlayPath)_planCanvas.HiddenUnderlayLayers.Clear();
            _planCanvas.SetUnderlays(indices);_underlayPath=path;_underlayStamp=stamp;
            if(_underlaySettings!=null)_underlaySettings.IsEnabled=indices.Count>0;
            var count=indices.Values.Sum(i=>i.Primitives.Count);
            var tip=indices.Count==0?"尚无参照底图，请在 CAD 楼层登记中更新参照底图。":$"已登记 {indices.Count} 层 · 矢量图元 {count}";
            ToolTip.SetTip(_underlayShow,tip);
            if(force)_status.Text=tip;
        }
        catch(Exception ex)
        {
            if(path==_filePath){_underlayPath=path;_underlayStamp=stamp;_status.Text="参照底图读取失败："+ex.Message;ToolTip.SetTip(_underlayShow,_status.Text);}
        }
        finally{_underlayLoading=false;}
    }
    private async Task OpenUnderlaySettingsAsync()
    {
        await CreateUnderlaySettingsWindow().ShowDialog(this);
    }
    private Window CreateUnderlaySettingsWindow()
    {
        var data=_planCanvas.Underlay;
        var window=new Window {Title="参照底图",Width=420,Height=540,MinWidth=320,MinHeight=350,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        window.Background=new SolidColorBrush(Color.Parse("#151B23"));
        window.Foreground=new SolidColorBrush(Color.Parse("#E8F1F4"));
        window.FontFamily=new FontFamily("Microsoft YaHei UI");window.FontSize=13;
        var root=new Grid {RowDefinitions=new RowDefinitions("Auto,Auto,*,Auto"),Margin=new Thickness(16)};
        root.Children.Add(new TextBlock {Text=$"当前楼层 · 图元 {data?.Primitives.Count??0}",Margin=new Thickness(0,0,0,12)});
        var opacity=new StackPanel {Spacing=6,Margin=new Thickness(0,0,0,12)};
        opacity.Children.Add(new TextBlock {Text="淡化程度"});
        var slider=new Slider {Minimum=0,Maximum=95,Value=(1-_planCanvas.UnderlayOpacity)*100};
        slider.ValueChanged+=(_,_)=>{_planCanvas.UnderlayOpacity=1-slider.Value/100;_planCanvas.InvalidateVisual();};opacity.Children.Add(slider);
        Grid.SetRow(opacity,1);root.Children.Add(opacity);
        var layers=new StackPanel {Spacing=5};
        foreach(var layer in (data?.Primitives.Select(p=>p.SourceLayer??"0")??Array.Empty<string>()).Concat(data?.Texts.Select(t=>t.Layer??"0")??Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l=>l))
        {
            var check=new CheckBox {Content=layer,IsChecked=!_planCanvas.HiddenUnderlayLayers.Contains(layer)};
            check.IsCheckedChanged+=(_,_)=>{if(check.IsChecked==true)_planCanvas.HiddenUnderlayLayers.Remove(layer);else _planCanvas.HiddenUnderlayLayers.Add(layer);_planCanvas.InvalidateVisual();};layers.Children.Add(check);
        }
        var scroll=new ScrollViewer {Content=layers,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};Grid.SetRow(scroll,2);root.Children.Add(scroll);
        var close=new Button {Content="关闭",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        close.Click+=(_,_)=>window.Close();Grid.SetRow(close,3);root.Children.Add(close);window.Content=root;
        return window;
    }
}
