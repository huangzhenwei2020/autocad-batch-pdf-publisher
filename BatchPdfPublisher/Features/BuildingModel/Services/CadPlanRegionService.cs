using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

namespace BatchPdfPublisher.Services
{
    public static class CadPlanRegionService
    {
        public static void Execute(Document document)
        {
            if(document==null)return;var editor=document.Editor;var first=editor.GetPoint("\n框选要编辑的区域，指定第一角点：");if(first.Status!=PromptStatus.OK)return;var second=editor.GetCorner(new PromptCornerOptions("\n指定对角点：",first.Value));if(second.Status!=PromptStatus.OK)return;
            var ucs=editor.CurrentUserCoordinateSystem;if(!ucs.CoordinateSystem3d.Zaxis.IsCodirectionalTo(Vector3d.ZAxis))throw new InvalidDataException("请在朝上的水平 UCS 中框选区域。");
            var selection=editor.SelectCrossingWindow(first.Value.TransformBy(ucs),second.Value.TransformBy(ucs));if(selection.Status!=PromptStatus.OK)return;
            var db=document.Database;var ids=selection.Value.GetObjectIds();var drafts=new List<CadPlanDoorDraft>();var walls=new HashSet<ObjectId>();var staticLines=new List<ComponentPlanPrimitive>();var originals=new Dictionary<string,string>();
            using(var tx=db.TransactionManager.StartTransaction())foreach(var id in ids){var entity=tx.GetObject(id,OpenMode.ForRead) as Entity;if(entity==null)continue;var block=entity as BlockReference;var state=block==null?null:CadLibraryDoorPlacement.ReadDoor(tx,id);
                if(state!=null){var p=CadLibraryDoorPlacement.OpeningOrigin(db,tx,block,state);var plan=ComponentPlanSymbols.Load(state.Plan);drafts.Add(new CadPlanDoorDraft {Key=block.Handle.ToString(),AssetId=state.AssetId,Name=plan.Name,Source=plan,X=p.X,Y=p.Y,Z=p.Z,Angle=block.Rotation,Units=state.Units,BaseX=state.BaseX,BaseY=state.BaseY,Width=state.Width,Height=state.Height,FlipAlong=state.FlipAlong,FlipAcross=state.FlipAcross,Code=state.Code??ComponentPlanSymbols.ReferenceCode(ComponentPlanSymbols.DoorPlanVariant(plan,new PointModel(state.BaseX,state.BaseY),state.Width,state.Height)),OpeningAngle=state.OpeningAngle,LabelAlong=state.LabelAlong,LabelNormal=state.LabelNormal,LabelHeight=Math.Max(.1,db.Textsize)*state.Units,Changed=!state.HasWallOrigin});var centerY=CadLibraryDoorPlacement.FrameCenterY(ComponentPlanSymbols.DoorPlanVariant(plan,new PointModel(state.BaseX,state.BaseY),state.Width,state.Height),new PointModel(state.BaseX,state.BaseY));var actual=new Point3d(state.BaseX,state.BaseY+centerY,0).TransformBy(block.BlockTransform);var expected=p+(state.FlipAlong?new Vector3d(Math.Cos(block.Rotation),Math.Sin(block.Rotation),0)*(state.Width/state.Units):new Vector3d(0,0,0));drafts[drafts.Count-1].Changed|=actual.DistanceTo(expected)>1e-6||state.Caps==null||state.Caps.Count!=2;originals[block.Handle.ToString()]=BlockSignature(block,state);foreach(var c in state.Cuts)walls.Add(Id(db,c.Wall));continue;}
                var source=CadLibraryDoorPlacement.ManagedWall(tx,entity);if(source!=null){walls.Add(Id(db,source));continue;}if(CadLibraryDoorPlacement.IsManagedCap(tx,entity))continue;
                if(entity is Curve&&CadLibraryDoorPlacement.IsWallLayer(entity.Layer)){walls.Add(id);continue;}if(entity.Visible)ReadLines(tx,entity,Matrix3d.Identity,staticLines,0);
            }
            if(drafts.Count==0){editor.WriteMessage("\n该区域没有沿墙插入的图库门。先在图库勾选“沿墙自动开门洞”插入门，再框选编辑。普通门窗作为背景显示，不会被转换或改写。");return;}
            var cornerA=first.Value.TransformBy(ucs);var cornerB=second.Value.TransformBy(ucs);var window=new CadPlanRegionWindow(drafts) {RegionBounds=new System.Windows.Rect(new System.Windows.Point(cornerA.X,cornerA.Y),new System.Windows.Point(cornerB.X,cornerB.Y))};
            window.PreviewRequested=all=>Preview(db,all,walls,staticLines);
            window.CenterRequested=()=>window.Edit(d=>{using(var tx=db.TransactionManager.StartTransaction()){var host=CadLibraryDoorPlacement.Preview(db,tx,d.Source,new PointModel(d.BaseX,d.BaseY),new Point3d(d.X,d.Y,d.Z),d.Angle,d.Units,d.Width,d.Height,d.FlipAlong,d.FlipAcross,d.Code,d.OpeningAngle,walls);var p=CadLibraryDoorPlacement.CenterOnWall(db,tx,new CadLibraryDoorState {Cuts=host.Cuts,Units=d.Units},d.Width,host.Angle);d.X=p.X;d.Y=p.Y;}},!window.Scene.HasGesture);
            window.SubmitRequested=all=>{Commit(db,all,originals,walls);editor.Regen();editor.WriteMessage("\n区域编辑已提交。可在 CAD 中 Ctrl+Z 撤销。");};
            window.RefreshPreview();Application.ShowModalWindow(window);
        }
        private static void Commit(Database db,IList<CadPlanDoorDraft> all,IDictionary<string,string> originals,ISet<ObjectId> walls)
        {
            using(var tx=db.TransactionManager.StartTransaction()){
                foreach(var d in all){var id=Id(db,d.Key);if(id.IsErased)throw new InvalidDataException("CAD 门已被删除，请重新框选。");var block=(BlockReference)tx.GetObject(id,OpenMode.ForRead);if(BlockSignature(block,CadLibraryDoorPlacement.ReadDoor(tx,id))!=originals[d.Key])throw new InvalidDataException("CAD 门已改变，请重新框选后编辑。");}
                foreach(var d in all.Where(d=>d.Changed))CadLibraryDoorPlacement.Remove(db,tx,Id(db,d.Key));
                foreach(var d in all.Where(d=>d.Changed&&!d.Deleted)){var record=new ComponentCatalogRecord {AssetId=d.AssetId,Version=1,Plan=d.Source,PlanHash=ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(d.Source))};CadLibraryDoorPlacement.Place(db,tx,record,new Point3d(d.X,d.Y,d.Z),d.Angle,d.Units,new PointModel(d.BaseX,d.BaseY),d.Width,d.Height,d.FlipAlong,d.FlipAcross,default(ObjectId),d.Code,d.OpeningAngle,walls,d.LabelAlong,d.LabelNormal);}
                tx.Commit();
            }
        }
        public static void RunNativeCheck(Document document)
        {
            var plan=new ComponentPlanSymbol {SchemaVersion=3,Category="Door",DoorAssembly="SingleSwing",Code="M0921",Name="区域编辑回归",Width=900,Height=2100,
                Primitives={new ComponentPlanPrimitive {Kind="Line",Y1=-20,Y2=180},new ComponentPlanPrimitive {Kind="Line",X1=900,X2=900,Y1=-20,Y2=180},new ComponentPlanPrimitive {Kind="Line",Y1=80,Y2=980},new ComponentPlanPrimitive {Kind="Line",X1=40,X2=40,Y1=80,Y2=980},new ComponentPlanPrimitive {Kind="Arc",Y1=80,Radius=900,SweepDegrees=90}},
                Parts=new List<ComponentPlanPart> {new ComponentPlanPart {Name="门套",Role="Casing",Primitives={0,1}},new ComponentPlanPart {Name="主扇",Role="PrimaryLeaf",Primitives={2,3}},new ComponentPlanPart {Name="开启",Role="OpeningSymbol",Primitives={4}}}};
            var originalBytes=ComponentPlanSymbols.Bytes(plan);var asset=Guid.NewGuid().ToString("D");var record=new ComponentCatalogRecord {AssetId=asset,Version=1,Plan=plan,PlanHash=ComponentCatalog.Hash(originalBytes)};
            using(var db=new Database(true,true)){
                db.DisableUndoRecording(false);var walls=new HashSet<ObjectId>();var originals=new Dictionary<string,string>();var drafts=new List<CadPlanDoorDraft>();
                using(var tx=db.TransactionManager.StartTransaction()){
                    var layers=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForWrite);var layer=new LayerTableRecord {Name="QA-Wall-墙"};var layerId=layers.Add(layer);tx.AddNewlyCreatedDBObject(layer,true);var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
                    foreach(var y in new[]{-100d,100d}){var wall=new Line(new Point3d(0,y,0),new Point3d(10000,y,0)) {LayerId=layerId};space.AppendEntity(wall);tx.AddNewlyCreatedDBObject(wall,true);walls.Add(wall.ObjectId);}
                    foreach(var x in new[]{2000d,5000d}){var id=CadLibraryDoorPlacement.Place(db,tx,record,new Point3d(x,0,0),0,1,new PointModel(),900,2100);var block=(BlockReference)tx.GetObject(id,OpenMode.ForRead);var state=CadLibraryDoorPlacement.ReadDoor(tx,id);if(Math.Abs(block.Position.Y+80)>.001||state.Caps.Count!=2)throw new InvalidDataException("门套没有对齐墙中线或封口缺失。");foreach(var capHandle in state.Caps){var cap=(Line)tx.GetObject(Id(db,capHandle),OpenMode.ForRead);if(Math.Abs(cap.Length-200)>.001)throw new InvalidDataException("封口未跨越两条墙线。");}
                        drafts.Add(new CadPlanDoorDraft {Key=block.Handle.ToString(),AssetId=asset,Name=plan.Name,Code="M0921",Source=plan,X=x,Y=0,Width=900,Height=2100,Changed=true});originals[block.Handle.ToString()]=BlockSignature(block,state);}
                    tx.Commit();
                }
                var first=drafts[0];first.Width=1200;first.Code="D-01";first.OpeningAngle=45;first.FlipAcross=true;var second=drafts[1];second.X=6000;
                var geometry=Preview(db,drafts,walls,new List<ComponentPlanPrimitive>());if(geometry.Count<8||Math.Abs(first.Preview.Primitives[0].Y1-100)>.001||Math.Abs(first.Preview.Primitives[0].Y2+100)>.001)throw new InvalidDataException("镜像门套预览没有居中。");
                using(var tx=db.TransactionManager.StartTransaction())foreach(var d in drafts)if(Id(db,d.Key).IsErased||CadLibraryDoorPlacement.ReadDoor(tx,Id(db,d.Key)).Width!=900)throw new InvalidDataException("草稿预览写入了 CAD。");
                second.X=2500;var rejected=false;try{Commit(db,drafts,originals,walls);}catch(InvalidDataException){rejected=true;}if(!rejected)throw new InvalidDataException("区域提交允许重叠门洞。");
                using(var tx=db.TransactionManager.StartTransaction())foreach(var d in drafts)if(Id(db,d.Key).IsErased||CadLibraryDoorPlacement.ReadDoor(tx,Id(db,d.Key)).Width!=900)throw new InvalidDataException("批量失败没有恢复原门。");
                second.X=6000;Commit(db,drafts,originals,walls);
                using(var tx=db.TransactionManager.StartTransaction()){
                    var placed=((BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Where(id=>!id.IsErased).Select(id=>CadLibraryDoorPlacement.ReadDoor(tx,id)).Where(s=>s!=null).ToArray();var edited=placed.Single(s=>s.Code=="D-01");if(placed.Length!=2||edited.Width!=1200||edited.OpeningAngle!=45||!edited.FlipAcross||edited.Caps.Count!=2)throw new InvalidDataException("区域提交参数未持久化。");
                    var editedId=((BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Where(v=>!v.IsErased).Single(v=>CadLibraryDoorPlacement.ReadDoor(tx,v)?.Code=="D-01");var block=(BlockReference)tx.GetObject(editedId,OpenMode.ForRead);var body=(BlockTableRecord)tx.GetObject(block.BlockTableRecord,OpenMode.ForRead);var arc=body.Cast<ObjectId>().Select(v=>tx.GetObject(v,OpenMode.ForRead)).OfType<Arc>().Single();if(Math.Abs(arc.TotalAngle-Math.PI/4)>1e-6)throw new InvalidDataException("开启角度未落入原生 CAD 弧。");
                }
            }
            foreach(var angle in new[]{Math.PI/5,-Math.PI/3,Math.PI/2})using(var db=new Database(true,true)){
                db.DisableUndoRecording(false);const double units=2;var origin=new Point3d(32000,-27000,0);var along=new Vector3d(Math.Cos(angle),Math.Sin(angle),0);var normal=new Vector3d(-along.Y,along.X,0);var walls=new HashSet<ObjectId>();var originals=new Dictionary<string,string>();var drafts=new List<CadPlanDoorDraft>();
                using(var tx=db.TransactionManager.StartTransaction()){
                    var layers=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForWrite);var layer=new LayerTableRecord {Name="QA-diagonal-wall"};var layerId=layers.Add(layer);tx.AddNewlyCreatedDBObject(layer,true);var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
                    foreach(var side in new[]{-1d,1d}){var a=origin+normal*(side*100/units);var b=a+along*(10000/units);var wall=new Polyline {LayerId=layerId};wall.AddVertexAt(0,new Point2d(a.X,a.Y),0,0,0);wall.AddVertexAt(1,new Point2d(b.X,b.Y),0,0,0);space.AppendEntity(wall);tx.AddNewlyCreatedDBObject(wall,true);walls.Add(wall.ObjectId);}
                    var at=origin+along*(1800/units);var id=CadLibraryDoorPlacement.Place(db,tx,record,at,angle,units,new PointModel(),900,2100);var block=(BlockReference)tx.GetObject(id,OpenMode.ForRead);var state=CadLibraryDoorPlacement.ReadDoor(tx,id);if(new Point3d(0,80,0).TransformBy(block.BlockTransform).DistanceTo(at)>1e-5)throw new InvalidDataException("斜墙门套中心偏离墙中线。");
                    drafts.Add(new CadPlanDoorDraft {Key=block.Handle.ToString(),AssetId=asset,Name=plan.Name,Code="D-diagonal",Source=plan,X=at.X,Y=at.Y,Angle=angle,Units=units,Width=1200,Height=2100,FlipAcross=true,OpeningAngle=60,LabelAlong=160,LabelNormal=-90,Changed=true});originals[block.Handle.ToString()]=BlockSignature(block,state);tx.Commit();
                }
                Preview(db,drafts,walls,new List<ComponentPlanPrimitive>());var d=drafts[0];if(new Point3d(d.WallStartX,d.WallStartY,0).DistanceTo(origin)>1e-5||Math.Abs(d.WallThickness-100)>1e-5)throw new InvalidDataException("斜墙公共墙段或单位换算错误。");
                using(var tx=db.TransactionManager.StartTransaction()){var p=CadLibraryDoorPlacement.Preview(db,tx,plan,new PointModel(),new Point3d(d.X,d.Y,0),angle,units,d.Width,d.Height,false,true,d.Code,d.OpeningAngle,walls);var center=CadLibraryDoorPlacement.CenterOnWall(db,tx,new CadLibraryDoorState {Cuts=p.Cuts,Units=units},d.Width,p.Angle);if(center.DistanceTo(origin+along*((10000-d.Width)/2/units))>1e-5)throw new InvalidDataException("斜墙右键居中错误。");}
                Commit(db,drafts,originals,walls);
                using(var tx=db.TransactionManager.StartTransaction()){
                    var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForRead);var id=space.Cast<ObjectId>().Where(v=>!v.IsErased).Single(v=>CadLibraryDoorPlacement.ReadDoor(tx,v)?.Code=="D-diagonal");var state=CadLibraryDoorPlacement.ReadDoor(tx,id);var block=(BlockReference)tx.GetObject(id,OpenMode.ForRead);var at=new Point3d(d.X,d.Y,0);if(new Point3d(0,80,0).TransformBy(block.BlockTransform).DistanceTo(at)>1e-5)throw new InvalidDataException("斜墙镜像后门套中心偏移。");
                    foreach(var h in state.Caps){var cap=(Line)tx.GetObject(Id(db,h),OpenMode.ForRead);if(Math.Abs(cap.Length-100)>1e-5||Math.Abs((cap.EndPoint-cap.StartPoint).DotProduct(along))>1e-5)throw new InvalidDataException("斜墙洞口封口方向或长度错误。");}
                    var label=block.AttributeCollection.Cast<ObjectId>().Select(v=>tx.GetObject(v,OpenMode.ForRead)).OfType<AttributeReference>().Single(v=>v.Tag=="WL_CODE");var expected=CadLibraryDoorPlacement.LabelPoint(at,angle,d.Width,units,100,160,-90);if(label.Position.DistanceTo(expected)>1e-5||state.LabelAlong!=160||state.LabelNormal!=-90)throw new InvalidDataException("斜墙编号移动未正确提交。");
                }
            }
            using(var db=new Database(true,true)){
                db.DisableUndoRecording(false);var angle=Math.PI/7;var along=new Vector3d(Math.Cos(angle),Math.Sin(angle),0);var normal=new Vector3d(-along.Y,along.X,0);var origin=new Point3d(15000,31000,0);var source=ComponentPlanSymbols.Load(originalBytes);source.Primitives.Add(new ComponentPlanPrimitive {Kind="Line",X1=-30,X2=70,Y1=880,Y2=880});source.Parts.Add(new ComponentPlanPart {Name="把手",Role="Handle",Primitives={5}});var originalCount=0;
                using(var tx=db.TransactionManager.StartTransaction()){var layers=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForWrite);var layer=new LayerTableRecord {Name="QA-jig-wall"};var layerId=layers.Add(layer);tx.AddNewlyCreatedDBObject(layer,true);var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);foreach(var side in new[]{-1,1}){var a=origin+normal*(side*100);var wall=new Line(a,a+along*10000) {LayerId=layerId};space.AppendEntity(wall);tx.AddNewlyCreatedDBObject(wall,true);}originalCount=space.Cast<ObjectId>().Count();tx.Commit();}
                CadLibraryDoorPlacement.PlanPreview host;
                using(var tx=db.TransactionManager.StartTransaction()){
                    var centered=CadLibraryDoorPlacement.InitialPreview(db,tx,source,new PointModel(),origin+along*200,angle,1,900,2100,true,100,200,"M0921",90);if(centered.Start.DistanceTo(origin+along*4550)>1e-5)throw new InvalidDataException("插入前居中未按斜墙计算。");
                    var edge=CadLibraryDoorPlacement.InitialPreview(db,tx,source,new PointModel(),origin+along*9500,angle,1,900,2100,false,100,200,"M0921",90);if(edge.Start.DistanceTo(origin+along*9000)>1e-5)throw new InvalidDataException("较近终点门垛位置错误。");
                    host=CadLibraryDoorPlacement.InitialPreview(db,tx,source,new PointModel(),origin+along*100,angle,1,900,2100,false,0,200,"M0921",90);if(host.Start.DistanceTo(origin)>1e-5)throw new InvalidDataException("零门垛位置错误。");
                    var rejected=false;try{CadLibraryDoorPlacement.InitialPreview(db,tx,source,new PointModel(),origin+along*2000,angle,1,900,2100,false,100,120,"M0921",90);}catch(InvalidDataException){rejected=true;}if(!rejected)throw new InvalidDataException("墙厚约束未参与双线墙识别。");
                }
                var jig=new CadDoorDirectionJig(db,source,new PointModel(),1,900,2100,"M0921",90,host);var center=host.Start+along*450;var handle=ComponentPlanSymbols.DirectionHandle(host.Plan);var reference=new Point3d(handle.X,handle.Y,0)-center;
                foreach(var a in new[]{-1,1})foreach(var b in new[]{-1,1}){jig.SetDirection(center+along*(a*700)+normal*(b*700));if(jig.FlipAlong!=((a<0)!=(reference.DotProduct(along)<0))||jig.FlipAcross!=((b<0)!=(reference.DotProduct(normal)<0)))throw new InvalidDataException("鼠标开启方向四象限计算错误。");}
                using(var tx=db.TransactionManager.StartTransaction()){if(((BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForRead)).Cast<ObjectId>().Count()!=originalCount)throw new InvalidDataException("方向预览写入 CAD。");var inserted=CadLibraryDoorPlacement.Place(db,tx,new ComponentCatalogRecord {AssetId=asset,Version=1,Plan=source,PlanHash=ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(source))},jig.Preview.Start,jig.Preview.Angle,1,new PointModel(),900,2100,jig.FlipAlong,jig.FlipAcross,default(ObjectId),"M0921",90);if(CadLibraryDoorPlacement.ReadDoor(tx,inserted).Caps.Count!=2)throw new InvalidDataException("零门垛未封口。");CadLibraryDoorPlacement.Remove(db,tx,inserted);tx.Commit();}
            }
            if(!originalBytes.SequenceEqual(ComponentPlanSymbols.Bytes(plan)))throw new InvalidDataException("区域编辑写回了共享资源源图。");
            document.Editor.WriteMessage("\nCAD_REGION_NATIVE_OK insertionCenter nearEndJamb zeroJamb thicknessFilter mouseDirection transientPreview diagonalPolyline rotatedUnits diagonalCenter labelPersistence frameCentered capClosure mirroredPreview deferredDraft code angle atomicBatch rollback unchangedCatalog");
        }
        private static IList<ComponentPlanPrimitive> Preview(Database db,IList<CadPlanDoorDraft> drafts,ISet<ObjectId> regionWalls,IList<ComponentPlanPrimitive> background)
        {
            var lines=new List<ComponentPlanPrimitive>(background);var excluded=new HashSet<string>(drafts.Select(d=>d.Key));
            using(var tx=db.TransactionManager.StartTransaction()){
                var cuts=new List<CadDoorCut>(CadLibraryDoorPlacement.ActiveCuts(db,tx,excluded));var walls=new HashSet<ObjectId>(regionWalls);
                foreach(var d in drafts.Where(d=>!d.Deleted)){
                    var p=CadLibraryDoorPlacement.Preview(db,tx,d.Source,new PointModel(d.BaseX,d.BaseY),new Point3d(d.X,d.Y,d.Z),d.Angle,d.Units,d.Width,d.Height,d.FlipAlong,d.FlipAcross,d.Code,d.OpeningAngle,regionWalls);
                    foreach(var cut in p.Cuts){if(cuts.Any(other=>cut.Wall==other.Wall&&Math.Min(cut.End,other.End)-Math.Max(cut.Start,other.Start)>1e-8))throw new InvalidDataException("门洞重叠，草稿未应用。请移动位置或减小宽度。");cuts.Add(cut);walls.Add(Id(db,cut.Wall));}
                    d.X=p.Start.X;d.Y=p.Start.Y;d.Angle=p.Angle;d.Preview=p.Plan;d.WallStartX=p.WallStart.X;d.WallStartY=p.WallStart.Y;d.WallEndX=p.WallEnd.X;d.WallEndY=p.WallEnd.Y;d.WallThickness=p.Thickness;
                    foreach(var cap in CadLibraryDoorPlacement.PreviewCaps(db,tx,p.Cuts,p.Angle))using(cap)ReadLines(tx,cap,Matrix3d.Identity,lines,0);
                }
                foreach(var id in walls){if(id.IsErased)throw new InvalidDataException("区域墙线已改变，请重新框选。");var wall=tx.GetObject(id,OpenMode.ForRead) as Curve;if(wall==null)continue;foreach(var piece in CadLibraryDoorPlacement.PreviewWall(wall,cuts))using(piece)ReadLines(tx,piece,Matrix3d.Identity,lines,0);}
            }return lines;
        }
        private static string BlockSignature(BlockReference block,CadLibraryDoorState state)=>block.Position+"|"+block.Rotation+"|"+block.ScaleFactors+"|"+block.BlockTableRecord.Handle+"|"+block.Layer+"|"+(state==null?"missing":ComponentCatalog.Hash(state.Plan));
        private static ObjectId Id(Database db,string handle)=>db.GetObjectId(false,new Handle(long.Parse(handle,System.Globalization.NumberStyles.HexNumber)),0);
        private static void ReadLines(Transaction tx,Entity entity,Matrix3d transform,IList<ComponentPlanPrimitive> lines,int depth)
        {
            if(depth>12||lines.Count>24000)throw new InvalidDataException("框选区域图元过多，请缩小范围。");
            var block=entity as BlockReference;if(block!=null){foreach(ObjectId id in (BlockTableRecord)tx.GetObject(block.BlockTableRecord,OpenMode.ForRead)){var child=tx.GetObject(id,OpenMode.ForRead) as Entity;if(child!=null&&child.Visible)ReadLines(tx,child,transform*block.BlockTransform,lines,depth+1);}return;}
            var curve=entity as Curve;if(curve==null)return;var line=curve as Line;if(line!=null){Add(lines,line.StartPoint.TransformBy(transform),line.EndPoint.TransformBy(transform));return;}
            var poly=curve as Polyline;if(poly!=null){for(var i=0;i<poly.NumberOfVertices-(poly.Closed?0:1);i++){if(Math.Abs(poly.GetBulgeAt(i))<1e-9)Add(lines,poly.GetPoint3dAt(i).TransformBy(transform),poly.GetPoint3dAt((i+1)%poly.NumberOfVertices).TransformBy(transform));else {var start=poly.GetPointAtParameter(i);for(var step=1;step<=24;step++){var end=poly.GetPointAtParameter(i+step/24d);Add(lines,start.TransformBy(transform),end.TransformBy(transform));start=end;}}}return;}
            var count=curve is Circle?96:48;var previous=curve.StartPoint;for(var i=1;i<=count;i++){var next=curve.GetPointAtParameter(curve.StartParam+(curve.EndParam-curve.StartParam)*i/count);Add(lines,previous.TransformBy(transform),next.TransformBy(transform));previous=next;}
        }
        private static void Add(IList<ComponentPlanPrimitive> lines,Point3d a,Point3d b){if(a.DistanceTo(b)<1e-8)return;lines.Add(new ComponentPlanPrimitive {Kind="Line",X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y});}
    }
}
