using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunOutdoorDrawingCheckAsync()
    {
        var saved=_session;var selected=_selectedId;var savedJson=_savedJson;
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        void PressKey(Key key,KeyModifiers modifiers=KeyModifiers.None,Control? target=null)=>(target??_viewport).RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=key,KeyModifiers=modifiers});
        var pointer=new Pointer(341,PointerType.Mouse,true);
        void Click(double x,double y)
        {
            var pixel=_viewport.ProjectModelPoint(x,y,_outdoor!.Elevation(_session.Model))!.Value;
            var root=_viewport.TranslatePoint(pixel,this)!.Value;
            _outdoorInput!.RaiseEvent(new PointerPressedEventArgs(_outdoorInput,pointer,this,root,0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
            _outdoorInput.RaiseEvent(new PointerReleasedEventArgs(_outdoorInput,pointer,this,root,0,
                new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        }
        async Task Capture(string name)
        {
            await _outdoorPreviewTask;await Task.Delay(200);
            var folder=Path.Combine(Environment.CurrentDirectory,".artifacts","outdoor-drawing");Directory.CreateDirectory(folder);
            var visual=ElementComposition.GetElementVisual(this)!;
            var shot=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
            shot.Save(Path.Combine(folder,name+"-"+(int)Bounds.Width+".png"),PngBitmapEncoderOptions.Default);
        }
        try
        {
            var model=new BuildingModelDocument {Name="三维室外构件回归"};
            model.Storeys.Add(new(){Id="outdoor",Name="屋面",Elevation=3000,Height=3000});
            _session=new BuildingModelEditSession(model);_selectedId=null;await RefreshModelAsync("室外构件回归");
            BeginOutdoorDrawing(false,new(Thickness:200,Height:1100,Purpose:WallPurpose.Parapet,BaseOffset:100));
            await _outdoorPreviewTask;Check(_workspaces.SelectedIndex==0,"Drawing forced plan workspace");
            var p=_viewport.ProjectModelPoint(1200,600,3100)!.Value;
            var projected=_viewport.TryScreenToPlan(p,3100,out var world);
            Check(projected&&Math.Abs(world.X-1200)<1&&Math.Abs(world.Y-600)<1,$"Ray/plane roundtrip failed {projected} ({world.X},{world.Y}) pixel={p} distance={_viewport.CameraDistance} target={_viewport.CameraTarget} bounds={_viewport.CurrentScene.Volume.Diagonal} faces={_viewport.CurrentScene.Volume.Faces.Count} valid={_outdoorParametersValid} status={_status.Text}");
            Click(0,0);ExecuteCommand("4000<0");ExecuteCommand("3000<90");
            await _outdoorPreviewTask;
            Check(_outdoor!.Points.Count==3&&_session.Revision==0,"Viewport clicks committed early");
            Check(_viewport.CurrentScene.Volume.Faces.Count>0,"No real mesh draft");
            PressKey(Key.Z,KeyModifiers.Control);Check(_outdoor.Points.Count==2&&_session.Revision==0,"Draft undo fell through");
            var field=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children).OfType<NumericUpDown>().First();
            field.Text="250";await _outdoorPreviewTask;
            Check(_outdoor.Elevation(_session.Model)==3250&&_session.Revision==0,"Dynamic datum preview changed model");
            var textField=field.GetVisualDescendants().OfType<TextBox>().First();
            PressKey(Key.Z,KeyModifiers.Control,textField);Check(_session.Revision==0&&_outdoor.Points.Count==2,"Text undo changed draft");
            PressKey(Key.Enter,target:textField);Check(_outdoor!=null&&_session.Revision==0,"Numeric Enter completed path");
            for(var i=0;i<30;i++){_outdoorCursor=new(4100+i,3000);UpdateOutdoorPreview();}
            await _outdoorPreviewTask;Check(!_outdoorPreviewRunning&&_outdoorPendingPreview==null,"Preview worker did not drain latest request");_outdoorCursor=null;
            Check(!await SaveModelAsync(false)&&_outdoor!.HasDraft,"Unfinished draft was reported saved");
            ExecuteCommand("3000<90");await Capture("parapet-draft");
            PressKey(Key.Enter);await _outdoorCommitTask;
            Check(_outdoor==null&&_session.Model.Walls.Count==2&&_session.Revision==1,"Whole path not committed once");
            PressKey(Key.Escape);Check(_session.Model.Walls.Count==2,"Esc erased applied parapet");
            await UndoModelAsync();Check(_session.Model.Walls.Count==0,"Path undo incomplete");await RedoModelAsync();
            var choiceWindow=new WallDrawingWindow(new(Purpose:WallPurpose.Parapet,Height:1100),3000);
            choiceWindow.Show(this);await Task.Delay(100);Check(choiceWindow.Draft()?.Purpose==WallPurpose.Parapet&&!choiceWindow.FollowStorey.IsEnabled,"Wall selector purpose broken");choiceWindow.Close();
            BeginOutdoorDrawing(true);await _outdoorPreviewTask;
            ExecuteCommand("0,-3000");ExecuteCommand("4000,-3000");ExecuteCommand("4000,-1000");ExecuteCommand("0,-1000");
            PressKey(Key.Enter);await _outdoorCommitTask;Check(_session.Model.Slabs.Count==0&&_outdoor!=null,"Unclosed balcony committed");
            ExecuteCommand("C");await Capture("balcony-draft");
            PressKey(Key.Enter);await _outdoorCommitTask;
            Check(_session.Model.Slabs.Count==1&&_session.Model.Slabs[0].Purpose==SlabPurpose.Balcony,"Balcony missing semantic data");
            BeginOutdoorDrawing(true);await _outdoorPreviewTask;
            ExecuteCommand("0,-3000");ExecuteCommand("4000,-3000");ExecuteCommand("4000,-1000");ExecuteCommand("0,-1000");ExecuteCommand("C");
            await FinishOutdoorDrawingAsync();Check(_session.Model.Slabs.Count==1&&_outdoorConflict!=null,"Duplicate slab not blocked");
            UpdateOutdoorPreview();Check(_outdoorConflict!=null,"Hover cleared overlap resolution");
            _outdoorConflictButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(_outdoor==null&&_selectedId==_session.Model.Slabs.Single().Id,"Select existing slab action failed");
            BeginOutdoorDrawing(true);ExecuteCommand("0,0");ExecuteCommand("1000,0");
            PressKey(Key.Escape);Check(_outdoor!=null&&!_outdoor.HasDraft,"First Esc didn't clear draft");
            PressKey(Key.Escape);Check(_outdoor==null&&_session.Model.Slabs.Count==1,"Second Esc changed model");
            BeginOutdoorDrawing(false,new(Purpose:WallPurpose.Parapet,Height:1100,Curved:true));await _outdoorPreviewTask;
            ExecuteCommand("6000,0");ExecuteCommand("9000,0");ExecuteCommand("7500,-1000");await _outdoorPreviewTask;
            Check(_outdoor!.Points.Count==2&&Math.Abs(_outdoor.Points[0].Bulge)>.01,"3D arc missing");
            PressKey(Key.Enter);await _outdoorCommitTask;Check(_session.Model.Walls.Any(w=>Math.Abs(w.Bulge)>.01),"Arc wasn't saved");
            var roundtrip=BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model));
            Check(roundtrip.Walls.All(w=>w.Purpose==WallPurpose.Parapet)&&roundtrip.Slabs.Single().Purpose==SlabPurpose.Balcony,"JSON lost purpose");
            await Capture("completed");
            Check(!_viewport.TryScreenToPlan(new Point(-1e9,-1e9),3000,out _),"Extreme plane intersection accepted");
            Console.WriteLine("OUTDOOR_UI_OK rayPlane realGpuDraft viewportClick dynamicDatum arc closedBalcony overlap Escape textUndo atomicUndo JSON");
        }
        finally
        {
            StopOutdoorDrawing();_session=saved;_selectedId=selected;_savedJson=savedJson;
            await RefreshModelAsync("室外构件回归结束");_viewport.ResetView();
        }
    }
}
