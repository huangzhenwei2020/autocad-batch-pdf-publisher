using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;
internal sealed partial class ProbeWindow
{
    private async Task RunAxisCheckAsync()
    {
        void Require(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        var model=new BuildingModelDocument {Name="轴网设置验证",Storeys=new() {new StoreyModel {Id="1F",Name="一层",Height=3000},new StoreyModel {Id="2F",Name="二层",Elevation=3000,Height=3000}},
            Walls=new() {new WallModel {Id="w1",StoreyId="1F",X1=0,Y1=0,X2=6000,Y2=0,Thickness=200},
                new WallModel {Id="w2",StoreyId="1F",X1=0,Y1=0,X2=0,Y2=4000,Thickness=200},
                new WallModel {Id="w3",StoreyId="1F",X1=0,Y1=4000,X2=6000,Y2=4000,Thickness=200},
                new WallModel {Id="w4",StoreyId="1F",X1=6000,Y1=0,X2=6000,Y2=4000,Thickness=200}},
            Axes=new() {new AxisModel {Id="middle",Vertical=true,Position=3000,AutomaticNumber=true}}};
        _session=new BuildingModelEditSession(model);
        model.CadImport=new CadModelImportState {PendingOpenings=new() {new CadPendingOpening {
            StoreyId="1F",SourceHandle="pending",Code="TLM1525",Kind="门",Width=1500,Height=2500,ReferencePosition=new PointModel(3000,2000)}}};
        _session=new BuildingModelEditSession(model);
        _workspaces.SelectedIndex=1;SetPlanTool(PlanTool.Select);await RefreshModelAsync("待定位门窗测试");
        _planCanvas.Fit();await Task.Delay(150);
        var placementPointer=new Pointer(991,PointerType.Mouse,true);
        void PlacementClick(PointModel position) {
            var p=_planCanvas.ModelToScreen(position);
            _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,placementPointer,this,_planCanvas.TranslatePoint(p,this)!.Value,0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
            _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,placementPointer,this,_planCanvas.TranslatePoint(p,this)!.Value,0,
                new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        }
        var placementOriginal=BuildingModelJson.ToJson(_session.Model);
        PlacementClick(new PointModel(3000,2000));Require(_planCanvas.HasPendingPlacement,"参考门窗不能点选移动。");
        PlacementClick(new PointModel(100,0));Require(!_planCanvas.ConfirmPendingPlacement(),"超墙端洞口错误提交。");
        Require(BuildingModelJson.ToJson(_session.Model)==placementOriginal,"失败预览写入模型。");
        PlacementClick(new PointModel(3000,0));
        await Snapshot(this,"pending-opening-placement-ui.png");
        _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=InputElement.KeyDownEvent,Key=Key.Enter});await Task.Delay(200);
        Require(_session.Model.Openings.Count==1&&_session.Model.CadImport.PendingOpenings.Count==0,"回车放置门窗失败。");
        Require(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==placementOriginal,"放置门窗不能一次撤销。");
        await RefreshModelAsync("放置门窗撤销");
        PlacementClick(new PointModel(3000,2000));
        _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=InputElement.KeyDownEvent,Key=Key.Escape});
        Require(!_planCanvas.HasPendingPlacement&&BuildingModelJson.ToJson(_session.Model)==placementOriginal,"取消定位改变模型。");
        Console.WriteLine("PENDING_OPENING_OK pick move preview enter cancel invalid rollback undo");
        Require(_session.TryPlacePendingOpening("1F","pending","w1",3000,out var gripId,out var gripError),gripError);
        await RefreshModelAsync("门窗夹点验证");_planCanvas.SetSelection(gripId);await Task.Delay(150);
        void GripPress(bool direction) {
            var p=_planCanvas.OpeningGripPoint(direction);
            _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,placementPointer,this,_planCanvas.TranslatePoint(p,this)!.Value,0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
        }
        void GripMove(PointModel position) {
            var p=_planCanvas.ModelToScreen(position);
            _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,placementPointer,this,_planCanvas.TranslatePoint(p,this)!.Value,0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.Other),KeyModifiers.None));
        }
        void GripRelease(PointModel position) {
            var p=_planCanvas.ModelToScreen(position);
            _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,placementPointer,this,_planCanvas.TranslatePoint(p,this)!.Value,0,
                new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        }
        var beforeGrip=BuildingModelJson.ToJson(_session.Model);
        GripPress(false);GripMove(new PointModel(3800,0));await Task.Delay(100);
        var cachedBuilds=_planCanvas.OpeningPreviewBuildCount;
        for(var i=0;i<12;i++){GripMove(new PointModel(3800+i*10,0));await Task.Delay(20);}
        Require(_planCanvas.OpeningPreviewBuildCount==cachedBuilds,"移动预览反复重建门窗几何。");
        Require(BuildingModelJson.ToJson(_session.Model)==beforeGrip,"夹点拖动中修改了模型。");
        await Snapshot(this,"opening-position-grip-ui.png");
        GripRelease(new PointModel(3910,0));await Task.Delay(200);
        Require(Math.Abs(_session.Model.Openings.Single().Offset-3910)<.01,"位置夹点移动未提交。");
        Require(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==beforeGrip,"位置夹点不能一次撤销。");
        await RefreshModelAsync("方向夹点验证");_planCanvas.SetSelection(gripId);
        GripPress(true);GripMove(new PointModel(2500,-900));await Task.Delay(100);
        await Snapshot(this,"opening-direction-grip-ui.png");
        GripRelease(new PointModel(2500,-900));await Task.Delay(200);
        Require(_session.Model.Openings.Single().PlanFlipAlong&&_session.Model.Openings.Single().PlanFlipNormal,"方向夹点镜像未提交。");
        Require(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==beforeGrip,"方向夹点不能一次撤销。");
        await RefreshModelAsync("取消夹点验证");_planCanvas.SetSelection(gripId);
        GripPress(false);GripMove(new PointModel(3500,0));
        _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=InputElement.KeyDownEvent,Key=Key.Escape});
        GripRelease(new PointModel(3500,0));
        Require(BuildingModelJson.ToJson(_session.Model)==beforeGrip,"Esc 取消夹点仍提交了模型。");
        Require(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==placementOriginal,"夹点检查污染了待定位测试模型。");
        await RefreshModelAsync("夹点验证完成");
        Console.WriteLine("OPENING_GRIPS_OK move direction cached-preview release undo escape");
        var axes=BuildingAxisLayout.Resolve(model);Require(axes.Count==5,"基线轴网数错误。");
        var middle=axes.Single(a=>a.Id=="middle");var initialName=middle.Name;
        middle.Hidden=true;Require(_session.TryReplaceAxes(axes,out _),"隐藏未保存。");
        Require(BuildingAxisLayout.Resolve(_session.Model).Single(a=>a.Id=="middle").Name==initialName,"隐藏导致重编号。");
        var plan=SampleModelFactory.CreatePlanView(model.Storeys[0]);
        var view=OrthographicProjector.Project(_session.Model,plan);
        Require(view.Circles.Count(c=>c.Layer==ViewLayers.Axis)==8,"隐藏轴线仍生成轴号。");
        Require(!BuildingVolumeBuilder.Build(_session.Model).GuideLines.Any(l=>l.ElementId=="middle"),"三维隐藏轴线未消失。");
        Require(_session.Undo(),"轴网不能撤销。");
        axes=BuildingAxisLayout.Resolve(_session.Model);middle=axes.Single(a=>a.Id=="middle");middle.Deleted=true;
        foreach(var a in axes)a.AutomaticNumber=true;
        Require(_session.TryReplaceAxes(axes,out _),"删除轴线失败。");
        var resolved=BuildingAxisLayout.Resolve(_session.Model);
        Require(resolved.Single(a=>a.Id=="middle").Deleted,"删除轴线被墙自动恢复。");
        Require(resolved.Single(a=>a.Vertical&&a.Position==6000).Name=="2","删除轴线后自动编号未重排。");
        _session.Undo();axes=BuildingAxisLayout.Resolve(_session.Model);
        var horizontal=axes.Single(a=>!a.Vertical&&a.Position==0);horizontal.StartRemoved=true;
        var extents=BuildingAxisLayout.Extents(_session.Model,horizontal,"1F",4500);
        Require(extents[0]==-600&&extents[1]==10600,"删号未缩到墙外 500 mm。");
        Require(_session.TryReplaceAxes(axes,out _),"单侧删号失败。");
        view=OrthographicProjector.Project(_session.Model,plan);
        Require(view.Circles.Count(c=>c.Layer==ViewLayers.Axis)==9,"单侧删号影响另一侧。");
        var saved=BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model));
        Require(saved.Axes.Single(a=>a.Id==horizontal.Id).StartRemoved,"轴端状态未保存。");
        var scales=new DrawingScaleSettings {Plan=200,Elevation=150,Section=75,Axonometric=200,OpeningElevation=25};
        Require(_session.TrySetDrawingScales(scales,true,out _),"比例设置失败。");
        Require(DrawingViewCatalogue.Resolve(_session.Model).Where(v=>v.Kind!=ViewKind.Schedule).All(v=>v.Scale==scales.For(v.Kind)),"图纸比例未更新。");
        var roundtrip=BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model));
        Require(roundtrip.DrawingScales.Plan==200,"比例默认值未保存。");
        Require(!_session.TrySetDrawingScales(new DrawingScaleSettings {Plan=0},true,out _),"非法比例被接受。");
        Require(_session.Undo()&&_session.Model.DrawingScales==null,"比例不能撤销。");
        _interactionSettings=new() {GridVisible=false,PickboxSize=12,CrosshairPercent=100};ApplyInteractionSettings();
        Require(!_planCanvas.GridVisible&&_planCanvas.PickboxSize==12&&_planCanvas.CrosshairPercent==100,"光标设置未应用。");
        await RefreshModelAsync("轴网和系统设置验证");_workspaces.SelectedIndex=1;_planCanvas.Fit();
        var pointer=new Pointer(0,PointerType.Mouse,true);
        _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,_planCanvas,new Point(500,320),0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));
        await Task.Delay(400);
        async Task Snapshot(Window window,string name){var visual=Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(window)!;
            var bitmap=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);bitmap.Save(System.IO.Path.GetFullPath(".artifacts/opening-editor/"+name),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);}
        await Snapshot(this,"axis-plan-ui.png");
        var dialog=new AxisSettingsWindow(_session.Model);var pending=dialog.ShowDialog<bool>(this);await Task.Delay(350);
        dialog.SetSide("右",false);dialog.Collect();
        Require(dialog.ResultAxes.Where(a=>!a.Vertical).All(a=>a.EndHidden),"右侧批量隐藏未执行。");
        Require(dialog.ResultAxes.Where(a=>a.Vertical).All(a=>!a.EndHidden),"批量隐藏误改上端。");
        await Snapshot(dialog,"axis-editor-ui.png");dialog.Close(false);await pending;
        var settings=new StudioSettingsWindow(_interactionSettings,null);var settingsPending=settings.ShowDialog<bool>(this);await Task.Delay(350);
        await Snapshot(settings,"system-settings-ui.png");settings.Close(false);await settingsPending;
        async Task Click(Point point) {
            var before=BuildingModelJson.ToJson(_session.Model);
            _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(point,this)!.Value,0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
            _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(point,this)!.Value,0,
                new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
            if(_planCanvas.AxisMode!=AxisEditMode.Add) {
                Require(BuildingModelJson.ToJson(_session.Model)==before,"选择阶段修改了模型。");
                _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});
            }
            for(var i=0;i<60&&_axisEditBusy;i++)await Task.Delay(25);
            Require(!_axisEditBusy,"点击编辑未完成。");await Task.Delay(40);
        }
        BeginAxisEditMode(AxisEditMode.LabelVisibility);
        Require(_workspaces.SelectedIndex==1&&_axisEditStrip!.IsVisible,"未进入视图内轴网编辑。");
        await Task.Delay(150);
        _planCanvas.Fit();
        await Click(_planCanvas.AxisEditPoint("middle",false));
        Require(_session.Model.Axes.Single(a=>a.Id=="middle").EndHidden,"直接点击轴号未隐藏。");
        BeginAxisEditMode(AxisEditMode.LabelShow);
        await Click(_planCanvas.AxisEditPoint("middle",false));
        Require(!_session.Model.Axes.Single(a=>a.Id=="middle").EndHidden,"点击灰色轴号未恢复。");
        BeginAxisEditMode(AxisEditMode.LabelDeletion);
        await Click(_planCanvas.AxisEditPoint("middle",true));
        Require(_session.Model.Axes.Single(a=>a.Id=="middle").StartRemoved,"直接删号失败。");
        BeginAxisEditMode(AxisEditMode.LabelRestore);
        await Click(_planCanvas.AxisEditPoint("middle",true));
        Require(!_session.Model.Axes.Single(a=>a.Id=="middle").StartRemoved,"点击删除标记未恢复轴号。");
        BeginAxisEditMode(AxisEditMode.LineVisibility);
        await Click(_planCanvas.AxisEditPoint("middle",true,true));
        Require(_session.Model.Axes.Single(a=>a.Id=="middle").Hidden,"直接点击轴线未隐藏。");
        BeginAxisEditMode(AxisEditMode.LineShow);
        await Click(_planCanvas.AxisEditPoint("middle",true,true));
        Require(!_session.Model.Axes.Single(a=>a.Id=="middle").Hidden,"点击灰色轴线未恢复。");
        BeginAxisEditMode(AxisEditMode.LineDeletion);
        await Click(_planCanvas.AxisEditPoint("middle",true,true));
        Require(_session.Model.Axes.Single(a=>a.Id=="middle").Deleted,"直接点击删轴失败。");
        await Snapshot(this,"dynamic-axis-ui.png");
        BeginAxisEditMode(AxisEditMode.LineRestore);
        await Click(_planCanvas.AxisEditPoint("middle",true,true));
        Require(!_session.Model.Axes.Single(a=>a.Id=="middle").Deleted,"直接点击恢复删轴失败。");
        BeginAxisEditMode(AxisEditMode.LabelVisibility);
        var batchBefore=BuildingModelJson.ToJson(_session.Model);
        var first=_planCanvas.AxisEditPoint("middle",true);var second=_planCanvas.AxisEditPoint("middle",false);
        var radius=450*_planCanvas.PixelsPerMillimetre+8;
        _planCanvas.SelectAxisBox(new Point(first.X-radius,Math.Min(first.Y,second.Y)-radius),
            new Point(first.X+radius,Math.Max(first.Y,second.Y)+radius));
        Require(_planCanvas.AxisSelectionCount==2&&BuildingModelJson.ToJson(_session.Model)==batchBefore,"框选两端或延迟确认错误。");
        _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,_planCanvas.TranslatePoint(second+new Vector(28,5),this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));
        await Snapshot(this,"axis-batch-hide-ui.png");
        _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Escape});
        Require(_planCanvas.AxisSelectionCount==0&&BuildingModelJson.ToJson(_session.Model)==batchBefore,"Esc 未取消批量预览。");
        _planCanvas.SelectAxisBox(new Point(first.X+radius,Math.Max(first.Y,second.Y)+radius),
            new Point(first.X-radius,Math.Min(first.Y,second.Y)-radius));
        _planCanvas.SelectAxisBox(new Point(first.X-radius,first.Y-radius),new Point(first.X+radius,first.Y+radius),true);
        Require(_planCanvas.AxisSelectionCount==1,"Shift 移除或交叉框选失败。");
        _planCanvas.SelectAxisBox(new Point(first.X-radius,first.Y-radius),new Point(first.X+radius,first.Y+radius));
        _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Space});
        for(var i=0;i<60&&_axisEditBusy;i++)await Task.Delay(25);
        Require(_session.Model.Axes.Single(a=>a.Id=="middle").StartHidden&&_session.Model.Axes.Single(a=>a.Id=="middle").EndHidden,"空格未批量隐藏两端。");
        Require(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==batchBefore,"批量操作未作为一次撤销。");
        await RefreshModelAsync("轴网批量测试");
        BeginAxisEditMode(AxisEditMode.LabelDeletion);
        first=_planCanvas.AxisEditPoint("middle",false);
        _planCanvas.SelectAxisBox(first-new Vector(radius,radius),first+new Vector(radius,radius));
        _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,_planCanvas.TranslatePoint(first+new Vector(28,5),this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));
        await Snapshot(this,"axis-batch-delete-ui.png");
        var edge=new Point(_planCanvas.Bounds.Width-2,_planCanvas.Bounds.Height-2);
        _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,_planCanvas.TranslatePoint(edge,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));
        await Snapshot(this,"axis-batch-edge-ui.png");
        Require(_planCanvas.AxisPromptBounds.Left>=0&&_planCanvas.AxisPromptBounds.Top>=0
            &&_planCanvas.AxisPromptBounds.Right<=_planCanvas.Bounds.Width
            &&_planCanvas.AxisPromptBounds.Bottom<=_planCanvas.Bounds.Height,"光标提示超出视口。");
        BeginAxisEditMode(AxisEditMode.LabelVisibility);
        Require(_planCanvas.AxisSelectionCount==0,"模式切换未清空选择。");
        var routedBefore=BuildingModelJson.ToJson(_session.Model);
        first=_planCanvas.AxisEditPoint("middle",false);second=_planCanvas.AxisEditPoint("middle",true);
        var from=new Point(first.X-radius,Math.Min(first.Y,second.Y)-radius);
        var to=new Point(first.X+radius,Math.Max(first.Y,second.Y)+radius);
        _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(from,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
        _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,_planCanvas.TranslatePoint(to,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.Other),KeyModifiers.None));
        await Snapshot(this,"axis-batch-window-ui.png");
        _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(to,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        Require(_planCanvas.AxisSelectionCount==2&&BuildingModelJson.ToJson(_session.Model)==routedBefore,"实际鼠标框选失败或立即写入。");
        _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(first,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.Shift,1));
        _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(first,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.Shift,MouseButton.Left));
        Require(_planCanvas.AxisSelectionCount==1,"实际 Shift 点选移除失败。");
        _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Escape});
        Require(BuildingModelJson.ToJson(_session.Model)==routedBefore,"鼠标框选取消写入模型。");
        _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(from,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
        _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(from,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,_planCanvas.TranslatePoint(to,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));
        Require(_planCanvas.AxisSelectionCount==0,"第一角松开鼠标后提前选择。");
        _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(to,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
        _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,_planCanvas.TranslatePoint(to,this)!.Value,0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        Require(_planCanvas.AxisSelectionCount==2&&BuildingModelJson.ToJson(_session.Model)==routedBefore,"CAD 式两角点击框选失败。");
        _planCanvas.ClearAxisSelection();
        _planCanvas.SelectAxisBox(first-new Vector(1,1),first+new Vector(1,1));
        Require(_planCanvas.AxisSelectionCount==0,"窗口框选错误包含部分轴号。");
        _planCanvas.SelectAxisBox(first+new Vector(1,1),first-new Vector(1,1));
        Require(_planCanvas.AxisSelectionCount==1,"交叉框选未包含相交轴号。");
        BeginAxisEditMode(AxisEditMode.LineVisibility);
        _planCanvas.SelectAxisBox(from,to);
        Require(_planCanvas.AxisSelectionCount==1,"整轴两端未去重。");
        _planCanvas.SetStorey("2F");Require(_planCanvas.AxisSelectionCount==0,"楼层切换未清空未提交选择。");
        _planCanvas.SetStorey("1F");
        _planCanvas.ClearAxisSelection();_planCanvas.ConfirmAxisSelection();
        Require(BuildingModelJson.ToJson(_session.Model)==routedBefore,"空选择仍然提交。");
        // Keep exact-coordinate creation checks outside the ten-pixel snap radius of neighbours.
        if(_planCanvas.PixelsPerMillimetre<.04)
            _planCanvas.ZoomAt(new Point(_planCanvas.Bounds.Width/2,_planCanvas.Bounds.Height/2),
                Math.Log(.04/_planCanvas.PixelsPerMillimetre)/Math.Log(1.15));
        BeginAxisEditMode(AxisEditMode.Add);_planCanvas.AxisAddVertical=true;
        await Click(_planCanvas.AxisEditWorldPoint(1500,2000));
        Require(_session.Model.Axes.Any(a=>a.Vertical&&Math.Abs(a.Position-1500)<.001),"点击添加竖轴失败。");
        var count=_session.Model.Axes.Count;
        await Click(_planCanvas.AxisEditWorldPoint(1500,2000));
        Require(_session.Model.Axes.Count==count,"重复添加了同位置轴线。");
        _planCanvas.AxisAddVertical=false;
        await Click(_planCanvas.AxisEditWorldPoint(4500,1000));
        Require(_session.Model.Axes.Any(a=>!a.Vertical&&Math.Abs(a.Position-1000)<.001),"点击添加横轴失败。");
        Require(_session.Undo()&&!_session.Model.Axes.Any(a=>!a.Vertical&&Math.Abs(a.Position-1000)<.001),"添加轴线不可撤销。");
        Require(_session.Redo()&&_session.Model.Axes.Any(a=>!a.Vertical&&Math.Abs(a.Position-1000)<.001),"添加轴线不可重做。");
        await SetIndependentAxesAsync(true);
        Require(_independentAxes.IsChecked==true,"独立轴网开关不同步。");
        BeginAxisEditMode(AxisEditMode.LabelVisibility);
        await Click(_planCanvas.AxisEditPoint("middle",false));
        Require(BuildingAxisLayout.Resolve(_session.Model,"1F").Single(a=>a.Id=="middle").EndHidden,"独立楼层轴号隐藏失败。");
        Require(!BuildingAxisLayout.Resolve(_session.Model,"2F").Single(a=>a.Id=="middle").EndHidden,"本层操作误改共用楼层。");
        BeginAxisEditMode(AxisEditMode.Add);_planCanvas.AxisAddVertical=true;
        await Click(_planCanvas.AxisEditWorldPoint(2500,2000));
        Require(BuildingAxisLayout.Resolve(_session.Model,"1F").Any(a=>a.Vertical&&Math.Abs(a.Position-2500)<.001),"独立层新增轴线未保存。");
        Require(!BuildingAxisLayout.Resolve(_session.Model,"2F").Any(a=>a.Vertical&&Math.Abs(a.Position-2500)<.001),"独立层新轴污染共用轴网。");
        var localSaved=BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model));
        Require(localSaved.StoreyAxes["1F"].Single(a=>a.Id=="middle").EndHidden,"独立层状态未序列化。");
        var localView=OrthographicProjector.Project(_session.Model,SampleModelFactory.CreatePlanView(_session.Model.Storeys[0]));
        Require(localView.Circles.Count(c=>c.Layer==ViewLayers.Axis)==14,"独立层平面出图未使用本层轴网。");
        await SetIndependentAxesAsync(false);
        Require(!_session.Model.StoreyAxes.ContainsKey("1F")&&!BuildingAxisLayout.Resolve(_session.Model,"1F").Any(a=>a.Vertical&&Math.Abs(a.Position-2500)<.001),"恢复共用轴网失败。");
        Require(_session.Undo()&&_session.Model.StoreyAxes.ContainsKey("1F"),"关闭独立不能撤销。");
        Require(_session.Redo()&&!_session.Model.StoreyAxes.ContainsKey("1F"),"关闭独立不能重做。");
        SetPlanTool(PlanTool.Select);Require(_planCanvas.AxisMode==AxisEditMode.Off&&!_axisEditStrip!.IsVisible,"完成后仍在编辑模式。");
        Require(_session.Model.Walls.Count==4&&_session.Model.Walls.All(w=>w.Thickness==200),"编辑轴网改变了墙体。");
        await RefreshModelAsync("视图内动态轴网验证通过");
        _workspaces.SelectedIndex=0;_viewport.ResetView();SelectById("w1");_viewport.FrameSelection();await Task.Delay(800);
        Console.WriteLine("AXIS_OK batchPoint window crossing shiftRemove deferredCommit space enter escape oneStepUndo lineDedup modeReset floorReset hideCursor deleteCursor hideKeepsNumber deleteRenumbers tombstone singleEndShortening projection volume serialization ghostRestore addVerticalHorizontal duplicateGuard perFloorIsolation");
    }
}
