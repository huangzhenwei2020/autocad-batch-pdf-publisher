using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task<bool> RunParameterPerfCheckAsync()
    {
        try
        {
            var original=BuildingModelJson.ToJson(_session.Model);
            var opening=_session.Model.Openings.First(o=>(o.Kind??"").Contains("门") && o.Height>300
                && _session.Model.Walls.Any(w=>w.Id==o.HostWallId));
            var revision=_session.Revision;
            SelectById(opening.Id);
            var field=_properties.GetVisualDescendants().OfType<TextBox>().Single(f=>f.Name=="OpeningThresholdHeight");
            var watch=Stopwatch.StartNew();
            foreach(var text in new[]{"1","12","120"})field.Text=text;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);
            var inputMs=watch.Elapsed.TotalMilliseconds;
            await _parameterPreviewTask;
            var previewMs=watch.Elapsed.TotalMilliseconds;
            if(_parameterPreview?.Openings.Single(o=>o.Id==opening.Id).ThresholdHeight!=120
                || _session.Revision!=revision || BuildingModelJson.ToJson(_session.Model)!=original)
                throw new InvalidOperationException("Threshold preview changed the saved model or used stale input.");
            var scene=_viewport.CurrentScene;
            watch.Restart();
            _properties.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ApplyOpeningGeometry")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await _geometryApplyTask;
            var applyMs=watch.Elapsed.TotalMilliseconds;
            if(!ReferenceEquals(scene,_viewport.CurrentScene))throw new InvalidOperationException("Apply rebuilt the completed preview scene.");
            if(_session.Revision!=revision+1 || !_session.Undo() || BuildingModelJson.ToJson(_session.Model)!=original)
                throw new InvalidOperationException("Threshold apply must be one undo transaction.");
            await RefreshModelAsync("Threshold performance cancel check");SelectById(opening.Id);
            field=_properties.GetVisualDescendants().OfType<TextBox>().Single(f=>f.Name=="OpeningThresholdHeight");
            field.Text="80";
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);
            await _parameterPreviewTask;
            watch.Restart();CancelParameterPreview();var cancelMs=watch.Elapsed.TotalMilliseconds;
            if(_parameterPreview!=null || BuildingModelJson.ToJson(_session.Model)!=original)
                throw new InvalidOperationException("Threshold cancel changed the model.");
            field.Text="60";CancelActiveCommand();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);
            await _parameterPreviewTask;
            if(_parameterPreview!=null)throw new InvalidOperationException("Cancelled queued input restarted a preview.");
            field=_properties.GetVisualDescendants().OfType<TextBox>().Single(f=>f.Name=="OpeningThresholdHeight");
            field.Text="60";field.Text="90";
            _properties.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ApplyOpeningGeometry")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await _geometryApplyTask;
            var edited=_session.Model.Openings.Single(o=>o.Id==opening.Id);
            if(edited.ThresholdHeight!=90 || edited.Offset!=opening.Offset || edited.Width!=opening.Width
                || edited.Height!=opening.Height || edited.Sill!=opening.Sill || !_session.Undo()
                || BuildingModelJson.ToJson(_session.Model)!=original)
                throw new InvalidOperationException("Immediate apply rounded untouched geometry or lost the latest input.");
            await RefreshModelAsync("Threshold performance passed");
            Console.WriteLine($"PARAMETER_PERF_OK model={_session.Model.Name} storeys={_session.Model.Storeys.Count} openings={_session.Model.Openings.Count} faces={scene.Volume.Faces.Count} inputMs={inputMs:F1} previewMs={previewMs:F1} applyMs={applyMs:F1} cancelMs={cancelMs:F1}");
            await RunOpeningMotionPerfCheckAsync();
            return true;
        }
        catch(Exception ex){Console.Error.WriteLine("PARAMETER_PERF_FAILED "+ex);return false;}
    }

    private async Task RunOpeningMotionPerfCheckAsync()
    {
        var original=BuildingModelJson.ToJson(_session.Model);
        var door=_session.Model.Openings.Where(o=>OpeningPlanGeometry.HasSwingDoor(o,OpeningConstruction.Resolve(_session.Model,o))
            && _session.Model.Walls.Any(w=>w.Id==o.HostWallId))
            .OrderByDescending(o=>_session.Model.Walls.Any(w=>w.Id==o.HostWallId
                && _session.Model.Storeys.Any(s=>s.TemplateStoreyId==w.StoreyId))).First();
        var wall=_session.Model.Walls.Single(w=>w.Id==door.HostWallId);
        var length=Math.Sqrt(Math.Pow(wall.X2-wall.X1,2)+Math.Pow(wall.Y2-wall.Y1,2));
        var ux=(wall.X2-wall.X1)/length;var uy=(wall.Y2-wall.Y1)/length;
        _workspaces.SelectedIndex=1;SetPlanTool(PlanTool.Select);
        _storeyChooser.SelectedItem=((IEnumerable<StoreyItem>)_storeyChooser.ItemsSource!).Single(s=>s.Id==wall.StoreyId);
        SelectById(door.Id);_planCanvas.Fit();await Task.Delay(150);
        var pointer=new Pointer(995,PointerType.Mouse,true);
        Point Root(Point p)=>_planCanvas.TranslatePoint(p,this)!.Value;
        void Press(Point p)=>_planCanvas.RaiseEvent(new PointerPressedEventArgs(_planCanvas,pointer,this,Root(p),0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
        void Move(PointModel p)=>_planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,Root(_planCanvas.ModelToScreen(p)),0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.Other),KeyModifiers.None));
        var watch=new Stopwatch();
        var inputTimes=new List<double>();
        foreach(var direction in new[]{false,true})
        {
            Press(_planCanvas.OpeningGripPoint(direction));
            if(!_planCanvas.HasOpeningGrip)throw new InvalidOperationException("Real-model grip could not be selected.");
            for(var i=0;i<120;i++)
            {
                var along=door.Offset+(direction?(i%2==0?-1:1)*door.Width:Math.Sin(i*.1)*20);
                var normal=direction?(i%4<2?-1:1)*door.Width:0;
                var p=WallReferenceGeometry.BodyPoint(wall,wall.X1+ux*along,wall.Y1+uy*along);
                watch.Restart();Move(new PointModel(p.X-uy*normal,p.Y+ux*normal));inputTimes.Add(watch.Elapsed.TotalMilliseconds);
            }
            await Task.Delay(100);_planCanvas.CancelDraft();pointer.Capture(null);
        }
        if(BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("Pointer preview committed model changes.");
        _workspaces.SelectedIndex=0;
        watch.Restart();await FlipSelectedOpeningAsync(true);var flipMs=watch.Elapsed.TotalMilliseconds;
        var flipped=_session.Model.Openings.Single(o=>o.Id==door.Id);
        if(flipped.PlanFlipAlong==door.PlanFlipAlong)throw new InvalidOperationException("Flip did not apply.");
        var beforeMove=_session.Model;
        double? target=null;
        foreach(var offset in new[]{door.Offset+100,door.Offset-100,door.Offset+20,door.Offset-20})
        {
            var trial=new BuildingModelEditSession(beforeMove);
            if(trial.TrySetOpeningPlacement(door.Id,wall.Id,offset,flipped.PlanFlipAlong,flipped.PlanFlipNormal,out _)){target=offset;break;}
        }
        if(target==null)throw new InvalidOperationException("No valid position for real-model movement check.");
        _workspaces.SelectedIndex=1;SelectById(door.Id);
        Press(_planCanvas.OpeningGripPoint(false));
        if(!_planCanvas.HasOpeningGrip)throw new InvalidOperationException("Movement grip did not activate.");
        var end=WallReferenceGeometry.BodyPoint(wall,wall.X1+ux*target.Value,wall.Y1+uy*target.Value);
        Move(new PointModel(end.X-uy*1500,end.Y+ux*1500));
        Move(end);
        watch.Restart();
        _planCanvas.RaiseEvent(new PointerReleasedEventArgs(_planCanvas,pointer,this,Root(_planCanvas.ModelToScreen(end)),0,
            new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        await _openingApplyTask;var moveMs=watch.Elapsed.TotalMilliseconds;
        if(Math.Abs(_session.Model.Openings.Single(o=>o.Id==door.Id).Offset-target.Value)>.001)
            throw new InvalidOperationException($"Grip release did not commit the intended position. expected={target} actual={_session.Model.Openings.Single(o=>o.Id==door.Id).Offset} status={_status.Text}");
        var incremental=_viewport.CurrentScene;
        var full=await Task.Run(()=>BuildingVolumeBuilder.Build(_session.Model));
        string Key(VolumeFace f)=>f.Kind+"|"+f.StoreyId+"|"+f.ElementId+"|"+f.NormalX+"|"+f.NormalY+"|"+f.NormalZ+"|"
            +string.Join(";",f.Points.Select(p=>$"{p.X:R},{p.Y:R},{p.Z:R}"));
        if(!incremental.Volume.Faces.Select(Key).Order().SequenceEqual(full.Faces.Select(Key).Order()))
            throw new InvalidOperationException("Incremental movement differs from full geometry.");
        if(!_session.Undo() || !_session.Undo() || BuildingModelJson.ToJson(_session.Model)!=original)
            throw new InvalidOperationException("Movement/flip did not undo independently.");
        await RefreshModelAsync("Opening motion performance passed");_workspaces.SelectedIndex=0;
        inputTimes.Sort();
        Console.WriteLine($"OPENING_MOTION_PERF_OK sourceFloor={wall.StoreyId} referenceFloors={_session.Model.Storeys.Count(s=>s.TemplateStoreyId==wall.StoreyId)} pointerMoves=240 pointerP95Ms={inputTimes[(int)(inputTimes.Count*.95)]:F2} pointerMaxMs={inputTimes[^1]:F2} flipMs={flipMs:F1} moveMs={moveMs:F1} fullGeometryEquivalent undo cancel");
    }
}
