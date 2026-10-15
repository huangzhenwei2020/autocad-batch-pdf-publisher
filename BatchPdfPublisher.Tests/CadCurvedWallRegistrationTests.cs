using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

internal static class CadCurvedWallRegistrationTests
{
    private static void Check(bool value,string message) { if(!value)throw new Exception(message); }

    public static void Run()
    {
        foreach(var bulge in new[]{Math.Tan(Math.PI/8),-Math.Tan(Math.PI/8),1d,-1d,Math.Tan(3*Math.PI/8)})
        {
            var source=Arc("arc",new PointModel(4000,0),new PointModel(0,4000),bulge);
            Check(CadBuildingProbeRules.VerifyCircularWall(source) && Math.Abs(source.CurveBulge-bulge)<1e-8,"Signed minor/major arc verification failed.");
            var model=Model();var registry=Registry(model,source);
            var result=CadFloorModelGeneration.Build(model,registry);
            Check(result.Walls.Count==1 && Math.Abs(result.Walls[0].Bulge-bulge)<1e-8 && model.Walls.Count==0,"CAD arc was skipped or flattened.");
            var view=OrthographicProjector.Project(result,new ViewDefinitionModel { Id="plan",Kind=ViewKind.Plan,StoreyIds=new List<string>{"1F"},Scale=100 });
            Check(view.Lines.Any(l=>Math.Abs(l.Bulge)>1e-10),"Registered wall plan lost analytic arcs.");
            var volume=BuildingVolumeBuilder.Build(result);
            Check(volume.Faces.Any(m=>m.ElementId==result.Walls[0].Id && m.SmoothSurfaceId!=null),"Registered arc has no 3D geometry.");
            var rebuilt=CadFloorModelGeneration.Build(result,registry);
            Check(rebuilt.Walls.Count==1 && rebuilt.Walls[0].Id==result.Walls[0].Id && rebuilt.Walls[0].Bulge==result.Walls[0].Bulge,"Arc reimport changed identity or duplicated walls.");
            var session=new BuildingModelEditSession(model);
            Check(session.TryImportCadFloors(registry,"arc-undo",out var error),error);
            Check(session.Model.Walls.Count==1 && session.Undo() && session.Model.Walls.Count==0 && session.Redo(),"Registered arcs must undo as one operation.");
            var reopened=BuildingModelJson.FromJson(BuildingModelJson.ToJson(result));
            Check(reopened.Walls[0].Bulge==result.Walls[0].Bulge && StandardStoreyLayout.Materialize(reopened).Walls.Count==2,"Arc persistence or standard-storey expansion failed.");
        }
        var metre=Arc("metre",new PointModel(104,200),new PointModel(100,204),Math.Tan(Math.PI/8));
        metre.CandidateThickness=.2;metre.CandidateAxisOffset=.03;
        var units=Registry(Model(),metre);units.Floors[0].Floor.Alignment.MillimetresPerCadUnit=1000;
        units.Floors[0].Floor.SetSourceDatum(new PointModel(100,200),new PointModel(100,201));
        var converted=CadFloorModelGeneration.Build(Model(),units).Walls.Single();
        Check(Math.Abs(CurveGeometry.ForWall(converted).Radius-4000)<.001 && Math.Abs(converted.Thickness-200)<.001
            && Math.Abs(converted.AxisOffset.Value-30)<.001 && Math.Abs(converted.X1)<.001 && Math.Abs(converted.Y1+4000)<.001,
            "Rotated source datum, units or asymmetric wall thickness were lost.");
        foreach(var invalid in new[]{"ellipse","tilt","length","full","missing"})
        {
            var source=Arc("bad",new PointModel(4000,0),new PointModel(0,4000),Math.Tan(Math.PI/8));
            if(invalid=="ellipse")source.CurveSamples[2].X+=50;
            if(invalid=="tilt")source.CurveSamples[2].Z=50;
            if(invalid=="length")source.CurveLength+=50;
            if(invalid=="full")source.CurveEnd=source.CurveStart;
            if(invalid=="missing")source.CurveSamples.Clear();
            Check(!CadBuildingProbeRules.VerifyCircularWall(source),"Unsupported curve accepted as circular: "+invalid);
            var rejected=CadFloorModelGeneration.Build(Model(),Registry(Model(),source));
            Check(rejected.Walls.Count==0 && rejected.CadImport.StructureMessages.Count>0,"Unsupported curve must warn without blocking the model.");
        }
        var perimeter=new List<CadBuildingProbeEntity> {
            Straight("bottom",0,0,4000,0),Arc("round",new PointModel(4000,0),new PointModel(0,4000),Math.Tan(Math.PI/8)),
            Straight("left",0,4000,0,0) };
        var enclosure=Registry(Model(),perimeter.ToArray());
        var enclosed=CadFloorModelGeneration.Build(Model(),enclosure);
        Check(enclosed.Walls.Count==3 && enclosed.Slabs.Count==1,"Rounded enclosure failed to generate its slab: "+string.Join(";",enclosed.CadImport.StructureMessages.Concat(enclosed.CadImport.SlabMessages)));
        var slab=enclosed.Slabs.Single();
        Check(slab.Outline.Any(p=>Math.Abs(p.Bulge)>1e-10) && SlabGeometry.Validate(slab)==null,"Rounded slab flattened or invalid.");
        Check(!SlabGeometry.Build(slab).Triangles.Any(t=>CadFloorSlabGeneration.Inside(new PointModel(3500,3500),t)),"Rounded slab became a rectangular bounding box.");
        BuildingModelJson.SaveModel(System.IO.Path.GetFullPath(".artifacts/cad-arc-registered-ui/建筑模型/ArcModel/model.json"),enclosed);
        foreach(var reverse in new[]{false,true})
        {
            var halfArc=Arc("half",new PointModel(reverse?-4000:4000,0),new PointModel(reverse?4000:-4000,0),reverse?-1:1);
            halfArc.CandidateAxisOffset=reverse?-30:30;
            var halfBase=Straight("base",reverse?4000:-4000,0,reverse?-4000:4000,0);
            var half=CadFloorModelGeneration.Build(Model(),Registry(Model(),halfArc,halfBase));
            Check(half.Walls.Count==2 && half.Slabs.Count==1 && half.Slabs[0].Outline.Any(p=>Math.Abs(p.Bulge)>1e-10)
                && SlabGeometry.Validate(half.Slabs[0])==null,"Semicircular or reversed enclosure lost its slab.");
            var boundaryArcs=half.Slabs[0].Outline.Select((p,i)=>new CurveGeometry(p,half.Slabs[0].Outline[(i+1)%half.Slabs[0].Outline.Count],p.Bulge)).Where(c=>c.IsArc).ToList();
            Check(boundaryArcs.Any(c=>Math.Abs(c.Radius-3870)<.5),"Offset arc slab boundary is not on the native inner wall face.");
        }
        var arcWall=enclosure.Floors[0].Floor.WallCandidates.Single(w=>w.Handle=="round");
        try { CadOpeningPlacement.Create(enclosure.Floors[0].Floor,"round",new PointModel(4000,0),new PointModel(0,4000),new PointModel(3000,1000),new PointModel(2000,2000),1414);throw new Exception("Arc aperture accepted straight-wall placement."); }
        catch(System.IO.InvalidDataException) { }
        Console.WriteLine("PASS CAD arc registration signedMinorMajor equalLengthStations rejectNonCircular planar units rotation asymThickness modelPlan3D standardFloors identity undo curvedSlab safeOpening");
    }

