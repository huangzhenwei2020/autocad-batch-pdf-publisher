using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Services
{
    public sealed class CadDoorCut
    {
        public string Wall {get;set;}
        public double Start {get;set;}
        public double End {get;set;}
    }
    public sealed class CadLibraryDoorState
    {
        public string AssetId {get;set;}
        public byte[] Plan {get;set;}
        public double BaseX {get;set;}
        public double BaseY {get;set;}
        public double Width {get;set;}
        public double Height {get;set;}
        public double Units {get;set;}
        public bool FlipAlong {get;set;}
        public bool FlipAcross {get;set;}
        public bool HasWallOrigin {get;set;}
        public string Code {get;set;}
        public double? OpeningAngle {get;set;}
        public double LabelAlong {get;set;}
        public double LabelNormal {get;set;}
        public double WallX {get;set;}
        public double WallY {get;set;}
        public double WallZ {get;set;}
        public List<string> Caps {get;set;}=new List<string>();
        public List<string> CapGeometry {get;set;}=new List<string>();
        public List<CadDoorCut> Cuts {get;set;}=new List<CadDoorCut>();
    }
    public sealed class CadLibraryWallState
    {
        public string Geometry {get;set;}
        public List<string> Fragments {get;set;}=new List<string>();
        public List<string> FragmentGeometry {get;set;}=new List<string>();
    }
    // The original native wall remains in the DWG, invisible while its managed fragments are drawn.
    // Wall and door changes share one transaction. No shared catalog record is rewritten.
    public static class CadLibraryDoorPlacement
    {
        private const string DoorKey="WL_LIBRARY_DOOR",WallKey="WL_LIBRARY_WALL",FragmentKey="WL_LIBRARY_WALL_FRAGMENT";
        private sealed class Edge {public ObjectId Id;public Point3d A,B;public double Parameter;public double ParameterSpan=1;public Vector3d Direction=Vector3d.XAxis;public double Length;}
        private sealed class Host {public Point3d Point;public double Angle;public List<CadDoorCut> Cuts;public double Score;}
        public static bool IsWallLayer(string name)=>name!=null&&(name.IndexOf("墙",StringComparison.OrdinalIgnoreCase)>=0||name.IndexOf("wall",StringComparison.OrdinalIgnoreCase)>=0);
        public static CadLibraryDoorState ReadDoor(Transaction tx,ObjectId id)
        {
            var block=tx.GetObject(id,OpenMode.ForRead) as BlockReference;
            return block==null?null:Read<CadLibraryDoorState>(tx,block,DoorKey);
        }
        public static string ManagedWall(Transaction tx,Entity entity)=>Read<string>(tx,entity,FragmentKey);
        public static bool IsManagedCap(Transaction tx,Entity entity)=>Read<string>(tx,entity,"WL_LIBRARY_CAP")!=null;
        public static string GeometrySignature(Curve curve)=>Signature(curve);
        public sealed class PlanPreview {public Point3d Start,WallStart,WallEnd;public double Angle,Thickness;public ComponentPlanSymbol Plan;public List<CadDoorCut> Cuts;}
        public static PlanPreview Preview(Database db,Transaction tx,ComponentPlanSymbol source,PointModel localBase,Point3d picked,double angle,double units,double width,double height,bool along,bool across,string code,double? opening,IEnumerable<ObjectId> candidates=null,double? thickness=null,bool fit=false)
        {
            var plan=ComponentPlanSymbols.DoorPlanVariant(source,localBase,width,height);if(opening.HasValue)plan=ComponentPlanSymbols.DoorOpeningVariant(plan,opening.Value);if(code!=null){plan.Code=code;plan.TextCandidates.Clear();ComponentPlanSymbols.Validate(plan);}
            var host=FindHost(db,tx,picked,angle,width/units,units,candidates,thickness,fit);var center=FrameCenterY(plan,localBase);var cos=Math.Cos(host.Angle);var sin=Math.Sin(host.Angle);
            Func<double,double,PointModel> map=(x,y)=>{var u=(along?width-(x-localBase.X):x-localBase.X)/units;var v=(across?-(y-localBase.Y-center):y-localBase.Y-center)/units;return new PointModel(host.Point.X+cos*u-sin*v,host.Point.Y+sin*u+cos*v);};
            foreach(var p in plan.Primitives){var a=map(p.X1,p.Y1);p.X1=a.X;p.Y1=a.Y;if(p.Kind=="Line"){var b=map(p.X2,p.Y2);p.X2=b.X;p.Y2=b.Y;}else {var start=p.StartDegrees*Math.PI/180;var x=Math.Cos(start)*(along?-1:1);var y=Math.Sin(start)*(across?-1:1);p.StartDegrees=Math.Atan2(sin*x+cos*y,cos*x-sin*y)*180/Math.PI;p.SweepDegrees*=((along?-1:1)*(across?-1:1));p.Radius/=units;}}
            var segment=WallSegment(db,tx,host.Cuts,host.Angle);return new PlanPreview {Start=host.Point,Angle=host.Angle,Plan=plan,Cuts=host.Cuts,WallStart=segment[0],WallEnd=segment[1],Thickness=segment[2].X};
        }
        public static PlanPreview InitialPreview(Database db,Transaction tx,ComponentPlanSymbol source,PointModel localBase,Point3d picked,double angle,double units,double width,double height,bool centered,double clearance,double? thickness,string code,double? opening)
        {
            if(!ComponentPlanSymbols.Finite(clearance)||clearance<0)throw new InvalidDataException("门垛净距无效。");
            var host=Preview(db,tx,source,localBase,picked,angle,units,width,height,false,false,code,opening,null,thickness,true);var length=host.WallStart.DistanceTo(host.WallEnd)*units;
            if(clearance>length-width&&!centered)throw new InvalidDataException("墙段容纳不下门垛和门洞，请减小门垛或洞口宽度。");
            var direction=new Vector3d(Math.Cos(host.Angle),Math.Sin(host.Angle),0);var offset=(host.Start-host.WallStart).DotProduct(direction)*units+width/2;bool first;OpeningPlanGeometry.WallEndClearance(length,new OpeningModel {Offset=offset,Width=width},out first);
            var start=centered?(length-width)/2:OpeningPlanGeometry.OffsetFromWallEnd(length,width,clearance,first)-width/2;
            return Preview(db,tx,source,localBase,host.WallStart+direction*(start/units),host.Angle,units,width,height,false,false,code,opening,host.Cuts.Select(c=>Id(db,c.Wall)),thickness);
        }
        public static IList<CadDoorCut> ActiveCuts(Database db,Transaction tx,ISet<string> excluding)=>Doors(db,tx).Where(pair=>!excluding.Contains(pair.Key.Handle.ToString())).SelectMany(pair=>pair.Value.Cuts).ToArray();
        public static IList<Curve> PreviewWall(Curve source,IEnumerable<CadDoorCut> allCuts)
        {
            var cuts=allCuts.Where(c=>c.Wall==source.Handle.ToString()).ToArray();if(cuts.Length==0)return new[]{(Curve)source.Clone()};
            var result=new List<Curve>();using(var pieces=SplitAtCuts(source,cuts))foreach(DBObject item in pieces){var curve=(Curve)item;var point=curve.GetPointAtParameter((curve.StartParam+curve.EndParam)/2);var t=source.GetParameterAtPoint(point);if(cuts.Any(c=>t>c.Start-1e-8&&t<c.End+1e-8))curve.Dispose();else result.Add(curve);}return result;
        }
        private static DBObjectCollection SplitAtCuts(Curve source,IEnumerable<CadDoorCut> cuts){var parameters=cuts.SelectMany(c=>new[]{c.Start,c.End}).Where(p=>p>source.StartParam+1e-8&&p<source.EndParam-1e-8).Distinct().OrderBy(p=>p).ToArray();if(parameters.Length>0)return source.GetSplitCurves(new DoubleCollection(parameters));var pieces=new DBObjectCollection();pieces.Add((DBObject)source.Clone());return pieces;}
        public static IList<Line> PreviewCaps(Database db,Transaction tx,IEnumerable<CadDoorCut> sourceCuts,double angle)
        {
            var direction=new Vector3d(Math.Cos(angle),Math.Sin(angle),0);var pairs=sourceCuts.Select(c=>{var source=(Curve)tx.GetObject(Id(db,c.Wall),OpenMode.ForRead);var a=source.GetPointAtParameter(c.Start);var b=source.GetPointAtParameter(c.End);return (b-a).DotProduct(direction)>0?new[]{a,b}:new[]{b,a};}).ToArray();return new[]{new Line(pairs[0][0],pairs[1][0]),new Line(pairs[0][1],pairs[1][1])};
        }
        public static ObjectId Place(Database db,Transaction tx,ComponentCatalogRecord record,Point3d picked,double preferredAngle,double units,PointModel localBase,double width,double height,bool flipAlong=false,bool flipAcross=false,ObjectId replace=default(ObjectId),string code=null,double? openingAngle=null,IEnumerable<ObjectId> candidates=null,double labelAlong=0,double labelNormal=0)
        {
            CheckUndo(db);
            localBase=localBase??ComponentPlanSymbols.SuggestedInsertionBase(record.Plan);
            var plan=ComponentPlanSymbols.DoorPlanVariant(record.Plan,localBase,width,height);
            if(openingAngle.HasValue)plan=ComponentPlanSymbols.DoorOpeningVariant(plan,openingAngle.Value);
            if(code!=null){plan.Code=code;plan.TextCandidates.Clear();ComponentPlanSymbols.Validate(plan);}
            if(!ComponentPlanSymbols.Finite(units)||units<=0||units>1000000)throw new InvalidDataException("单位换算无效。");
            var host=FindHost(db,tx,picked,preferredAngle,width/units,units,candidates);
            var previous=replace.IsNull?null:ReadDoor(tx,replace);
            var active=Doors(db,tx).Where(pair=>pair.Key!=replace).ToArray();
            foreach(var cut in host.Cuts)if(active.Any(pair=>pair.Value.Cuts.Any(other=>other.Wall==cut.Wall&&Math.Min(other.End,cut.End)-Math.Max(other.Start,cut.Start)>1e-8)))
                throw new InvalidDataException("门洞与另一樘门洞重叠，请移动位置或减小宽度。");
            foreach(var cut in host.Cuts) {
                var wall=tx.GetObject(Id(db,cut.Wall),OpenMode.ForWrite) as Curve;
                if(wall==null)throw new InvalidDataException("墙体已改变。");
                CheckLayer(db,tx,wall);
                if(Read<CadLibraryWallState>(tx,wall,WallKey)==null)Write(tx,wall,WallKey,new CadLibraryWallState {Geometry=Signature(wall)});
            }
            var variant=new ComponentCatalogRecord {AssetId=record.AssetId,Version=record.Version,Plan=plan,PlanHash=ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(plan))};
            var newId=CadComponentLibraryService.Insert(db,tx,variant,host.Point,host.Angle,units,localBase);
            var block=(BlockReference)tx.GetObject(newId,OpenMode.ForWrite);
            block.ScaleFactors=new Scale3d((flipAlong?-1:1)/units,(flipAcross?-1:1)/units,1/units);
            var normal=new Vector3d(-Math.Sin(host.Angle),Math.Cos(host.Angle),0);
            var centerY=FrameCenterY(plan,localBase);
            block.Position=host.Point-normal*((flipAcross?-centerY:centerY)/units);
            if(flipAlong)block.Position+=new Vector3d(Math.Cos(host.Angle),Math.Sin(host.Angle),0)*(width/units);
            RefreshAttributes(tx,block);
            var segment=WallSegment(db,tx,host.Cuts,host.Angle);var labelPoint=LabelPoint(host.Point,host.Angle,width,units,segment[2].X,labelAlong,labelNormal);
            foreach(ObjectId attributeId in block.AttributeCollection){var attribute=(AttributeReference)tx.GetObject(attributeId,OpenMode.ForWrite);if(attribute.Tag=="WL_CODE"){attribute.Position=labelPoint;attribute.Rotation=host.Angle;attribute.IsMirroredInX=false;attribute.IsMirroredInY=false;}}
            var newState=new CadLibraryDoorState {AssetId=record.AssetId,Plan=ComponentPlanSymbols.Bytes(record.Plan),BaseX=localBase.X,BaseY=localBase.Y,
                Width=width,Height=height,Units=units,FlipAlong=flipAlong,FlipAcross=flipAcross,Cuts=host.Cuts,HasWallOrigin=true,WallX=host.Point.X,WallY=host.Point.Y,WallZ=host.Point.Z,Code=code,OpeningAngle=openingAngle,LabelAlong=labelAlong,LabelNormal=labelNormal};
            if(!replace.IsNull){CheckLayer(db,tx,(Entity)tx.GetObject(replace,OpenMode.ForRead));RemoveCaps(db,tx,previous);tx.GetObject(replace,OpenMode.ForWrite).Erase();}
            AddCaps(db,tx,newState,host.Angle);
            Write(tx,block,DoorKey,newState);
            Rebuild(db,tx,host.Cuts.Select(c=>c.Wall).Concat(previous?.Cuts.Select(c=>c.Wall)??Enumerable.Empty<string>()));
            return newId;
        }
        public static void Remove(Database db,Transaction tx,ObjectId id)
        {
            CheckUndo(db);
            var state=ReadDoor(tx,id);if(state==null)throw new InvalidDataException("请选择沿墙插入的图库门。");
            CheckLayer(db,tx,(Entity)tx.GetObject(id,OpenMode.ForRead));RemoveCaps(db,tx,state);tx.GetObject(id,OpenMode.ForWrite).Erase();Rebuild(db,tx,state.Cuts.Select(c=>c.Wall));
        }
        public static Point3d Origin(BlockReference block,CadLibraryDoorState state)=>state.HasWallOrigin?new Point3d(state.WallX,state.WallY,state.WallZ):state.FlipAlong?block.Position-new Vector3d(Math.Cos(block.Rotation),Math.Sin(block.Rotation),0)*(state.Width/state.Units):block.Position;
        public static Point3d OpeningOrigin(Database db,Transaction tx,BlockReference block,CadLibraryDoorState state)
        {
            if(state.HasWallOrigin)return Origin(block,state);
            var direction=new Vector3d(Math.Cos(block.Rotation),Math.Sin(block.Rotation),0);
            var starts=state.Cuts.Select(c=>{var wall=(Curve)tx.GetObject(Id(db,c.Wall),OpenMode.ForRead);var a=wall.GetPointAtParameter(c.Start);var b=wall.GetPointAtParameter(c.End);return (b-a).DotProduct(direction)>0?a:b;}).ToArray();
            return new Point3d(starts.Average(p=>p.X),starts.Average(p=>p.Y),starts.Average(p=>p.Z));
        }
        public static double FrameCenterY(ComponentPlanSymbol plan,PointModel localBase)
        {
            var parts=plan.Parts?.Where(p=>p.Role=="Casing"&&p.Primitives.Count>0).ToArray();
            if(parts==null||parts.Length==0)parts=plan.Parts?.Where(p=>p.Role=="Frame"&&p.Primitives.Count>0).ToArray();
            if(parts==null||parts.Length==0)throw new InvalidDataException("沿墙插入前请标注门套或门框，以确定墙中线对齐位置。");
            var ys=parts.SelectMany(p=>p.Primitives).SelectMany(i=>{var p=plan.Primitives[i];return p.Kind=="Line"?new[]{p.Y1,p.Y2}:new[]{p.Y1-p.Radius,p.Y1+p.Radius};}).ToArray();
            return (ys.Min()+ys.Max())/2-localBase.Y;
        }
        private static void AddCaps(Database db,Transaction tx,CadLibraryDoorState state,double angle)
        {
            var direction=new Vector3d(Math.Cos(angle),Math.Sin(angle),0);var walls=state.Cuts.Select(c=>(Curve)tx.GetObject(Id(db,c.Wall),OpenMode.ForRead)).ToArray();
            var pairs=state.Cuts.Select((c,i)=>{var a=walls[i].GetPointAtParameter(c.Start);var b=walls[i].GetPointAtParameter(c.End);return (b-a).DotProduct(direction)>0?new[]{a,b}:new[]{b,a};}).ToArray();
            var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
            for(var end=0;end<2;end++){var cap=new Line(pairs[0][end],pairs[1][end]);cap.SetPropertiesFrom(walls[0]);cap.Visible=true;space.AppendEntity(cap);tx.AddNewlyCreatedDBObject(cap,true);Write(tx,cap,"WL_LIBRARY_CAP",Signature(cap));state.Caps.Add(cap.Handle.ToString());state.CapGeometry.Add(Signature(cap));}
        }
        private static void RemoveCaps(Database db,Transaction tx,CadLibraryDoorState state)
        {
            if(state?.Caps==null)return;
            for(var i=0;i<state.Caps.Count;i++){var id=Id(db,state.Caps[i]);if(id.IsErased)throw new InvalidDataException("门洞封口已被删除，请先撤销该修改。");var cap=(Curve)tx.GetObject(id,OpenMode.ForWrite);CheckLayer(db,tx,cap);if(Signature(cap)!=state.CapGeometry[i])throw new InvalidDataException("门洞封口已被修改，请先撤销该修改。");cap.Erase();}
        }
        public static Point3d CenterOnWall(Database db,Transaction tx,CadLibraryDoorState state,double width,double angle)
        {
            var segment=WallSegment(db,tx,state.Cuts,angle);var direction=new Vector3d(Math.Cos(angle),Math.Sin(angle),0);var length=segment[0].DistanceTo(segment[1]);if(length<width/state.Units)throw new InvalidDataException("墙段容纳不下门洞。");return segment[0]+direction*((length-width/state.Units)/2);
        }
        private static Point3d[] WallSegment(Database db,Transaction tx,IEnumerable<CadDoorCut> cuts,double angle)
        {
            var selected=new List<Edge>();foreach(var cut in cuts) {
                var source=(Entity)tx.GetObject(Id(db,cut.Wall),OpenMode.ForRead);
                var edge=Edges(source).FirstOrDefault(candidate=>cut.Start>=candidate.Parameter-1e-8&&cut.End<=candidate.Parameter+candidate.ParameterSpan+1e-8);
                if(edge==null)throw new InvalidDataException("墙段已改变，无法居中。");selected.Add(edge);
            }
            var direction=new Vector3d(Math.Cos(angle),Math.Sin(angle),0);var normal=new Vector3d(-direction.Y,direction.X,0);
            Func<Point3d,double> dot=point=>point.GetAsVector().DotProduct(direction);
            var start=selected.Max(edge=>Math.Min(dot(edge.A),dot(edge.B)));var end=selected.Min(edge=>Math.Max(dot(edge.A),dot(edge.B)));
            var perpendicular=selected.Average(edge=>edge.A.GetAsVector().DotProduct(normal));
            var at=Point3d.Origin+normal*perpendicular+Vector3d.ZAxis*selected[0].A.Z;return new[]{at+direction*start,at+direction*end,new Point3d(Math.Abs((selected[0].A-selected[1].A).DotProduct(normal)),0,0)};
        }
        public static Point3d LabelPoint(Point3d origin,double angle,double width,double units,double thickness,double along,double normal)
        {if(!ComponentPlanSymbols.Finite(along)||!ComponentPlanSymbols.Finite(normal)||Math.Abs(along)>1000000||Math.Abs(normal)>1000000)throw new InvalidDataException("编号偏移无效。");var y=-thickness/2-140/units+normal/units;var minimum=thickness/2+70/units;if(Math.Abs(y)<minimum)y=(y<0?-1:1)*minimum;return origin+new Vector3d(Math.Cos(angle),Math.Sin(angle),0)*((width/2+along)/units)+new Vector3d(-Math.Sin(angle),Math.Cos(angle),0)*y;}
        public static void Synchronize(Database db,Transaction tx)
        {
            CheckUndo(db);
            var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForRead);
            var liveCaps=new HashSet<string>(Doors(db,tx).SelectMany(pair=>pair.Value.Caps??new List<string>()));
            foreach(ObjectId id in space){if(id.IsErased)continue;var entity=tx.GetObject(id,OpenMode.ForRead) as Curve;if(entity==null)continue;var signature=Read<string>(tx,entity,"WL_LIBRARY_CAP");if(signature==null||liveCaps.Contains(entity.Handle.ToString()))continue;if(Signature(entity)!=signature)throw new InvalidDataException("已删除门的封口线被修改，请先核对。");CheckLayer(db,tx,entity);entity.UpgradeOpen();entity.Erase();}
            Rebuild(db,tx,space.Cast<ObjectId>().Where(id=>!id.IsErased).Select(id=>tx.GetObject(id,OpenMode.ForRead) as Entity)
                .Where(entity=>entity!=null&&Read<string>(tx,entity,FragmentKey)==null&&Read<CadLibraryWallState>(tx,entity,WallKey)!=null).Select(entity=>entity.Handle.ToString()).ToArray());
        }
        private static Host FindHost(Database db,Transaction tx,Point3d pick,double preferred,double width,double units,IEnumerable<ObjectId> wallCandidates=null,double? expectedThickness=null,bool fit=false)
        {
            var edges=new List<Edge>();var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForRead);
            foreach(ObjectId id in wallCandidates??space.Cast<ObjectId>()) {
                if(id.IsErased)continue;var entity=tx.GetObject(id,OpenMode.ForRead) as Entity;
                if(entity==null||!IsWallLayer(entity.Layer)||Read<string>(tx,entity,FragmentKey)!=null||Read<string>(tx,entity,"WL_LIBRARY_CAP")!=null)continue;
                var layer=(LayerTableRecord)tx.GetObject(entity.LayerId,OpenMode.ForRead);
                if(layer.IsLocked||layer.IsOff||layer.IsFrozen||(!entity.Visible&&Read<CadLibraryWallState>(tx,entity,WallKey)==null))continue;
                foreach(var edge in Edges(entity)) {
                    if(Math.Abs(edge.A.Z-pick.Z)>.1/units)continue;
                    var along=(pick-edge.A).DotProduct(edge.Direction);var near=edge.A+edge.Direction*Math.Max(0,Math.Min(edge.Length,along));
                    if(near.DistanceTo(pick)<=1200/units)edges.Add(edge);
                }
            }
            if(edges.Count>500)throw new InvalidDataException("附近墙线过多，请清理重复墙线或缩小范围。");
            var candidates=new List<Host>();var preferredDirection=new Vector3d(Math.Cos(preferred),Math.Sin(preferred),0);
            for(var i=0;i<edges.Count;i++)for(var j=i+1;j<edges.Count;j++) {
                var a=edges[i];var b=edges[j];if(a.Direction.CrossProduct(b.Direction).Length>1e-6||Math.Abs(a.A.Z-b.A.Z)>.1/units)continue;
                var direction=a.Direction;if(direction.DotProduct(preferredDirection)<0)direction=-direction;
                var normal=new Vector3d(-direction.Y,direction.X,0);var thickness=Math.Abs((b.A-a.A).DotProduct(normal));
                if(thickness<50/units||thickness>1000/units)continue;
                if(expectedThickness.HasValue&&Math.Abs(thickness*units-expectedThickness.Value)>1)continue;
                var centerOffset=(b.A-a.A).DotProduct(normal)/2;var origin=a.A+direction*(pick-a.A).DotProduct(direction)+normal*centerOffset;
                var score=origin.DistanceTo(pick);if(score>Math.Max(thickness,200/units))continue;
                if(fit){Func<Point3d,double> dot=p=>p.GetAsVector().DotProduct(direction);var start=Math.Max(Math.Min(dot(a.A),dot(a.B)),Math.Min(dot(b.A),dot(b.B)));var end=Math.Min(Math.Max(dot(a.A),dot(a.B)),Math.Max(dot(b.A),dot(b.B)));if(end-start<width-1e-8)continue;origin+=direction*(Math.Max(start,Math.Min(end-width,dot(origin)))-dot(origin));}
                var cuts=new List<CadDoorCut>();var valid=true;
                foreach(var edge in new[]{a,b}) {
                    var t0=(origin-edge.A).DotProduct(edge.Direction)/edge.Length;var t1=(origin+direction*width-edge.A).DotProduct(edge.Direction)/edge.Length;
                    var lo=Math.Min(t0,t1);var hi=Math.Max(t0,t1);
                    if(lo < -1e-8||hi>1+1e-8){valid=false;break;}lo=Math.Max(0,lo);hi=Math.Min(1,hi);
                    cuts.Add(new CadDoorCut {Wall=edge.Id.Handle.ToString(),Start=edge.Parameter+lo*edge.ParameterSpan,End=edge.Parameter+hi*edge.ParameterSpan});
                }
                if(valid)candidates.Add(new Host {Point=origin,Angle=Math.Atan2(direction.Y,direction.X),Cuts=cuts,Score=score});
            }
            var ordered=candidates.OrderBy(c=>c.Score).ToArray();if(ordered.Length==0)throw new InvalidDataException("未找到能容纳门洞的直线双线墙。墙图层名称须含“墙”或“wall”；天正原生墙及弧形墙暂不能按普通墙线开洞。");
            if(ordered.Length>1&&Math.Abs(ordered[1].Score-ordered[0].Score)<.1/units&&Math.Abs(Math.Sin(ordered[1].Angle-ordered[0].Angle))>.01)
                throw new InvalidDataException("墙角有多个方向，请在墙段中部指定插入位置。");
            return ordered[0];
        }
        private static IEnumerable<Edge> Edges(Entity entity)
        {
            var line=entity as Line;if(line!=null) {
                var direction=line.EndPoint-line.StartPoint;var length=direction.Length;if(length>1e-8&&Math.Abs(direction.Z)<1e-8)yield return new Edge {Id=line.ObjectId,A=line.StartPoint,B=line.EndPoint,Direction=direction/length,Length=length,Parameter=line.StartParam,ParameterSpan=line.EndParam-line.StartParam};yield break;
            }
            var poly=entity as Polyline;if(poly==null||!poly.Normal.IsCodirectionalTo(Vector3d.ZAxis))yield break;
            for(var i=0;i<poly.NumberOfVertices;i++)if(Math.Abs(poly.GetBulgeAt(i))>1e-8)yield break;
            for(var i=0;i<poly.NumberOfVertices-(poly.Closed?0:1);i++) {
                var a=poly.GetPoint3dAt(i);var b=poly.GetPoint3dAt((i+1)%poly.NumberOfVertices);var direction=b-a;var length=direction.Length;
                if(length>1e-8)yield return new Edge {Id=poly.ObjectId,A=a,B=b,Direction=direction/length,Length=length,Parameter=i};
            }
        }
        private static IEnumerable<KeyValuePair<ObjectId,CadLibraryDoorState>> Doors(Database db,Transaction tx)
        {
            foreach(ObjectId id in (BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForRead)) {
                if(id.IsErased)continue;var state=ReadDoor(tx,id);if(state!=null)yield return new KeyValuePair<ObjectId,CadLibraryDoorState>(id,state);
            }
        }
        private static void Rebuild(Database db,Transaction tx,IEnumerable<string> handles)
        {
            var doors=Doors(db,tx).ToArray();var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
            foreach(var handle in handles.Distinct()) {
                var source=tx.GetObject(Id(db,handle),OpenMode.ForWrite) as Curve;var state=source==null?null:Read<CadLibraryWallState>(tx,source,WallKey);
                if(state==null)throw new InvalidDataException("原墙线缺失，无法恢复洞口。");CheckLayer(db,tx,source);
                if(Signature(source)!=state.Geometry)throw new InvalidDataException("原墙线已被修改，请先核对墙体再编辑门洞。");
                for(var i=0;i<state.Fragments.Count;i++) {
                    var id=Id(db,state.Fragments[i]);if(id.IsErased)throw new InvalidDataException("门洞附近墙线已被删除，请先撤销墙线修改。");
                    var fragment=(Curve)tx.GetObject(id,OpenMode.ForWrite);CheckLayer(db,tx,fragment);
                    if(Signature(fragment)!=state.FragmentGeometry[i])throw new InvalidDataException("门洞附近墙线已被修改，请先撤销墙线修改。");fragment.Erase();
                }
                state.Fragments.Clear();state.FragmentGeometry.Clear();
                var cuts=doors.SelectMany(pair=>pair.Value.Cuts).Where(c=>c.Wall==handle).OrderBy(c=>c.Start).ToArray();
                if(cuts.Length==0){source.Visible=true;var dictionary=(DBDictionary)tx.GetObject(source.ExtensionDictionary,OpenMode.ForWrite);var oldRecord=dictionary.GetAt(WallKey);dictionary.Remove(WallKey);tx.GetObject(oldRecord,OpenMode.ForWrite).Erase();continue;}
                source.Visible=false;
                using(var split=SplitAtCuts(source,cuts))foreach(DBObject item in split) {
                    var curve=item as Curve;if(curve==null){item.Dispose();continue;}
                    var midpoint=curve.GetPointAtParameter((curve.StartParam+curve.EndParam)/2);var parameter=source.GetParameterAtPoint(midpoint);
                    if(cuts.Any(c=>parameter>c.Start-1e-8&&parameter<c.End+1e-8)){curve.Dispose();continue;}
                    curve.Visible=true;space.AppendEntity(curve);tx.AddNewlyCreatedDBObject(curve,true);
                    Write(tx,curve,FragmentKey,handle);state.Fragments.Add(curve.Handle.ToString());state.FragmentGeometry.Add(Signature(curve));
                }
                Write(tx,source,WallKey,state);
            }
        }
        private static void RefreshAttributes(Transaction tx,BlockReference block)
        {
            var definitions=((BlockTableRecord)tx.GetObject(block.BlockTableRecord,OpenMode.ForRead)).Cast<ObjectId>().Select(id=>tx.GetObject(id,OpenMode.ForRead)).OfType<AttributeDefinition>().ToDictionary(a=>a.Tag);
            foreach(ObjectId id in block.AttributeCollection) {var attribute=(AttributeReference)tx.GetObject(id,OpenMode.ForWrite);attribute.SetAttributeFromBlock(definitions[attribute.Tag],block.BlockTransform);attribute.TextString=definitions[attribute.Tag].TextString;}
        }
        private static void CheckLayer(Database db,Transaction tx,Entity entity) {if(((LayerTableRecord)tx.GetObject(entity.LayerId,OpenMode.ForRead)).IsLocked)throw new InvalidDataException("墙或门所在图层已锁定，请先解锁。");}
        private static void CheckUndo(Database db){if(!db.UndoRecording)throw new InvalidDataException("请先启用 CAD 撤销记录，再编辑墙洞，以便失败时完整恢复。");}
        private static ObjectId Id(Database db,string handle)=>db.GetObjectId(false,new Handle(long.Parse(handle,System.Globalization.NumberStyles.HexNumber)),0);
        private static string Signature(Curve curve)
        {
            var line=curve as Line;var poly=curve as Polyline;
            var points=line!=null?new[]{line.StartPoint,line.EndPoint}:poly!=null?Enumerable.Range(0,poly.NumberOfVertices).Select(poly.GetPoint3dAt).ToArray():new[]{curve.StartPoint,curve.EndPoint};
            return string.Join("|",points.Select(p=>p.X.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+","+p.Y.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+","+p.Z.ToString("R",System.Globalization.CultureInfo.InvariantCulture)))+"|"+curve.Layer+"|"+curve.ColorIndex+"|"+curve.LinetypeId.Handle+"|"+curve.LineWeight+
                (poly==null?"":"|"+poly.Closed+"|"+string.Join("|",Enumerable.Range(0,poly.NumberOfVertices).Select(i=>poly.GetBulgeAt(i)+","+poly.GetStartWidthAt(i)+","+poly.GetEndWidthAt(i))));
        }
        private static T Read<T>(Transaction tx,DBObject entity,string key) where T:class
        {
            if(entity.ExtensionDictionary.IsNull)return null;var dictionary=(DBDictionary)tx.GetObject(entity.ExtensionDictionary,OpenMode.ForRead);if(!dictionary.Contains(key))return null;
            var record=(Xrecord)tx.GetObject(dictionary.GetAt(key),OpenMode.ForRead);using(var data=record.Data) {
                var bytes=data.AsArray().SelectMany(value=>(byte[])value.Value).ToArray();if(bytes.Length>4*1024*1024)throw new InvalidDataException("门洞数据超限。");
                using(var stream=new MemoryStream(bytes))return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
            }
        }
        private static void Write<T>(Transaction tx,DBObject entity,string key,T value)
        {
            if(!entity.IsWriteEnabled)entity.UpgradeOpen();if(entity.ExtensionDictionary.IsNull)entity.CreateExtensionDictionary();
            var dictionary=(DBDictionary)tx.GetObject(entity.ExtensionDictionary,OpenMode.ForWrite);Xrecord record;
            if(dictionary.Contains(key))record=(Xrecord)tx.GetObject(dictionary.GetAt(key),OpenMode.ForWrite);
            else {record=new Xrecord();dictionary.SetAt(key,record);tx.AddNewlyCreatedDBObject(record,true);}
            using(var stream=new MemoryStream()) {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value);var bytes=stream.ToArray();var values=new List<TypedValue>();
                for(var i=0;i<bytes.Length;i+=127)values.Add(new TypedValue((int)DxfCode.BinaryChunk,bytes.Skip(i).Take(127).ToArray()));
                using(var buffer=new ResultBuffer(values.ToArray()))record.Data=buffer;
            }
        }
        public static void RunNativeCheck(Autodesk.AutoCAD.ApplicationServices.Document document)
        {
            var plan=new ComponentPlanSymbol {SchemaVersion=3,Category="Door",DoorAssembly="SingleSwing",Name="墙洞回归门",Code="",Width=900,Height=2100,Primitives=new List<ComponentPlanPrimitive> {
                new ComponentPlanPrimitive {Kind="Line",X1=0,Y1=-100,X2=0,Y2=100},new ComponentPlanPrimitive {Kind="Line",X1=900,Y1=-100,X2=900,Y2=100},
                new ComponentPlanPrimitive {Kind="Line",X1=0,Y1=0,X2=0,Y2=900},new ComponentPlanPrimitive {Kind="Line",X1=40,Y1=0,X2=40,Y2=900},
                new ComponentPlanPrimitive {Kind="Circle",X1=40,Y1=850,Radius=12},new ComponentPlanPrimitive {Kind="Arc",Radius=900,StartDegrees=0,SweepDegrees=90}},
                Parts=new List<ComponentPlanPart> {new ComponentPlanPart {Name="门框",Role="Frame",Primitives=new List<int>{0,1}},new ComponentPlanPart {Name="门扇",Role="PrimaryLeaf",Primitives=new List<int>{2,3}},new ComponentPlanPart {Name="把手",Role="Handle",Primitives=new List<int>{4}},new ComponentPlanPart {Name="开启",Role="OpeningSymbol",Primitives=new List<int>{5}}}};
            var record=new ComponentCatalogRecord {AssetId=Guid.NewGuid().ToString("D"),Version=1,Plan=plan,PlanHash=ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(plan))};
            var before=ComponentPlanSymbols.Bytes(plan);var variant=ComponentPlanSymbols.DoorPlanVariant(plan,new PointModel(),1200,2400);
            if(variant.Primitives[3].X1-variant.Primitives[2].X1!=40||variant.Primitives[4].Radius!=12||variant.Primitives[2].Y2!=1200||variant.Primitives[5].Radius!=1200||!before.SequenceEqual(ComponentPlanSymbols.Bytes(plan)))throw new InvalidDataException("部件变尺或来源不变失败。");
            var path=Path.Combine(Path.GetTempPath(),"WanLuoWallQa-"+Guid.NewGuid().ToString("N")+".dwg");ObjectId polyDoor;
            using(var db=new Database(true,true)) {
                db.DisableUndoRecording(false);
                using(var tx=db.TransactionManager.StartTransaction()) {
                    var table=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForWrite);var layer=new LayerTableRecord {Name="QA-Wall-墙"};table.Add(layer);tx.AddNewlyCreatedDBObject(layer,true);
                    var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
                    foreach(var line in new[]{new Line(new Point3d(0,-100,0),new Point3d(10000,-100,0)),new Line(new Point3d(10000,100,0),new Point3d(0,100,0))}){line.LayerId=layer.ObjectId;space.AppendEntity(line);tx.AddNewlyCreatedDBObject(line,true);}
                    var first=Place(db,tx,record,new Point3d(2000,0,0),0,1,new PointModel(),900,2100);
                    var second=Place(db,tx,record,new Point3d(5000,0,0),0,1,new PointModel(),900,2100);
                    CheckLength(db,tx,16400);
                    first=Place(db,tx,record,new Point3d(3000,0,0),0,1,new PointModel(),1200,2400,true,true,first);CheckLength(db,tx,15800);
                    if(ReadDoor(tx,first).Width!=1200||!ReadDoor(tx,first).FlipAcross)throw new InvalidDataException("门参数未保存。");
                    try{Place(db,tx,record,new Point3d(5300,0,0),0,1,new PointModel(),900,2100);throw new Exception("重叠未拒绝。");}catch(InvalidDataException){}
                    try{Place(db,tx,record,new Point3d(9800,0,0),0,1,new PointModel(),900,2100);throw new Exception("墙角越界未拒绝。");}catch(InvalidDataException){}
                    Remove(db,tx,first);CheckLength(db,tx,18200);Remove(db,tx,second);CheckLength(db,tx,20000);
                    var rotation=Math.PI/6;var direction=new Vector3d(Math.Cos(rotation),Math.Sin(rotation),0);var normal=new Vector3d(-direction.Y,direction.X,0);var wallOrigin=new Point3d(0,10000,0);
                    var obliqueWalls=new List<ObjectId>();foreach(var offset in new[]{-10d,10d}){var line=new Line(wallOrigin+normal*offset,wallOrigin+direction*1000+normal*offset);line.LayerId=layer.ObjectId;space.AppendEntity(line);tx.AddNewlyCreatedDBObject(line,true);obliqueWalls.Add(line.ObjectId);}
                    var oblique=Place(db,tx,record,wallOrigin+direction*200,rotation,10,new PointModel(),900,2100,true,false);CheckLength(db,tx,21820);
                    var middle=CenterOnWall(db,tx,ReadDoor(tx,oblique),900,rotation);if(middle.DistanceTo(wallOrigin+direction*455)>.001)throw new InvalidDataException("斜墙单位换算或居中失败。");
                    Remove(db,tx,oblique);CheckLength(db,tx,22000);foreach(var wallId in obliqueWalls)tx.GetObject(wallId,OpenMode.ForWrite).Erase();CheckLength(db,tx,20000);
                    var poly=new Polyline();poly.AddVertexAt(0,new Point2d(0,3000),0,0,0);poly.AddVertexAt(1,new Point2d(10000,3000),0,0,0);poly.AddVertexAt(2,new Point2d(10000,3200),0,0,0);poly.AddVertexAt(3,new Point2d(0,3200),0,0,0);poly.Closed=true;poly.LayerId=layer.ObjectId;space.AppendEntity(poly);tx.AddNewlyCreatedDBObject(poly,true);
                    polyDoor=Place(db,tx,record,new Point3d(2000,3100,0),0,1,new PointModel(),900,2100);CheckLength(db,tx,38600);document.Editor.WriteMessage("\nCAD_LIBRARY_POLY_SPLIT_OK");
                    tx.GetObject(polyDoor,OpenMode.ForWrite).Erase();Synchronize(db,tx);CheckLength(db,tx,40400);
                    polyDoor=Place(db,tx,record,new Point3d(2000,3100,0),0,1,new PointModel(),900,2100);CheckLength(db,tx,38600);document.Editor.WriteMessage("\nCAD_LIBRARY_ERASE_SYNC_OK");tx.Commit();
                }
                // A cancelled/failed transaction must leave the committed wall and door untouched.
                using(var tx=db.TransactionManager.StartTransaction()){Remove(db,tx,polyDoor);}
                using(var tx=db.TransactionManager.StartTransaction()){CheckLength(db,tx,38600);document.Editor.WriteMessage("\nCAD_LIBRARY_ROLLBACK_OK");}
                db.SaveAs(path,DwgVersion.Current);
            }
            try {using(var db=new Database(false,true)) {
                db.ReadDwgFile(path,FileOpenMode.OpenForReadAndWriteNoShare,false,"");db.CloseInput(true);
                db.DisableUndoRecording(false);
                using(var tx=db.TransactionManager.StartTransaction()){var doors=Doors(db,tx).ToArray();if(doors.Length!=1)throw new InvalidDataException("重开门洞关联丢失。");Remove(db,tx,doors[0].Key);CheckLength(db,tx,40400);tx.Commit();}
            }}finally{if(File.Exists(path))File.Delete(path);}
            document.Editor.WriteMessage("\nCAD_LIBRARY_WALL_OK layerMatch nativeLines closedPolyline multiDoor move flip resize rigidProfiles obliqueUnits center overlap corner restore rollback saveReopen unchangedCatalog");
        }
        private static void CheckLength(Database db,Transaction tx,double expected)
        {
            var sum=((BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Where(id=>!id.IsErased).Select(id=>tx.GetObject(id,OpenMode.ForRead)).OfType<Curve>().Where(curve=>curve.Visible&&IsWallLayer(curve.Layer)&&Read<string>(tx,curve,"WL_LIBRARY_CAP")==null).Sum(curve=>curve.GetDistanceAtParameter(curve.EndParam)-curve.GetDistanceAtParameter(curve.StartParam));
            if(Math.Abs(sum-expected)>.01)throw new InvalidDataException("墙洞几何长度不符："+sum+" / "+expected);
        }
    }
}
