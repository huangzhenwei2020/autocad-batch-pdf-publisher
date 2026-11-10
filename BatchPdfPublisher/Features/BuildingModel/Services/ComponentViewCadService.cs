using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Features.BuildingModel.Services
{
    internal static class ComponentViewCadService
    {
        private static string _pending;
        private static DateTime _last;
        public static void Install(){Application.Idle+=Idle;}
        public static void Remove(){Application.Idle-=Idle;}
        private static bool Matches(Document doc,ComponentCadViews job)=>doc!=null&&job.CadProcessId==System.Diagnostics.Process.GetCurrentProcess().Id
            &&string.Equals(doc.Database.FingerprintGuid.ToString(),job.DrawingFingerprint,StringComparison.OrdinalIgnoreCase)
            &&string.Equals(doc.Name,job.DrawingPath,StringComparison.OrdinalIgnoreCase);
        private static void Idle(object sender,EventArgs args)
        {
            if(_pending!=null||DateTime.UtcNow-_last<TimeSpan.FromSeconds(1))return;_last=DateTime.UtcNow;
            var doc=Application.DocumentManager.MdiActiveDocument;if(doc==null||!doc.Editor.IsQuiescent)return;
            var root=ComponentCadViews.Root(System.Diagnostics.Process.GetCurrentProcess().Id);if(!Directory.Exists(root))return;
            foreach(var file in Directory.EnumerateFiles(root,"*.json").OrderBy(File.GetCreationTimeUtc))
                try{if(File.Exists(file+".cancel")||DateTime.UtcNow-File.GetCreationTimeUtc(file)>TimeSpan.FromMinutes(5)){File.Move(file,file+".done");File.WriteAllText(file+".result.txt","请求已取消或过期，图纸未改变。");continue;}if(!Matches(doc,ComponentCadViews.Read(file)))continue;_pending=file;doc.SendStringToExecute("WLCOMPONENTVIEWS ",true,false,false);return;}
                catch(Exception ex){try{File.Move(file,file+".failed");File.WriteAllText(file+".result.txt",ex.Message);}catch{} }
        }
        public static void Execute(Document doc)
        {
            var file=_pending;_pending=null;if(file==null)return;
            try
            {
                var job=ComponentCadViews.Read(file);if(!Matches(doc,job))throw new InvalidOperationException("目标图纸已切换，请回到来源图纸后重新生成。");
                if(job.Views==null||job.Views.Count<1||job.Views.Count>4||job.Views.Any(v=>v==null||v.Lines==null)||job.Views.Sum(v=>v.Lines.Count)>200000||double.IsNaN(job.Gap)||double.IsInfinity(job.Gap)||job.Gap<0)throw new InvalidDataException("立面数据或排布间距无效。");
                if(File.Exists(file+".cancel")){File.WriteAllText(file+".result.txt","已取消，图纸未改变。");return;}
                var point=doc.Editor.GetPoint("\n指定导入构件立面的插入点（各向分别成块）：");
                if(point.Status!=PromptStatus.OK||File.Exists(file+".cancel")){File.WriteAllText(file+".result.txt","已取消，图纸未改变。");return;}
                Insert(doc.Database,job,point.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem));
                File.WriteAllText(file+".result.txt","已插入 "+job.Views.Count+" 个立面块，可一步撤销。");doc.Editor.WriteMessage("\n已生成构件 CAD 立面，可一步撤销。");
            }
            catch(Exception ex){File.WriteAllText(file+".result.txt","未插入："+ex.Message);doc?.Editor.WriteMessage("\n立面未插入："+ex.Message);}
            finally{if(File.Exists(file))File.Move(file,file+".done");}
        }
        internal static void Insert(Database db,ComponentCadViews job,Point3d anchor)
        {
            using(var tx=db.TransactionManager.StartTransaction())
            {
                var layers=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForRead);
                var layerName="WL-图库-立面轮廓";ObjectId layer;
                if(layers.Has(layerName))layer=layers[layerName];else{layers.UpgradeOpen();var rec=new LayerTableRecord {Name=layerName,Color=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci,7)};layer=layers.Add(rec);tx.AddNewlyCreatedDBObject(rec,true);}
                const string annotationName="WL-图库-立面标注";ObjectId annotation;
                if(layers.Has(annotationName))annotation=layers[annotationName];else{if(!layers.IsWriteEnabled)layers.UpgradeOpen();var rec=new LayerTableRecord {Name=annotationName,Color=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci,3)};annotation=layers.Add(rec);tx.AddNewlyCreatedDBObject(rec,true);}
                var blocks=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForWrite);
                var space=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);var x=0d;
                foreach(var view in job.Views)
                {
                    var block=new BlockTableRecord {Name="WL_ASSET_VIEW_"+Guid.NewGuid().ToString("N")};var id=blocks.Add(block);tx.AddNewlyCreatedDBObject(block,true);
                    foreach(var l in view.Lines)
                    {
                        if(new[]{l.X1,l.Y1,l.X2,l.Y2}.Any(v=>double.IsNaN(v)||double.IsInfinity(v)||Math.Abs(v)>1e7))throw new InvalidDataException("立面包含无效坐标。");
                        var line=new Line(new Point3d(l.X1,l.Y1,0),new Point3d(l.X2,l.Y2,0)) {LayerId=layer};block.AppendEntity(line);tx.AddNewlyCreatedDBObject(line,true);
                    }
                    if(job.AddTitles){var text=new DBText {TextString=(view.Title??job.Name??"立面").Replace("\n"," "),Position=new Point3d(0,job.AddDimensions?-250:-120,0),Height=50,LayerId=annotation};block.AppendEntity(text);tx.AddNewlyCreatedDBObject(text,true);}
                    if(job.AddDimensions&&view.Lines.Count>0)
                    {
                        var width=view.Lines.SelectMany(l=>new[]{l.X1,l.X2}).Max();var height=view.Lines.SelectMany(l=>new[]{l.Y1,l.Y2}).Max();
                        foreach(var dimension in new[]{new RotatedDimension(0,Point3d.Origin,new Point3d(width,0,0),new Point3d(0,-100,0),"",db.Dimstyle),new RotatedDimension(Math.PI/2,Point3d.Origin,new Point3d(0,height,0),new Point3d(-100,0,0),"",db.Dimstyle)})
                        {dimension.LayerId=annotation;dimension.Dimtxt=25;dimension.Dimasz=10;block.AppendEntity(dimension);tx.AddNewlyCreatedDBObject(dimension,true);}
                    }
                    var reference=new BlockReference(anchor+new Vector3d(x,0,0),id){LayerId=layer};space.AppendEntity(reference);tx.AddNewlyCreatedDBObject(reference,true);
                    x+=view.Lines.SelectMany(l=>new[]{l.X1,l.X2}).DefaultIfEmpty(0).Max()+job.Gap;
                }
                tx.Commit();
            }
        }
    }
}
