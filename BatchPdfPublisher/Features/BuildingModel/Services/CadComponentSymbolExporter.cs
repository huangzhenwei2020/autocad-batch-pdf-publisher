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
    public static class CadComponentSymbolExporter
    {
        public static void RunNativeCheck(Document document)
        {
            using(var database=new Database(true,true)) {
                ObjectId rootId;
                using(var tx=database.TransactionManager.StartTransaction()) {
                    var table=(BlockTable)tx.GetObject(database.BlockTableId,OpenMode.ForWrite);
                    var definition=new BlockTableRecord {Name="WL_COMPONENT_SOURCE_TEST"};table.Add(definition);tx.AddNewlyCreatedDBObject(definition,true);
                    void Add(Entity e){e.SetDatabaseDefaults(database);definition.AppendEntity(e);tx.AddNewlyCreatedDBObject(e,true);}
                    Add(new Line(Point3d.Origin,new Point3d(600,0,0)));Add(new Arc(new Point3d(100,0,0),20,0,Math.PI/2));Add(new Circle(new Point3d(200,0,0),Vector3d.ZAxis,10));
                    var poly=new Polyline();poly.AddVertexAt(0,new Point2d(0,10),.414213562373095,0,0);poly.AddVertexAt(1,new Point2d(20,10),0,0,0);poly.AddVertexAt(2,new Point2d(20,30),0,0,0);poly.Closed=true;Add(poly);
                    Add(new DBText {TextString="C1216",Position=new Point3d(300,80,0),Height=20});
                    var nestedDefinition=new BlockTableRecord {Name="WL_COMPONENT_CHILD_TEST"};table.Add(nestedDefinition);tx.AddNewlyCreatedDBObject(nestedDefinition,true);
                    var nestedLine=new Line(Point3d.Origin,new Point3d(10,0,0));nestedLine.SetDatabaseDefaults(database);nestedDefinition.AppendEntity(nestedLine);tx.AddNewlyCreatedDBObject(nestedLine,true);
                    Add(new BlockReference(new Point3d(100,10,0),nestedDefinition.ObjectId));
                    var space=(BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                    var root=new BlockReference(new Point3d(1000,2000,0),definition.ObjectId) {Rotation=Math.PI/2,ScaleFactors=new Scale3d(2)};root.SetDatabaseDefaults(database);rootId=space.AppendEntity(root);tx.AddNewlyCreatedDBObject(root,true);tx.Commit();
                }
                using(var tx=database.TransactionManager.StartTransaction()) {
                    var symbol=Extract(tx,rootId,new ComponentPlanFrame(1000,2000,0,0,1,1),"C1216","Window",1200,1600);
                    if(symbol.Primitives.Count!=7||symbol.Primitives.Count(p=>p.Kind=="Arc")!=2||symbol.TextCandidates.Count!=1||Math.Abs(symbol.Primitives[0].X2-1200)>.001)
                        throw new InvalidDataException("CAD 原生提取数量、圆弧或变换错误。");
                    var settings=new CadComponentPlanSettings {BlockHandle=rootId.Handle.ToString(),Code="C1216",Category="Window",Axis="Y",
                        X=1000,Y=2000,Z=0,Width=1200,Height=1600,MillimetresPerCadUnit=1};
                    var fromUi=Extract(tx,rootId,Frame(settings,Matrix3d.Identity),settings.Code,settings.Category,settings.Width,settings.Height);
                    if(Math.Abs(fromUi.Primitives[0].X2-1200)>.001||Math.Abs(fromUi.Primitives[0].Y2)>.001)throw new InvalidDataException("UI Y 方向错误。");
                    settings.Axis="X";settings.X=2000;settings.Y=-1000;
                    fromUi=Extract(tx,rootId,Frame(settings,Matrix3d.Rotation(Math.PI/2,Vector3d.ZAxis,Point3d.Origin)),settings.Code,settings.Category,settings.Width,settings.Height);
                    if(Math.Abs(fromUi.Primitives[0].X2-1200)>.001||Math.Abs(fromUi.Primitives[0].Y2)>.001)throw new InvalidDataException("UI 旋转 UCS X 方向错误。");
                    var root=(BlockReference)tx.GetObject(rootId,OpenMode.ForWrite);root.ScaleFactors=new Scale3d(-2,2,2);
                    var mirrored=Extract(tx,rootId,new ComponentPlanFrame(1000,2000,0,0,1,1),"C1216","Window",1200,1600);
                    if(mirrored.Primitives.Where(p=>p.Kind=="Arc").Any(p=>p.SweepDegrees>=0))throw new InvalidDataException("CAD 镜像开启弧方向错误。");
                    root.ScaleFactors=new Scale3d(2,3,1);var refused=false;
                    try{Extract(tx,rootId,new ComponentPlanFrame(1000,2000,0,0,1,1),"C1216","Window",1200,1600);}catch(InvalidDataException){refused=true;}
                    if(!refused)throw new InvalidDataException("非等比圆弧未拒绝。");
                    // No commit: all mutations are isolated to a temporary side database.
                }
                document.Editor.WriteMessage("\nCAD_COMPONENT_SOURCE_NATIVE_OK line arc circle bulge nested rotation scale mirror textCandidate nonuniformReject temporaryDatabase ui-Y ui-X-rotatedUcs");
            }
        }
        public static void Execute(Document document, string category = "Window", Func<ComponentPlanSymbol,string> store = null,ComponentPlanSymbol initial=null)
        {
            if(document==null)return;var editor=document.Editor;
            try {
                // Keep one coordinate frame for numeric input and all subsequent picks.
                var ucs=editor.CurrentUserCoordinateSystem;
                if(!ucs.CoordinateSystem3d.Zaxis.IsParallelTo(Vector3d.ZAxis))
                    throw new InvalidDataException("请先切换到与世界 XY 平行的 CAD 坐标系。");
                var window=new CadComponentPlanWindow(category,
                    owner => {
                        using(editor.StartUserInteraction(owner)) {
                            var options=new PromptEntityOptions("\n选择门或窗的平面块：");
                            options.SetRejectMessage("\n请选择块参照。");options.AddAllowedClass(typeof(BlockReference),true);
                            var selected=editor.GetEntity(options);if(selected.Status!=PromptStatus.OK)return null;
                            using(var tx=document.Database.TransactionManager.StartTransaction()) {
                                var block=(BlockReference)tx.GetObject(selected.ObjectId,OpenMode.ForRead);
                                var record=(BlockTableRecord)tx.GetObject(block.BlockTableRecord,OpenMode.ForRead);
                                return new CadComponentPlanBlock {Handle=selected.ObjectId.Handle.ToString(),Name=record.Name};
                            }
                        }
                    },
                    owner => {
                        using(editor.StartUserInteraction(owner)) {
                            var picked=editor.GetPoint("\n拾取洞口起端、墙参考线上的基点：");
                            if(picked.Status!=PromptStatus.OK)return null;
                            var point=picked.Value.TransformBy(editor.CurrentUserCoordinateSystem).TransformBy(ucs.Inverse());
                            return new[]{point.X,point.Y,point.Z};
                        }
                    },
                    (owner,settings) => {
                        ComponentPlanSymbol symbol;
                        using(var tx=document.Database.TransactionManager.StartTransaction()) {
                            var id=document.Database.GetObjectId(false,new Handle(Convert.ToInt64(settings.BlockHandle,16)),0);
                            symbol=Extract(tx,id,Frame(settings,ucs),settings.Code,settings.Category,settings.Width,settings.Height);
                        }
                        if(store!=null)return store(symbol);
                        using(var save=new System.Windows.Forms.SaveFileDialog {Title=settings.Category=="Door"?"导出门平面":"导出窗平面",
                            Filter="CAD 构件符号 (*.wlplan.json)|*.wlplan.json",FileName=SafeFileName(symbol.Code)+".wlplan.json",OverwritePrompt=true}) {
                            if(save.ShowDialog(new DialogOwner(owner))!=System.Windows.Forms.DialogResult.OK)return null;
                            ComponentPlanSymbols.Save(save.FileName,symbol);
                            editor.WriteMessage("\n已导出原生矢量符号："+save.FileName);
                            return "已导出："+save.FileName+"\n图元 "+symbol.Primitives.Count+" · 编号候选 "+symbol.TextCandidates.Count;
                        }
                    },store==null?"导出平面":"平面入库");
                if(initial!=null)window.SetInitialPlan(initial);
                Application.ShowModalWindow(window);
            }catch(Exception ex){editor.WriteMessage("\n构件符号未导出："+ex.Message);}
        }

        private sealed class DialogOwner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle {get;}
            public DialogOwner(System.Windows.Window window){Handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;}
        }
        private static string SafeFileName(string code)=>new string(code.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c).ToArray());
        public static ComponentPlanFrame Frame(CadComponentPlanSettings settings,Matrix3d ucs)
        {
            settings.Validate();
            if(!ucs.CoordinateSystem3d.Zaxis.IsParallelTo(Vector3d.ZAxis))throw new InvalidDataException("墙方向必须在水平平面内。");
            var point=new Point3d(settings.X,settings.Y,settings.Z).TransformBy(ucs);
            var axis=(settings.Axis=="X"?Vector3d.XAxis:Vector3d.YAxis).TransformBy(ucs);
            return new ComponentPlanFrame(point.X,point.Y,point.Z,axis.X,axis.Y,settings.MillimetresPerCadUnit);
        }

        public static ComponentPlanSymbol Extract(Transaction transaction,ObjectId blockId,ComponentPlanFrame frame,string code,string category,double width,double height)
        {
            var symbol=new ComponentPlanSymbol {Name=code,Code=code,Category=category,Width=width,Height=height};
            var issues=new List<string>();var stack=new HashSet<ObjectId>();var visited=0;
            PointModel Point(Point3d point,List<Matrix3d> chain) {
                foreach(var matrix in chain)point=point.TransformBy(matrix);return frame.Point(point.X,point.Y,point.Z);
            }
            void Add(ComponentPlanPrimitive primitive,Entity source,string path) {
                primitive.SourcePath=path;primitive.SourceLayer=source.Layer;primitive.SourceLineType=source.Linetype;symbol.Primitives.Add(primitive);
                if(symbol.Primitives.Count>ComponentPlanSymbols.MaxPrimitives)throw new InvalidDataException("图元数量超过 8192。");
            }
            void CurveArc(Entity source,string path,Point3d center,Point3d start,Vector3d normal,double angle,bool circle,List<Matrix3d> chain) {
                var radial=start-center;var tangent=normal.GetNormal().CrossProduct(radial);
                Add(frame.Arc(Point(center,chain),Point(start,chain),Point(center+tangent,chain),angle,circle),source,path);
            }
            void Visit(Entity entity,List<Matrix3d> chain,string path,int depth) {
                if(++visited>20000||depth>16)throw new InvalidDataException("块嵌套或实体数量超限。");
                if(!entity.Visible)return;
                if(!entity.LayerId.IsNull) {
                    var layer=(LayerTableRecord)transaction.GetObject(entity.LayerId,OpenMode.ForRead);
                    if(layer.IsOff||layer.IsFrozen)return;
                }
                try {
                    var block=entity as BlockReference;
                    if(block!=null) {
                        var record=(BlockTableRecord)transaction.GetObject(block.BlockTableRecord,OpenMode.ForRead);
                        if(record.IsFromExternalReference||record.IsFromOverlayReference)throw new InvalidDataException("不支持外部参照。");
                        if(!stack.Add(record.ObjectId))throw new InvalidDataException("块循环引用。");
                        var nested=new List<Matrix3d>(chain);nested.Insert(0,block.BlockTransform);
                        try {
                            foreach(ObjectId id in record) {
                                var child=transaction.GetObject(id,OpenMode.ForRead) as Entity;
                                if(child!=null)Visit(child,nested,path+"/"+id.Handle,depth+1);
                            }
                            foreach(ObjectId id in block.AttributeCollection) {
                                var attribute=transaction.GetObject(id,OpenMode.ForRead) as AttributeReference;
                                if(attribute!=null&&!attribute.Invisible)Text(attribute.TextString,attribute.Position,chain);
                            }
                        }finally{stack.Remove(record.ObjectId);}return;
                    }
                    var line=entity as Line;
                    if(line!=null){var a=Point(line.StartPoint,chain);var b=Point(line.EndPoint,chain);Add(new ComponentPlanPrimitive {Kind="Line",X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y},entity,path);return;}
                    var arc=entity as Arc;
                    if(arc!=null){CurveArc(entity,path,arc.Center,arc.StartPoint,arc.Normal,arc.TotalAngle,false,chain);return;}
                    var circle=entity as Circle;
                    if(circle!=null){var axis=circle.Normal.GetPerpendicularVector().GetNormal();CurveArc(entity,path,circle.Center,circle.Center+axis*circle.Radius,circle.Normal,Math.PI*2,true,chain);return;}
                    var poly=entity as Polyline;
                    if(poly!=null) {
                        var count=poly.Closed?poly.NumberOfVertices:poly.NumberOfVertices-1;
                        for(var i=0;i<count;i++) {
                            var bulge=poly.GetBulgeAt(i);
                            if(Math.Abs(bulge)<.000000001) {var a=Point(poly.GetPoint3dAt(i),chain);var b=Point(poly.GetPoint3dAt((i+1)%poly.NumberOfVertices),chain);Add(new ComponentPlanPrimitive {Kind="Line",X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y},entity,path+":"+i);}
                            else {using(var segment=poly.GetArcSegmentAt(i))CurveArc(entity,path+":"+i,segment.Center,poly.GetPoint3dAt(i),poly.Normal*Math.Sign(bulge),Math.Abs(4*Math.Atan(bulge)),false,chain);}
                        }return;
                    }
                    var legacy=entity as Polyline2d;
                    if(legacy!=null) {
                        var exploded=new DBObjectCollection();legacy.Explode(exploded);
                        try {var index=0;foreach(DBObject item in exploded){var child=item as Entity;if(child==null)throw new InvalidDataException("多段线包含未知对象。");Visit(child,chain,path+":legacy-"+index++,depth+1);}}
                        finally{foreach(DBObject item in exploded)item.Dispose();}return;
                    }
                    var definition=entity as AttributeDefinition;if(definition!=null){if(definition.Constant&&!definition.Invisible)Text(definition.TextString,definition.Position,chain);return;}
                    var text=entity as DBText;if(text!=null){Text(text.TextString,text.Position,chain);return;}
                    var mtext=entity as MText;if(mtext!=null){Text(mtext.Text,mtext.Location,chain);return;}
                    throw new InvalidDataException("不支持实体 "+entity.GetType().Name+"。");
                }catch(InvalidDataException ex){issues.Add(path+"："+ex.Message);}
            }
            void Text(string text,Point3d position,List<Matrix3d> chain) {
                var point=Point(position,chain);symbol.TextCandidates.Add(new ComponentPlanTextCandidate {Text=text,X=point.X,Y=point.Y});
            }
            var root=transaction.GetObject(blockId,OpenMode.ForRead) as BlockReference;
            if(root==null)throw new InvalidDataException("请选择块参照。");
            Visit(root,new List<Matrix3d>(),blockId.Handle.ToString(),0);
            if(issues.Count>0)throw new InvalidDataException("存在 "+issues.Count+" 项不能正确提取的实体，本次没有导出：\n"+string.Join("\n",issues.Take(8)));
            ComponentPlanSymbols.Validate(symbol);return symbol;
        }
    }
}
