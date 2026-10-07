using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Views;

namespace BatchPdfPublisher.Services
{
    public static class CadComponentLibraryService
    {
        public static void Execute(Document document)
        {
            if(document==null)return;
            var catalog=new ComponentCatalog();
            var window=new CadComponentLibraryWindow(catalog);
            window.StorePlan=(category,selected)=> {
                CadComponentSymbolExporter.Execute(document,category,plan=> {
                    if(selected!=null && System.Windows.MessageBox.Show("更新所选资源的平面？平面有变化时，对应三维模型将标记为待复核。", "更新平面",System.Windows.MessageBoxButton.OKCancel)!=System.Windows.MessageBoxResult.OK)return null;
                    var saved=catalog.SavePlan(plan,selected?.AssetId,selected?.Version??0);
                    window.Reload(saved.AssetId);
                    return "已入库："+saved.Plan.Code+" · CAD / 三维共享";
                },selected?.Plan);
            };
            window.InsertPlan=(record,axis,units)=> {
                using(document.Editor.StartUserInteraction(window)) {
                    var picked=document.Editor.GetPoint("\n指定图库构件插入基点：");
                    if(picked.Status!=PromptStatus.OK)return false;
                    var ucs=document.Editor.CurrentUserCoordinateSystem;
                    if(!ucs.CoordinateSystem3d.Zaxis.IsCodirectionalTo(Vector3d.ZAxis))throw new InvalidDataException("请使用朝上的水平 UCS 插入构件。");
                    var current=catalog.Get(record.AssetId);
                    if(current.Version!=record.Version)throw new IOException("图库已更新，请刷新后插入。");
                    using(var tx=document.Database.TransactionManager.StartTransaction()) {
                        Insert(document.Database,tx,current,picked.Value.TransformBy(ucs),
                            Math.Atan2(ucs.CoordinateSystem3d.Xaxis.Y,ucs.CoordinateSystem3d.Xaxis.X)+(axis=="Y"?Math.PI/2:0),units);
                        tx.Commit();
                    }
                    document.Editor.Regen();
                    return true;
                }
            };
            Application.ShowModalWindow(window);
        }

