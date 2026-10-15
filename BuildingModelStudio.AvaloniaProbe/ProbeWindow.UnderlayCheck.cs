using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunUnderlayCheckAsync()
    {
        void Require(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        var folder=Path.GetFullPath(".artifacts/cad-underlay-ui/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"model.json");
        var model=SampleModelFactory.CreateTwoStoreyHouse();model.Storeys.Add(new StoreyModel {Id="3F",Name="标准层引用",TemplateStoreyId="1F",Elevation=7200,Height=3600});
        var floor=CadFloorRegistrationContext.Create(path,model,"1F",new PointModel(),1);
        floor.SetSourceDatum(new PointModel(),new PointModel(10,0));floor.RegionMin=new(0,0);floor.RegionMax=new(6000,4000);
        floor.RegionPolygon=new(){new(0,0),new(6000,0),new(6000,4000),new(0,4000)};
        ComponentPlanPrimitive Line(double x1,double y1,double x2,double y2)=>new(){Kind="Line",SourceLayer="CAD-普通线",X1=x1,Y1=y1,X2=x2,Y2=y2};
        var capture=new CadFloorPlanCapture {Floor=floor,Probe=new() {DrawingFingerprint="qa"},Underlay=new() {Primitives=new(){
            Line(1000,1200,4000,2200),Line(1000,2200,4000,1200),
            Line(3500,2800,4200,2800),Line(4200,2800,4200,3200),Line(4200,3200,3500,3200),Line(3500,3200,3500,2800),
            new() {Kind="Circle",SourceLayer="CAD-曲线",X1=4500,Y1=2000,Radius=300,SweepDegrees=360},
            new() {Kind="Arc",SourceLayer="CAD-曲线",X1=1300,Y1=3000,Radius=250,StartDegrees=0,SweepDegrees=90}},
            Texts=new(){new() {Text="CAD reference",Layer="CAD-文字",X=3200,Y=3000,Height=120}}}};
        BuildingModelJson.SaveModel(path,model);var before=File.ReadAllBytes(path);
        var registry=new CadFloorPlanRegistry {ModelPath=path};registry.SaveFloor(capture);
        _session=new BuildingModelEditSession(model);_filePath=path;RefreshStoreys();await ReloadUnderlaysAsync(true);
        _workspaces.SelectedIndex=1;await Task.Delay(200);_planCanvas.SetStorey("1F");_planCanvas.Fit();
        Require(_planCanvas.Underlay?.Primitives.Count==8,"底图未加载普通线与曲线。");
        var index=_planCanvas.Underlay;
        var snap=_planCanvas.SnapUnderlay(2501,1701,10,new SnapResult {X=2501,Y=1701});
        Require(snap.Kind=="CAD · 交点"&&snap.X==2500&&snap.Y==1700,"底图交点未接入捕捉。");
        var discrete=new SnapResult {X=2502,Y=1701,Kind=PlanEditing.SnapEndpoint};
        Require(ReferenceEquals(discrete,_planCanvas.SnapUnderlay(2501,1701,10,discrete)),"模型端点失去优先级。");
        _underlayCapture.IsChecked=false;Require(!_planCanvas.SnapUnderlay(2501,1701,10,new SnapResult()).Snapped,"关闭底图捕捉无效。");
        _underlayCapture.IsChecked=true;
        _underlayShow.IsChecked=false;Require(!_planCanvas.SnapUnderlay(2501,1701,10,new SnapResult()).Snapped,"隐藏底图仍捕捉。");
        _underlayShow.IsChecked=true;
        _planCanvas.HiddenUnderlayLayers.Add("CAD-普通线");Require(!_planCanvas.SnapUnderlay(2501,1701,10,new SnapResult()).Snapped,"隐藏图层仍捕捉。");
        _planCanvas.HiddenUnderlayLayers.Clear();
        _planCanvas.SetStorey("3F");Require(ReferenceEquals(index,_planCanvas.Underlay),"标准层未复用同一底图索引。");
        _planCanvas.SetStorey("2F");Require(_planCanvas.Underlay==null,"独立楼层误显示其他底图。");
        _planCanvas.SetStorey("1F");
        var pointer=new Pointer(107,PointerType.Mouse,true);var p=_planCanvas.AxisEditWorldPoint(2500,1700);
        var selected="unpicked";void Pick(string? id){selected=id??"none";}
        _planCanvas.ElementPicked+=Pick;
        _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(p,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
        _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(p,this)!.Value,1,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        _planCanvas.ElementPicked-=Pick;Require(selected=="none","参照线成为可编辑对象。");
        async Task Snapshot(Window window,string name)
        {
            await Task.Delay(250);var visual=ElementComposition.GetElementVisual(window)!;
            using var bitmap=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
            bitmap.Save(Path.Combine(folder,name),PngBitmapEncoderOptions.Default);
        }
        await Snapshot(this,"plan.png");
        var settings=CreateUnderlaySettingsWindow();var pending=settings.ShowDialog(this);await Task.Delay(150);
        var checks=settings.GetVisualDescendants().OfType<CheckBox>().ToArray();Require(checks.Length==3,"图层列表缺失。");
        var layer=checks.Single(c=>c.Content?.ToString()=="CAD-普通线");layer.IsChecked=false;
        Require(_planCanvas.HiddenUnderlayLayers.Contains("CAD-普通线"),"图层窗口未生效。");layer.IsChecked=true;
        var slider=settings.GetVisualDescendants().OfType<Slider>().Single();slider.Value=30;
        Require(Math.Abs(_planCanvas.UnderlayOpacity-.7)<.00001,"淡化滑块无效。");
        await Snapshot(settings,"layers.png");settings.Close();await pending;
        var stamp=File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(path)).Ticks;var draft=registry.Clone();
        draft.Floors[0].Underlay!.Primitives.Add(Line(800,2500,2800,2500));registry.SaveUnderlays(draft,stamp);
        await ReloadUnderlaysAsync();Require(_planCanvas.Underlay?.Primitives.Count==9,"CAD 更新底图未刷新。");
        Require(File.ReadAllBytes(path).SequenceEqual(before),"参照操作修改了模型文件。");
        Require(_session.Model.Walls.Count==model.Walls.Count&&_session.Model.Openings.Count==model.Openings.Count,"参照操作修改了模型构件。");
        _filePath=Path.Combine(folder,"legacy.json");await ReloadUnderlaysAsync(true);
        Require(_planCanvas.Underlay==null,"切换旧项目残留底图。");
        _filePath=null;_planCanvas.SetUnderlays(new());_underlaySettings!.IsEnabled=false;
        _workspaces.SelectedIndex=0;
        Console.WriteLine("CAD_UNDERLAY_UI_OK nativeGeometry layers opacity showHide snapToggle modelPriority standardStorey isolation readonly selectionExcluded liveReload legacy unchangedModel "+folder);
    }
}
