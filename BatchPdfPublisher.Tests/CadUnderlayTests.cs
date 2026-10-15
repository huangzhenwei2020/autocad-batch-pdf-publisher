using BatchPdfPublisher.BuildingModel;
using System.Diagnostics;

static class CadUnderlayTests
{
    static void Require(bool condition,string message) {if(!condition)throw new Exception(message);}
    static ComponentPlanPrimitive Line(double x1,double y1,double x2,double y2,string layer="CAD-LINE") =>
        new() {Kind="Line",X1=x1,Y1=y1,X2=x2,Y2=y2,SourceLayer=layer};
    static CadFloorPlanCapture Capture(string path) => new() {
        Floor=new() {ModelPath=path,Storey=new() {Id="1F",Name="1F",Height=3000},
            Alignment=new() {StoreyId="1F",CadBase=new(100,200),ModelBase=new(1000,2000),MillimetresPerCadUnit=2},
            DirectionPoint=new(110,200),RegionMin=new(0,0),RegionMax=new(6000,6000),
            RegionPolygon=new(){new(0,0),new(6000,0),new(6000,6000),new(0,6000)}},
        Probe=new() {DrawingFingerprint="qa"},Underlay=new() {Primitives=new(){Line(100,200,1100,200),Line(600,100,600,300)},
            Texts=new(){new() {Text="QA",X=700,Y=600,Height=20,Layer="CAD-TEXT"}}}};
    static void Main()
    {
        var directory=Path.Combine(Path.GetTempPath(),"WanLuoUnderlay-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"model.json");
        var capture=Capture(path);var index=new CadUnderlayIndex(capture);
        var endpoint=index.Snap(1002,2001,10,new HashSet<string>());
        Require(endpoint.Kind=="CAD · 端点"&&endpoint.X==1000&&endpoint.Y==2000,"datum/unit endpoint");
        var intersection=index.Snap(2003,2002,10,null);
        Require(intersection.Kind=="CAD · 交点"&&intersection.X==2000&&intersection.Y==2000,"line intersection");
        var nearest=index.Snap(1501,2002,10,null);
        Require(nearest.Kind=="CAD · 最近点"&&nearest.Y==2000,"line nearest");
        Require(!index.Snap(1501,2002,10,new HashSet<string>{"CAD-LINE"}).Snapped,"hidden layer snapped");
        Require(!index.Snap(2200,2800,10,null).Snapped,"text snapped");
        capture.Underlay!.NonSnapPrimitiveIndices.Add(0);
        Require(!new CadUnderlayIndex(capture).Snap(1501,2002,10,null).Snapped,"dimension line snapped");
        capture.Underlay.NonSnapPrimitiveIndices.Clear();
        var foot=index.Snap(1601,2001,10,null,new(1600,2400));
        Require(foot.Kind=="CAD · 垂足"&&foot.X==1600,"perpendicular");
        capture.Underlay!.Primitives.Add(new() {Kind="Arc",X1=1600,Y1=1200,Radius=100,StartDegrees=0,SweepDegrees=-90,SourceLayer="ARC"});
        capture.Underlay.Primitives.Add(new() {Kind="Circle",X1=2600,Y1=1200,Radius=100,SweepDegrees=360,SourceLayer="CIRCLE"});
        index=new(capture);
        Require(index.Snap(4001,4001,10,null).Kind=="CAD · 圆心","arc center");
        var middle=index.Snap(4141.421,3858.579,1,null);
        Require(middle.Kind=="CAD · 中点","clockwise midpoint");
        Require(index.Snap(3858.579,4141.421,1,null).Kind=="无","arc opposite side");
        Require(CadUnderlayIndex.Intersections(Line(2400,1200,2800,1200),capture.Underlay.Primitives.Last()).Count()==2,"line circle intersections");
        Require(CadUnderlayIndex.Intersections(capture.Underlay.Primitives.Last(),new() {Kind="Circle",X1=2700,Y1=1200,Radius=100,SweepDegrees=360}).Count()==2,"circle intersections");
        capture.Floor.Alignment.RotationRadians=-Math.PI/2;
        var rotated=new CadUnderlayIndex(capture);
        Require(Math.Abs(rotated.Primitives[0].X2-1000)<.000001&&Math.Abs(rotated.Primitives[0].Y2)<.000001,"rotated datum");
        capture.Floor.Alignment.RotationRadians=0;
        capture.Underlay.Primitives.Add(Line(-100,200,100,200));
        Require(!new CadUnderlayIndex(capture).Snap(600,2000,1,null).Snapped,"out-of-region snap");
        var model=SampleModelFactory.CreateTwoStoreyHouse();BuildingModelJson.SaveModel(path,model);
        var before=File.ReadAllBytes(path);
        var registry=new CadFloorPlanRegistry {ModelPath=path};registry.SaveFloor(capture);
        var loaded=CadFloorPlanRegistry.Load(path);Require(loaded.Floors[0].Underlay!.Primitives.Count==5,"roundtrip geometry");
        var stamp=File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(path)).Ticks;
        var draft=loaded.Clone();draft.Floors[0].Underlay!.Primitives.Add(Line(300,500,700,500));loaded.SaveUnderlays(draft,stamp);
        Require(File.ReadAllBytes(path).SequenceEqual(before),"updated model semantics");
        loaded.Floors[0].Underlay=null;registry.SaveFloor(loaded.Floors[0]);Require(CadFloorPlanRegistry.Load(path).Floors[0].Underlay==null,"legacy record");
        var many=Capture(path);many.Underlay!.Primitives=Enumerable.Range(0,20000).Select(i=>Line(i*10,0,i*10,4000)).ToList();
        many.Floor.RegionMax=new(210000,6000);many.Floor.RegionPolygon=new(){new(0,0),new(210000,0),new(210000,6000),new(0,6000)};
        var timer=Stopwatch.StartNew();var large=new CadUnderlayIndex(many);timer.Restart();
        for(int i=0;i<500;i++)Require(large.Snap(1000+i*20,4000,5,null).Snapped,"indexed geometry not found");
        Require(timer.ElapsedMilliseconds<2000,"large drawing snap performance");
        Console.WriteLine($"CAD_UNDERLAY_CORE_OK endpoint midpoint intersections clockwise nearest perpendicular center layerFilter textExcluded regionClip datum rotation units legacy roundtrip unchangedModel spatialIndex 500QueriesMs={timer.ElapsedMilliseconds}");
    }
}
