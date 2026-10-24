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
    private async Task RunOutdoorReferenceCheckAsync()
    {
        var saved=_session;var selected=_selectedId;var json=_savedJson;
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        void KeyPress(Key key,KeyModifiers modifiers=KeyModifiers.None)=>_viewport.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=key,KeyModifiers=modifiers});
        void ReferenceMode(int index)=>_properties.Children.OfType<ComboBox>().Single(c=>c.Items.OfType<string>().Contains("逐边取线")).SelectedIndex=index;
        var pointer=new Pointer(452,PointerType.Mouse,true);
        void Input(Point pixel,bool click)
        {
            var root=_viewport.TranslatePoint(pixel,this)!.Value;
            if(!click){_outdoorInput!.RaiseEvent(new PointerEventArgs(PointerMovedEvent,_outdoorInput,pointer,this,root,0,
                new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));return;}
            _outdoorInput!.RaiseEvent(new PointerPressedEventArgs(_outdoorInput,pointer,this,root,0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
            _outdoorInput.RaiseEvent(new PointerReleasedEventArgs(_outdoorInput,pointer,this,root,0,
                new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,MouseButton.Left));
        }
        Point EdgePixel(OutdoorReferenceEdge edge)=>_viewport.ProjectModelPoint(edge.Curve.At(.5).X,edge.Curve.At(.5).Y,edge.Ring.Elevation)!.Value;
        async Task Capture(string name)
        {
            await _outdoorPreviewTask;await Task.Delay(180);
            var folder=Path.GetFullPath(".artifacts/outdoor-reference-ui");Directory.CreateDirectory(folder);
            var visual=ElementComposition.GetElementVisual(this)!;
            using var bitmap=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
            bitmap.Save(Path.Combine(folder,name+"-"+(int)Width+".png"),PngBitmapEncoderOptions.Default);
        }
        try
        {
            var model=new BuildingModelDocument {Name="三维取边与阳台重绘回归"};
            model.Storeys.Add(new(){Id="floor",Name="一层",Height=3000});
            model.Storeys.Add(new(){Id="upper",Name="遮挡校对",Elevation=4000,Height=3000});
            model.Storeys.Add(new(){Id="std",Name="共用层",Elevation=8000,Height=4000,TemplateStoreyId="floor"});
            model.Slabs.Add(new(){Id="plate",Code="S-1",StoreyId="floor",Thickness=150,TopOffset=3000,
                Outline=new(){new(0,0){Bulge=.15},new(6000,0),new(6000,4000),new(0,4000)},
                Openings=new(){new(){Id="well",Name="板洞",Outline=new(){new(2000,1500),new(3000,1500),new(3000,2500),new(2000,2500)}}}});
            model.Slabs.Add(new(){Id="cover",Code="S-2",StoreyId="upper",Thickness=100,TopOffset=0,
                Outline=new(){new(-20000,-20000),new(20000,-20000),new(20000,20000),new(-20000,20000)}});
            model.Slabs.Add(new(){Id="balcony",Code="YT-99",Purpose=SlabPurpose.Balcony,StoreyId="floor",Thickness=140,TopOffset=-100,FollowsStoreyTop=true,
                Outline=new(){new(12000,0),new(16000,0),new(16000,3000),new(12000,3000)},
                Openings=new(){new(){Id="saved-hole",Name="保留洞口",Outline=new(){new(13000,1000),new(14000,1000),new(14000,2000),new(13000,2000)}}}});
            _session=new(model);_selectedId=null;await RefreshModelAsync("取边回归");
            var hidden=new[]{"cover","plate@STD@std","balcony@STD@std"};
            _viewport.SetViewState(hidden,Array.Empty<string>());_viewport.SelectElement("plate");_viewport.FrameSelection();
            BeginOutdoorDrawing(false,new(Thickness:200,Height:1100,Purpose:WallPurpose.Parapet));await _outdoorPreviewTask;
            ReferenceMode(1);var source=OutdoorReferenceEdges.Build(model,"floor").Single(r=>r.Key=="plate/outer");var edges=source.Edges().ToArray();
            Input(EdgePixel(edges[0]),false);
            Check(_outdoorHoveredEdge?.Ring.Key==source.Key&&_session.Revision==0,"Hover did not identify visible semantic arc");
            Check(_outdoorOverlay!.ReferencePoints.Count==2,"Single edge hover selected whole building");
            Input(EdgePixel(edges[0]),true);await _outdoorPreviewTask;
            Check(_outdoor!.Points.Count==2&&Math.Abs(_outdoor.Points[0].Bulge-.15)<1e-9&&_outdoor.Elevation(_session.Model)==3000,"Reference lost arc or double-added elevation");
            Input(EdgePixel(edges[2]),true);Check(_outdoor.Points.Count==2&&_session.Revision==0,"Unconnected edge bridged gap");
            KeyPress(Key.Z,KeyModifiers.Control);Check(!_outdoor.HasDraft,"Single edge undo failed");
            ReferenceMode(2);Input(EdgePixel(edges[0]),false);Check(_outdoorOverlay.ReferencePoints.Count==4,"Explicit whole ring not highlighted");
            await Capture("whole-ring-hover");Input(EdgePixel(edges[0]),true);await _outdoorPreviewTask;
            Check(_outdoor.Closed&&_outdoor.Points.Count==4&&_session.Model.Walls.Count==0,"Whole ring committed early");
            Check(!_viewport.CurrentScene.Volume.Faces.Any(f=>f.ElementId.StartsWith("outdoor-preview-")&&f.StoreyId=="std"),"New path preview showed hidden floor");
            var original=_outdoor.Points.Select(OutdoorDrawingDraft.Copy).ToList();ReverseOutdoorPath();
            Check(_outdoor.Points[0].X==original[^1].X&&_outdoor.Points[0].Y==original[^1].Y,"Path reverse failed");
            KeyPress(Key.Z,KeyModifiers.Control);Check(_outdoor.Points[0].X==original[0].X,"Reverse undo failed");
            KeyPress(Key.Z,KeyModifiers.Control);Check(!_outdoor.HasDraft,"Whole ring undo was not atomic");
            ReferenceMode(1);
            var pixel=EdgePixel(edges[0]);
            _viewport.SetViewState(Array.Empty<string>(),Array.Empty<string>());
            var point=edges[0].Curve.At(.5);
            Check(!_viewport.IsModelPointVisible(point.X,point.Y,3000,true)&&PickOutdoorReference(pixel)==null,"Occluded lower edge remained selectable");
            _viewport.SetViewState(hidden.Concat(new[]{"plate"}),Array.Empty<string>());
            Check(PickOutdoorReference(pixel)==null,"Hidden source remained selectable");
            _viewport.SetViewState(hidden,new[]{"plate"});Check(PickOutdoorReference(pixel)==null,"Frozen source remained selectable");
            _viewport.SetViewState(hidden,Array.Empty<string>());
            var inside=_viewport.ProjectModelPoint(4500,2500,3000)!.Value;
            Check(PickOutdoorReference(inside)==null,"Interior triangulation edge became a reference");
            ReferenceMode(2);Input(EdgePixel(edges[0]),true);await Capture("parapet-from-ring");
            KeyPress(Key.Enter);await _outdoorCommitTask;
            Check(_session.Model.Walls.Count==4&&_session.Revision==1,"Selected ring did not commit one path");
            KeyPress(Key.Escape);Check(_session.Model.Walls.Count==4,"Esc removed applied path");
            await UndoModelAsync();Check(_session.Model.Walls.Count==0,"Model undo did not remove whole path");
            _viewport.SetViewState(hidden,Array.Empty<string>());
            var slab=_session.Model.Slabs.Single(s=>s.Id=="balcony");SelectById(slab.Id);_viewport.SelectElement(slab.Id);_viewport.FrameSelection();
            var before=BuildingModelJson.ToJson(_session.Model);BeginSlabTool(PlanTool.SlabOutline);await _outdoorPreviewTask;
            Check(_workspaces.SelectedIndex==0&&_outdoor?.OriginalSlab?.Id==slab.Id,"Balcony edit forced plan view");
            Check(_outdoorOverlay.OriginalPoints.Count==4,"Original outline not available as reference");
            ReferenceMode(1);var balconyEdge=OutdoorReferenceEdges.Build(model,"floor").Single(r=>r.Key=="balcony/outer").Edges().First();
            var balconyPixel=EdgePixel(balconyEdge);
            Check(PickOutdoorReference(balconyPixel)?.Ring.ElementId==slab.Id,"Edited slab ghost cannot serve as reference");
            _viewport.SetViewState(hidden.Concat(new[]{slab.Id}),Array.Empty<string>());
            Check(PickOutdoorReference(balconyPixel)==null,"Hidden edited slab remained a reference");
            _viewport.SetViewState(hidden,Array.Empty<string>());ReferenceMode(0);
            ExecuteCommand("12000,0");ExecuteCommand("16500,0");ExecuteCommand("16500,3000");
            KeyPress(Key.Escape);KeyPress(Key.Escape);
            Check(_outdoor==null&&before==BuildingModelJson.ToJson(_session.Model)&&_viewport.CurrentScene.Volume.Faces.Any(f=>f.ElementId==slab.Id),"Cancel failed to restore original slab");
            SelectById(slab.Id);BeginSlabTool(PlanTool.SlabOutline);await _outdoorPreviewTask;
            ExecuteCommand("12000,0");ExecuteCommand("16500,0");ExecuteCommand("16500,3000");ExecuteCommand("12000,3000");ExecuteCommand("C");
            _outdoorOffsetInput!.Value=2800;await _outdoorPreviewTask;
            Check(before==BuildingModelJson.ToJson(_session.Model),"Redraw mutated model before apply");
            Check(!_viewport.CurrentScene.Volume.Faces.Any(f=>f.ElementId==slab.Id)&&_viewport.CurrentScene.Volume.Faces.Any(f=>f.ElementId.StartsWith("outdoor-preview-")),"Preview duplicated old/new slab");
            Check(!_viewport.CurrentScene.Volume.Faces.Any(f=>f.ElementId.StartsWith("outdoor-preview-")&&f.StoreyId=="std"),"Hidden standard floor reappeared in preview");
            _viewport.SetViewState(new[]{"cover","plate@STD@std"},Array.Empty<string>());UpdateOutdoorPreview();await _outdoorPreviewTask;
            Check(_viewport.CurrentScene.Volume.Faces.Any(f=>f.ElementId.StartsWith("outdoor-preview-")&&f.StoreyId=="std"),"Visible standard floor redraw preview absent");
            _viewport.SetViewState(hidden,Array.Empty<string>());UpdateOutdoorPreview();await _outdoorPreviewTask;
            Check(!_viewport.CurrentScene.Volume.Faces.Any(f=>f.ElementId.StartsWith("outdoor-preview-")&&f.StoreyId=="std"),"Visibility toggle failed to hide standard preview");
            await Capture("balcony-redraw");var revision=_session.Revision;KeyPress(Key.Enter);await _outdoorCommitTask;
            var edited=_session.Model.Slabs.Single(s=>s.Id==slab.Id);
            Check(_session.Revision==revision+1&&_session.Model.Slabs.Count==3&&edited.Code=="YT-99"&&edited.Openings.Single().Id=="saved-hole"
                &&edited.Outline.Max(p=>p.X)==16500&&edited.FollowsStoreyTop&&edited.TopOffset==-200,"Redraw lost identity/void/follow-top datum");
            await UndoModelAsync();Check(before==BuildingModelJson.ToJson(_session.Model),"Redraw undo did not restore exact model");
            SelectById("balcony@STD@std");BeginSlabTool(PlanTool.SlabOutline);
            Check(_outdoor==null&&before==BuildingModelJson.ToJson(_session.Model),"Instance edit silently redirected to source floor");
            Console.WriteLine("OUTDOOR_REFERENCE_UI_OK visibleSemanticEdges hiddenFrozenDepth noDiagonals arc adoptElevation gaps singleWholeReverseUndo standardPreview nativeBalconyRedraw identityVoids cancel atomicApply");
        }
        finally
        {
            StopOutdoorDrawing();_viewport.SetViewState(Array.Empty<string>(),Array.Empty<string>());
            _session=saved;_selectedId=selected;_savedJson=json;await RefreshModelAsync("取边回归完成");_viewport.ResetView();
        }
    }
}
