using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using BatchPdfPublisher.BuildingModel;

internal static class CadPlanElevationTests
{
    private static void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);}
    internal static void Run()
    {
        foreach(var scale in new[]{1d,1000d})foreach(var z in new[]{0d,-10742.71653,3000d})foreach(var hasField in new[]{true,false})
        {
            var model=new BuildingModelDocument {Name="Plan elevation fixture",Storeys=new List<StoreyModel>{
                new StoreyModel {Id="A",Height=3000},new StoreyModel {Id="B",Elevation=6000,Height=3200}}};
            model.Walls.Add(new WallModel {Id="manual-other-floor",StoreyId="A",X2=1000,Thickness=200});
            var floor=CadFloorRegistrationContext.Create(Path.Combine(Path.GetTempPath(),"plan-z-qa","model.json"),model,"B",new PointModel(),scale);
            floor.SetSourceDatum(new PointModel(100,200),new PointModel(100+10/scale,200));
            floor.RegionMin=new PointModel(90,190);floor.RegionMax=new PointModel(100+7000/scale,210);
            floor.RegionPolygon=new List<PointModel>{floor.RegionMin,new PointModel(floor.RegionMax.X,190),floor.RegionMax,new PointModel(90,210)};
            var wall=new CadBuildingProbeEntity {Handle="W",DxfName="TCH_WALL",StraightHorizontalLineVerified=true,
                CurveStart=new CadProbePoint {X=100,Y=200,Z=z/scale},CurveEnd=new CadProbePoint {X=100+6000/scale,Y=200,Z=z/scale},
                CandidateThickness=200/scale,CandidateAxisOffset=0};
            if(hasField)wall.Fields.Add(new CadProbeField {Name="Elevation",Number=z/scale});
            floor.WallCandidates.Add(wall);
            var opening=new CadBuildingProbeEntity {Handle="O",DxfName="TCH_OPENING",DisplaySegments=new List<CadProbeSegment>{
                Segment(100+1000/scale,200-100/scale,100+1000/scale,200+100/scale),
                Segment(100+1900/scale,200-100/scale,100+1900/scale,200+100/scale)}};
            var placement=CadOpeningJambPlacement.Find(floor,opening,900);
            Check(placement!=null&&Math.Abs(placement.DistanceFromWallStart-1450)<.001,"Z=0 native display did not locate on the elevated source wall.");
            opening.DisplaySegments[0].End.Z=100/scale;
            Check(CadOpeningJambPlacement.Find(floor,opening,900)==null,"Tilted display edge was accepted as a plan jamb.");opening.DisplaySegments[0].End.Z=0;
            var capture=new CadFloorPlanCapture {Floor=floor,Probe=new CadBuildingProbeDocument {DrawingFingerprint="plan-z",Entities=new List<CadBuildingProbeEntity>{wall,opening}},
                Openings=new List<CadFloorOpeningItem>{new CadFloorOpeningItem {SourceHandle="O",Code="C0915",Kind="窗",Width=900,Height=1500,Sill=800,Placement=placement}}};
            var registry=new CadFloorPlanRegistry {ModelPath=floor.ModelPath,ReplaceRegisteredConstruction=true,Floors=new List<CadFloorPlanCapture>{capture}};
            var sourceZ=wall.CurveStart.Z;
            var session=new BuildingModelEditSession(model);var source=BuildingModelJson.ToJson(session.Model);
            Check(session.TryImportCadFloors(registry,"plan-z",out var error),error);
            var built=session.Model;var imported=built.Walls.Single(w=>w.StoreyId=="B");
            Check(built.Walls.Count==2&&built.Openings.Count==1&&built.BaseElevationOf(imported)==6000&&built.HeightOf(imported)==3200
                &&imported.X1==0&&Math.Abs(imported.X2-6000)<.001&&imported.Thickness==200&&imported.BaseOffset==0,"CAD Z changed model floor height, XY or thickness.");
            Check(built.Walls.Any(w=>w.Id=="manual-other-floor")&&wall.CurveStart.Z==sourceZ,"Unregistered floor/source coordinates changed.");
            Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==source&&session.Redo(),"Projected-plan import lost atomic undo.");
            var repeat=CadFloorModelGeneration.Build(built,registry);
            Check(repeat.Walls.Count==built.Walls.Count&&repeat.Openings.Count==built.Openings.Count,"Repeated projection import duplicates elements.");
            // Unknown Elevation on a known native curtain line uses the existing
            // default wall thickness; no dimensions are inferred from its AABB.
            var curtain=registry.Clone();curtain.Floors[0].Floor.WallCandidates[0].DxfName="TCH_CURTAIN_WALL";
            curtain.Floors[0].Floor.WallCandidates[0].Fields.Clear();curtain.Floors[0].Floor.WallCandidates[0].CandidateThickness=null;
            curtain.Floors[0].Floor.WallCandidates[0].CandidateAxisOffset=null;
            Check(CadFloorModelGeneration.Build(model,curtain).Walls.Count==2,"Missing curtain elevation field suppressed a verified line.");
        }
        Console.WriteLine("CAD_PLAN_ELEVATION_OK positiveNegativeUnknown sourceZZeroDisplay mmMetres modelFloorHeight XYThickness curtainDefault tiltRejected atomicUndo repeated unregisteredUnchanged");
    }
    private static CadProbeSegment Segment(double x,double y,double x2,double y2)=>new CadProbeSegment {
        Start=new CadProbePoint {X=x,Y=y},End=new CadProbePoint {X=x2,Y=y2}};

    internal static void VerifyPrivate(string path,string output,string restoredModelPath=null)
    {
        var root=Path.GetFullPath(".artifacts")+Path.DirectorySeparatorChar;
        Check(Path.GetFullPath(path).StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Path.GetFullPath(output).StartsWith(root,StringComparison.OrdinalIgnoreCase),"Private fixture copies only.");
        var before=File.ReadAllBytes(path);var registryBytes=File.ReadAllBytes(path+".cad-floors.json");
        var model=BuildingModelJson.LoadModel(path);CadFloorPlanRegistry registry;
        using(var stream=new MemoryStream(registryBytes))registry=(CadFloorPlanRegistry)new DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).ReadObject(stream);
        if(restoredModelPath!=null)Check(string.Equals(Path.GetFullPath(restoredModelPath),Path.GetFullPath(registry.ModelPath),StringComparison.OrdinalIgnoreCase),"Restore metadata must match originally registered project.");
        registry.ModelPath=path;registry.ReplaceRegisteredConstruction=true;
        foreach(var capture in registry.Floors)
        {
            capture.Floor.ModelPath=path;
            foreach(var item in capture.Openings.Where(o=>o.Include))
            {
                if(item.Placement?.Source?.StartsWith("人工",StringComparison.Ordinal)==true||item.Placement?.Source?.StartsWith("用户选择",StringComparison.Ordinal)==true)continue;
                var source=capture.Probe.Entities.FirstOrDefault(e=>string.Equals(e.Handle,item.SourceHandle,StringComparison.OrdinalIgnoreCase));
                item.Placement=CadOpeningJambPlacement.Find(capture.Floor,source,item.Width.GetValueOrDefault());
            }
        }
        var expected=registry.Floors.Sum(c=>c.Floor.WallCandidates.Count(w=>!(c.ExcludedWallHandles??new List<string>()).Contains(w.Handle)));
        var session=new BuildingModelEditSession(model);var original=BuildingModelJson.ToJson(session.Model);
        Check(session.TryImportCadFloors(registry,Guid.NewGuid().ToString("N"),out var error),error);var built=session.Model;
        Check(built.Walls.Count==expected,"Registered walls still missing: "+string.Join(";",built.CadImport.StructureMessages));
        Check(!built.CadImport.StructureMessages.Any(m=>m.Contains("本墙自动识别已跳过")),"A registered wall was skipped.");
        Check(built.Walls.All(w=>built.BaseElevationOf(w)==built.FindStorey(w.StoreyId).Elevation&&w.Height==0),"Wall base/height not mapped to model storey.");
        var repeated=CadFloorModelGeneration.Build(built,registry);
        Check(repeated.Walls.Count==built.Walls.Count&&repeated.Openings.Count==built.Openings.Count,"Private repeated import duplicates geometry.");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original&&session.Redo(),"Private import undo failed.");
        BuildingModelJson.SaveModel(output,built);registry.ModelPath=restoredModelPath??output;
        foreach(var context in registry.Floors.Select(c=>c.Floor).Concat(registry.Datums))context.ModelPath=registry.ModelPath;
        using(var stream=File.Create(output+".cad-floors.json"))new DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).WriteObject(stream,registry);
        Check(BuildingModelJson.LoadModel(output).Walls.Count==expected,"Restored model failed save/reopen.");
        Check(before.SequenceEqual(File.ReadAllBytes(path))&&registryBytes.SequenceEqual(File.ReadAllBytes(path+".cad-floors.json")),"Private input mutated.");
        Console.WriteLine("PRIVATE_PLAN_ELEVATION_OK walls="+model.Walls.Count+"->"+built.Walls.Count+" openings="+model.Openings.Count+"->"+built.Openings.Count+" pending="+built.CadImport.PendingOpenings.Count+" slabs="+built.Slabs.Count+" repeat undo sourceUnchanged");
        foreach(var c in registry.Floors)Console.WriteLine("FLOOR "+c.Floor.Storey.Id+" walls="+built.Walls.Count(w=>w.StoreyId==c.Floor.Storey.Id)+" placements="+c.Openings.Count(o=>o.Placement!=null));
        foreach(var message in built.CadImport.StructureMessages.Concat(built.CadImport.SlabMessages))Console.WriteLine("NOTICE "+message);
    }
}
