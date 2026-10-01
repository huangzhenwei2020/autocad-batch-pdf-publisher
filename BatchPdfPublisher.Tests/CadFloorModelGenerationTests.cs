using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

internal static class CadFloorModelGenerationTests
{
    private static void Check(bool success,string message) { if(!success)throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch(InvalidDataException) { return; } throw new Exception("Invalid generation accepted."); }
    public static void Run()
    {
        var folder=Path.GetFullPath(".artifacts/cad-floor-workflow-20261001/generation-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"model.json");
        var model=new BuildingModelDocument { Name="登记生成核验", Storeys=new List<StoreyModel> {
            new StoreyModel { Id="1F",Name="1F",Height=3000 },new StoreyModel { Id="2F",Name="2F",Elevation=3000,Height=2800,TemplateStoreyId="1F" } } };
        BuildingModelJson.SaveModel(path,model);
        var floor=CadFloorRegistrationContext.Create(path,model,"1F",new PointModel(0,0),1);
        floor.SetSourceDatum(new PointModel(100,200),new PointModel(110,200));
        floor.RegionMin=new PointModel(0,0); floor.RegionMax=new PointModel(7000,1000);
        floor.RegionPolygon=new List<PointModel> { new PointModel(0,0),new PointModel(7000,0),new PointModel(7000,1000),new PointModel(0,1000) };
        var wall=new CadBuildingProbeEntity { Handle="W1",DxfName="TCH_WALL",StraightHorizontalLineVerified=true,
            CurveStart=new CadProbePoint { X=100,Y=200 },CurveEnd=new CadProbePoint { X=6100,Y=200 },
            CandidateThickness=200,CandidateAxisOffset=0,
            Fields=new List<CadProbeField> { new CadProbeField { Name="Height",Number=3000 },new CadProbeField { Name="Elevation",Number=0 } } };
        floor.WallCandidates.Add(wall);
        var source=new CadBuildingProbeEntity { Handle="M1",DxfName="TCH_OPENING",DisplaySegments=new List<CadProbeSegment> {
            Segment(1100,100,1100,300),Segment(1900,100,1900,300) } };
        var location=CadOpeningJambPlacement.Find(floor,source,800);
        Check(location!=null && location.DistanceFromWallStart==1400,"Own jambs must locate the opening exactly on the host.");
        Check(CadOpeningJambPlacement.Find(floor,source,900)==null,"Wrong jamb width must not locate an opening.");
        source.DisplaySegments=new List<CadProbeSegment> { Segment(1100,200,1100,300),Segment(1900,200,1900,300) };
        Check(CadOpeningJambPlacement.Find(floor,source,800)==null,"Partial door leaf lines must not be accepted as jambs.");
        source.DisplaySegments=new List<CadProbeSegment> { Segment(1100,100,1900,100) };
        Check(CadOpeningJambPlacement.Find(floor,source,800)?.DistanceFromWallStart==1400,"A full-width frame edge inside the unique wall body must supply exact placement.");
        source.DisplaySegments=new List<CadProbeSegment> { Segment(1100,600,1900,600) };
        Check(CadOpeningJambPlacement.Find(floor,source,800)==null,"A parallel line outside the wall body must not locate an opening.");
        source.DisplaySegments=new List<CadProbeSegment> { Segment(1100,100,1900,100),Segment(2100,100,2900,100) };
        Check(CadOpeningJambPlacement.Find(floor,source,800)==null,"Two different positions supplied by the same opening must be rejected as ambiguous.");
        source.DisplaySegments=new List<CadProbeSegment> { Segment(1100,100,1100,300),Segment(1900,100,1900,300) };
        var other=new CadBuildingProbeEntity { Handle="W2",DxfName="TCH_WALL",StraightHorizontalLineVerified=true,CurveStart=wall.CurveStart,CurveEnd=wall.CurveEnd,CandidateThickness=200,CandidateAxisOffset=0 };
        floor.WallCandidates.Add(other);
        Check(CadOpeningJambPlacement.Find(floor,source,800)==null,"Ambiguous host must require confirmation."); floor.WallCandidates.Remove(other);
        wall.StraightHorizontalLineVerified=false;
        Check(CadOpeningJambPlacement.Find(floor,source,800)==null,"Unverified curved walls must not receive inferred placement."); wall.StraightHorizontalLineVerified=true;
        source.DisplaySegments.Clear();source.BoundsMin=new CadProbePoint { X=1100,Y=100 };source.BoundsMax=new CadProbePoint { X=1900,Y=850,Z=3000 };
        Check(CadOpeningJambPlacement.Find(floor,source,800)?.DistanceFromWallStart==1400,"Native envelope spanning exactly the native width must recover openings without exploded lines.");
        source.BoundsMax.X=2100;
        Check(CadOpeningJambPlacement.Find(floor,source,800)==null,"Annotation-enlarged envelope must not supply an opening position.");source.BoundsMax.X=1900;
        floor.WallCandidates.Add(other);
        Check(CadOpeningJambPlacement.Find(floor,source,800)==null,"Envelope intersecting two hosts must be rejected.");floor.WallCandidates.Remove(other);
        source.BoundsMin.Z=20;
        Check(CadOpeningJambPlacement.Find(floor,source,800)==null,"A different drawing plane must not be accepted.");source.BoundsMin.Z=0;
        wall.CurveEnd=new CadProbePoint { X=6100,Y=300 };
        Check(CadOpeningJambPlacement.Find(floor,source,800)==null,"A rotated envelope cannot supply exact opening endpoints.");wall.CurveEnd=new CadProbePoint { X=6100,Y=200 };
        source.BoundsMin=null;source.BoundsMax=null;
        wall.CurveEnd=new CadProbePoint { X=100,Y=6200 };
        source.DisplaySegments=new List<CadProbeSegment> { Segment(100,1245,100,1300) };
        source.DisplayArcs=new List<CadProbeArc> { new CadProbeArc { Center=new CadProbePoint { X=100,Y=1272.5 },
            Start=new CadProbePoint { X=-971.647,Y=1245 },End=new CadProbePoint { X=100,Y=200 },Radius=1072.5,Sweep=1.54515249 } };
        Check(Math.Abs(CadOpeningJambPlacement.Find(floor,source,1100).DistanceFromWallStart-550)<.001,"Single open leaf must recover its full native aperture, including measured half leaf thickness.");
        Check(CadOpeningJambPlacement.Find(floor,source,1072.5)==null,"Leaf radius must never replace the native door opening width.");
        source.DisplaySegments=new List<CadProbeSegment> { Segment(100,1200,100,1237.5),Segment(100,2662.5,100,2700) };
        source.DisplayArcs=new List<CadProbeArc> { new CadProbeArc { Center=new CadProbePoint { X=100,Y=1218.75 },
            Start=new CadProbePoint { X=100,Y=1950 },End=new CadProbePoint { X=831.01,Y=1237.5 },Radius=731.25,Sweep=1.54515249 },
            new CadProbeArc { Center=new CadProbePoint { X=100,Y=2681.25 },Start=new CadProbePoint { X=831.01,Y=2662.5 },
                End=new CadProbePoint { X=100,Y=1950 },Radius=731.25,Sweep=1.54515249 } };
        Check(Math.Abs(CadOpeningJambPlacement.Find(floor,source,1500).DistanceFromWallStart-1750)<.001,"Two leaves sharing the closed meeting point recover the entire double door aperture.");
        source.DisplayArcs[1].End.Y=2000;
        Check(CadOpeningJambPlacement.Find(floor,source,1500)==null,"Unrelated leaf arcs must not be combined.");source.DisplayArcs.Clear();
        wall.DxfName="TCH_CURTAIN_WALL";wall.CurveEnd=new CadProbePoint { X=6100,Y=200 };
        wall.BoundsMin=new CadProbePoint { X=100,Y=100 };wall.BoundsMax=new CadProbePoint { X=6100,Y=300,Z=3000 };
        CadBuildingProbeRules.DeriveCurtainWall(wall,1);
        source.DisplaySegments=new List<CadProbeSegment> { Segment(1100,100,1900,100) };
        Check(Math.Abs(wall.CandidateThickness.Value-200)<.001 && CadBuildingProbeRules.IsWall(wall.DxfName)
            && CadOpeningJambPlacement.Find(floor,source,800)!=null,"Native curtain body extent and actual datum must serve as an opening host.");
        wall.BoundsMax.X+=100;CadBuildingProbeRules.DeriveCurtainWall(wall,1);
        Check(wall.CandidateThickness==null,"Curtain extent that disagrees with its datum must be rejected.");
        wall.DxfName="TCH_WALL";wall.CandidateThickness=200;wall.CandidateAxisOffset=0;
        // A source drawing rotated relative to the model, with metre source units.
        floor.Alignment.MillimetresPerCadUnit=1000;
        floor.SetSourceDatum(new PointModel(100,200),new PointModel(100,201));
        wall.CurveEnd=new CadProbePoint { X=100,Y=206 }; wall.CandidateThickness=.2;
        source.DisplaySegments=new List<CadProbeSegment> { Segment(99.9,201,100.1,201),Segment(99.9,201.8,100.1,201.8) };
        var rotated=CadOpeningJambPlacement.Find(floor,source,800);
        Check(rotated!=null && Math.Abs(rotated.DistanceFromWallStart-1400)<.001 && Math.Abs(rotated.ModelCenter.Y)<.001,"Rotation and source units must preserve exact location.");
        floor.Alignment.MillimetresPerCadUnit=1; floor.SetSourceDatum(new PointModel(100,200),new PointModel(110,200));
        wall.CurveEnd=new CadProbePoint { X=6100,Y=200 }; wall.CandidateThickness=200;
        var item=new CadFloorOpeningItem { SourceHandle="M1",Code="M01",Kind="门",Width=800,Height=2200,Sill=0,Placement=location };
        var capture=new CadFloorPlanCapture { Floor=floor,Probe=new CadBuildingProbeDocument { DrawingFingerprint="drawing-1" },Openings=new List<CadFloorOpeningItem> { item } };
        var registry=new CadFloorPlanRegistry { ModelPath=path,Floors=new List<CadFloorPlanCapture> { capture } };
        var generated=CadFloorModelGeneration.Build(model,registry,"first");
        Check(model.Walls.Count==0 && generated.Walls.Count==1 && generated.Openings.Count==1,"Build must be atomic and not mutate the original.");
        Check(generated.Walls[0].X1==0 && generated.Walls[0].X2==6000 && generated.Openings[0].Offset==1400,"Imported geometry must use model coordinates.");
        Check(generated.Walls[0].Height==0 && generated.HeightOf(generated.Walls[0])==3000,
            "Imported walls must follow model storeys instead of storing the native CAD height.");
        var tallModel=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));
        tallModel.Storeys=StoreyElevationLayout.Resolve(new[] {
            new StoreyModel { Id="1F",Name="一层",Height=5900 },
            new StoreyModel { Id="2F",Name="二层",Height=4500,TemplateStoreyId="1F" },
            new StoreyModel { Id="3F",Name="三层",Height=3600,TemplateStoreyId="1F" } },"1F",0);
        wall.Fields.First(f=>f.Name=="Height").Number=4500;
        var tallGenerated=CadFloorModelGeneration.Build(tallModel,registry);
        var tallVolume=BuildingVolumeBuilder.Build(tallGenerated);
        foreach(var storey in tallGenerated.Storeys)
            Check(Math.Abs(tallVolume.Faces.Where(f=>f.Kind=="wall" && f.StoreyId==storey.Id)
                .SelectMany(f=>f.Points).Max(p=>p.Z)-(storey.Elevation+storey.Height))<.01,
                "Every floor wall must end at its actual storey top, including 5900/4500/3600 mm heights.");
        var heightSession=new BuildingModelEditSession(tallGenerated); string heightError;
        var resized=StoreyElevationLayout.Resolve(new[] {
            new StoreyModel { Id="1F",Name="一层",Height=6200 },
            new StoreyModel { Id="2F",Name="二层",Height=4500,TemplateStoreyId="1F" },
            new StoreyModel { Id="3F",Name="三层",Height=3600,TemplateStoreyId="1F" } },"1F",0);
        Check(heightSession.TryReplaceStoreys(resized,out heightError) && heightSession.Model.HeightOf(heightSession.Model.Walls[0])==6200,
            "Changing the floor setting must change imported wall height immediately.");
        Check(heightSession.Undo() && heightSession.Model.HeightOf(heightSession.Model.Walls[0])==5900,
            "Undo must restore the wall's previous effective height together with the floor settings.");
        var legacy=BuildingModelJson.FromJson(BuildingModelJson.ToJson(generated));
        legacy.Walls[0].Height=4500; legacy.CadImport.Walls[0].Height=4500;
        Check(CadFloorModelGeneration.Build(legacy,registry).Walls[0].Height==0,
            "Reimport must repair old fixed-height CAD walls without changing their source identities.");
        legacy.Walls[0].Height=4400; Reject(()=>CadFloorModelGeneration.Build(legacy,registry));
        wall.Fields.First(f=>f.Name=="Height").Number=3000;
        Check(registry.BuildSchedule(generated).Single().Quantity==2,"The standard reference must reuse the source and count real floor instances.");
        Check(BuildingVolumeBuilder.Build(generated).Faces.Where(f=>f.Kind=="wall").Select(f=>f.StoreyId).Distinct().Count()==2,"Standard references must generate their wall volumes.");
        var session=new BuildingModelEditSession(model); string error;
        Check(session.TryImportCadFloors(registry,"first",out error),error);
        Check(session.Undo() && session.Model.Walls.Count==0 && session.Model.Openings.Count==0,"Generation must undo as one edit.");
        Check(session.Redo() && session.Model.Openings.Count==1,"Generation must redo all components.");
        var id=session.Model.Walls[0].Id;
        Check(session.TryImportCadFloors(registry,"second",out error) && session.Model.Walls.Count==1 && session.Model.Walls[0].Id==id,"Reimport must replace stable source identities without duplicates.");
        item.Placement=CadOpeningPlacement.Create(floor,"W1",new PointModel(100,200),new PointModel(6100,200),new PointModel(2100,200),new PointModel(2900,200),800);
        Check(session.TryImportCadFloors(registry,"third",out error) && session.Model.Openings[0].Offset==2400,"Reframing moved openings must update existing instances.");
        session.Model.Walls.Add(new WallModel { Id="manual",StoreyId="1F",X1=10000,X2=15000,Thickness=200 });
        Check(session.TryImportCadFloors(registry,"fourth",out error) && session.Model.Walls.Count==2,"Unrelated manual construction must be preserved.");
        var before=BuildingModelJson.ToJson(session.Model); var revision=session.Revision;
        item.Sill=null;
        item.Kind="待确认";
        Check(session.TryImportCadFloors(registry,"default-door",out error) && session.Model.Openings[0].Kind=="门" && session.Model.Openings[0].Sill==0,
            "Legacy unconfirmed types and missing sill must generate a default door without manual confirmation.");
        Check(item.Kind=="待确认" && item.Sill==null,"Generation defaults must not pretend to be values read from CAD.");
        item.Code=" C01 "; item.Height=1200;
        Check(CadFloorModelGeneration.Build(model,registry).Openings[0].Kind=="窗" && CadFloorModelGeneration.Build(model,registry).Openings[0].Sill==900,
            "Known window labels must generate ordinary windows and default sill.");
        item.Height=2700;
        Check(CadFloorModelGeneration.Build(model,registry).Openings[0].Sill==100,"Default sill must fit the shortest actual reference-floor wall without changing native opening size.");
        item.Sill=0;
        Check(CadFloorModelGeneration.Build(model,registry).Openings[0].Sill==0,"Explicit zero must override default window sill.");
        item.Sill=900; Reject(()=>CadFloorModelGeneration.Build(model,registry));
        item.Code="—"; item.Sill=null; item.Height=1200;
        Check(CadFloorModelGeneration.Build(model,registry).Openings[0].Code=="C0812","Legacy missing-label placeholders must become explicit default size codes.");
        item.Code=null;
        var defaultSchedule=registry.BuildSchedule(model).Single();
        Check(defaultSchedule.Code=="C0812" && defaultSchedule.ElevationType=="普通窗","Missing labels must also produce a usable MCLM schedule.");
        item.Kind="门"; item.Code="C01";
        Check(CadFloorModelGeneration.Build(model,registry).Openings[0].Kind=="门","Existing explicit categories must survive automatic defaults.");
        item.Code="M01"; item.Height=2200; item.Sill=0;
        before=BuildingModelJson.ToJson(session.Model); revision=session.Revision; item.Width=double.NaN;
        Check(!session.TryImportCadFloors(registry,"invalid",out error) && before==BuildingModelJson.ToJson(session.Model) && session.Revision==revision,"Invalid dimensions must still reject the whole edit without changing history."); item.Width=800;
        item.Placement=null;
        var withoutLocation=CadFloorModelGeneration.Build(model,registry);
        Check(withoutLocation.Walls.Count==1 && withoutLocation.Openings.Count==0
            && withoutLocation.CadImport.PendingOpenings.Single().Code=="M01"
            && withoutLocation.CadImport.PendingOpenings[0].ReferencePosition==null,
            "Unlocated openings must warn without blocking walls or inventing a location.");
        capture.Probe.Entities.Add(new CadBuildingProbeEntity { Handle="M1",
            BoundsMin=new CadProbePoint { X=1000,Y=600 },BoundsMax=new CadProbePoint { X=2000,Y=900 } });
        var withReference=CadFloorModelGeneration.Build(model,registry);
        var pending=withReference.CadImport.PendingOpenings.Single();
        Check(pending.ReferencePosition.X==1400 && pending.ReferencePosition.Y==550
            && withReference.Openings.Count==0,"Drawing envelope must only supply a reference marker, not a guessed aperture.");
        var roundtrip=BuildingModelJson.FromJson(BuildingModelJson.ToJson(withReference));
        BuildingModelJson.SaveModel(Path.Combine(folder,"pending-marker.json"),withReference);
        Check(roundtrip.CadImport.PendingOpenings.Single().ReferencePosition.X==1400,
            "Pending opening markers must survive save/reopen.");
        item.Placement=location;
        var completed=CadFloorModelGeneration.Build(withReference,registry);
        Check(completed.Openings.Count==1 && completed.CadImport.PendingOpenings.Count==0,
            "Correcting placement must replace the pending marker with a real opening.");
        capture.Openings.Add(new CadFloorOpeningItem { SourceHandle="M2",Code="M02",Kind="门",
            Width=900,Height=2100,Sill=0 });
        var mixed=CadFloorModelGeneration.Build(model,registry);
        Check(mixed.Walls.Count==1 && mixed.Openings.Count==1
            && mixed.CadImport.PendingOpenings.Single().Code=="M02",
            "One unlocated opening must not suppress confirmed openings or model walls.");
        var pendingRequest=CadModelGenerationRequest.Queue(registry);
        Check(CadModelGenerationRequest.Load(path).Id==pendingRequest.Id,
            "Queue validation must also allow unlocated openings.");
        CadModelGenerationRequest.Acknowledge(path,pendingRequest.Id,null);
        capture.Openings.RemoveAt(capture.Openings.Count-1);
        capture.Probe.Entities.Clear();
        wall.StraightHorizontalLineVerified=false; Reject(()=>CadFloorModelGeneration.Build(model,registry)); wall.StraightHorizontalLineVerified=true;
        wall.Fields.First(f=>f.Name=="Elevation").Number=double.NaN; Reject(()=>CadFloorModelGeneration.Build(model,registry)); wall.Fields.First(f=>f.Name=="Elevation").Number=0;
        wall.Fields.First(f=>f.Name=="Height").Number=null;
        Check(CadFloorModelGeneration.Build(model,registry).Walls[0].Height==0,"Missing native wall height must still use the configured model storey height.");
        item.Height=2900; Reject(()=>CadFloorModelGeneration.Build(model,registry)); item.Height=2200;
        wall.Fields.First(f=>f.Name=="Height").Number=3000;
        session.Model.Walls.First(w=>w.Id==id).Thickness=250;
        before=BuildingModelJson.ToJson(session.Model);
        Check(!session.TryImportCadFloors(registry,"changed",out error) && before==BuildingModelJson.ToJson(session.Model),"Manual changes to imported elements must be protected.");
        item.Kind="待确认"; item.Sill=null;
        var request=CadModelGenerationRequest.Queue(registry);
        Check(CadModelGenerationRequest.Load(path).Id==request.Id,"Generation request must round-trip geometry and source identity.");
        CadModelGenerationRequest.Acknowledge(path,"an-older-request",null);
        Check(CadModelGenerationRequest.Load(path).Id==request.Id,"Acknowledging an older request must preserve the newer job.");
        CadModelGenerationRequest.Acknowledge(path,request.Id,"validation error");
        Check(!File.Exists(CadModelGenerationRequest.FilePath(path)) && !CadModelGenerationRequest.LoadResult(path).Succeeded,"Completed/rejected jobs must not replay after undo and reopen.");
        CadModelGenerationRequest.Queue(registry);
        BuildingModelJson.SaveModel(path,generated);
        Check(BuildingModelJson.LoadModel(path).CadImport.RequestId=="first","Import baseline and request id must persist.");
        StudioLaunch.RegisterSession(path); Check(StudioLaunch.HasLiveSession(path),"Live editor marker must match a live process.");
        StudioLaunch.RemoveSession(path); Check(!StudioLaunch.HasLiveSession(path),"Editor marker must be removed on close.");
        var invoked=false;
        var window=new CadFloorRegistrationWindow(path,generateModel:r=>{ invoked=true; });
        var root=(FrameworkElement)window.Content; root.Measure(new Size(1060,610)); root.Arrange(new Rect(new Size(1060,610))); root.UpdateLayout();
        var buttons=Descendants(root).OfType<Button>().ToList();
        Check(!buttons.Any(b=>(b.Content as string)=="门窗表与立面"),"Registration must not send the user to elevations.");
        buttons.Single(b=>(b.Content as string)=="生成建筑模型").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(invoked,"Generate button must invoke the model generation action.");
        Console.WriteLine("GENERATION_FIXTURE "+path);
    }
    private static CadProbeSegment Segment(double x1,double y1,double x2,double y2) => new CadProbeSegment { Start=new CadProbePoint { X=x1,Y=y1 },End=new CadProbePoint { X=x2,Y=y2 } };
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested; } }
}
