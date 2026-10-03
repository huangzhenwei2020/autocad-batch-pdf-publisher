using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

internal static class CadFloorSlabGenerationTests
{
    private static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    private static List<PointModel> Ring(params double[] values)=>Enumerable.Range(0,values.Length/2).Select(i=>new PointModel(values[i*2],values[i*2+1])).ToList();
    private static List<WallModel> Walls(List<PointModel> ring,double offset=0)=>ring.Select((p,i)=>new WallModel {
        Id="w"+i,StoreyId="1F",X1=p.X,Y1=p.Y,X2=ring[(i+1)%ring.Count].X,Y2=ring[(i+1)%ring.Count].Y,AxisOffset=offset }).ToList();
    private static CadRoomLabel Label(string name,double x,double y)=>new CadRoomLabel { Name=name,Position=new PointModel(x,y) };
    private static List<SlabModel> Build(List<WallModel> walls,params CadRoomLabel[] labels)=>CadFloorSlabGeneration.Build(walls,labels,100,"1F",new List<string>());
    public static void Run()
    {
        var walls=Walls(Ring(0,0,10000,0,10000,8000,0,8000));
        walls.Add(new WallModel { Id="divider",StoreyId="1F",X1=3000,Y1=0,X2=3000,Y2=8000 });
        var shaft=Walls(Ring(6000,2000,8000,2000,8000,6000,6000,6000));
        foreach(var w in shaft)w.Id="shaft-"+w.Id;walls.AddRange(shaft);
        var slabs=Build(walls,Label("楼梯间",7000,4000),Label("楼梯间",7100,4100));
        Check(slabs.Count==1 && slabs[0].Openings.Count==1,"Duplicate room labels must cut one hole.");
        Check(slabs[0].Outline.Min(p=>p.X)==100 && slabs[0].Outline.Max(p=>p.X)==9900,"Slab must stop at the inner exterior-wall face.");
        Check(Math.Abs(Math.Abs(CadFloorSlabGeneration.Area(slabs[0].Openings[0].Outline))-2200*4200)<.01,"Void must clear its surrounding wall footprints, not only the room's net area.");
        var area=SlabGeometry.Build(slabs[0]).Triangles.Sum(t=>Math.Abs(CadFloorSlabGeneration.Area(t)));
        Check(Math.Abs(area-(9800d*7800-2200d*4200))<.01,"Triangulation must leave the staircase and its wall tops empty.");
        Check(!SlabGeometry.Build(slabs[0]).Triangles.Any(t=>CadFloorSlabGeneration.Inside(new PointModel(6000,4000),t)),
            "A slab must not overlap a wall bordering an internal shaft opening.");
        var varyingShaft=walls.Select(w=>new WallModel { Id=w.Id,StoreyId=w.StoreyId,X1=w.X1,Y1=w.Y1,X2=w.X2,Y2=w.Y2,Thickness=w.Thickness,AxisOffset=w.AxisOffset }).ToList();
        foreach(var pair in new[] { Tuple.Create("shaft-w0",100d),Tuple.Create("shaft-w1",300d),Tuple.Create("shaft-w2",400d),Tuple.Create("shaft-w3",200d) })
            varyingShaft.Single(w=>w.Id==pair.Item1).Thickness=pair.Item2;
        var varyingHole=Build(varyingShaft,Label("电梯井",7000,4000)).Single().Openings.Single().Outline;
        Check(varyingHole.Min(p=>p.X)==5900 && varyingHole.Max(p=>p.X)==8150 && varyingHole.Min(p=>p.Y)==1950 && varyingHole.Max(p=>p.Y)==6200,
            "Each void edge must clear that surrounding wall's own thickness.");
        var mixed=Walls(Ring(0,0,10000,0,10000,8000,0,8000));
        mixed[0].Thickness=300;mixed[1].Thickness=400;mixed[2].Thickness=500;mixed[3].Thickness=600;
        var mixedSlab=Build(mixed).Single();
        Check(mixedSlab.Outline.Min(p=>p.X)==300 && mixedSlab.Outline.Max(p=>p.X)==9800
            && mixedSlab.Outline.Min(p=>p.Y)==150 && mixedSlab.Outline.Max(p=>p.Y)==7750,
            "Each slab edge must use that exterior wall's actual thickness.");
        var edgeWalls=Walls(Ring(0,0,10000,0,10000,8000,0,8000));
        edgeWalls.Add(new WallModel { Id="stair-left",X1=6000,Y1=0,X2=6000,Y2=4000 });
        edgeWalls.Add(new WallModel { Id="stair-top",X1=6000,Y1=4000,X2=8000,Y2=4000 });
        edgeWalls.Add(new WallModel { Id="stair-right",X1=8000,Y1=4000,X2=8000,Y2=0 });
        var edgeSlab=Build(edgeWalls,Label("楼梯间",7000,2000)).Single();
        Check(edgeSlab.Openings.Count==0 && SlabGeometry.Validate(edgeSlab)==null
            && !SlabGeometry.Build(edgeSlab).Triangles.Any(t=>CadFloorSlabGeneration.Inside(new PointModel(7000,2000),t)),
            "A staircase touching the slab perimeter must become an empty notch with a valid simple outline.");
        foreach(var keyword in new[] { "电梯厅","电梯间","管井","风井","采光井" })
            Check(Build(walls,Label(keyword,7000,4000))[0].Openings.Count==1,"Keyword void was lost: "+keyword);
        Check(Build(walls,Label("办公室",1000,4000))[0].Openings.Count==0,"An ordinary room must have a slab.");
        var misleading=Label("楼梯间",1000,4000);misleading.AreaSquareMetres=5;
        Check(Build(walls,misleading)[0].Openings.Count==0,"A room name in a larger connected space must not remove the entire floor.");
        var messages=new List<string>();
        var noMatch=CadFloorSlabGeneration.Build(walls,new[] { Label("电梯间",20000,4000) },100,"1F",messages);
        Check(noMatch[0].Openings.Count==0 && messages.Count==1,"Missing boundary must report the room, without cutting guessed geometry.");
        Check(Build(walls.Take(3).ToList()).Count==0,"Open exterior walls must not produce a bounding-box slab.");
        var concave=Build(Walls(Ring(0,0,8000,0,8000,3000,3000,3000,3000,8000,0,8000)))[0];
        Check(!CadFloorSlabGeneration.Inside(new PointModel(6000,6000),concave.Outline),"Concave exterior must not become a convex hull.");
        var disconnected=Walls(Ring(0,0,4000,0,4000,4000,0,4000));
        disconnected.AddRange(Walls(Ring(10000,0,14000,0,14000,4000,10000,4000)));
        Check(Build(disconnected).Count==2,"Disjoint closed buildings must retain separate slab contours.");
        foreach(var offset in new[] { -100d,100d }) {
            var biased=Build(Walls(Ring(0,0,6000,0,6000,4000,0,4000),offset));
            Check(biased.Count==1 && SlabGeometry.Validate(biased[0])==null,"Face-aligned walls must join without corner leaks.");
        }
        var rotated=walls.Select(w=>new WallModel { Id=w.Id,Thickness=w.Thickness,AxisOffset=w.AxisOffset,
            X1=1e9+(w.X1-w.Y1)/Math.Sqrt(2),Y1=-1e9+(w.X1+w.Y1)/Math.Sqrt(2),
            X2=1e9+(w.X2-w.Y2)/Math.Sqrt(2),Y2=-1e9+(w.X2+w.Y2)/Math.Sqrt(2) }).ToList();
        var transformed=Label("楼梯间",1e9+(7000-4000)/Math.Sqrt(2),-1e9+(7000+4000)/Math.Sqrt(2));
        var rotatedSlab=Build(rotated,transformed).Single();
        Check(rotatedSlab.Openings.Count==1 && Math.Abs(Math.Abs(CadFloorSlabGeneration.Area(rotatedSlab.Outline))-9800d*7800)<100,"Oblique walls and large source coordinates must preserve area and holes.");
        Check(Math.Abs(Math.Abs(CadFloorSlabGeneration.Area(rotatedSlab.Openings[0].Outline))-2200d*4200)<100,
            "Oblique void walls must be cleared through their full thickness as well.");
        NativeCoordinateNoise();
        Integration();
        Console.WriteLine("PASS: CAD automatic slabs, concave/oblique/offset walls, named room voids, default thickness, reference floors and safe reimport.");
    }
    private static void NativeCoordinateNoise()
    {
        // Anonymised native wall coordinates: adjoining faces differ by 0.001 mm.
        // Previously the generated perimeter folded back over itself and blocked import.
        var model=BuildingModelJson.LoadModel(Path.Combine(AppContext.BaseDirectory,"Fixtures","SlabNativeCoordinateNoise.json"));
        foreach(var shift in new[] { 0d,1e9 }) {
            var walls=model.Walls.Select(w=>new WallModel { Id=w.Id,StoreyId="1F",Thickness=w.Thickness,AxisOffset=w.AxisOffset,
                X1=w.X1+shift,Y1=w.Y1-shift,X2=w.X2+shift,Y2=w.Y2-shift }).ToList();
            var slabs=Build(walls);
            Check(slabs.Count==1 && SlabGeometry.Validate(slabs[0])==null,"Native coordinate noise must not produce crossing slab boundaries.");
            var geometry=SlabGeometry.Build(slabs[0]);
            var area=geometry.Triangles.Sum(t=>Math.Abs(CadFloorSlabGeneration.Area(t)));
            Check(Math.Abs(area-186800000)<100,"Topology cleanup must preserve the native floor area before named-room voids: "+area);
        }
    }
    private static void Integration()
    {
        var folder=Path.GetFullPath(".artifacts/cad-auto-slabs/test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var model=new BuildingModelDocument { Name="自动楼板",Storeys=new List<StoreyModel> {
            new StoreyModel { Id="1F",Name="一层",Height=3000 },new StoreyModel { Id="2F",Name="二层",Elevation=3000,Height=4500,TemplateStoreyId="1F" } } };
        var path=Path.Combine(folder,"model.json");BuildingModelJson.SaveModel(path,model);
        var context=CadFloorRegistrationContext.Create(path,model,"1F",new PointModel(0,0),1000);
        context.SetSourceDatum(new PointModel(10,20),new PointModel(11,20));
        context.RegionMin=new PointModel(9,19);context.RegionMax=new PointModel(17,25);
        context.RegionPolygon=Ring(9,19,17,19,17,25,9,25);
        var sourceWalls=Walls(Ring(10,20,16,20,16,24,10,24));
        var sourceShaft=Walls(Ring(12,21,14,21,14,23,12,23));foreach(var w in sourceShaft)w.Id="shaft-"+w.Id;
        sourceWalls.AddRange(sourceShaft);
        context.WallCandidates=sourceWalls.Select(w=>new CadBuildingProbeEntity {
            Handle=w.Id,DxfName="TCH_WALL",StraightHorizontalLineVerified=true,
            CurveStart=new CadProbePoint { X=w.X1,Y=w.Y1 },CurveEnd=new CadProbePoint { X=w.X2,Y=w.Y2 },
            Fields=new List<CadProbeField> { new CadProbeField { Name="Elevation",Number=0 } } }).ToList();
        var capture=new CadFloorPlanCapture { Floor=context,Probe=new CadBuildingProbeDocument { DrawingFingerprint="slab-fixture",
            RoomLabels=new List<CadRoomLabel> { Label("电梯井",13,22) } } };
        var registry=new CadFloorPlanRegistry { ModelPath=path,Floors=new List<CadFloorPlanCapture> { capture } };
        registry.SaveFloor(capture);
        var generated=CadFloorModelGeneration.Build(model,registry);
        Check(generated.Slabs.Count==1 && generated.Slabs[0].Thickness==100 && generated.Walls.All(w=>w.Thickness==200),"Default slab/wall thickness must be millimetres, regardless of CAD units.");
        Check(generated.Slabs[0].Openings.Count==1 && generated.Slabs[0].TopOffset==0 && generated.TopElevationOf(generated.Slabs[0])==3000,"Native rooms must align and the slab top must cap the wall.");
        var volume=BuildingVolumeBuilder.Build(generated);
        Check(volume.Faces.Any(f=>f.Kind=="slab" && f.StoreyId=="2F"),"Standard reference floors must reuse the slab and holes.");
        var expanded=StandardStoreyLayout.Materialize(generated);
        Check(expanded.TopElevationOf(expanded.Slabs.Single(s=>s.StoreyId=="2F"))==7500,
            "A reference slab must cap its own floor height, rather than reuse the source floor height.");
        var resize=new BuildingModelEditSession(generated);string resizeError;
        Check(resize.TryReplaceStoreys(new[] { new StoreyModel { Id="1F",Name="一层",Elevation=100,Height=4200 },
            new StoreyModel { Id="2F",Name="二层",Elevation=4300,Height=5000,TemplateStoreyId="1F" } },out resizeError),resizeError);
        var resized=StandardStoreyLayout.Materialize(resize.Model);
        Check(resized.TopElevationOf(resized.Slabs.Single(s=>s.StoreyId=="1F"))==4300
            && resized.TopElevationOf(resized.Slabs.Single(s=>s.StoreyId=="2F"))==9300,
            "Changing storey elevations/heights must keep all generated slab tops at their wall tops.");
        Check(CadFloorModelGeneration.Build(resize.Model,registry).TopElevationOf(resize.Model.Slabs.Single())==4300,
            "Changing floor settings must not block reimport of automatic slabs.");
        var absolute=BuildingModelJson.FromJson(BuildingModelJson.ToJson(generated));
        absolute.Slabs[0].TopOffset=null;absolute.CadImport.Slabs[0].TopOffset=null;
        var absoluteEdit=new BuildingModelEditSession(absolute);
        Check(absoluteEdit.TryReplaceStoreys(new[] { new StoreyModel { Id="1F",Name="一层",Elevation=100,Height=4200 },
            new StoreyModel { Id="2F",Name="二层",Elevation=4300,Height=5000,TemplateStoreyId="1F" } },out resizeError),resizeError);
        Check(absoluteEdit.Model.CadImport.Slabs[0].TopElevation==4300
            && CadFloorModelGeneration.Build(absoluteEdit.Model,registry).Slabs.Count==1,
            "Legacy absolute slab baselines must move with floor settings and remain reimportable.");
        Check(resize.Undo() && resize.Model.TopElevationOf(resize.Model.Slabs.Single())==3000,"Floor-top slab positioning must survive undo.");
        registry.SlabThickness=150;registry.SaveDatum(context);
        Check(CadFloorPlanRegistry.Load(path).ResolvedSlabThickness==150,"Thickness must survive saving a datum.");
        registry.SaveFloor(capture);Check(CadFloorPlanRegistry.Load(path).ResolvedSlabThickness==150,"Thickness must survive saving a floor.");
        var stamp=File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(path)).Ticks;var copy=registry.Clone();copy.SlabThickness=175;registry.ReplaceFrom(copy,stamp);
        Check(registry.ResolvedSlabThickness==175,"Atomic refresh must carry slab settings.");
        var updated=CadFloorModelGeneration.Build(generated,registry);
        Check(updated.Slabs.Count==1 && updated.Slabs[0].Id==generated.Slabs[0].Id && updated.Slabs[0].Thickness==175,"Reimport must replace the registered slab without duplication.");
        var session=new BuildingModelEditSession(model);string error;
        Check(session.TryImportCadFloors(registry,"slabs",out error) && session.Model.Slabs.Count==1,"Live import must include slabs.");
        Check(session.Undo() && session.Model.Slabs.Count==0 && session.Redo() && session.Model.Slabs.Count==1,"Undo and redo must include slabs and holes.");
        generated.Slabs[0].Openings[0].Name="手动调整";
        try { CadFloorModelGeneration.Build(generated,registry);throw new Exception("Edited slab was overwritten."); }
        catch(InvalidDataException) { }
        var legacy=BuildingModelJson.FromJson(BuildingModelJson.ToJson(updated));legacy.CadImport.Slabs=null;
        legacy.Slabs.Clear();Check(CadFloorModelGeneration.Build(legacy,registry).Slabs.Count==1,"Older import state without a slab baseline must upgrade safely.");
        Check(new SlabModel().Thickness==100 && new WallModel().Thickness==200,"Editor and CAD defaults must agree.");
        BuildingModelJson.SaveModel(Path.Combine(folder,"generated-model.json"),updated);
    }
}
