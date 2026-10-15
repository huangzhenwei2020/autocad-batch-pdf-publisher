using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunCurvesCheckAsync()
    {
        void Require(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        var folder=Path.GetFullPath(".artifacts/curves-ui/"+(int)Width+"x"+(int)Height);Directory.CreateDirectory(folder);
        var model=new BuildingModelDocument {Name="弧墙与圆角楼板校对",Storeys=new(){new(){Id="1F",Name="一层",Height=3000}},
            Walls=new(){new(){Id="a",Code="W-1",StoreyId="1F",Y1=3000},new(){Id="b",Code="W-2",StoreyId="1F",X2=3000}},
            Slabs=new(){new(){Id="s",Code="S-1",StoreyId="1F",TopElevation=0,Outline=new(){new(-100,3100),new(-100,-100),new(3100,-100),new(3100,3100)}}}};
        _session=new(model);_filePath=null;await RefreshModelAsync("圆弧校对");_workspaces.SelectedIndex=1;
        SetPlanTool(PlanTool.Select);_planCanvas.GridVisible=false;_planCanvas.SetUnderlays(new());_planCanvas.Fit();
        var pointer=new Pointer(781,PointerType.Mouse,true);
        void Click(PointModel world)
        {
            var p=_planCanvas.ModelToScreen(world);var root=_planCanvas.TranslatePoint(p,this)!.Value;
            _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,root,0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
            _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,root,1,
                new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        }
        void Hover(PointModel world)
        {
            var p=_planCanvas.ModelToScreen(world);
            _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,
                _planCanvas.TranslatePoint(p,this)!.Value,0,new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));
        }
        async Task Snapshot(string name)
        {
            await Task.Delay(250);var visual=ElementComposition.GetElementVisual(this)!;
            using var bitmap=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
            bitmap.Save(Path.Combine(folder,name),PngBitmapEncoderOptions.Default);
        }
        SelectById(null);await BeginFilletAsync(1000);Click(new(0,2000));Hover(new(2000,0));
        await Snapshot("fillet-pick.png");Click(new(2000,0));if(_curveApplyTask!=null)await _curveApplyTask;
        var arc=_session.Model.Walls.Single(w=>w.Bulge!=0);
        Require(_session.Model.Slabs[0].Outline.Any(p=>p.Bulge!=0),"墙圆角没有同步楼板。");
        var applied=BuildingModelJson.ToJson(_session.Model);CancelActiveCommand();
        Require(applied==BuildingModelJson.ToJson(_session.Model),"Esc 撤销了已应用圆角。");
        var curve=CurveGeometry.ForWall(arc);Hover(curve.At(.5));
        Require(_planCanvas.PreselectedElement==arc.Id,"弧墙预选错误。");Click(curve.At(.4));
        Require(_selectedId==arc.Id,"弧墙点选错误。");await Snapshot("arc-selection.png");
        _planCanvas.FilletRadius=500;SelectById("s");await BeginFilletAsync(500);Click(new(3100,3100));
        if(_curveApplyTask!=null)await _curveApplyTask;
        Require(_session.Model.Slabs[0].Outline.Count(p=>p.Bulge!=0)==2,"楼板圆角点选没有提交。");
        CancelActiveCommand();Require(_session.Undo(),"楼板圆角不能撤销。");await RefreshModelAsync("已撤销楼板圆角");
        Require(_session.Model.Slabs[0].Outline.Count(p=>p.Bulge!=0)==1,"圆角撤销范围错误。");
        _planCanvas.SetModel(_session.Model,"1F");SetPlanTool(PlanTool.ArcWall);
        Click(new(3500,0));Click(new(5500,0));Hover(new(4500,1000));await Snapshot("arc-draft.png");Click(new(4500,1000));
        if(_curveApplyTask!=null)await _curveApplyTask;
        Require(_session.Model.Walls.Count(w=>w.Bulge!=0)==2,"三点画弧墙没有提交。");
        var newArc=_session.Model.Walls.Last();var newCurve=CurveGeometry.ForWall(newArc);
        Require(Math.Abs(newCurve.Radius-1000)<.01,"三点弧墙参数错误。");
        SetPlanTool(PlanTool.Select);SelectById(newArc.Id);_planCanvas.FrameSelection();await Snapshot("arc-wall.png");
        // Arc grip drop commits without a dialog or Enter.
        var mid=newCurve.At(.5);var pos=_planCanvas.ModelToScreen(mid);var rootPoint=_planCanvas.TranslatePoint(pos,this)!.Value;
        _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,rootPoint,0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
        var target=new PointModel(mid.X,mid.Y+200);pos=_planCanvas.ModelToScreen(target);rootPoint=_planCanvas.TranslatePoint(pos,this)!.Value;
        _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,rootPoint,0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.Other),KeyModifiers.None));
        _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,rootPoint,1,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        await Task.Delay(350);Require(Math.Abs(_session.Model.Walls.Last().Bulge-newArc.Bulge)>.01,"弧度夹点没有拖放提交。");
        _planCanvas.Fit();SetPlanTool(PlanTool.Slab);
        Click(new(3500,-2500));Click(new(5500,-2500));ExecuteCommand("A");Click(new(5500,-1000));Click(new(5800,-1750));
        Click(new(3500,-1000));ExecuteCommand("C");await Task.Delay(400);
        Require(_session.Model.Slabs.Count==2&&_session.Model.Slabs.Last().Outline.Any(p=>p.Bulge!=0),"楼板圆弧边绘制失败。");
        SetPlanTool(PlanTool.Slab);Click(new(3500,-4000));Click(new(5500,-4000));Click(new(5500,-3000));
        ExecuteCommand("A");Click(new(3500,-4000));Click(new(3500,-3000));await Task.Delay(400);
        Require(_session.Model.Slabs.Count==3,"圆弧闭合楼板没有一次提交。");
        CancelActiveCommand();SelectById(null);_planCanvas.Fit();await Snapshot("rounded-plan.png");
        var plan=OrthographicProjector.Project(_session.Model,new(){Id="curve-plan",Title="圆弧校对",Kind=ViewKind.Plan,Scale=100,StoreyIds=new(){"1F"}});
        _workspaces.SelectedIndex=2;_drawingCanvas.SetView(plan);await Snapshot("rounded-drawing.png");
        Require(plan.Lines.Count(l=>l.Bulge!=0)>=5,"图纸未保留圆弧。");
        var path=Path.Combine(folder,"model.json");BuildingModelJson.SaveModel(path,_session.Model);
        Require(BuildingModelJson.LoadModel(path).Slabs.Any(s=>s.Outline.Any(p=>p.Bulge!=0)),"保存模型丢失圆角。");
        _workspaces.SelectedIndex=0;_viewport.ResetView();await Snapshot("rounded-3d.png");
        _viewport.FrameSelection();await Snapshot("rounded-3d-focused.png");
        Require(_viewport.CurrentScene.TriangleCount>30,"三维未生成弧墙和楼板。");
        var volume=BuildingVolumeBuilder.Build(_session.Model);
        var facetEdges=ModelMeshEdges.Build(volume).Where(e=>e.ElementId==arc.Id
            && Math.Abs(e.Start.X-e.End.X)<.001&&Math.Abs(e.Start.Y-e.End.Y)<.001).ToList();
        Require(facetEdges.Count<=4,"弧墙实体边线暴露了分段网格。");
        Console.WriteLine("CURVE_UI_OK threePointWall fillet linkedSlab standaloneSlab arcContour preselection gripDrop EscPersistence undo nativeArcDrawing solidScene "+folder);
    }
}
