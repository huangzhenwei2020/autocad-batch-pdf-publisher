using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunWallDrawingCheckAsync()
    {
        void Require(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        var saved=_session;var settings=_wallDrawingChoice;var selected=_selectedId;
        var folder=Path.GetFullPath(".artifacts/wall-drawing-ui/"+(int)Width+"x"+(int)Height);Directory.CreateDirectory(folder);
        async Task Snapshot(Visual target,string name){await Task.Delay(150);var v=ElementComposition.GetElementVisual(target)!;
            using var bitmap=await v.Compositor.CreateCompositionVisualSnapshot(v,1);bitmap.Save(Path.Combine(folder,name),PngBitmapEncoderOptions.Default);}
        var pointer=new Pointer(815,PointerType.Mouse,true);
        void Click(PointModel point){var screen=_planCanvas.ModelToScreen(point);var p=_planCanvas.TranslatePoint(screen,this)!.Value;
            _planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,p,0,new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
            _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,p,1,new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));}
        async Task<WallDrawingWindow> Open(string command){ExecuteCommand(command);await Task.Delay(100);
            var w=_wallDrawingWindow??throw new InvalidOperationException(command+" 未打开墙体窗口。");
            w.Width=Width<1000?800:1060;w.Height=Width<1000?560:720;await Task.Delay(100);return w;}
        try {
            var model=SampleModelFactory.CreateEmptyModel("墙体选型绘制");model.Storeys[0].Height=3700;
            model.Walls.Add(new(){Id="base",StoreyId="1F",X2=8000,Y1=-4000,Y2=-4000});
            _session=new(model);_wallDrawingChoice=new();_planCanvas.SetWallDrawing(_wallDrawingChoice);
            await RefreshModelAsync("墙体窗口校对");_workspaces.SelectedIndex=1;SetPlanTool(PlanTool.Select);_planCanvas.GridVisible=false;_planCanvas.Fit();
            var before=BuildingModelJson.ToJson(_session.Model);
            var w=await Open("WA");Require(w.Choices.ItemCount==2&&w.Choices.SelectedIndex==0,"墙体类型或默认选择错误。");
            Require(w.VolumePreview.FaceCount>0&&w.PlanPreview.Bounds.Width>100,"墙体预览空白或挤没。");
            Require(RibbonButtonVisual("墙体")==("wall-plan","WA")&&!_planToolButtons.ContainsKey(PlanTool.ArcWall),"Ribbon 未合并墙体入口。");
            var tabs=w.GetVisualDescendants().OfType<TabControl>().Single();tabs.SelectedIndex=1;await Task.Delay(100);
            Require(w.VolumePreview.Bounds.Width>100&&w.VolumePreview.Bounds.Height>100,"三维预览尺寸不正确。");
            w.ThicknessInput.Value=240;w.FollowStorey.IsChecked=false;w.HeightInput.Value=2800;w.AxisInput.SelectedIndex=1;w.MaterialInput.Text="混凝土";
            Require(w.Draft()?.Thickness==240&&w.Draw.IsEnabled,"插入前参数无效。");
            w.ThicknessInput.Value=null;Require(!w.Draw.IsEnabled&&w.PlanPreview.FaceCount==0,"空数值仍可绘制。");w.ThicknessInput.Value=240;
            w.ThicknessInput.Text="abc";Require(!w.Draw.IsEnabled,"非法输入仍可使用旧数值绘制。");w.ThicknessInput.Text="240";
            w.HeightInput.Text="0";Require(!w.Draw.IsEnabled,"未勾选随层却使用零墙高。");w.HeightInput.Text="2800";
            await Snapshot(w,"straight-window.png");w.Confirm();if(_wallDrawingTask!=null)await _wallDrawingTask;
            Require(before==BuildingModelJson.ToJson(_session.Model),"打开窗口或确认参数提前创建墙体。");
            Require(_planCanvas.Tool==PlanTool.Wall,"选型后没有进入直墙绘制。");
            Click(new(0,0));Click(new(3000,0));if(_curveApplyTask!=null)await _curveApplyTask;
            var wall=_session.Model.Walls.Last();Require(wall.Thickness==240&&wall.Height==2800&&wall.AxisPlacement==WallAxisPlacement.LeftFace&&wall.Material=="混凝土","绘制没有采用窗口参数。");
            Require(WallReferenceGeometry.BodyOffset(wall)==-120,"左墙面定位错误。");
            var applied=BuildingModelJson.ToJson(_session.Model);CancelActiveCommand();Require(applied==BuildingModelJson.ToJson(_session.Model),"Esc 撤销了已经画好的墙。");
            w=await Open("WA");Require(w.ThicknessInput.Value==240&&w.HeightInput.Value==2800,"同会话参数未记住。");
            w.ThicknessInput.Value=400;w.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Escape});if(_wallDrawingTask!=null)await _wallDrawingTask;
            Require(_wallDrawingChoice.Thickness==240&&applied==BuildingModelJson.ToJson(_session.Model),"取消窗口修改了参数或模型。");
            w=await Open("AW");Require(w.Choices.SelectedIndex==1,"AW 未预选弧墙。");
            w.ThicknessInput.Value=10000;Require(w.Draw.IsEnabled&&w.VolumePreview.FaceCount>0,"大墙厚预览异常。");w.ThicknessInput.Value=240;
            w.FollowStorey.IsChecked=true;w.AxisInput.SelectedIndex=3;w.OffsetInput.Value=75;
            Require(w.HeightInput.Value==3700&&!w.HeightInput.IsEnabled,"随楼层高度没有显示实际层高。");
            tabs=w.GetVisualDescendants().OfType<TabControl>().Single();tabs.SelectedIndex=0;await Snapshot(w,"arc-window.png");
            var buttonBottom=w.Draw.TranslatePoint(new Point(0,w.Draw.Bounds.Height),w)!.Value;
            Require(buttonBottom.Y<=w.Height&&w.Draw.Bounds.Width>=120&&w.ThicknessInput.Bounds.Width>=150,"绘制按钮或参数在小窗口被截断。");
            w.Confirm();if(_wallDrawingTask!=null)await _wallDrawingTask;
            Click(new(4000,0));Click(new(6000,0));Click(new(5000,1000));if(_curveApplyTask!=null)await _curveApplyTask;
            wall=_session.Model.Walls.Last();Require(wall.Bulge!=0&&wall.Thickness==240&&wall.Height==0&&wall.AxisOffset==75,"弧墙未使用同一套参数。");
            Require(_session.Model.HeightOf(wall)==3700,"随楼层高度未生效。");CancelActiveCommand();
            Require(_session.Undo()&&_session.Model.Walls.Count==2&&_session.Redo(),"新墙撤销／重做错误。");
            var loaded=BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model));Require(loaded.Walls.Last().AxisOffset==75,"保存丢失墙参数。");
            _planCanvas.Fit();SelectById(null);await Snapshot(this,"drawn-walls.png");
            Require(new WallDrawingChoice(Thickness:double.NaN).Validate()!=null&&new WallDrawingChoice(AxisOffset:double.PositiveInfinity).Validate()!=null,"非法墙参数未拒绝。");
            Console.WriteLine("WALL_DRAWING_UI_OK singleRibbon WA AW realThumbnails plan3D thickness height material faceDatum customOffset followStorey remember cancel noPrematureModel EscPersistence undo persistence responsive "+folder);
        }finally {
            _wallDrawingWindow?.Close();if(_wallDrawingTask!=null)await _wallDrawingTask;
            _session=saved;_wallDrawingChoice=settings;_planCanvas.SetWallDrawing(settings);_selectedId=selected;
            await RefreshModelAsync("墙体窗口校对完成");SetPlanTool(PlanTool.Select);
        }
    }
}
