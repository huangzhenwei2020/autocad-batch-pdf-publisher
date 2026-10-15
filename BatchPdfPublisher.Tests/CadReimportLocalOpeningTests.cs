using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

internal static class CadReimportLocalOpeningTests
{
    private static void Check(bool value,string message) { if(!value)throw new Exception(message); }
    private static string OpeningJson(OpeningModel value) => BuildingModelJson.ToJson(new BuildingModelDocument { Openings=new List<OpeningModel>{value} });

    public static void Run()
    {
        var model=BuildingModelProjectWorkflow.Empty("Local opening reimport QA");
        model.Storeys.Add(new StoreyModel { Id="2F",Name="2F",Elevation=3300,Height=3300,TemplateStoreyId="1F" });
        var path=System.IO.Path.GetFullPath(".artifacts/local-opening-reimport/model.json");
        var floor=CadFloorRegistrationContext.Create(path,model,"1F",new PointModel(0,0),1);
        floor.SetSourceDatum(new PointModel(0,0),new PointModel(10,0));
        floor.RegionMin=new PointModel(-10000,-10000);floor.RegionMax=new PointModel(20000,10000);
        floor.RegionPolygon=new List<PointModel> {new PointModel(-10000,-10000),new PointModel(20000,-10000),new PointModel(20000,10000),new PointModel(-10000,10000)};
        var host=new CadBuildingProbeEntity { Handle="host",DxfName="TCH_WALL",StraightHorizontalLineVerified=true,
            CurveStart=new CadProbePoint(),CurveEnd=new CadProbePoint {X=10000},CandidateThickness=200,CandidateAxisOffset=0,
            Fields=new List<CadProbeField> {new CadProbeField {Name="Elevation",Number=0}} };
        floor.WallCandidates.Add(host);
        const double nativeWidth=7660.996575;
        var source=new CadFloorOpeningItem {SourceHandle="opening",Code="MLC01C",Kind="门联窗",Width=nativeWidth,Height=3000,Sill=0,
            Placement=CadOpeningPlacement.Create(floor,"host",new PointModel(),new PointModel(10000,0),
                new PointModel(5000-nativeWidth/2,0),new PointModel(5000+nativeWidth/2,0),nativeWidth)};
        var capture=new CadFloorPlanCapture {Floor=floor,Openings=new List<CadFloorOpeningItem>{source},
            Probe=new CadBuildingProbeDocument {DrawingFingerprint="local-opening-qa",Entities=new List<CadBuildingProbeEntity>{host}}};
        var registry=new CadFloorPlanRegistry {ModelPath=path,Floors=new List<CadFloorPlanCapture>{capture}};
        var initial=CadFloorModelGeneration.Build(model,registry);
        var edited=initial.Openings.Single();edited.Width=7660;edited.Code="MLC01CA";edited.PlanOpenAngle=45;
        edited.PlanFlipNormal=true;edited.ThresholdHeight=20;edited.PlanLabelAlong=80;
        initial.OpeningTypes.Add(new OpeningTypeModel {Code=edited.Code,Kind="门联窗",Width=7660,Height=3000,
            CustomColumnWidths="1915,750,750,750,750,2745",CustomRowHeights="800,2200",OpeningMode="平开"});
        var expected=OpeningJson(edited);
        var arc=CadCurvedWallRegistrationTests.Arc("new-arc",new PointModel(14000,0),new PointModel(10000,4000),Math.Tan(Math.PI/8));
        floor.WallCandidates.Add(arc);capture.Probe.Entities.Add(arc);
        var session=new BuildingModelEditSession(initial);var before=BuildingModelJson.ToJson(session.Model);
        Check(session.TryImportCadFloors(registry,"add-arc",out var error),error);
        var updated=session.Model;
        Check(updated.Walls.Count==2 && updated.Walls.Count(w=>Math.Abs(w.Bulge)>1e-10)==1,"Local dimensions blocked the unrelated new arc.");
        Check(OpeningJson(updated.Openings.Single())==expected && updated.OpeningTypes.Single().CustomRowHeights=="800,2200","Edited code, dimensions, layout or instance settings were overwritten.");
        Check(updated.CadImport.Openings.Single().Width==nativeWidth && updated.CadImport.StructureMessages.Any(m=>m.Contains("MLC01CA")),"Local override must retain its separate CAD baseline and warning.");
        Check(session.Undo() && BuildingModelJson.ToJson(session.Model)==before && session.Redo(),"Selective reimport must undo and redo in one step.");
        var reopened=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        var repeated=CadFloorModelGeneration.Build(reopened,registry);
        Check(repeated.Walls.Count==2 && OpeningJson(repeated.Openings.Single())==expected,"Repeated import or save reset the local override.");
        Check(StandardStoreyLayout.Materialize(repeated).Walls.Count==4,"Standard-storey arc instances lost.");
        var deleted=BuildingModelJson.FromJson(BuildingModelJson.ToJson(initial));deleted.Openings.Clear();
        var afterDelete=CadFloorModelGeneration.Build(deleted,registry);
        Check(afterDelete.Walls.Count==2 && afterDelete.Openings.Count==0 && afterDelete.CadImport.PendingOpenings.Count==0,"Deleted opening resurrected or blocked the new arc.");
        Check(CadFloorModelGeneration.Build(afterDelete,registry).Openings.Count==0,"Deleted opening tombstone was lost on repeat.");
        var shortened=registry.Clone();shortened.Floors[0].Floor.WallCandidates.Single(w=>w.Handle=="host").CurveEnd.X=5000;
        var retainedHost=CadFloorModelGeneration.Build(reopened,shortened);
        Check(retainedHost.Walls.Count==2 && retainedHost.Walls.Single(w=>w.Id==edited.HostWallId).X2==10000
            && OpeningJson(retainedHost.Openings.Single())==expected,"Source host changes moved or invalidated a preserved opening.");
        var removedHost=registry.Clone();removedHost.Floors[0].Floor.WallCandidates.RemoveAll(w=>w.Handle=="host");
        var retained=CadFloorModelGeneration.Build(reopened,removedHost);
        Check(retained.Walls.Count==2 && OpeningJson(retained.Openings.Single())==expected,"Missing source host deleted the local opening or blocked other construction.");
        Console.WriteLine("PASS selective CAD reimport localOpening 7660round suffix layout angle threshold hostDependency unrelatedArc repeated save standardFloors deletedTombstone oneUndo");
        Replacement();
    }
    private static void Replacement()
    {
        var model=BuildingModelProjectWorkflow.Empty("Replacement QA");
        model.Storeys.Add(new StoreyModel {Id="2F",Name="2F",Height=3300,Elevation=3300,TemplateStoreyId="1F"});
        model.Storeys.Add(new StoreyModel {Id="3F",Name="3F",Height=3300,Elevation=6600});
        var floor=CadFloorRegistrationContext.Create(System.IO.Path.GetFullPath(".artifacts/replacement-qa/model.json"),model,"1F",new PointModel(),1);
        floor.SetSourceDatum(new PointModel(),new PointModel(10,0));
        floor.RegionMin=new PointModel(-100,-100);floor.RegionMax=new PointModel(10100,10000);
        floor.RegionPolygon=new List<PointModel> {new PointModel(-100,-100),new PointModel(10100,-100),new PointModel(10100,10000),new PointModel(-100,10000)};
        var host=new CadBuildingProbeEntity {Handle="host",StraightHorizontalLineVerified=true,DxfName="TCH_WALL",
            CurveStart=new CadProbePoint(),CurveEnd=new CadProbePoint {X=10000},CandidateThickness=200,CandidateAxisOffset=0,
            Fields=new List<CadProbeField> {new CadProbeField {Name="Elevation",Number=0}}};
        floor.WallCandidates.Add(host);
        var source=new CadFloorOpeningItem {SourceHandle="opening",Code="M0921",Kind="门",Width=900,Height=2100,
            Placement=CadOpeningPlacement.Create(floor,"host",new PointModel(),new PointModel(10000,0),new PointModel(2050,0),new PointModel(2950,0),900)};
        var registry=new CadFloorPlanRegistry {ModelPath=floor.ModelPath,Floors=new List<CadFloorPlanCapture> {
            new CadFloorPlanCapture {Floor=floor,Openings=new List<CadFloorOpeningItem>{source},Probe=new CadBuildingProbeDocument {
                DrawingFingerprint="replacement",Entities=new List<CadBuildingProbeEntity>{host}}}}};
        var initial=CadFloorModelGeneration.Build(model,registry);
        var old=initial.Openings.Single();old.Offset=5000;old.PlanOpenAngle=30;old.OpenIn3D=true;old.ThresholdHeight=20;
        var type=OpeningConstruction.Default(old);type.Material="edited-material";type.PanelThickness=60;
        initial.OpeningTypes.Add(type);initial.OpeningTemplates.Add(OpeningConstruction.Copy(type));
        initial.Walls.Single().X2=6000;
        initial.Walls.Add(new WallModel {Id="manual",StoreyId="1F",X2=500,Thickness=200});
        initial.Slabs.Add(new SlabModel {Id="manual-slab",StoreyId="1F",Outline=new List<PointModel>{new PointModel(),new PointModel(500,0),new PointModel(0,500)}});
        initial.Columns.Add(new ColumnModel {Id="manual-column",StoreyId="1F",Width=300,Depth=300});
        initial.Beams.Add(new BeamModel {Id="manual-beam",StoreyId="1F",X2=500,Width=200,Depth=300});
        initial.Walls.Add(new WallModel {Id="untouched",StoreyId="3F",X2=5000,Thickness=200});
        initial.Openings.Add(new OpeningModel {Id="untouched-opening",HostWallId="untouched",Code=old.Code,Kind=old.Kind,Width=900,Height=2100,Offset=2500});
        initial.CadImport.Walls.Add(new WallModel {Id="untouched",StoreyId="3F",X2=5000,Thickness=200});
        initial.CadImport.Openings.Add(initial.Openings.Last());
        var before=BuildingModelJson.ToJson(initial);var untouched=OpeningJson(initial.Openings.Last());
        registry.ReplaceRegisteredConstruction=true;
        Check(registry.Clone().ReplaceRegisteredConstruction,"Replacement policy lost in serialization.");
        var session=new BuildingModelEditSession(initial);
        before=BuildingModelJson.ToJson(session.Model);
        Check(session.TryImportCadFloors(registry,"replace",out var error),error);
        var result=session.Model;var updated=result.Openings.Single(o=>o.Id==old.Id);
        Check(result.Walls.Count==2 && result.Walls.Single(w=>w.StoreyId=="1F").X2==10000 && result.Slabs.Count==0
            && result.Columns.Count==0 && result.Beams.Count==0,"Replacement retained edited/manual construction or blocked on baseline.");
        Check(updated.Offset==2500 && updated.Width==900 && updated.PlanOpenAngle==30 && updated.OpenIn3D==true && updated.ThresholdHeight==20,
            "Matching opening did not keep settings while taking fresh CAD placement.");
        Check(OpeningConstruction.Resolve(result,updated).Material=="edited-material" && result.OpeningTemplates.Count==1,"Matching layout/material or global template lost.");
        Check(OpeningJson(result.Openings.Single(o=>o.Id=="untouched-opening"))==untouched && result.CadImport.Walls.Count==2,"Unregistered floor or its baseline changed.");
        var physical=StandardStoreyLayout.Materialize(result);
        Check(OpeningConstruction.Resolve(physical,physical.Openings.Single(o=>o.Id.EndsWith("@STD@2F"))).Material=="edited-material","Standard-floor construction definition lost.");
        Check(session.Undo() && BuildingModelJson.ToJson(session.Model)==before && session.Redo(),"Replacement is not one atomic undo.");
        var reopened=BuildingModelJson.FromJson(BuildingModelJson.ToJson(result));
        var repeated=CadFloorModelGeneration.Build(reopened,registry);
        Check(repeated.Walls.Count==2 && repeated.Openings.Count==2 && repeated.OpeningOverrides.Count==result.OpeningOverrides.Count,"Repeat appended construction/overrides.");
        var changed=registry.Clone();var item=changed.Floors[0].Openings[0];item.Width=1200;
        item.Placement=CadOpeningPlacement.Create(changed.Floors[0].Floor,"host",new PointModel(),new PointModel(10000,0),new PointModel(1900,0),new PointModel(3100,0),1200);
        var reset=CadFloorModelGeneration.Build(result,changed);var resetOpening=reset.Openings.Single(o=>o.Id==old.Id);
        Check(OpeningConstruction.Resolve(reset,resetOpening).Material!="edited-material" && resetOpening.OpenIn3D!=true && resetOpening.ThresholdHeight==0,"Changed aperture inherited edited construction.");
        Check(OpeningConstruction.Resolve(reset,reset.Openings.Single(o=>o.Id=="untouched-opening")).Material=="edited-material","Reset affected the shared type on an unregistered floor.");
        var resetPhysical=StandardStoreyLayout.Materialize(reset);
        Check(OpeningConstruction.Resolve(resetPhysical,resetPhysical.Openings.Single(o=>o.Id.EndsWith("@STD@2F"))).Material!="edited-material","Standard-floor reset definition lost.");
        changed=registry.Clone();changed.Floors[0].Openings[0].Code="M0921A";
        reset=CadFloorModelGeneration.Build(result,changed);
        Check(OpeningConstruction.Resolve(reset,reset.Openings.Single(o=>o.Id==old.Id)).Material!="edited-material","Changed visible code inherited old edits.");
        changed=registry.Clone();changed.Floors[0].Openings[0].SourceHandle="new-source";
        reset=CadFloorModelGeneration.Build(result,changed);
        Check(OpeningConstruction.Resolve(reset,reset.Openings.Single(o=>o.HostWallId!= "untouched")).Material=="edited-material","Unique matching code/size failed after CAD handle changed.");
        changed=registry.Clone();changed.GenerationWarning="read failed";
        reset=CadFloorModelGeneration.Build(initial,changed);
        Check(reset.Walls.Count==initial.Walls.Count && reset.Walls.First().X2==6000,"Failed replacement damaged original construction.");
        changed=registry.Clone();changed.Floors[0].Floor.WallCandidates.Clear();
        reset=CadFloorModelGeneration.Build(initial,changed);
        Check(reset.Walls.Count==1 && reset.Openings.Count==1 && reset.CadImport.PendingOpenings.Count==1,"Explicit reference-only replacement retained old floor entities or blocked.");
        var editSession=new BuildingModelEditSession(result);var draft=OpeningConstruction.Copy(OpeningConstruction.Resolve(result,updated));draft.Material="new-material";
        Check(editSession.TrySetOpeningConstruction(updated.Id,draft,false,out error),error);
        Check(OpeningConstruction.Resolve(editSession.Model,editSession.Model.Openings.First()).Material=="new-material","Reimport snapshot prevented subsequent group editing.");
        Console.WriteLine("PASS CAD replacement manual/edited construction sameCodeSize preserve resetPlacement mismatch isolation standardFloors handles persistence repeated oneUndo referenceOnly rollback subsequentEditing");
    }
}
