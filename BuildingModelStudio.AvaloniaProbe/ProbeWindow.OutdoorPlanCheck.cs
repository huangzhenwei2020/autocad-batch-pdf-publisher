using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunOutdoorPlanCheckAsync()
    {
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        var pointer=new Pointer(617,PointerType.Mouse,true);
        void Input(double x,double y,bool click=true)
        {
            var pixel=_planCanvas.OutdoorScreen(x,y);var root=_planCanvas.TranslatePoint(pixel,this)!.Value;
            if(!click){_planCanvas.RaiseEvent(new PointerEventArgs(PointerMovedEvent,_planCanvas,pointer,this,root,0,
                new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));return;}
            _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,root,0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
            _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,root,0,
                new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        }
        void KeyPress(Key key,KeyModifiers mods=KeyModifiers.None)=>_planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=key,KeyModifiers=mods});
        async Task Capture(string name)
        {
            await _outdoorPreviewTask;await Task.Delay(180);
            var folder=Path.GetFullPath(".artifacts/outdoor-plan-ui");Directory.CreateDirectory(folder);
            var visual=ElementComposition.GetElementVisual(this)!;
            using var shot=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
            shot.Save(Path.Combine(folder,name+"-"+(int)Width+".png"),PngBitmapEncoderOptions.Default);
        }
        var model=new BuildingModelDocument {Name="平面阳台与女儿墙回归"};
        model.Storeys.Add(new(){Id="floor",Name="屋面",Elevation=6000,Height=3000});
        model.Storeys.Add(new(){Id="std",Name="共用屋面",Elevation=9000,Height=3000,TemplateStoreyId="floor"});
        _session=new(model);_selectedId=null;await RefreshModelAsync("平面绘制回归");
        var floor=CadFloorRegistrationContext.Create(Path.Combine(Path.GetTempPath(),"outdoor-plan-qa.json"),model,"floor",new(),1);
        floor.SetSourceDatum(new(),new(10,0));floor.RegionMin=new(-1000,-1000);floor.RegionMax=new(8000,6000);
        floor.RegionPolygon=new(){new(-1000,-1000),new(8000,-1000),new(8000,6000),new(-1000,6000)};
        var capture=new CadFloorPlanCapture {Floor=floor,Probe=new(),Underlay=new(){Primitives=new(){
            new(){Kind="Line",X1=0,Y1=0,X2=4000,Y2=0,SourceLayer="CAD-测试"},
            new(){Kind="Line",X1=4000,Y1=0,X2=4000,Y2=2500,SourceLayer="CAD-测试"}}}};
        _planCanvas.SetUnderlays(new(){["floor"]=new(capture)});_planCanvas.UnderlayVisible=true;_planCanvas.UnderlaySnapEnabled=true;
        SetOrthogonal(false);SetPolar(false,45);
        BeginOutdoorDrawing(true);await _outdoorPreviewTask;await Task.Delay(100);_planCanvas.Fit();
        Check(_workspaces.SelectedIndex==1&&_outdoorPlanTools.IsVisible&&!_outdoorTools.IsVisible,"Plan drawing entry/toolbar missing");
        Check(ReferenceEquals(_planCanvas.OutdoorDraft,_outdoor),"Canvas used independent draft");
        Input(3,2);Check(_outdoor!.Points.Single().X==0&&_outdoor.Points[0].Y==0,"CAD endpoint wasn't snapped by plan pointer");
        Input(3997,3);Input(4002,2497);Input(0,2500);Input(1,1);
        Check(_outdoor.Closed&&_outdoor.Points.Count==4&&_session.Revision==0,"Plan click closure mutated model");
        Check(_planCanvas.OutdoorGeometry?.Slabs.Count==1,"Plan actual slab preview missing");
        await Capture("balcony-plan");
        var draft=_outdoor;_workspaces.SelectedIndex=0;await _outdoorPreviewTask;
        Check(ReferenceEquals(draft,_outdoor)&&_outdoorTools.IsVisible&&!_outdoorPlanTools.IsVisible,"Switch to 3D lost draft");
        Check(_viewport.CurrentScene.Volume.Faces.Any(f=>f.ElementId=="outdoor-preview-slab"),"3D didn't share plan geometry");
        _workspaces.SelectedIndex=1;KeyPress(Key.Z,KeyModifiers.Control);
        Check(!_outdoor.Closed&&_outdoor.Points.Count==4,"Plan draft undo didn't remove close step");
        ExecuteCommand("C");KeyPress(Key.Enter);await _outdoorCommitTask;
        Check(_outdoor==null&&_session.Revision==1&&_session.Model.Slabs.Single().Purpose==SlabPurpose.Balcony,"Plan balcony apply failed");
        Check(Math.Abs(_session.Model.TopElevationOf(_session.Model.Slabs[0])-5900)<.01,"Plan elevation/negative offset lost");
        KeyPress(Key.Escape);Check(_session.Model.Slabs.Count==1,"Esc deleted applied balcony");
        await UndoModelAsync();Check(_session.Model.Slabs.Count==0,"Plan balcony wasn't one undo");await RedoModelAsync();
        _planCanvas.SetUnderlays(new());
        BeginOutdoorDrawing(false,new(Thickness:200,Height:1100,Purpose:WallPurpose.Parapet,BaseOffset:100));
        Input(2,1);Check(_outdoor!.Points[0].X==0&&_outdoor.Points[0].Y==0,"Model slab endpoint wasn't snapped without CAD");
        SetOrthogonal(true);Input(3998,110);Check(_outdoor.Points[1].Y==0,"Plan ortho failed");SetOrthogonal(false);
        var beforeZoom=_outdoor.Points.Select(OutdoorDrawingDraft.Copy).ToArray();
        _planCanvas.ZoomAt(_planCanvas.OutdoorScreen(2000,0),1);
        Check(_outdoor.Points.Count==beforeZoom.Length,"Plan zoom changed draft");
        Input(4000,2500,false);await Capture("parapet-plan");
        Check(_planCanvas.OutdoorGeometry?.Walls.Count==2,"Dynamic plan wall body preview missing");
        Input(4000,2500);_outdoorOffsetInput!.Value=200;await _outdoorPreviewTask;
        KeyPress(Key.Enter);await _outdoorCommitTask;
        Check(_session.Model.Walls.Count==2&&_session.Model.Walls.All(w=>w.Purpose==WallPurpose.Parapet&&Math.Abs(_session.Model.BaseElevationOf(w)-6200)<.01),"Plan parapet parameters lost");
        await UndoModelAsync();Check(_session.Model.Walls.Count==0,"Plan wall path undo wasn't atomic");
        BeginOutdoorDrawing(false,new(Purpose:WallPurpose.Parapet,Height:1100,Curved:true));
        Input(5000,0);Input(7000,0);Input(6000,-600);
        Check(Math.Abs(_outdoor!.Points[0].Bulge)>.01,"Plan three-point arc failed");StopOutdoorDrawing();
        BeginOutdoorDrawing(false,new(Purpose:WallPurpose.Parapet,Height:1100));
        var mode=_properties.Children.OfType<ComboBox>().Single(c=>c.Items.OfType<string>().Contains("逐边取线"));mode.SelectedIndex=2;
        Input(2000,0,false);Check(_planCanvas.OutdoorReference.Count==4,"Plan whole-ring hover missing");
        Input(2000,0);Check(_outdoor!.Closed&&_outdoor.Points.Count==4&&_session.Model.Walls.Count==0,"Plan ring copy applied early");
        var edge=OutdoorReferenceEdges.Build(_session.Model,"floor").Single(r=>r.Key.EndsWith("/outer")).Edges().First();
        var pixel=_planCanvas.OutdoorScreen(edge.Curve.At(.5).X,edge.Curve.At(.5).Y);
        _planCanvas.SetViewState(new[]{edge.Ring.ElementId},Array.Empty<string>());
        Check(PickOutdoorReference(pixel,plan:true)==null,"Plan hidden edge picked");
        _planCanvas.SetViewState(Array.Empty<string>(),new[]{edge.Ring.ElementId});
        Check(PickOutdoorReference(pixel,plan:true)==null,"Plan frozen edge picked");
        _planCanvas.SetViewState(Array.Empty<string>(),Array.Empty<string>());
        KeyPress(Key.Escape);KeyPress(Key.Escape);Check(_outdoor==null&&_session.Model.Slabs.Count==1,"Plan cancel changed model");
        Check(_planCanvas.OutdoorDraft==null&&_planCanvas.OutdoorGeometry==null&&_storeyChooser.IsEnabled,"Plan exit state leaked");
        _storeyChooser.SelectedItem=_storeyChooser.Items.OfType<StoreyItem>().Single(s=>s.Id=="std");BeginOutdoorDrawing(true);
        Check(_outdoor==null,"Plan silently edited standard source");
        _planCanvas.SetUnderlays(new());
        Console.WriteLine("OUTDOOR_PLAN_UI_OK defaultPlan pointerRouting cadSnap actualDraft closedBalcony arc dynamicParameters shared3D atomicUndo ringHover hiddenFrozen cancel standardGuard");
    }
}
