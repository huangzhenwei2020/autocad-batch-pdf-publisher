using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

internal static class CurveWallSlabTests
{
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    private static bool Near(double a,double b,double tolerance=.001)=>Math.Abs(a-b)<tolerance;
    internal static void Run()
    {
        var start=new PointModel(0,0);var end=new PointModel(2000,0);var through=new PointModel(1000,1000);
        var bulge=CurveGeometry.ThroughBulge(start,end,through);var c=new CurveGeometry(start,end,bulge);
        Check(Near(c.Radius,1000)&&Near(c.At(.5).Y,1000)&&Near(c.Length,Math.PI*1000),"三点弧墙几何错误。");
        Check(c.BoundsPoints().Any(p=>Near(p.Y,1000)),"弧墙范围漏掉极值。");
        var source=new BuildingModelDocument {Storeys=new List<StoreyModel> {new StoreyModel {Id="1F",Name="一层",Height=3000}}};
        var session=new BuildingModelEditSession(source);
        Check(session.TryAddWall(new WallModel {StoreyId="1F",X2=2000,Bulge=bulge},out var id,out var error),error);
        var wall=session.Model.Walls.Single();
        Check(Near(wall.Bulge,bulge)&&Near(PlanEditing.WallLength(wall),c.Length),"圆弧参数未进入模型。");
        Check(PlanEditing.HitTest(session.Model,"1F",1000,1000,5)?.Id==id,"弧墙无法命中。");
        Check(PlanEditing.HitTest(session.Model,"1F",1000,0,5)==null,"错误选中弦线。");
        var snap=PlanEditing.Snap(session.Model,"1F",1100,999,20,false,0,0);
        Check(snap.Snapped&&c.DistanceTo(snap.X,snap.Y)<.001,"圆弧墙身捕捉不在弧上。");
        var line=new CurveGeometry(new PointModel(1000,-200),new PointModel(1000,2000),0);
        Check(CurveGeometry.Intersections(c,line).Any(p=>Near(p.X,1000)&&Near(p.Y,1000)),"线弧交点错误。");
        Check(CurveGeometry.Intersections(c,new CurveGeometry(new PointModel(500,500),new PointModel(1500,500),-1)).Count>0,"弧弧交点缺失。");
        var volume=BuildingVolumeBuilder.Build(session.Model);
        Check(volume.Faces.All(f=>f.ElementId==id)&&volume.Faces.Count>20,"弧墙被拆为不同构件或丢失。");
        Check(Near(volume.MaxY,1100,.6)&&Near(volume.MinY,0,.6),"弧墙实体范围错误。");
        var roundTrip=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        Check(Near(roundTrip.Walls[0].Bulge,bulge),"保存重开丢失圆弧。");
        Check(session.TryTransformWall(id,300,400,90,true,out var copied,out error),error);
        Check(Near(session.Model.Walls.Single(w=>w.Id==copied).Bulge,bulge),"复制/旋转丢失圆弧。");
        Check(session.Undo()&&session.Model.Walls.Count==1&&session.Redo(),"弧墙复制撤销失败。");
        var before=BuildingModelJson.ToJson(session.Model);
        Check(!session.TryAddOpening(new OpeningModel {HostWallId=id,Width=600,Height=1000,Offset=1500},out _,out error)
            &&error.Contains("直墙")&&before==BuildingModelJson.ToJson(session.Model),"弧墙洞口未安全拒绝。");
        Check(!session.TryAddWall(new WallModel {StoreyId="1F",X2=50,Bulge=1,Thickness=200},out _,out error),"内半径无效弧墙被接受。");
        Check(session.TrySetWallGeometry(id,4000,200,0,out error),error);
        Check(Near(PlanEditing.WallLength(session.Model.Walls.First(w=>w.Id==id)),4000),"弧长参数错误。");

        source.Walls=new List<WallModel> {new WallModel {Id="a",StoreyId="1F",X1=0,Y1=3000,X2=0,Y2=0},
            new WallModel {Id="b",StoreyId="1F",X1=0,Y1=0,X2=3000,Y2=0}};
        source.Openings=new List<OpeningModel> {new OpeningModel {Id="o",HostWallId="b",Width=600,Height=2100,Offset=2300,Kind="门",Code="M0621"}};
        source.Slabs.Add(new SlabModel {Id="s",StoreyId="1F",TopElevation=3000,Outline=new List<PointModel> {
            new PointModel(-100,3100),new PointModel(-100,-100),new PointModel(3100,-100),new PointModel(3100,3100)}});
        session=new BuildingModelEditSession(source);before=BuildingModelJson.ToJson(session.Model);
        Check(!session.TryFilletWalls("a",new PointModel(0,2000),"b",new PointModel(2000,0),4000,out _,out error),"过大半径接受。");
        Check(before==BuildingModelJson.ToJson(session.Model),"非法圆角修改模型。");
        Check(session.TryFilletWalls("a",new PointModel(0,2000),"b",new PointModel(2000,0),1000,out var arcId,out error,true),error);
        var arc=session.Model.Walls.Single(w=>w.Id==arcId);var arcCurve=CurveGeometry.ForWall(arc);
        Check(Near(arcCurve.Radius,1000)&&Near(arcCurve.Center.X,1000)&&Near(arcCurve.Center.Y,1000),"墙圆角不相切。");
        Check(Near(session.Model.Openings[0].Offset,1300),"圆角移动了剩余墙上的门。");
        var slab=session.Model.Slabs[0];var curvedVertex=slab.Outline.Single(p=>Math.Abs(p.Bulge)>1e-10);
        var index=slab.Outline.IndexOf(curvedVertex);var slabCurve=new CurveGeometry(curvedVertex,slab.Outline[(index+1)%slab.Outline.Count],curvedVertex.Bulge);
        Check(Near(slabCurve.Radius,1100)&&Near(slabCurve.Center.X,1000)&&Near(slabCurve.Center.Y,1000),"相接楼板圆角没有按外皮对齐。");
        var slabGeometry=SlabGeometry.Build(slab);Check(slabGeometry.Triangles.Count>3,"圆角楼板未实体化。");
        var plan=OrthographicProjector.Project(session.Model,new ViewDefinitionModel {Id="p",Kind=ViewKind.Plan,StoreyIds=new List<string>{"1F"},Scale=100});
        Check(plan.Lines.Count(l=>Math.Abs(l.Bulge)>1e-10&&l.Layer==ViewLayers.Cut)==2,"墙平面不是两条真实圆弧。");
        Check(!plan.Lines.Any(l=>Math.Abs(l.Bulge)<1e-10&&l.Layer==ViewLayers.Cut
            &&Near((l.X1+l.X2)/2+plan.OriginX,0)&&Near((l.Y1+l.Y2)/2+plan.OriginY,1000)),"相切处残留封口线。");
        Check(!plan.Lines.Any(l=>Math.Abs(l.Bulge)>1e-10&&l.Layer==ViewLayers.Elevation),"重合楼板弧边重复显示。");
        ViewDocument viewRoundTrip;
        using(var stream=new System.IO.MemoryStream()) {
            var serializer=new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ViewDocument));
            serializer.WriteObject(stream,plan);stream.Position=0;viewRoundTrip=(ViewDocument)serializer.ReadObject(stream);
        }
        Check(viewRoundTrip.Lines.Any(l=>Math.Abs(l.Bulge)>1e-10),"视图序列化丢圆弧。");
        var sheet=SheetComposer.ComposeModelSpace(new[]{plan},new SheetDefinitionModel {Id="sheet",Paper="A3",ViewIds=new List<string>{"p"}});
        Check(sheet.Lines.Count(l=>Math.Abs(l.Bulge)>1e-10)==plan.Lines.Count(l=>Math.Abs(l.Bulge)>1e-10),"图纸合成丢失圆弧。");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before&&session.Redo(),"墙/楼板同步圆角不是一步撤销。");
        var loaded=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        loaded.Storeys.Add(new StoreyModel {Id="2F",Name="二层",Height=3000,Elevation=3000,TemplateStoreyId="1F"});
        var expanded=StandardStoreyLayout.Materialize(loaded);
        Check(expanded.Walls.Count(w=>Math.Abs(w.Bulge)>1e-10)==2&&expanded.Slabs.Count(s=>s.Outline.Any(p=>Math.Abs(p.Bulge)>1e-10))==2,"标准层丢失曲线。");
        volume=BuildingVolumeBuilder.Build(loaded);Check(volume.Faces.Any(f=>f.StoreyId=="2F"&&f.Kind=="wall"),"标准层弧墙未展开。");
        session=new BuildingModelEditSession(source);
        Check(session.TryFilletSlab("s",1,600,out error),error);
        Check(Near(new CurveGeometry(session.Model.Slabs[0].Outline[1],session.Model.Slabs[0].Outline[2],session.Model.Slabs[0].Outline[1].Bulge).Radius,600),"楼板独立圆角错误。");
        Check(session.TryTransformSlab("s",200,300,true,out var slabCopy,out error),error);
        Check(session.Model.Slabs.Single(s=>s.Id==slabCopy).Outline.Any(p=>p.Bulge!=0),"楼板复制丢弧。");
        session=new BuildingModelEditSession(source);session.Model.Openings[0].Offset=500;before=BuildingModelJson.ToJson(session.Model);
        Check(!session.TryFilletWalls("a",new PointModel(0,2000),"b",new PointModel(2000,0),1000,out _,out error)
            &&before==BuildingModelJson.ToJson(session.Model),"圆角切穿门窗或部分提交。");
        Console.WriteLine("PASS arc walls three-point analytic bulge snap intersections solid identity copy rotate persistence standardFloors fillet tangent doorProtection atomicUndo slabRoundedOutline linkedOuterSkin nativeArcViews inwardInk sheet");
    }
}
