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
    private static string Geometry(BuildingModelDocument model)
    {
        var copy=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));copy.CadImport=null;
        return BuildingModelJson.ToJson(copy);
    }
    private static void WarnAndPreserve(BuildingModelDocument model,CadFloorPlanRegistry registry)
    {
        var before=Geometry(model);var result=CadFloorModelGeneration.Build(model,registry,"warning-only");
        Check(Geometry(result)==before&&result.CadImport.RequestId=="warning-only"&&result.CadImport.StructureMessages.Count>0,
            "Recognition or update failure must retain the model and return a nonblocking warning.");
    }
    private static void CheckGenerationProgress(string path,CadFloorPlanRegistry registry,CadModelGenerationRequest request)
    {
        Check(CadModelGenerationRequest.LoadProgress(path,"wrong-job")==null,"Progress leaked across jobs.");
        Check(CadModelGenerationRequest.LoadProgress(path,request.Id).State=="waiting","Queued progress must be indeterminate waiting.");
        try{CadModelGenerationRequest.Queue(registry);throw new Exception("Duplicate job replaced pending request.");}catch(IOException){}
        Check(CadModelGenerationRequest.Load(path).Id==request.Id,"Duplicate queue changed request identity.");
        CadModelGenerationRequest.ReportProgress(path,new CadGenerationProgress {RequestId=request.Id,Stage="floor",Completed=1,Total=2});
        Check(CadModelGenerationRequest.LoadProgress(path,request.Id).Completed==1,"Progress did not round-trip.");
        var source=BuildingModelJson.LoadModel(path);var original=BuildingModelJson.ToJson(source);
        var session=new BuildingModelEditSession(source);var steps=new List<CadGenerationProgress>();
        session.ImportCadFloorsAsync(registry,request.Id,steps.Add).GetAwaiter().GetResult();
        Check(steps.Any(p=>p.Total==registry.Floors.Count&&p.Completed==p.Total)
            &&steps.All(p=>p.Total<=0||p.Completed>=0&&p.Completed<=p.Total),"Floor progress has no truthful denominator.");
        Check(session.Model.CadImport.RequestId==request.Id&&session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,
            "Async generation must commit in one undoable edit.");
        Check(BuildingModelJson.ToJson(source)==original,"Async generation mutated its input snapshot.");
        var conflict=new BuildingModelEditSession(BuildingModelJson.FromJson(original));
        using(var entered=new System.Threading.ManualResetEventSlim())
        using(var release=new System.Threading.ManualResetEventSlim())
        {
            var task=conflict.ImportCadFloorsAsync(registry,"conflict",p=>{entered.Set();if(!release.Wait(5000))throw new TimeoutException();});
            try
            {
                Check(entered.Wait(5000),"Async generation did not start.");
                Check(conflict.TryReplaceDrawingViews(new List<ViewDefinitionModel>(),out var error),error);
            }
            finally{release.Set();}
            try{task.GetAwaiter().GetResult();throw new Exception("Async generation overwrote intervening edit.");}catch(InvalidOperationException){}
        }
        Check(conflict.Model.CadImport?.RequestId!="conflict","Revision conflict committed generated data.");
        var blockedPath=Path.Combine(Path.GetDirectoryName(path),"progress-unavailable","model.json");
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath));BuildingModelJson.SaveModel(blockedPath,source);
        Directory.CreateDirectory(CadModelGenerationRequest.ProgressPath(blockedPath));
        var blockedRegistry=registry.Clone();blockedRegistry.ModelPath=blockedPath;
        var blockedRequest=CadModelGenerationRequest.Queue(blockedRegistry);
        CadModelGenerationRequest.Acknowledge(blockedPath,blockedRequest.Id,null);
        Check(CadModelGenerationRequest.LoadResult(blockedPath).Succeeded&&!File.Exists(CadModelGenerationRequest.FilePath(blockedPath)),
            "Unavailable optional progress sidecar prevented the authoritative result.");
        Console.WriteLine("CAD_GENERATION_PROGRESS_CORE_OK requestIsolation duplicateQueue floorCounts backgroundSnapshot oneUndo revisionConflict");
    }
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
        var slidingSource=new CadBuildingProbeEntity {Handle="TLM",DxfName="TCH_OPENING",
            Fields=new List<CadProbeField>{new CadProbeField {Name="OpeningCode",Text="TLM0822"}},
            DisplaySegments=new List<CadProbeSegment>{Segment(1100,180,1520,180),Segment(1480,220,1900,220)}};
        Check(CadOpeningJambPlacement.Find(floor,slidingSource,800)?.DistanceFromWallStart==1400,"Overlapping sliding tracks must recover exact native aperture.");
        Check(CadOpeningJambPlacement.Find(floor,slidingSource,900)==null,"Sliding tracks must match native width.");
        var duplicateHost=new CadBuildingProbeEntity {Handle="duplicate",DxfName="TCH_WALL",StraightHorizontalLineVerified=true,
            CurveStart=wall.CurveStart,CurveEnd=wall.CurveEnd,CandidateThickness=200,CandidateAxisOffset=0};
        floor.WallCandidates.Add(duplicateHost);
        Check(CadOpeningJambPlacement.Find(floor,slidingSource,800)==null,"Sliding tracks cannot guess between two hosts.");
        floor.WallCandidates.Remove(duplicateHost);
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
        var pendingRegistry=registry.Clone();pendingRegistry.Floors[0].Openings.Add(new CadFloorOpeningItem {
            SourceHandle="manual-pending",Code="TLM0822",Kind="门",Width=800,Height=2200});
        var withPending=CadFloorModelGeneration.Build(generated,pendingRegistry);
        var pendingSession=new BuildingModelEditSession(withPending);
        Check(pendingSession.TryPlacePendingOpening("1F","manual-pending",withPending.Walls[0].Id,3500,out var manualId,out var pendingError),pendingError);
        var rebuiltPending=CadFloorModelGeneration.Build(pendingSession.Model,pendingRegistry);
        Check(rebuiltPending.Openings.Count==2&&rebuiltPending.Openings.Any(o=>o.Id==manualId&&o.Offset==3500)
            &&rebuiltPending.CadImport.PendingOpenings.Count==0,"Reimport must preserve a model-placed pending opening without duplication or a new pending mark.");
        var independent=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));
        independent.Storeys[1].TemplateStoreyId=null;
        var both=registry.Clone();var second=registry.Clone().Floors[0];
        second.Floor.Storey=independent.Storeys[1];second.Floor.Alignment.StoreyId="2F";both.Floors.Add(second);
        var populated=CadFloorModelGeneration.Build(independent,both);
        var editable=new BuildingModelEditSession(populated);
        var edited=BuildingModelJson.FromJson(BuildingModelJson.ToJson(populated)).Storeys;
        edited[1].TemplateStoreyId="1F";
        Check(editable.TryReplaceStoreys(edited,out var editError,allowContentChanges:true),editError);
        var reimport=CadFloorModelGeneration.Build(editable.Model,both);
        Check(reimport.Walls.Count==1 && reimport.Openings.Count==1 && both.Floors.Count==2,
            "Reimport must omit inactive reference-floor captures without mutating the original registration.");
        Check(editable.Undo(),"Undo floor source edit.");
        edited=edited.Where(s=>s.Id!="2F").ToList();
        Check(editable.TryReplaceStoreys(edited,out editError,allowContentChanges:true),editError);
        Check(CadFloorModelGeneration.Build(editable.Model,both).Walls.Count==1,
            "Deleted floors must not be recreated by old CAD captures.");
        var sync=both.Clone();sync.ReconcileStoreys(editable.Model);
        Check(sync.Floors.Count==1 && sync.Floors[0].Floor.Storey.Height==editable.Model.Storeys[0].Height,
            "Registration must use the current model's active floors and dimensions.");
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
        legacy.Walls[0].Height=4400; WarnAndPreserve(legacy,registry);
        wall.Fields.First(f=>f.Name=="Height").Number=3000;
        Check(registry.BuildSchedule(generated).Single().Quantity==2,"The standard reference must reuse the source and count real floor instances.");
        Check(BuildingVolumeBuilder.Build(generated).Faces.Where(f=>f.Kind=="wall").Select(f=>f.StoreyId).Distinct().Count()==2,"Standard references must generate their wall volumes.");
        var rangeModel=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));
        rangeModel.Storeys=StoreyElevationLayout.Resolve(StandardStoreyLayout.ExpandRanges(new[] {
            new StoreyModel { Id="1F",Name="1～12层",Height=3000 } }),"1F",0);
        var rangeGenerated=CadFloorModelGeneration.Build(rangeModel,registry,"range");
        Check(rangeGenerated.Walls.Count==1 && StandardStoreyLayout.Materialize(rangeGenerated).Walls.Count==12
            && registry.BuildSchedule(rangeGenerated).Single().Quantity==12,"One standard-range CAD capture must generate twelve actual floors and count twelve openings.");
        var rangeRow=new CadFloorRegistrationRow { Storey=rangeGenerated.FindStorey("1F") };
        Check(rangeRow.Name=="1～12层" && rangeRow.ElevationDisplay=="0～33000" && rangeRow.CanRegister,
            "CAD registration must display the range and per-floor elevations, with one active capture action.");
        var session=new BuildingModelEditSession(model); string error;
        Check(session.TryImportCadFloors(registry,"first",out error),error);
        Check(session.Undo() && session.Model.Walls.Count==0 && session.Model.Openings.Count==0,"Generation must undo as one edit.");
        Check(session.Redo() && session.Model.Openings.Count==1,"Generation must redo all components.");
        var id=session.Model.Walls[0].Id;
        session.Model.Openings[0].PlanLabelAlong=75;session.Model.Openings[0].PlanLabelNormal=125;
        session.Model.Openings[0].PlanOpenAngle=30;session.Model.Openings[0].OpenIn3D=true;
        session.Model.Openings[0].ThresholdHeight=80;
        session.Model.Openings[0].Code="C-自定义";session.Model.Openings[0].CodeManuallyEdited=true;
        Check(session.TryImportCadFloors(registry,"second",out error) && session.Model.Walls.Count==1 && session.Model.Walls[0].Id==id,"Reimport must replace stable source identities without duplicates.");
        Check(session.Model.Openings[0].PlanLabelAlong==75&&session.Model.Openings[0].PlanLabelNormal==125,"Reimport must preserve edited plan code displacement.");
        Check(session.Model.Openings[0].PlanOpenAngle==30&&session.Model.Openings[0].OpenIn3D==true&&session.Model.Openings[0].Code=="C-自定义","Reimport must preserve instance angle, 3D switch and edited code.");
        Check(session.Model.Openings[0].ThresholdHeight==80,"Reimport must preserve instance threshold height.");
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
        item.Sill=900;Check(CadFloorModelGeneration.Build(model,registry).CadImport.PendingOpenings.Count==1,
            "A too-high opening must become pending rather than block generation.");
        item.Code="—"; item.Sill=null; item.Height=1200;
        Check(CadFloorModelGeneration.Build(model,registry).Openings[0].Code=="C0812","Legacy missing-label placeholders must become explicit default size codes.");
        item.Code=null;
        var defaultSchedule=registry.BuildSchedule(model).Single();
        Check(defaultSchedule.Code=="C0812" && defaultSchedule.ElevationType=="普通窗","Missing labels must also produce a usable MCLM schedule.");
        item.Kind="门"; item.Code="C01";
        Check(CadFloorModelGeneration.Build(model,registry).Openings[0].Kind=="门","Existing explicit categories must survive automatic defaults.");
        item.Code="M01"; item.Height=2200; item.Sill=0;
        before=BuildingModelJson.ToJson(session.Model); revision=session.Revision; item.Width=double.NaN;
        var geometry=Geometry(session.Model);
        Check(session.TryImportCadFloors(registry,"invalid",out error)&&geometry==Geometry(session.Model)
            &&session.Revision==revision+1&&session.Model.CadImport.StructureMessages.Count>0,
            "Invalid registration dimensions must warn without corrupting geometry or blocking the model.");
        Check(session.Undo()&&before==BuildingModelJson.ToJson(session.Model),"Warning-only generation must undo cleanly.");item.Width=800;
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
        wall.StraightHorizontalLineVerified=false; WarnAndPreserve(model,registry); wall.StraightHorizontalLineVerified=true;
        wall.Fields.First(f=>f.Name=="Elevation").Number=double.NaN; WarnAndPreserve(model,registry); wall.Fields.First(f=>f.Name=="Elevation").Number=0;
        wall.Fields.First(f=>f.Name=="Height").Number=null;
        Check(CadFloorModelGeneration.Build(model,registry).Walls[0].Height==0,"Missing native wall height must still use the configured model storey height.");
        item.Height=2900;Check(CadFloorModelGeneration.Build(model,registry).CadImport.PendingOpenings.Count==1,
            "Opening height errors must not block walls or the editable model.");item.Height=2200;
        wall.Fields.First(f=>f.Name=="Height").Number=3000;
        session.Model.Walls.First(w=>w.Id==id).Thickness=250;
        before=BuildingModelJson.ToJson(session.Model);
        WarnAndPreserve(session.Model,registry);
        item.Kind="待确认"; item.Sill=null;
        var request=CadModelGenerationRequest.Queue(registry);
        Check(CadModelGenerationRequest.Load(path).Id==request.Id,"Generation request must round-trip geometry and source identity.");
        CheckGenerationProgress(path,registry,request);
        CadModelGenerationRequest.Acknowledge(path,"an-older-request",null);
        Check(CadModelGenerationRequest.Load(path).Id==request.Id,"Acknowledging an older request must preserve the newer job.");
        CadModelGenerationRequest.Acknowledge(path,request.Id,"validation error");
        Check(CadModelGenerationRequest.LoadProgress(path,request.Id).State=="failed"
            &&CadModelGenerationRequest.LoadProgress(path,request.Id).Completed==0,"Failed generation must not show complete progress.");
        CadModelGenerationRequest.ReportProgress(path,new CadGenerationProgress {RequestId=request.Id,Stage="late heartbeat"});
        Check(CadModelGenerationRequest.LoadProgress(path,request.Id).State=="failed","Late heartbeat replaced terminal state.");
        Check(!File.Exists(CadModelGenerationRequest.FilePath(path)) && !CadModelGenerationRequest.LoadResult(path).Succeeded,"Completed/rejected jobs must not replay after undo and reopen.");
        var nextRequest=CadModelGenerationRequest.Queue(registry);
        CadModelGenerationRequest.ReportProgress(path,new CadGenerationProgress {RequestId=request.Id,Stage="old heartbeat"});
        Check(CadModelGenerationRequest.LoadProgress(path,nextRequest.Id).State=="waiting","Old heartbeat overwrote the next job.");
        BuildingModelJson.SaveModel(path,generated);
        Check(BuildingModelJson.LoadModel(path).CadImport.RequestId=="first","Import baseline and request id must persist.");
        StudioLaunch.RegisterSession(path); Check(StudioLaunch.HasLiveSession(path),"Live editor marker must match a live process.");
        StudioLaunch.RemoveSession(path); Check(!StudioLaunch.HasLiveSession(path),"Editor marker must be removed on close.");
        var invoked=false;
        var window=new CadFloorRegistrationWindow(path,generateModel:r=>{ invoked=true;Check(r.ReplaceRegisteredConstruction,"CAD UI must default to explicit replacement."); });
        var root=(FrameworkElement)window.Content; root.Measure(new Size(1060,610)); root.Arrange(new Rect(new Size(1060,610))); root.UpdateLayout();
        var buttons=Descendants(root).OfType<Button>().ToList();
        Check(!buttons.Any(b=>(b.Content as string)=="门窗表与立面"),"Registration must not send the user to elevations.");
        buttons.Single(b=>(b.Content as string)=="生成建筑模型").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(invoked,"Generate button must invoke the model generation action.");
        var safeWindow=new CadFloorRegistrationWindow(path,generateModel:r=>Check(!r.ReplaceRegisteredConstruction,"Safe-update choice lost."));
        var safeRoot=(FrameworkElement)safeWindow.Content;safeRoot.Measure(new Size(1060,610));safeRoot.Arrange(new Rect(new Size(1060,610)));safeRoot.UpdateLayout();
        Descendants(safeRoot).OfType<ComboBox>().Single(c=>c.Name=="CadGenerationMode").SelectedIndex=1;
        Descendants(safeRoot).OfType<Button>().Single(b=>(b.Content as string)=="生成建筑模型").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        foreach(var readFailure in new[] {false,true})
        {
            var submitted=false;
            var nonblocking=new CadFloorRegistrationWindow(path,generateModel:r=>{
                submitted=true;var safe=CadFloorModelGeneration.Build(BuildingModelJson.LoadModel(path),r);
                Check(r.ModelPath==path,"A failed CAD read must not replace the registry target with its partial draft.");
                Check(Geometry(safe)==Geometry(BuildingModelJson.LoadModel(path))&&safe.CadImport.StructureMessages.Count>0,
                    "CAD pre-generation failure must keep a safe editable model.");
                if(readFailure)Check(r.GenerationWarning.Contains("源对象无法读取"),"CAD read warning did not reach the model request.");
            },readPlan:r=>{if(readFailure){r.Floors.Clear();r.ModelPath="wrong-path";throw new InvalidDataException("源对象无法读取");}});
            var panel=(FrameworkElement)nonblocking.Content;panel.Measure(new Size(1060,610));panel.Arrange(new Rect(new Size(1060,610)));panel.UpdateLayout();
            if(!readFailure)Descendants(panel).OfType<TextBox>().Single(t=>t.Name=="CadSlabThickness").Text="invalid";
            Descendants(panel).OfType<Button>().Single(b=>(b.Content as string)=="生成建筑模型").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(submitted,"CAD thickness or re-read warnings must not block the Generate button.");
        }
        RunReferenceOnlyTests(model,registry,rebuiltPending,folder);
        RunNonBlockingTests(model,registry,generated);
        RunRoundedOpeningFitTests(model,registry);
        Console.WriteLine("GENERATION_FIXTURE "+path);
    }
    private static void RunRoundedOpeningFitTests(BuildingModelDocument original,CadFloorPlanRegistry source)
    {
        var model=BuildingModelJson.FromJson(BuildingModelJson.ToJson(original));
        model.Storeys[0].Height=4000;model.Storeys[1].Height=3108;
        var registry=source.Clone();var capture=registry.Floors[0];var wall=capture.Floor.WallCandidates.Single();
        wall.CurveStart=new CadProbePoint {X=100,Y=200};wall.CurveEnd=new CadProbePoint {X=3868,Y=200};
        capture.Openings=new List<CadFloorOpeningItem> {new CadFloorOpeningItem {
            SourceHandle="rounded-opening",Code="MLC04B",Kind="门联窗",Width=3770,Height=2210,Sill=900,
            Placement=CadOpeningPlacement.Create(capture.Floor,wall.Handle,new PointModel(100,200),new PointModel(3868,200),
                new PointModel(100,200),new PointModel(3868,200),3770) }};
        capture.Probe.Entities=new List<CadBuildingProbeEntity> {new CadBuildingProbeEntity {
            Handle="rounded-opening",BoundsMin=new CadProbePoint {X=100,Y=100},BoundsMax=new CadProbePoint {X=3868,Y=300} }};
        var before=BuildingModelJson.ToJson(model);
        foreach(var replacement in new[] {false,true})
        {
            registry.ReplaceRegisteredConstruction=replacement;
            var fitted=CadFloorModelGeneration.Build(model,registry,"rounded-fit");
            Check(fitted.Walls.Count==1&&fitted.Openings.Count==1&&fitted.CadImport.PendingOpenings.Count==0,
                "Rounding-only CAD discrepancies must not suppress a verified wall or opening.");
            var opening=fitted.Openings.Single();var baseline=fitted.CadImport.Openings.Single();
            Check(opening.Code=="MLC04B"&&opening.Width==3768&&opening.Height==2208&&opening.Offset==1884&&opening.Sill==900
                &&fitted.Walls.Single().X2==3768,
                "Generated dimensions must fit actual wall endpoints and the shortest shared storey without moving or extending geometry.");
            Check(baseline.Id==opening.Id&&baseline.Width==opening.Width&&baseline.Height==opening.Height&&baseline.Offset==opening.Offset,
                "The CAD baseline must store fitted physical dimensions, not the oversized nominal dimensions.");
            Check(StandardStoreyLayout.Materialize(fitted).Openings.All(o=>o.Width==3768&&o.Height==2208),
                "Standard-storey instances must reuse the fitted physical opening.");
            Check(fitted.CadImport.StructureMessages.Any(m=>m.Contains("MLC04B")&&m.Contains("3770")&&m.Contains("3768")
                &&m.Contains("2210")&&m.Contains("2208")),"CAD fit adjustments must be reported.");
            var repeated=CadFloorModelGeneration.Build(fitted,registry,"rounded-repeat");
            Check(Geometry(repeated)==Geometry(fitted)&&repeated.CadImport.Openings.Single().Width==3768
                &&repeated.CadImport.PendingOpenings.Count==0,"Repeated CAD generation must remain fitted without treating its own baseline as a local edit.");
            Check(BuildingModelJson.ToJson(model)==before&&capture.Openings.Single().Width==3770&&capture.Openings.Single().Height==2210
                &&capture.Openings.Single().Placement.PickedWidth==3768&&wall.CurveEnd.X==3868,
                "CAD fitting must not rewrite source geometry, registration dimensions or picked endpoints.");
        }
        registry.ReplaceRegisteredConstruction=false;
        var offsetRegistry=registry.Clone();var offsetCapture=offsetRegistry.Floors[0];var offsetWall=offsetCapture.Floor.WallCandidates.Single();
        offsetWall.CurveEnd.X=6100;
        offsetCapture.Openings.Single().Placement=CadOpeningPlacement.Create(offsetCapture.Floor,offsetWall.Handle,
            new PointModel(100,200),new PointModel(6100,200),new PointModel(100,200),new PointModel(3868,200),3770);
        var offsetFit=CadFloorModelGeneration.Build(model,offsetRegistry);
        Check(offsetFit.Openings.Single().Width==3768&&offsetFit.Openings.Single().Offset==1884&&offsetFit.Walls.Single().X2==6000,
            "The width limit must use the current symmetric center, not the entire wall length or a recentered opening.");
        var fractional=registry.Clone();var fractionalCapture=fractional.Floors[0];fractionalCapture.Floor.WallCandidates.Single().CurveEnd.X=3868.6;
        fractionalCapture.Openings.Single().Placement=CadOpeningPlacement.Create(fractionalCapture.Floor,wall.Handle,
            new PointModel(100,200),new PointModel(3868.6,200),new PointModel(100,200),new PointModel(3868.6,200),3770);
        Check(CadFloorModelGeneration.Build(model,fractional).Openings.Single().Width==3768,
            "A fractional actual limit must fit inward to whole millimetres.");
        var boundary=registry.Clone();boundary.Floors[0].Openings.Single().Height=2213;
        Check(CadFloorModelGeneration.Build(model,boundary).Openings.Single().Height==2208,
            "An exactly 5 mm discrepancy in the same 10 mm bucket must fit inward.");
        foreach(var requestedWidth in new[] {3774d,3780d})
        {
            var oversized=registry.Clone();oversized.Floors[0].Openings.Single().Width=requestedWidth;
            var rejected=CadFloorModelGeneration.Build(model,oversized);
            Check(rejected.Walls.Count==1&&rejected.Openings.Count==0&&rejected.CadImport.PendingOpenings.Single().Width==requestedWidth
                &&rejected.CadImport.PendingOpenings.Single().ReferencePosition.X==1884,
                "Genuinely oversized CAD openings must retain their requested size and reference without blocking walls.");
        }
        var otherBucket=registry.Clone();var bucketCapture=otherBucket.Floors[0];bucketCapture.Floor.WallCandidates.Single().CurveEnd.X=3864;
        bucketCapture.Openings.Single().Width=3766;
        bucketCapture.Openings.Single().Placement=CadOpeningPlacement.Create(bucketCapture.Floor,wall.Handle,
            new PointModel(100,200),new PointModel(3864,200),new PointModel(100,200),new PointModel(3864,200),3766);
        var bucketRejected=CadFloorModelGeneration.Build(model,otherBucket);
        Check(bucketRejected.Walls.Count==1&&bucketRejected.Openings.Count==0&&bucketRejected.CadImport.PendingOpenings.Single().Width==3766
            &&bucketRejected.CadImport.PendingOpenings.Single().Reason.Contains("端部"),
            "Even a verified 2 mm picked-width discrepancy must remain pending when its actual limit is in a different 10 mm bucket.");
        foreach(var requestedHeight in new[] {2214d,2216d})
        {
            var tall=registry.Clone();tall.Floors[0].Openings.Single().Height=requestedHeight;
            var heightRejected=CadFloorModelGeneration.Build(model,tall);
            Check(heightRejected.Walls.Count==1&&heightRejected.Openings.Count==0&&heightRejected.CadImport.PendingOpenings.Single().Height==requestedHeight,
                "A height overflow greater than 5 mm must remain a reference, including requests in the same 10 mm bucket.");
        }
        Console.WriteLine("CAD_ROUNDED_OPENING_FIT_OK width3770To3768 shortestSharedHeight inwardWholeMm fixedCenter baseline repeat sourcePreserved oversizePending");
    }
    private static void RunReferenceOnlyTests(BuildingModelDocument empty, CadFloorPlanRegistry source,
        BuildingModelDocument existing, string folder)
    {
        var registry=source.Clone();var capture=registry.Floors[0];capture.Floor.WallCandidates.Clear();
        capture.Openings.ForEach(o=>o.Placement=null);
        capture.Underlay=new CadPlanUnderlay { Primitives=new List<ComponentPlanPrimitive> {
            new ComponentPlanPrimitive { Kind="Line",SourceLayer="普通线",X1=100,Y1=200,X2=6100,Y2=200 } } };
        capture.Probe.Entities=new List<CadBuildingProbeEntity> {
            new CadBuildingProbeEntity { Handle="M1",BoundsMin=new CadProbePoint {X=1100,Y=100},BoundsMax=new CadProbePoint {X=1900,Y=300} },
            new CadBuildingProbeEntity { Handle="invalid-column",DxfName="TCH_COLUMN" } };
        string RegistryJson(CadFloorPlanRegistry value)
        {
            using(var stream=new MemoryStream()) {
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).WriteObject(stream,value);
                return Convert.ToBase64String(stream.ToArray());
            }
        }
        var emptyBefore=BuildingModelJson.ToJson(empty);var registryBefore=RegistryJson(registry);
        var session=new BuildingModelEditSession(empty);
        Check(session.TryImportCadFloors(registry,"reference-only",out var error),error);
        Check(session.Model.Walls.Count==0&&session.Model.Openings.Count==0&&session.Model.Columns.Count==0&&session.Model.Beams.Count==0,
            "Reference-only generation must succeed without inventing construction.");
        Check(session.Model.CadImport.PendingOpenings.Single().ReferencePosition.X==1400
            &&session.Model.CadImport.StructureMessages.Any(m=>m.Contains("未识别到可生成")),
            "No recognised construction must warn and retain opening reference positions.");
        Check(BuildingModelJson.ToJson(empty)==emptyBefore&&RegistryJson(registry)==registryBefore,
            "Reference-only generation must not mutate the source model or underlay registration.");
        Check(session.Model.Storeys.Count==2&&session.Model.Storeys[1].TemplateStoreyId=="1F",
            "Reference-only generation must preserve standard storeys.");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==emptyBefore&&session.Redo(),
            "Reference-only generation must undo and redo as one edit.");
        registry.ModelPath=Path.Combine(folder,"reference-only.json");capture.Floor.ModelPath=registry.ModelPath;
        BuildingModelJson.SaveModel(registry.ModelPath,empty);
        registry.SaveFloor(capture);var request=CadModelGenerationRequest.Queue(registry);
        Check(CadModelGenerationRequest.Load(registry.ModelPath).Id==request.Id,"Reference-only request must queue.");
        BuildingModelJson.SaveModel(registry.ModelPath,session.Model);
        Check(BuildingModelJson.LoadModel(registry.ModelPath).CadImport.PendingOpenings.Count==1
            &&CadFloorPlanRegistry.Load(registry.ModelPath).Floors[0].Underlay.Primitives.Count==1,
            "Reference-only model and CAD underlay must survive reopening.");
        CadModelGenerationRequest.Acknowledge(registry.ModelPath,request.Id,null);
        Check(CadModelGenerationRequest.LoadResult(registry.ModelPath).Succeeded&&!File.Exists(CadModelGenerationRequest.FilePath(registry.ModelPath)),
            "Reference-only request must acknowledge success without replay.");
        var retained=BuildingModelJson.FromJson(BuildingModelJson.ToJson(existing));
        retained.Walls[0].Thickness+=25;
        retained.Columns.Add(new ColumnModel {Id="manual-column",StoreyId="1F",X=7000,Y=1000,Width=400,Depth=400});
        retained.Beams.Add(new BeamModel {Id="manual-beam",StoreyId="1F",X1=7000,Y1=1000,X2=10000,Y2=1000,Width=200,Depth=500});
        var geometryBefore=BuildingModelJson.ToJson(new BuildingModelDocument {Walls=retained.Walls,Openings=retained.Openings,
            Slabs=retained.Slabs,Columns=retained.Columns,Beams=retained.Beams});
        var baselineBefore=BuildingModelJson.ToJson(new BuildingModelDocument {Walls=retained.CadImport.Walls,Openings=retained.CadImport.Openings});
        var reference=CadFloorModelGeneration.Build(retained,registry,"keep-existing");
        Check(BuildingModelJson.ToJson(new BuildingModelDocument {Walls=reference.Walls,Openings=reference.Openings,
            Slabs=reference.Slabs,Columns=reference.Columns,Beams=reference.Beams})==geometryBefore,
            "An empty recognition result must not delete imported, edited or manual construction.");
        Check(BuildingModelJson.ToJson(new BuildingModelDocument {Walls=reference.CadImport.Walls,Openings=reference.CadImport.Openings})==baselineBefore
            &&reference.CadImport.ResolvedOpenings.Count==retained.CadImport.ResolvedOpenings.Count&&reference.CadImport.PendingOpenings.Count==0,
            "Reference-only refresh must preserve import protection and already placed openings without duplicate markers.");
        var blank=registry.Clone();blank.Floors[0].Openings.Clear();blank.Floors[0].Probe.Entities.Clear();
        Check(CadFloorModelGeneration.Build(empty,blank).Walls.Count==0,"A plain CAD-line-only floor must generate an empty model.");
        capture.Openings[0].Width=double.NaN;
        WarnAndPreserve(empty,registry);
        Console.WriteLine("CAD_REFERENCE_ONLY_OK plainLines pending save queue ack undo standardFloors existingGeometry baselineProtection invalidSize");
    }
    private static void RunNonBlockingTests(BuildingModelDocument model,CadFloorPlanRegistry registry,BuildingModelDocument generated)
    {
        foreach(var change in new Action<CadBuildingProbeEntity>[] {
            w=>w.StraightHorizontalLineVerified=false,w=>w.CurveStart=null,
            w=>w.CandidateAxisOffset=null,w=>w.CandidateThickness=-10,
            w=>w.Fields.First(f=>f.Name=="Elevation").Number=100,
            w=>w.Fields.First(f=>f.Name=="Elevation").Number=null,
            w=>w.CurveEnd.X=double.NaN,w=>w.CurveEnd=w.CurveStart,w=>w.Handle=null })
        {
            var mixed=registry.Clone();var bad=registry.Clone().Floors[0].Floor.WallCandidates[0];bad.Handle="bad-wall";change(bad);
            mixed.Floors[0].Floor.WallCandidates.Add(bad);
            var built=CadFloorModelGeneration.Build(model,mixed);
            Check(built.Walls.Count==1&&built.Openings.Count==1&&built.CadImport.StructureMessages.Any(m=>m.Contains("已跳过")),
                "One unsupported wall must not block the valid wall and opening.");
        }
        var noFloors=registry.Clone();noFloors.Floors.Clear();WarnAndPreserve(model,noFloors);
        var duplicate=registry.Clone();duplicate.Floors.Add(duplicate.Floors[0]);WarnAndPreserve(model,duplicate);
        var invalidSlab=registry.Clone();invalidSlab.SlabThickness=-10;WarnAndPreserve(model,invalidSlab);
        var absentIdentity=registry.Clone();absentIdentity.Floors[0].Probe.DrawingFingerprint=null;WarnAndPreserve(model,absentIdentity);
        var oldSkipped=registry.Clone();oldSkipped.Floors[0].Floor.WallCandidates[0].StraightHorizontalLineVerified=false;
        var preserved=CadFloorModelGeneration.Build(generated,oldSkipped);
        Check(Geometry(preserved)==Geometry(generated)&&preserved.CadImport.Walls.Count==1,
            "An unverified old wall must keep its geometry and import baseline.");
        Console.WriteLine("CAD_NONBLOCKING_GENERATION_OK mixedWalls missingCurve thickness offset elevation finiteGeometry identity noFloors duplicates slabSetting preserveImported");
    }
    private static CadProbeSegment Segment(double x1,double y1,double x2,double y2) => new CadProbeSegment { Start=new CadProbePoint { X=x1,Y=y1 },End=new CadProbePoint { X=x2,Y=y2 } };
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested; } }
}
