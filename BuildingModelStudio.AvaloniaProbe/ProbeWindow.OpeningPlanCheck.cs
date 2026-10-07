using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;

namespace BuildingModelStudio.AvaloniaProbe;
internal sealed partial class ProbeWindow
{
    private async Task RunOpeningPlanCheckAsync()
    {
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        var model=new BuildingModelDocument {Name="纯鼠标门窗夹点校对",Storeys=new(){new StoreyModel {Id="1F",Name="一层",Height=3000}},
            Walls=new(){new WallModel {Id="host",StoreyId="1F",X2=6000,Thickness=200}},
            Openings=new(){new OpeningModel {Id="door",HostWallId="host",Code="原编号-M0921A",Kind="门",Offset=1800,Width=900,Height=2100},
                new OpeningModel {Id="same-code",HostWallId="host",Code="原编号-M0921A",Kind="门",Offset=4000,Width=900,Height=2100}}};
        var cells=new[]{new DoorWindowLayoutCell {Right=900,Top=2100,IsDoor=true,Opening="右平开",Material="实板"}};
        model.OpeningTypes.Add(new OpeningTypeModel {Code="原编号-M0921A",Width=900,Height=2100,ElevationType="普通门",DivisionPreset="自定义",
            HasInstallationGap=false,CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells)});
        _session=new BuildingModelEditSession(model);_workspaces.SelectedIndex=1;SetPlanTool(PlanTool.Select);
        await RefreshModelAsync("纯鼠标夹点校对");SelectById("door");_planCanvas.Fit();await Task.Delay(180);
        Check(_openingContextTools.Bounds.Width<=_planCanvas.Bounds.Width-16,"门窗操作条在小窗口被裁切");
        Check(_openingContextTools.Children.OfType<Button>().All(b=>Math.Abs(b.Bounds.Height-32)<.01),"门窗操作按钮高度不一致");
        var pointer=new Pointer(992,PointerType.Mouse,true);Point last=default;
        Point Root(Point p)=>_planCanvas.TranslatePoint(p,this)!.Value;
        void Press(Point p){last=p;_planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,Root(p),0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));}
        void Release(){_planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,Root(last),0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));}
        void Move(PointModel p,bool held=false){last=_planCanvas.ModelToScreen(p);_planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,Root(last),0,
            new PointerPointProperties(held?RawInputModifiers.LeftMouseButton:RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));}
        void Click(Point p){Press(p);Release();}
        void RightClick(bool held=false,InputElement? target=null){
            target??=_planCanvas;
            var modifiers=RawInputModifiers.RightMouseButton|(held?RawInputModifiers.LeftMouseButton:RawInputModifiers.None);
            target.RaiseEvent(new PointerPressedEventArgs(target,pointer,this,Root(last),0,
                new PointerPointProperties(modifiers,PointerUpdateKind.RightButtonPressed),KeyModifiers.None,1));
            _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,Root(last),0,
                new PointerPointProperties(held?RawInputModifiers.LeftMouseButton:RawInputModifiers.None,PointerUpdateKind.RightButtonReleased),KeyModifiers.None,MouseButton.Right));
        }
        var before=BuildingModelJson.ToJson(_session.Model);
        Click(_planCanvas.OpeningGripPoint(false));
        Check(_planCanvas.HasOpeningGrip&&BuildingModelJson.ToJson(_session.Model)==before,"点夹点松开后没有保持预览，或提前提交");
        Move(new PointModel(2200,0));await Task.Delay(80);var builds=_planCanvas.OpeningPreviewBuildCount;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        for(var i=0;i<240;i++)Move(new PointModel(2200+i,0));timer.Stop();await Task.Delay(80);
        Check(_planCanvas.OpeningPreviewBuildCount==builds,"无按键移动时反复重建预览几何");
        Check(BuildingModelJson.ToJson(_session.Model)==before,"松开鼠标移动预览污染模型");
        await SaveOpeningCheck("click-position.png");Click(last);await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&Math.Abs(_session.Model.Openings[0].Offset-2439)<.001,"第二次点击没有提交位置");
        Check(_session.Model.Openings[1].Offset==4000&&_session.Model.Openings[0].Code=="原编号-M0921A","位置修改影响同编号实例或原编号");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"位置移动不支持一次撤销");
        await RefreshModelAsync("方向四象限校对");SelectById("door");
        foreach(var state in new (double x,double y,bool along,bool normal)[]{(1400,900,true,false),(2200,900,false,false),(1400,-900,true,true),(2200,-900,false,true)}) {
            Click(_planCanvas.OpeningGripPoint(true));Check(_planCanvas.HasOpeningGrip,"自由端圆夹点无法激活");
            Move(new PointModel(state.x,state.y));await Task.Delay(70);
            Check(BuildingModelJson.ToJson(_session.Model)==before,"方向预览写入模型");
            _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(180);
            Check(_session.Model.Openings[0].PlanFlipAlong==state.along&&_session.Model.Openings[0].PlanFlipNormal==state.normal,"右铰链基准的四象限方向错误");
            Check(_session.Model.Openings[0].Offset==1800&&_session.Model.Openings[0].Width==900&&_session.Model.Openings[1].PlanFlipAlong==false,"方向改变洞口或同编号实例");
            if(BuildingModelJson.ToJson(_session.Model)!=before)Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"方向操作不能一次撤销");
            await RefreshModelAsync("四象限状态完成");SelectById("door");
        }
        Click(_planCanvas.OpeningGripPoint(true));Move(new PointModel(1400,-900));await Task.Delay(100);
        var stableHandle=_planCanvas.OpeningGripPoint(true);
        var zone=2/_planCanvas.PixelsPerMillimetre;Move(new PointModel(1800+zone,zone));
        var afterZone=_planCanvas.OpeningGripPoint(true);
        Check(Math.Abs(afterZone.X-stableHandle.X)+Math.Abs(afterZone.Y-stableHandle.Y)<.01,"墙中线死区导致方向抖动");
        await SaveOpeningCheck("click-direction.png");
        _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Escape});
        Check(!_planCanvas.HasOpeningGrip&&BuildingModelJson.ToJson(_session.Model)==before,"方向取消仍然写入");
        Click(_planCanvas.OpeningGripPoint(false));Move(new PointModel(100,0));Click(last);await Task.Delay(100);
        Check(_planCanvas.HasOpeningGrip&&BuildingModelJson.ToJson(_session.Model)==before,"越墙端无效预览被提交，或无法继续调整");
        Move(new PointModel(2600,0));_planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Space});await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&Math.Abs(_session.Model.Openings[0].Offset-2600)<.001,"无效位置后不能修正并空格确认 offset="+_session.Model.Openings[0].Offset+" active="+_planCanvas.HasOpeningGrip);
        Check(_session.Undo(),"修正位置不能撤销");await RefreshModelAsync("取消上下文校对");SelectById("door");
        Click(_planCanvas.OpeningGripPoint(false));Move(new PointModel(2300,0));SelectById("same-code");
        Check(!_planCanvas.HasOpeningGrip&&BuildingModelJson.ToJson(_session.Model)==before,"选择其他实例没有取消草稿");
        SelectById("door");Press(_planCanvas.OpeningGripPoint(false));pointer.Capture(null);
        Check(!_planCanvas.HasOpeningGrip,"真正失去捕获没有取消草稿");Release();
        foreach(var point in new[]{new PointModel(2250,500),new PointModel(1613.6,636.4),new PointModel(1900,50),new PointModel(1700,300)}) {
            SelectById(null);Move(point);await Task.Delay(50);
            Check(_planCanvas.PreselectedElement=="door","门扇/开启弧/洞口/开启区域没有参与悬停拾取");
            Check(BuildingModelJson.ToJson(_session.Model)==before,"悬停预选污染模型");
            await SaveOpeningCheck("hover-selection.png");Click(last);Check(_selectedId=="door","预选对象与实际选择对象不一致");
        }
        SelectById(null);Move(new PointModel(1400,850));Check(_planCanvas.PreselectedElement!="door","开启扇用矩形范围误选空白");
        SelectById("door");Press(_planCanvas.OpeningGripPoint(false));Move(new PointModel(2400,0),true);
        last=_planCanvas.ModelToScreen(new PointModel(2600,0));Release();await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&Math.Abs(_session.Model.Openings[0].Offset-2600)<.001,"拖动松手没有直接提交最终位置 offset="+_session.Model.Openings[0].Offset+" active="+_planCanvas.HasOpeningGrip);
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"拖动位置不能一次撤销");await RefreshModelAsync("拖动方向校对");SelectById("door");
        Press(_planCanvas.OpeningGripPoint(true));Move(new PointModel(1400,-900),true);Release();await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].PlanFlipAlong&&_session.Model.Openings[0].PlanFlipNormal,"拖动方向松手没有提交");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"拖动方向不能撤销");await RefreshModelAsync("编号独立夹点校对");SelectById("door");
        var labelPoint=_planCanvas.OpeningLabelGripPoint();Press(labelPoint);
        last=labelPoint+new Vector(35,-25);_planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,Root(last),0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.Other),KeyModifiers.None));
        Check(BuildingModelJson.ToJson(_session.Model)==before,"编号预览提前写入模型");await SaveOpeningCheck("label-grip.png");Release();await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].PlanLabelAlong!=0&&_session.Model.Openings[0].Offset==1800,"编号夹点没有独立移动");
        Check(_session.Model.Openings[1].PlanLabelAlong==0,"编号移动影响同编号实例");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"编号移动不能一次撤销");await RefreshModelAsync("编号取消校对");SelectById("door");
        Click(_planCanvas.OpeningLabelGripPoint());Move(new PointModel(2400,600));_planCanvas.CancelDraft();
        Check(BuildingModelJson.ToJson(_session.Model)==before&&!_planCanvas.HasOpeningGrip,"编号取消仍提交");
        Click(_planCanvas.OpeningGripPoint(false));Move(new PointModel(4700,0));await Task.Delay(100);
        Check(_openingDistanceHost.IsVisible&&_openingDistanceCaption.Text!.Contains("终点"),"没有显示最近终点墙边净距");
        Check(_openingDistanceHost.Bounds.Right<=_planCanvas.Bounds.Width+1&&_openingDistanceHost.Bounds.Bottom<=_planCanvas.Bounds.Height+1,"距离输入框超出小窗口");
        _openingDistanceInput.Text="300";await SaveOpeningCheck("wall-distance-input.png");
        _openingDistanceInput.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].Offset==5250,"输入净距未从终点洞口边换算");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"精确净距不能一次撤销");await RefreshModelAsync("无效净距校对");SelectById("door");
        Click(_planCanvas.OpeningGripPoint(false));_openingDistanceInput.Text="-1";
        _openingDistanceInput.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(50);
        Check(_planCanvas.HasOpeningGrip&&BuildingModelJson.ToJson(_session.Model)==before,"非法净距污染模型或丢失草稿");
        _openingDistanceInput.Text="600";_openingDistanceInput.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].Offset==1050,"无效净距后不能修正");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"修正净距不能撤销");await RefreshModelAsync("新交互校对完成");SelectById("door");
        string? moveHint=null;void ObserveMoveHint(string text)=>moveHint=text;
        _planCanvas.MoveStageChanged+=ObserveMoveHint;
        Click(_planCanvas.OpeningGripPoint(false));_planCanvas.MoveStageChanged-=ObserveMoveHint;
        _openingDistanceInput.Text="-1";await Task.Delay(80);
        Check(_openingCenterHint.Text=="右键：沿墙居中"&&_openingCenterHint.IsVisible&&!_openingCenterHint.IsHitTestVisible,"右键居中提示缺失或仍为浮动按钮");
        Check(moveHint?.Contains("右键：沿墙居中")==true,"移动状态没有提示右键居中");
        await SaveOpeningCheck("center-distance-option.png");
        var anchor=_planCanvas.ModelToScreen(new PointModel(0,0));
        RightClick();await Task.Delay(200);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].Offset==3000&&_session.Model.Openings[1].Offset==4000,"移动居中未直接完成或影响其他实例");
        Check(_planCanvas.ModelToScreen(new PointModel(0,0))==anchor,"右键居中错误触发视图平移");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"移动居中不能一次撤销");await RefreshModelAsync("工具条居中校对");SelectById("door");
        Press(_planCanvas.OpeningGripPoint(false));Move(new PointModel(2300,0),true);RightClick(true);Release();await Task.Delay(200);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].Offset==3000,"按住左键拖动时右键居中被松手位置覆盖");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"拖动右键居中未作为一次操作撤销");
        await RefreshModelAsync("输入框右键居中校对");SelectById("door");Click(_planCanvas.OpeningGripPoint(false));
        _openingDistanceInput.Text="-1";RightClick(target:_openingDistanceInput);await Task.Delay(200);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].Offset==3000,"距离输入框内右键被文本菜单拦截");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"输入框右键居中无法撤销");
        await RefreshModelAsync("工具条居中校对");SelectById("door");
        await CenterSelectedOpeningAsync();
        Check(_session.Model.Openings[0].Offset==3000,"工具条居中没有应用");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before,"工具条居中不能撤销");await RefreshModelAsync("冲突居中校对");SelectById("door");
        _session.Model.Openings[1].Offset=3200;_planCanvas.SetModel(_session.Model,"1F");_planCanvas.SetSelection("door");
        var conflict=BuildingModelJson.ToJson(_session.Model);Press(_planCanvas.OpeningGripPoint(false));Move(new PointModel(2300,0),true);
        RightClick(true);Release();await Task.Delay(80);
        Check(_planCanvas.HasOpeningGrip&&BuildingModelJson.ToJson(_session.Model)==conflict,"冲突居中提交或丢失草稿");
        Check(_openingDistanceInput.Text=="2550","冲突居中仍显示旧的净距输入");
        RightClick();Check(_planCanvas.HasOpeningGrip&&BuildingModelJson.ToJson(_session.Model)==conflict,"再次右键丢失冲突草稿");
        _openingDistanceInput.Text="500";_openingDistanceInput.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].Offset==950,"冲突居中不能用净距修正");
        _session=new BuildingModelEditSession(BuildingModelJson.FromJson(before));await RefreshModelAsync("居中校对完成");SelectById("door");
        anchor=_planCanvas.ModelToScreen(new PointModel(0,0));last=new Point(40,40);
        _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,Root(last),0,
            new PointerPointProperties(RawInputModifiers.RightMouseButton,PointerUpdateKind.RightButtonPressed),KeyModifiers.None,1));
        last+=new Vector(20,15);
        _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,Root(last),0,
            new PointerPointProperties(RawInputModifiers.RightMouseButton,PointerUpdateKind.Other),KeyModifiers.None));
        _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,Root(last),0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.RightButtonReleased),KeyModifiers.None,MouseButton.Right));
        Check(_planCanvas.ModelToScreen(new PointModel(0,0))==anchor+new Vector(20,15)&&BuildingModelJson.ToJson(_session.Model)==before,"非移动状态的右键平移被改变");
        _planCanvas.Fit();Click(_planCanvas.OpeningGripPoint(true));RightClick();
        Check(BuildingModelJson.ToJson(_session.Model)==before,"方向夹点右键误居中");_planCanvas.CancelDraft();
        Click(_planCanvas.OpeningLabelGripPoint());RightClick();
        Check(BuildingModelJson.ToJson(_session.Model)==before,"编号夹点右键误居中");_planCanvas.CancelDraft();
        Console.WriteLine("OPENING_CENTER_OK rightPressRelease hint dragHeld inputRightClick directCommit toolbar noEnter oneStepUndo instanceIsolation collisionDraft distanceRetry idlePan directionLabelIsolation");
        Console.WriteLine("OPENING_EDIT_OK hoverFullSymbol arcSector emptySpace dragRelease labelGrip independentUndo numericBothEnds invalidRetry smallInputBounds");
        var rotated=BuildingModelJson.FromJson(before);var wall=rotated.Walls[0];wall.X2=3600;wall.Y2=4800;
        _session=new BuildingModelEditSession(rotated);await RefreshModelAsync("斜墙夹点校对");SelectById("door");_planCanvas.Fit();
        var rotatedBefore=BuildingModelJson.ToJson(_session.Model);
        Click(_planCanvas.OpeningGripPoint(true));
        var center=new PointModel(1080,1440);Move(new PointModel(center.X-.6*400+.8*900,center.Y-.8*400-.6*900));
        _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(180);
        Check(_session.Model.Openings[0].PlanFlipAlong&&_session.Model.Openings[0].PlanFlipNormal,"斜墙错误按屏幕坐标判断方向");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==rotatedBefore,"斜墙方向不能撤销");
        await RefreshModelAsync("斜墙净距校对");SelectById("door");
        Click(_planCanvas.OpeningGripPoint(false));Move(new PointModel(4700*.6,4700*.8));
        _openingDistanceInput.Text="300";_openingDistanceInput.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&Math.Abs(_session.Model.Openings[0].Offset-5250)<.001,"斜墙净距错误地使用屏幕距离");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==rotatedBefore,"斜墙净距不能撤销");
        await RefreshModelAsync("斜墙居中校对");SelectById("door");Click(_planCanvas.OpeningGripPoint(false));
        RightClick();await Task.Delay(180);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].Offset==3000,"斜墙居中不沿宿主墙计算");
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==rotatedBefore,"斜墙居中不能一次撤销");
        Console.WriteLine($"OPENING_MOUSE_OK clickRelease moveNoButton secondClick enter space escape invalidRetry oneStepUndo instanceIsolation captureLoss rotatedWall rightHinge fourStates cache240Moves={timer.ElapsedMilliseconds}ms");
        _session=new BuildingModelEditSession(BuildingModelJson.FromJson(before));
        _showSlabProperties?.Invoke();
        await RefreshModelAsync("开启角度及编号校对");SelectById("door");await Task.Delay(180);
        T Field<T>(string name) where T:Control=>_properties.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        Check(Field<CheckBox>("OpeningOpenIn3D").IsChecked==false,"新模型三维开启默认未关闭");
        foreach(var index in new[]{3,2,1,0}) {
            Field<ComboBox>("OpeningAnglePresets").SelectedIndex=index;
            var expected=new[]{90,45,30,15}[index];
            Check(Field<TextBox>("OpeningPlanAngle").Text==expected.ToString(),"角度预设没有更新输入框");
            Field<TextBox>("OpeningPlanAngle").RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(300);
            Check(_session.Model.Openings[0].PlanOpenAngle==expected&&_session.Model.Openings[1].PlanOpenAngle==90,"UI角度未保存或影响同编号");
            var grip=OpeningPlanGeometry.DirectionHandle(_session.Model.Openings[0],OpeningConstruction.Resolve(_session.Model,_session.Model.Openings[0]),200)!;
            Check(Math.Abs(grip.Y-(72+796*Math.Sin(expected*Math.PI/180)))<.001,"预设后净扇宽自由端夹点错误");
        }
        Field<TextBox>("OpeningCode").Text="M0921-测试";Field<TextBox>("OpeningPlanAngle").Text="37.5";
        Field<CheckBox>("OpeningOpenIn3D").IsChecked=true;
        Field<TextBox>("OpeningCode").RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(400);
        Check(_session.Model.Openings[0].Code=="M0921-测试"&&_session.Model.Openings[0].PlanOpenAngle==37.5&&_session.Model.Openings[0].OpenIn3D==true,"UI编号或自定义角度/三维开关未保存");
        var presentationJson=BuildingModelJson.ToJson(_session.Model);
        Field<TextBox>("OpeningPlanAngle").Text="NaN";
        Field<TextBox>("OpeningPlanAngle").RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(100);
        Check(BuildingModelJson.ToJson(_session.Model)==presentationJson,"UI非法角度写入模型");
        Field<TextBox>("OpeningPlanAngle").Text="37.5";
        Check(_session.Undo(),"UI编号与开启设置不能撤销");await RefreshModelAsync("一次撤销校对");SelectById("door");
        Check(_session.Model.Openings[0].Code=="原编号-M0921A"&&_session.Model.Openings[0].OpenIn3D==false,"UI撤销未恢复编号/关闭开关");
        Field<ComboBox>("OpeningAnglePresets").SelectedIndex=1;
        Field<TextBox>("OpeningPlanAngle").RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(300);
        foreach(var field in new Control[]{Field<TextBox>("OpeningCode"),Field<TextBox>("OpeningPlanAngle"),Field<ComboBox>("OpeningAnglePresets"),Field<CheckBox>("OpeningOpenIn3D")})
            Check(field.Bounds.Width>60&&field.Bounds.Height>=30,"开启参数控件被裁切 "+field.Name+" "+field.Bounds);
        _planCanvas.Fit();await Task.Delay(180);
        await SaveOpeningCheck("angle-45-properties.png");
        Console.WriteLine("OPENING_PRESENTATION_OK presets15/30/45/90 customAngle codeEdit defaultClosed 3Dswitch instanceIsolation invalidRollback undo controlBounds");
        var casementModel=BuildingModelJson.LoadModel(System.IO.Path.GetFullPath(".artifacts/window-directions/c1824.json"));
        _session=new BuildingModelEditSession(casementModel);await RefreshModelAsync("开启窗方向校对");SelectById("casement");_planCanvas.Fit();await Task.Delay(180);
        var windowBefore=BuildingModelJson.ToJson(_session.Model);
        Check(Field<TextBox>("OpeningPlanAngle").Text=="90"&&!_properties.GetVisualDescendants().OfType<CheckBox>().Any(c=>c.Name=="OpeningOpenIn3D"),"窗平面角度缺失或误加门的三维开关");
        foreach(var angle in new[]{15,30,45,90}) {
            Field<ComboBox>("OpeningAnglePresets").SelectedIndex=Array.IndexOf(new[]{90,45,30,15},angle);
            Field<TextBox>("OpeningPlanAngle").RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Enter});await Task.Delay(220);
            Check(_session.Model.Openings[0].PlanOpenAngle==angle&&_session.Model.OpeningTypes[0].OpenAngle==80,"窗的平面角度未保存或改动三维角度");
            var changedWindow=_session.Model.Openings[0];
            var windowSymbol=OpeningPlanGeometry.Build(changedWindow,OpeningConstruction.Resolve(_session.Model,changedWindow),200);
            Check(windowSymbol.Count(l=>l.OpeningArcId?.StartsWith("swing-")==true)==2*OpeningPlanGeometry.SwingArcSegments,"窗角度应用后丢失两扇开启弧");
            Check(windowSymbol.Count(l=>l.OpeningArcId?.StartsWith("window-leaf-")==true)==2&&windowSymbol.Where(l=>l.OpeningArcId!=null).All(l=>l.LineType=="DASHED"&&l.StrokeAreaId==null),"窗开启部分没有全部使用无厚度虚线");
            if(angle!=90){Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==windowBefore,"窗角度不能一次撤销");await RefreshModelAsync("窗角度预设校对");SelectById("casement");}
        }
        _planCanvas.FrameSelection();SelectById(null);Move(new PointModel(1400,400));
        Check(_planCanvas.PreselectedElement=="casement","窗开启区域不能预选");Click(last);Check(_selectedId=="casement","窗开启区域不能点击选择");
        _planCanvas.Fit();
        Check(_openingContextTools.Children.OfType<Button>().Take(2).All(b=>b.IsEnabled),"平开窗的翻转按钮仍禁用");
        foreach(var name in new[]{"FlipOpeningAlong","FlipOpeningNormal"}) {
            Field<Button>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));await _openingApplyTask;
            Check(name=="FlipOpeningAlong"?_session.Model.Openings[0].PlanFlipAlong:_session.Model.Openings[0].PlanFlipNormal,"窗属性翻转按钮未提交 "+name);
            Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==windowBefore,"窗属性翻转不能一次撤销");
            await RefreshModelAsync("窗翻转按钮校对完成");SelectById("casement");
        }
        Press(_planCanvas.OpeningGripPoint(true));Move(new PointModel(2200,-700),true);
        Check(_planCanvas.HasOpeningGrip&&BuildingModelJson.ToJson(_session.Model)==windowBefore,"窗方向预览提前写入模型");
        Release();await Task.Delay(220);
        Check(!_planCanvas.HasOpeningGrip&&_session.Model.Openings[0].PlanFlipAlong&&_session.Model.Openings[0].PlanFlipNormal,"窗方向夹点拖动松手未提交");
        _planCanvas.FrameSelection();_planCanvas.ZoomAt(new Point(_planCanvas.Bounds.Width/2,_planCanvas.Bounds.Height/2),-2);
        await Task.Delay(100);await SaveOpeningCheck("casement-directions.png");_planCanvas.Fit();
        Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==windowBefore,"窗方向拖动无法一次撤销");
        await RefreshModelAsync("窗方向取消校对");SelectById("casement");Click(_planCanvas.OpeningGripPoint(true));Move(new PointModel(2200,-700));
        _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Escape});
        Check(!_planCanvas.HasOpeningGrip&&BuildingModelJson.ToJson(_session.Model)==windowBefore,"窗方向 Esc 取消写入模型");
        _workspaces.SelectedIndex=0;SelectById("casement");await Task.Delay(150);
        Check(Field<Button>("FlipOpeningNormal").IsEnabled,"三维视图不能调整窗内外方向");
        Field<Button>("FlipOpeningNormal").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));await _openingApplyTask;
        Check(_session.Model.Openings[0].PlanFlipNormal,"三维属性按钮未保存窗方向");
        var expectedWindow=BuildingVolumeBuilder.BuildOpeningParts(_session.Model,"casement");
        string WindowPoints(BuildingVolume volume)=>string.Join(";",volume.Faces.Where(f=>f.ElementId=="casement"&&f.Kind=="glass")
            .SelectMany(f=>f.Points).Select(p=>$"{p.X:R},{p.Y:R},{p.Z:R}"));
        Check(WindowPoints(expectedWindow)==WindowPoints(_viewport.CurrentScene.Volume),"窗方向提交后主场景未刷新真实玻璃位置");
        _viewport.SetDisplayMode(ModelViewport.DisplayMode.Shaded);_viewport.RotateForBenchmark(MathF.PI);FrameActiveSelection();_viewport.SelectElements(Array.Empty<string>());
        var windowFrame=_viewport.RenderedFrameCount;await WaitForFrameAsync(_viewport.CurrentScene.VertexCount,windowFrame);
        await SaveOpeningCheck("casement-3d-direction.png");
        Console.WriteLine("WINDOW_DIRECTION_UI_OK casementTransom toolbar inspector dragRelease escape atomicUndo 3Dcontrols planAngle15-30-45-90 realSwingArcs selectionSector");
        _workspaces.SelectedIndex=1;await Task.Delay(100);
        await RunWallSelectionPreviewCheckAsync();
        await RunOpeningPlacementCheckAsync();
        foreach(var name in new[]{"door","window"}) {
            var atlas=BuildingModelJson.LoadModel(System.IO.Path.GetFullPath($".artifacts/opening-symbols/{name}-atlas.json"));
            var axes=BuildingAxisLayout.Resolve(atlas);foreach(var axis in axes)axis.Hidden=true;atlas.Axes=axes;
            _session=new BuildingModelEditSession(atlas);await RefreshModelAsync("门窗图例校对");_workspaces.SelectedIndex=1;SelectById("o0");
            _planCanvas.Fit();await Task.Delay(300);await SaveOpeningCheck(name+"-atlas.png");
        }
        _workspaces.SelectedIndex=0;_viewport.ResetView();_viewport.SelectElement("w0");_viewport.FrameSelection();await Task.Delay(300);
    }
    private async Task SaveOpeningCheck(string name)
    {
        var visual=ElementComposition.GetElementVisual(this)!;
        var bitmap=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
        System.IO.Directory.CreateDirectory(".artifacts/opening-symbols");
        bitmap.Save(System.IO.Path.GetFullPath($".artifacts/opening-symbols/{(int)Width}x{(int)Height}-"+name),PngBitmapEncoderOptions.Default);
    }
}