        public static ObjectId Insert(Database db,Transaction tx,ComponentCatalogRecord record,Point3d point,double angle,double mmPerUnit)
        {
            ComponentPlanSymbols.Validate(record.Plan);
            if(!new[]{point.X,point.Y,point.Z,angle,mmPerUnit}.All(n=>!double.IsNaN(n)&&!double.IsInfinity(n))||mmPerUnit<=0||mmPerUnit>1000000)
                throw new InvalidDataException("插入坐标、方向或单位无效。");
            var profile=DraftingStandardService.LoadProfile();
            var key=record.Plan.Category=="Door"?DraftingStandardProfile.DoorWindowDoorLayerKey:record.Plan.Category=="Window"?DraftingStandardProfile.DoorWindowWindowLayerKey:DraftingStandardProfile.FineKey;
            var fine=DraftingStandardService.EnsureLayerFor(db,tx,key,profile);
            var textLayer=DraftingStandardService.EnsureLayerFor(db,tx,DraftingStandardProfile.AnnotationTextLayerKey,profile);
            var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead);
            // Layer profile is part of block identity so changed standards do not reuse old layers.
            var name="WL_LIB_"+Guid.Parse(record.AssetId).ToString("N")+"_"+record.PlanHash.Substring(0,12)+"_"+fine.Handle+"_"+textLayer.Handle;
            ObjectId definitionId;
            if(table.Has(name))definitionId=table[name];
            else {
                table.UpgradeOpen();var definition=new BlockTableRecord {Name=name,Units=UnitsValue.Undefined};
                definitionId=table.Add(definition);tx.AddNewlyCreatedDBObject(definition,true);
                foreach(var p in record.Plan.Primitives) {
                    Entity entity;
                    if(p.Kind=="Line")entity=new Line(new Point3d(p.X1,p.Y1,0),new Point3d(p.X2,p.Y2,0));
                    else if(p.Kind=="Circle")entity=new Circle(new Point3d(p.X1,p.Y1,0),Vector3d.ZAxis,p.Radius);
                    else {
                        var start=p.StartDegrees*Math.PI/180;var sweep=p.SweepDegrees*Math.PI/180;
                        entity=new Arc(new Point3d(p.X1,p.Y1,0),p.Radius,sweep<0?start+sweep:start,sweep<0?start:start+sweep);
                    }
                    entity.SetDatabaseDefaults(db);entity.LayerId=fine;definition.AppendEntity(entity);tx.AddNewlyCreatedDBObject(entity,true);
                }
                var anchor=record.Plan.TextCandidates.FirstOrDefault(t=>string.Equals(t.Text,record.Plan.Code,StringComparison.OrdinalIgnoreCase));
                var code=new AttributeDefinition {Tag="WL_CODE",Prompt="门窗编号",TextString=record.Plan.Code,
                    Position=new Point3d(anchor?.X??record.Plan.Width/2,anchor?.Y??-Math.Max(50,db.Textsize*mmPerUnit*1.5),0),Height=Math.Max(.1,db.Textsize*mmPerUnit)};
                code.LayerId=textLayer;definition.AppendEntity(code);tx.AddNewlyCreatedDBObject(code,true);
                var identity=new AttributeDefinition {Tag="WL_RESOURCE_ID",TextString=record.AssetId,Invisible=true,Height=1};
                identity.LayerId=textLayer;definition.AppendEntity(identity);tx.AddNewlyCreatedDBObject(identity,true);
            }
            var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
            var block=new BlockReference(point,definitionId) {Rotation=angle,ScaleFactors=new Scale3d(1/mmPerUnit),LayerId=fine};
            var id=space.AppendEntity(block);tx.AddNewlyCreatedDBObject(block,true);
            foreach(ObjectId item in (BlockTableRecord)tx.GetObject(definitionId,OpenMode.ForRead)) {
                var def=tx.GetObject(item,OpenMode.ForRead) as AttributeDefinition;if(def==null)continue;
                var attribute=new AttributeReference();attribute.SetAttributeFromBlock(def,block.BlockTransform);attribute.TextString=def.TextString;
                if(def.Tag=="WL_CODE")attribute.Height=Math.Max(.1,db.Textsize);
                block.AttributeCollection.AppendAttribute(attribute);tx.AddNewlyCreatedDBObject(attribute,true);
            }
            return id;
        }
        public static void RunNativeCheck(Document document)
        {
            var plan=new ComponentPlanSymbol {Name="原生检查",Code="C1216",Width=1200,Height=1600,Primitives={
                new ComponentPlanPrimitive {Kind="Line",X2=1200},new ComponentPlanPrimitive {Kind="Arc",X1=100,Radius=30,StartDegrees=90,SweepDegrees=-90},
                new ComponentPlanPrimitive {Kind="Circle",X1=200,Radius=20}}};
            var record=new ComponentCatalogRecord {AssetId=Guid.NewGuid().ToString("D"),Version=1,Plan=plan,PlanHash=ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(plan))};
            using(var db=new Database(true,true))using(var tx=db.TransactionManager.StartTransaction()) {
                var a=Insert(db,tx,record,new Point3d(100,200,0),Math.PI/2,10);
                var b=Insert(db,tx,record,new Point3d(300,400,0),0,1);
                var block=(BlockReference)tx.GetObject(a,OpenMode.ForRead);var second=(BlockReference)tx.GetObject(b,OpenMode.ForRead);
                if(block.BlockTableRecord!=second.BlockTableRecord||block.AttributeCollection.Count!=2)throw new InvalidDataException("块复用或资源属性失败。");
                var extracted=CadComponentSymbolExporter.Extract(tx,a,new ComponentPlanFrame(100,200,0,0,1,10),"C1216","Window",1200,1600);
                if(extracted.Primitives.Count!=3||Math.Abs(extracted.Primitives[0].X2-1200)>.001||extracted.Primitives.Count(p=>p.Kind=="Arc")!=1||!extracted.TextCandidates.Any(t=>t.Text=="C1216"))
                    throw new InvalidDataException("插入后的毫米尺寸、曲线或编号失败。");
                if(extracted.Primitives.Any(p=>p.SourceLayer=="0"))throw new InvalidDataException("规范图层未应用。");
            }
            document.Editor.WriteMessage("\nCAD_COMPONENT_LIBRARY_OK nativeBlock reuse curves units rotatedAxis code resourceId standardLayers temporaryDatabase");
        }
    }
}