    private static BuildingModelDocument Model() => new BuildingModelDocument { Name="CAD arc QA",Storeys=new List<StoreyModel> {
        new StoreyModel { Id="1F",Name="1F",Height=3000 },new StoreyModel { Id="2F",Name="2F",Height=3200,Elevation=3000,TemplateStoreyId="1F" } } };

    private static CadFloorPlanRegistry Registry(BuildingModelDocument model,params CadBuildingProbeEntity[] walls)
    {
        var path=System.IO.Path.GetFullPath(".artifacts/arc-registration-fixture/model.json");
        var floor=CadFloorRegistrationContext.Create(path,model,"1F",new PointModel(0,0),1);
        floor.SetSourceDatum(new PointModel(0,0),new PointModel(10,0));
        floor.RegionMin=new PointModel(-10000,-10000);floor.RegionMax=new PointModel(10000,10000);
        floor.RegionPolygon=new List<PointModel> {new PointModel(-10000,-10000),new PointModel(10000,-10000),new PointModel(10000,10000),new PointModel(-10000,10000)};
        floor.WallCandidates=walls.ToList();
        return new CadFloorPlanRegistry { ModelPath=path,Floors=new List<CadFloorPlanCapture> {
            new CadFloorPlanCapture { Floor=floor,Probe=new CadBuildingProbeDocument { DrawingFingerprint="arc-qa",Entities=walls.ToList() } } } };
    }

    internal static CadBuildingProbeEntity Arc(string handle,PointModel a,PointModel b,double bulge)
    {
        var curve=new CurveGeometry(a,b,bulge);
        var source=new CadBuildingProbeEntity { Handle=handle,DxfName="TCH_WALL",CandidateThickness=200,CandidateAxisOffset=0,
            CurveStart=new CadProbePoint { X=a.X,Y=a.Y },CurveEnd=new CadProbePoint { X=b.X,Y=b.Y },CurveLength=curve.Length,
            Fields=new List<CadProbeField> { new CadProbeField { Name="Elevation",Number=0 } } };
        source.CurveSamples=Enumerable.Range(0,9).Select(i=>{var p=curve.At(i/8d);return new CadProbePoint { X=p.X,Y=p.Y };}).ToList();
        CadBuildingProbeRules.VerifyCircularWall(source);return source;
    }

    private static CadBuildingProbeEntity Straight(string handle,double x1,double y1,double x2,double y2)
        => new CadBuildingProbeEntity { Handle=handle,DxfName="TCH_WALL",StraightHorizontalLineVerified=true,CandidateThickness=200,CandidateAxisOffset=0,
            CurveStart=new CadProbePoint { X=x1,Y=y1 },CurveEnd=new CadProbePoint { X=x2,Y=y2 },Fields=new List<CadProbeField> {new CadProbeField {Name="Elevation",Number=0}} };
}
