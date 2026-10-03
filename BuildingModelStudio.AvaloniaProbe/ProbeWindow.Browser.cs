using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private readonly HashSet<string> _hiddenVisualIds=new(),_frozenVisualIds=new();
    private readonly List<Action> _browserStateUpdates=new();
    private readonly Dictionary<string,string> _browserElementFloors=new();
    private string? _isolatedBrowserKey;
    private double _browserWidth=270;
    private Action<double>? _resizeBrowserWidth;
    private BuildingModelDocument? _browserPhysicalSource,_browserPhysicalModel;
    private BuildingModelDocument BrowserPhysicalModel()
    {
        if(!ReferenceEquals(_browserPhysicalSource,_session.Model)){
            _browserPhysicalSource=_session.Model;_browserPhysicalModel=StandardStoreyLayout.Materialize(_session.Model);
        }
        return _browserPhysicalModel!;
    }
    private async Task RunBrowserStateCheckAsync()
    {
        var guideProbe=new BuildingVolume {MinX=0,MinY=0,MinZ=0,MaxX=1000,MaxY=1000,MaxZ=1000,
            GuideLines=new(){
                new VolumeGuideLine {Start=new Point3DModel(0,0,1000),End=new Point3DModel(100,0,1000),IsWallJoint=true},
                new VolumeGuideLine {Start=new Point3DModel(0,0,0),End=new Point3DModel(100,0,0)}}};
        if(ModelViewport.PrepareScene(guideProbe).VertexCount!=2)throw new InvalidOperationException("墙交接辅助线仍进入三维渲染网格。");
        var before=BuildingModelJson.ToJson(_session.Model);
        _resizeBrowserWidth?.Invoke(440);await Task.Delay(300);
        var wide=_browserTree.Bounds.Width;_resizeBrowserWidth?.Invoke(310);await Task.Delay(150);
        if(wide-_browserTree.Bounds.Width<100)throw new InvalidOperationException("浏览器宽度没有随调整变化。");
        _resizeBrowserWidth?.Invoke(440);await Task.Delay(150);
        Point? hit=null;string? id=null;
        for(var x=20d;x<_viewport.Bounds.Width&&id==null;x+=30)for(var y=20d;y<_viewport.Bounds.Height;y+=30){var p=new Point(x,y);var candidate=_viewport.PickAt(p);if(candidate!=null&&_browserElementFloors.ContainsKey(candidate)){hit=p;id=candidate;break;}}
        if(id==null||hit==null)throw new InvalidOperationException("浏览器检查没有可拾取构件。");
        _storeyChooser.SelectedItem=(_storeyChooser.ItemsSource as IEnumerable<StoreyItem>)?.First(s=>s.Id==_browserElementFloors[id]);
        var sourceId=StandardStoreyLayout.SourceElementId(_session.Model,id)!;
        SelectById(id);var target=_viewport.CameraTarget;var distance=_viewport.CameraDistance;
        _viewport.BeginInteraction(new Point(100,100),false,false,true,true);_viewport.MoveInteraction(new Point(110,108),false,true);_viewport.EndInteraction(new Point(110,108));
        if(System.Numerics.Vector3.Distance(target,_viewport.CameraTarget)>.0001f||Math.Abs(distance-_viewport.CameraDistance)>.0001f)throw new InvalidOperationException("旋转重置了中心或距离。");
        _viewport.BeginInteraction(new Point(110,108),false,false,true,true);_viewport.MoveInteraction(new Point(100,100),false,true);_viewport.EndInteraction(new Point(100,100));
        _frozenVisualIds.Add(id);ApplyBrowserViewState();SelectById(id);
        if(_selectedVisualIds.Contains(id)||_viewport.PickAt(hit.Value)==id||!_viewport.IsElementVisible(id)||!_planCanvas.IsElementShown(sourceId)||_planCanvas.IsElementSelectable(sourceId))throw new InvalidOperationException("冻结没有阻止选择或错误隐藏构件。");
        _frozenVisualIds.Remove(id);ApplyBrowserViewState();
        var eye=(Button)((Grid)((Border)_browserNodes[id].Header!).Child!).Children[2];
        void ClickEye(KeyModifiers modifiers)
        {
            // Exercise the routed input path, including already-handled pointer events.
            var pointer=new Pointer(0,PointerType.Mouse,true);
            var pressed=new PointerPressedEventArgs(eye,pointer,this,new Point(1,1),0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),modifiers,1){Handled=true};
            eye.RaiseEvent(pressed);
            eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pointer.Capture(null);
        }
        ClickEye(KeyModifiers.None);
        if(_viewport.IsElementVisible(id)||_planCanvas.IsElementShown(sourceId))throw new InvalidOperationException("隐藏仍有构件网格。");
        ClickEye(KeyModifiers.None);
        if(!_viewport.IsElementVisible(id))throw new InvalidOperationException("显示没有恢复网格。");
        ClickEye(KeyModifiers.Alt);
        if(!_viewport.IsElementVisible(id)||_browserElementFloors.Keys.Any(other=>other!=id&&_viewport.IsElementVisible(other)))throw new InvalidOperationException("Alt 独立显示失败。");
        ClickEye(KeyModifiers.Alt);
        if(_hiddenVisualIds.Count!=0||!_viewport.IsElementVisible(id))throw new InvalidOperationException("Alt 全部显示失败。");
        ClickEye(KeyModifiers.None);
        if(_viewport.IsElementVisible(id)||_hiddenVisualIds.Count!=1)throw new InvalidOperationException("Alt 松开后普通点击仍独立显示。");
        ClickEye(KeyModifiers.None);
        var floorIds=_browserElementFloors.Where(p=>p.Value==_browserElementFloors[id]).Select(p=>p.Key).ToArray();
        ToggleBrowserVisibility(floorIds,"floor-check",false);
        if(floorIds.Any(_viewport.IsElementVisible))throw new InvalidOperationException("楼层隐藏没有覆盖全部构件。");
        ToggleBrowserVisibility(floorIds,"floor-check",false);
        ToggleBrowserVisibility(floorIds,"floor-check",true);
        await Task.Delay(800);
        var isolatedVisual=Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(this)!;
        var isolatedImage=await isolatedVisual.Compositor.CreateCompositionVisualSnapshot(isolatedVisual,1);
        isolatedImage.Save(System.IO.Path.GetFullPath(".artifacts/opening-editor/browser-isolated-floor-ui.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        ToggleBrowserVisibility(floorIds,"floor-check",true);
        if(before!=BuildingModelJson.ToJson(_session.Model))throw new InvalidOperationException("视图开关修改了模型数据。");
        SelectById(null);await Task.Delay(500);
        var visual=Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(this)!;
        var image=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
        image.Save(System.IO.Path.GetFullPath(".artifacts/opening-editor/browser-state-ui.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Console.WriteLine("BROWSER_STATE_OK noWallJointGuides routedEyeClick altIsolate/all altRelease hide/show freeze/pick floor width=440 cameraStable modelUnchanged");
    }
    private static bool IsBrowserControl(object? source)=>source is Visual v &&
        (v is Button || v.GetVisualAncestors().OfType<Button>().Any(b=>Equals(b.Tag,"browser-state")));

    private Control BrowserStateHeader(string title,string icon,IEnumerable<string> elements,string key)
    {
        var ids=elements.Distinct().ToArray();
        var row=new Grid {ColumnDefinitions=new("20,*,26,26"),ColumnSpacing=4,MinHeight=30};
        row.Children.Add(CommandIcon(icon,16));
        var label=new TextBlock {Text=title,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(label,1);row.Children.Add(label);ToolTip.SetTip(label,title);
        Button StateButton(int column){var b=new Button {Tag="browser-state",Width=26,Height=26,Padding=new Thickness(4),Background=Brushes.Transparent,BorderThickness=new Thickness(0)};Grid.SetColumn(b,column);row.Children.Add(b);return b;}
        var eye=StateButton(2);var freeze=StateButton(3);var alt=false;
        // Button handles pointer presses itself; capture modifiers on the tunnel route.
        eye.AddHandler(InputElement.PointerPressedEvent,(_,e)=>alt=e.KeyModifiers.HasFlag(KeyModifiers.Alt),RoutingStrategies.Tunnel,handledEventsToo:true);
        eye.Click+=(_,_)=>{ToggleBrowserVisibility(ids,key,alt);alt=false;};
        freeze.Click+=(_,_)=>{var all=ids.Length>0&&ids.All(_frozenVisualIds.Contains);foreach(var id in ids)if(all)_frozenVisualIds.Remove(id);else _frozenVisualIds.Add(id);ApplyBrowserViewState();};
        void Update(){
            var visible=ids.Count(id=>!_hiddenVisualIds.Contains(id));var frozen=ids.Count(_frozenVisualIds.Contains);
            eye.Content=CommandIcon(visible==0 ? "eye-off" : "eye",17);eye.Opacity=visible>0&&visible<ids.Length ? .55 : 1;
            freeze.Content=CommandIcon(frozen>0 ? "lock-keyhole" : "lock-keyhole-open",15);freeze.Opacity=frozen>0&&frozen<ids.Length ? .55 : frozen==0 ? .55 : 1;
            ToolTip.SetTip(eye,visible==0 ? "显示 · Alt 单击独立显示 / 再次 Alt 单击全部显示" : "隐藏 · Alt 单击独立显示 / 再次 Alt 单击全部显示");
            ToolTip.SetTip(freeze,frozen==ids.Length&&ids.Length>0 ? "解除冻结" : "冻结：保持显示，禁止选择与编辑");
        }
        _browserStateUpdates.Add(Update);Update();return row;
    }
    private void ToggleBrowserVisibility(IEnumerable<string> elements,string key,bool isolate)
    {
        var ids=elements.ToHashSet();
        if(isolate){
            if(_isolatedBrowserKey==key){_hiddenVisualIds.Clear();_isolatedBrowserKey=null;}
            else {_hiddenVisualIds.Clear();_hiddenVisualIds.UnionWith(_browserElementFloors.Keys.Where(id=>!ids.Contains(id)));_isolatedBrowserKey=key;}
        }else{
            _isolatedBrowserKey=null;var show=ids.All(_hiddenVisualIds.Contains);
            foreach(var id in ids)if(show)_hiddenVisualIds.Remove(id);else _hiddenVisualIds.Add(id);
        }
        ApplyBrowserViewState();
    }
    private void ApplyBrowserViewState()
    {
        _viewport.SetViewState(_hiddenVisualIds,_frozenVisualIds);
        var floor=(_storeyChooser.SelectedItem as StoreyItem)?.Id;
        string[] InPlan(HashSet<string> state)=>state.Where(id=>_browserElementFloors.GetValueOrDefault(id)==floor)
            .Select(id=>StandardStoreyLayout.SourceElementId(_session.Model,id)).Where(id=>id!=null).Cast<string>().ToArray();
        _planCanvas.SetViewState(InPlan(_hiddenVisualIds),InPlan(_frozenVisualIds));
        if(_selectedVisualIds.Any(id=>_hiddenVisualIds.Contains(id)||_frozenVisualIds.Contains(id))){
            var retained=_selectedVisualIds.Where(id=>!_hiddenVisualIds.Contains(id)&&!_frozenVisualIds.Contains(id)).ToArray();
            SelectById(null);foreach(var id in retained)SelectById(id,true);
        }
        foreach(var update in _browserStateUpdates)update();
        _status.Text="显示与冻结已更新 · Alt 单击眼睛可独立显示，再次单击全部显示";
    }
}
