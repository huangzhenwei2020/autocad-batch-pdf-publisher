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
            window.EditPlacedDoor=()=> {using(document.Editor.StartUserInteraction(window))EditPlacedDoor(document);};
            window.EditRegion=()=>{using(document.Editor.StartUserInteraction(window))CadPlanRegionService.Execute(document);};
            window.SynchronizeWalls=()=>{using(var tx=document.Database.TransactionManager.StartTransaction()){CadLibraryDoorPlacement.Synchronize(document.Database,tx);tx.Commit();}document.Editor.Regen();};
            window.StorePlan=(category,selected)=> {
                CadComponentSymbolExporter.Execute(document,category,plan=> {
                    if(selected!=null && System.Windows.MessageBox.Show("更新所选资源的平面？平面有变化时，对应三维模型将标记为待复核。", "更新平面",System.Windows.MessageBoxButton.OKCancel)!=System.Windows.MessageBoxResult.OK)return null;
                    var saved=catalog.SavePlan(plan,selected?.AssetId,selected?.Version??0);
                    window.Reload(saved.AssetId);
                    return "已入库："+ComponentPlanSymbols.DisplayName(saved.Plan)+" · CAD / 三维共享";
                },selected?.Plan);
            };
            window.InsertPlan=(record,axis,units,localBase)=> {
                var wallInsertion=record.Plan.Category=="Door"&&window.AutoWall;var width=wallInsertion?window.DoorWidth:record.Plan.Width;var height=wallInsertion?window.DoorHeight:record.Plan.Height;
                using(document.Editor.StartUserInteraction(window)) {
                    var picked=document.Editor.GetPoint("\n指定图库构件插入基点：");
                    if(picked.Status!=PromptStatus.OK)return false;
                    var ucs=document.Editor.CurrentUserCoordinateSystem;
                    if(!ucs.CoordinateSystem3d.Zaxis.IsCodirectionalTo(Vector3d.ZAxis))throw new InvalidDataException("请使用朝上的水平 UCS 插入构件。");
                    var current=catalog.Get(record.AssetId);
                    if(current.Version!=record.Version)throw new IOException("图库已更新，请刷新后插入。");
                    var preferred=Math.Atan2(ucs.CoordinateSystem3d.Xaxis.Y,ucs.CoordinateSystem3d.Xaxis.X)+(axis=="Y"?Math.PI/2:0);CadDoorDirectionJig jig=null;
                    if(wallInsertion){CadLibraryDoorPlacement.PlanPreview initial;using(var previewTx=document.Database.TransactionManager.StartTransaction()){initial=CadLibraryDoorPlacement.InitialPreview(document.Database,previewTx,current.Plan,localBase,picked.Value.TransformBy(ucs),preferred,units,width,height,window.Centered,window.JambClearance,window.WallThickness,window.InstanceCode,window.OpeningAngle);}jig=new CadDoorDirectionJig(document.Database,current.Plan,localBase,units,width,height,window.InstanceCode,window.OpeningAngle,initial);if(document.Editor.Drag(jig).Status!=PromptStatus.OK)return false;}
                    if(catalog.Get(current.AssetId).Version!=current.Version)throw new IOException("插入期间图库已改变，请刷新后重新插入。");
                    using(var tx=document.Database.TransactionManager.StartTransaction()) {
                        var angle=preferred;
                        if(wallInsertion)
                            CadLibraryDoorPlacement.Place(document.Database,tx,current,jig.Preview.Start,jig.Preview.Angle,units,localBase,width,height,jig.FlipAlong,jig.FlipAcross,default(ObjectId),window.InstanceCode,window.OpeningAngle);
                        else Insert(document.Database,tx,current,picked.Value.TransformBy(ucs),angle,units,localBase);
                        tx.Commit();
                    }
                    document.Editor.Regen();
                    return true;
                }
            };
            Application.ShowModalWindow(window);
        }
        public static void EditPlacedDoor(Document document)
        {
            if(document==null)return;var options=new PromptEntityOptions("\n选择沿墙插入的图库门：");options.SetRejectMessage("请选择图库门块。");options.AddAllowedClass(typeof(BlockReference),true);
            var picked=document.Editor.GetEntity(options);if(picked.Status!=PromptStatus.OK)return;var id=picked.ObjectId;CadLibraryDoorState state;
            using(var tx=document.Database.TransactionManager.StartTransaction()){state=CadLibraryDoorPlacement.ReadDoor(tx,id);}
            if(state==null){document.Editor.WriteMessage("\n该块尚无墙洞关联，请用图库勾选“沿墙自动开门洞”重新插入。");return;}
            var source=ComponentPlanSymbols.Load(state.Plan);var localBase=new PointModel(state.BaseX,state.BaseY);
            var window=new CadLibraryDoorEditWindow(source,localBase,state.Width,state.Height,state.FlipAlong,state.FlipAcross);
            window.ApplyRequested=(owner,move)=> {
                Point3d target;double angle;
                using(var tx=document.Database.TransactionManager.StartTransaction()){var block=(BlockReference)tx.GetObject(id,OpenMode.ForRead);state=CadLibraryDoorPlacement.ReadDoor(tx,id);target=CadLibraryDoorPlacement.OpeningOrigin(document.Database,tx,block,state);angle=block.Rotation;}
                if(move)using(document.Editor.StartUserInteraction(owner)) {
                    var point=document.Editor.GetPoint(new PromptPointOptions("\n指定新的墙上位置，Enter／右键沿原墙段居中：") {AllowNone=true});
                    if(point.Status==PromptStatus.None){using(var tx=document.Database.TransactionManager.StartTransaction()){target=CadLibraryDoorPlacement.CenterOnWall(document.Database,tx,state,window.DoorWidth,angle);}}
                    else if(point.Status==PromptStatus.OK)target=point.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);else return false;
                }
                using(var tx=document.Database.TransactionManager.StartTransaction()) {
                    var record=new ComponentCatalogRecord {AssetId=state.AssetId,Version=1,Plan=source,PlanHash=ComponentCatalog.Hash(state.Plan)};
                    var replacement=CadLibraryDoorPlacement.Place(document.Database,tx,record,target,angle,state.Units,localBase,window.DoorWidth,window.DoorHeight,window.FlipAlong,window.FlipAcross,id,state.Code,state.OpeningAngle,null,state.LabelAlong,state.LabelNormal);
                    tx.Commit();id=replacement;
                }
                document.Editor.Regen();return true;
            };
            window.RemoveRequested=()=>{using(var tx=document.Database.TransactionManager.StartTransaction()){CadLibraryDoorPlacement.Remove(document.Database,tx,id);tx.Commit();}document.Editor.Regen();};
            Application.ShowModalWindow(window);
        }

        public static ObjectId Insert(Database db,Transaction tx,ComponentCatalogRecord record,Point3d point,double angle,double mmPerUnit,PointModel localBase=null)
        {
            ComponentPlanSymbols.Validate(record.Plan);
            localBase=localBase??ComponentPlanSymbols.SuggestedInsertionBase(record.Plan);
            if(!new[]{localBase.X,localBase.Y}.All(n=>!double.IsNaN(n)&&!double.IsInfinity(n)&&Math.Abs(n)<=1000000))throw new InvalidDataException("插入基点无效。");
            if(!new[]{point.X,point.Y,point.Z,angle,mmPerUnit}.All(n=>!double.IsNaN(n)&&!double.IsInfinity(n))||mmPerUnit<=0||mmPerUnit>1000000)
                throw new InvalidDataException("插入坐标、方向或单位无效。");
            var profile=DraftingStandardService.LoadProfile();
            var key=record.Plan.Category=="Door"?DraftingStandardProfile.DoorWindowDoorLayerKey:record.Plan.Category=="Window"?DraftingStandardProfile.DoorWindowWindowLayerKey:DraftingStandardProfile.FineKey;
            var fine=DraftingStandardService.EnsureLayerFor(db,tx,key,profile);
            var textLayer=DraftingStandardService.EnsureLayerFor(db,tx,DraftingStandardProfile.AnnotationTextLayerKey,profile);
            var partLayers=new System.Collections.Generic.Dictionary<string,ObjectId>();
            if(record.Plan.Category=="Door")foreach(var role in ComponentPlanSymbols.PartRoles) {
                var layerKey=PartLayerKey(role);if(!partLayers.ContainsKey(layerKey))partLayers[layerKey]=DraftingStandardService.EnsureLayerFor(db,tx,layerKey,profile);
            }
            var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead);
            // Layer profile is part of block identity so changed standards do not reuse old layers.
            var baseHash=ComponentCatalog.Hash(System.Text.Encoding.UTF8.GetBytes(localBase.X.ToString("R",CultureInfo.InvariantCulture)+"|"+localBase.Y.ToString("R",CultureInfo.InvariantCulture)+"|"+
                string.Join("|",partLayers.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>pair.Key+":"+pair.Value.Handle))));
            var name="WL_LIB2_"+Guid.Parse(record.AssetId).ToString("N")+"_"+record.PlanHash.Substring(0,12)+"_"+fine.Handle+"_"+textLayer.Handle+"_"+baseHash.Substring(0,12);
            ObjectId definitionId;
            if(table.Has(name))definitionId=table[name];
            else {
                table.UpgradeOpen();var definition=new BlockTableRecord {Name=name,Units=UnitsValue.Undefined,Origin=new Point3d(localBase.X,localBase.Y,0)};
                definitionId=table.Add(definition);tx.AddNewlyCreatedDBObject(definition,true);
                for(var index=0;index<record.Plan.Primitives.Count;index++) {
                    var p=record.Plan.Primitives[index];
                    Entity entity;
                    if(p.Kind=="Line")entity=new Line(new Point3d(p.X1,p.Y1,0),new Point3d(p.X2,p.Y2,0));
                    else if(p.Kind=="Circle")entity=new Circle(new Point3d(p.X1,p.Y1,0),Vector3d.ZAxis,p.Radius);
                    else {
                        var start=p.StartDegrees*Math.PI/180;var sweep=p.SweepDegrees*Math.PI/180;
                        entity=new Arc(new Point3d(p.X1,p.Y1,0),p.Radius,sweep<0?start+sweep:start,sweep<0?start:start+sweep);
                    }
                    entity.SetDatabaseDefaults(db);
                    var role=ComponentPlanSymbols.PrimitiveRole(record.Plan,index);
                    entity.LayerId=record.Plan.Category=="Door"?partLayers[PartLayerKey(role)]:fine;
                    entity.ColorIndex=256;entity.LinetypeId=db.ByLayerLinetype;entity.LineWeight=LineWeight.ByLayer;
                    entity.LinetypeScale=role=="OpeningSymbol"?3:1;
                    definition.AppendEntity(entity);tx.AddNewlyCreatedDBObject(entity,true);
                }
                var referenceCode=ComponentPlanSymbols.ReferenceCode(record.Plan);
                var anchor=record.Plan.TextCandidates.FirstOrDefault(t=>string.Equals(t.Text,referenceCode,StringComparison.OrdinalIgnoreCase));
                var code=new AttributeDefinition {Tag="WL_CODE",Prompt="门窗编号",TextString=referenceCode,
                    Position=new Point3d(anchor?.X??localBase.X+record.Plan.Width/2,anchor?.Y??localBase.Y-Math.Max(50,db.Textsize*mmPerUnit*1.5),0),Height=Math.Max(.1,db.Textsize*mmPerUnit)};
                code.LayerId=textLayer;definition.AppendEntity(code);tx.AddNewlyCreatedDBObject(code,true);
                var identity=new AttributeDefinition {Tag="WL_RESOURCE_ID",TextString=record.AssetId,Invisible=true,Height=1,Position=new Point3d(localBase.X,localBase.Y,0)};
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
        private static string PartLayerKey(string role)=>role=="Casing"?"DoorWindowDoorCasing":role=="Frame"?"DoorWindowDoorFrame":
            role=="PrimaryLeaf"||role=="SecondaryLeaf"?"DoorWindowDoorLeaf":role=="Handle"?"DoorWindowDoorHandle":
            role=="Hardware"?"DoorWindowDoorHardware":role=="FixedPanel"?"DoorWindowDoorFixed":role=="OpeningSymbol"?DraftingStandardProfile.DoorWindowOpeningLayerKey:DraftingStandardProfile.DoorWindowDoorLayerKey;
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
                var decoration=ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(plan));decoration.SchemaVersion=3;decoration.Category="Door";decoration.Code="";decoration.Name="装修门原生插入";decoration.DoorAssembly="SingleSwing";
                decoration.Parts=new System.Collections.Generic.List<ComponentPlanPart> {new ComponentPlanPart {Name="门套",Role="Frame",Primitives=new System.Collections.Generic.List<int> {0}}};
                var doorRecord=new ComponentCatalogRecord {AssetId=Guid.NewGuid().ToString("D"),Version=1,Plan=decoration,PlanHash=ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(decoration))};
                var doorId=Insert(db,tx,doorRecord,new Point3d(500,600,0),0,1);
                var door=(BlockReference)tx.GetObject(doorId,OpenMode.ForRead);
                var attributes=door.AttributeCollection.Cast<ObjectId>().Select(id=>(AttributeReference)tx.GetObject(id,OpenMode.ForRead)).ToArray();
                if(attributes.Single(attribute=>attribute.Tag=="WL_CODE").TextString!="M1216"||attributes.Single(attribute=>attribute.Tag=="WL_RESOURCE_ID").TextString!=doorRecord.AssetId)
                    throw new InvalidDataException("装修门未定编号入库后的插入编号或资源身份错误。");
                var offset=ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(decoration));
                offset.Parts.Add(new ComponentPlanPart {Name="开启线",Role="OpeningSymbol",Primitives=new System.Collections.Generic.List<int> {1}});
                offset.Parts.Add(new ComponentPlanPart {Name="把手",Role="Handle",Primitives=new System.Collections.Generic.List<int> {2}});
                foreach(var primitive in offset.Primitives){primitive.X1+=2300;primitive.X2+=2300;primitive.Y1+=4200;primitive.Y2+=4200;}
                var offsetRecord=new ComponentCatalogRecord {AssetId=Guid.NewGuid().ToString("D"),Plan=offset,PlanHash=ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(offset))};
                var corrected=Insert(db,tx,offsetRecord,new Point3d(5000,6000,0),Math.PI/2,10);
                var local=CadComponentSymbolExporter.Extract(tx,corrected,new ComponentPlanFrame(5000,6000,0,0,1,10),"M1216","Door",1200,1600);
                if(Math.Abs(local.Primitives[0].X1)>.001||Math.Abs(local.Primitives[0].Y1)>.001||Math.Abs(local.Primitives[0].X2-1200)>.001)
                    throw new InvalidDataException("历史坐标偏移未在插入点消除。");
                var correctedBlock=(BlockReference)tx.GetObject(corrected,OpenMode.ForRead);
                if(correctedBlock.Position.DistanceTo(new Point3d(5000,6000,0))>.001)throw new InvalidDataException("块基点没有落在指定点。");
                var correctedAttributes=correctedBlock.AttributeCollection.Cast<ObjectId>().Select(id=>(AttributeReference)tx.GetObject(id,OpenMode.ForRead)).ToArray();
                if(correctedAttributes.Single(attribute=>attribute.Tag=="WL_RESOURCE_ID").Position.DistanceTo(correctedBlock.Position)>.001||correctedAttributes.Single(attribute=>attribute.Tag=="WL_CODE").Position.DistanceTo(correctedBlock.Position)>record.Plan.Width)
                    throw new InvalidDataException("编号或隐藏资源属性远离插入位置。");
                var geometry=((BlockTableRecord)tx.GetObject(correctedBlock.BlockTableRecord,OpenMode.ForRead)).Cast<ObjectId>().Select(id=>tx.GetObject(id,OpenMode.ForRead)).OfType<Curve>().ToArray();
                var frameLayer=(LayerTableRecord)tx.GetObject(geometry[0].LayerId,OpenMode.ForRead);
                var openingLayer=(LayerTableRecord)tx.GetObject(geometry[1].LayerId,OpenMode.ForRead);
                if(frameLayer.Color.ColorIndex==openingLayer.Color.ColorIndex||geometry[0].LayerId==geometry[1].LayerId||geometry.Any(e=>e.ColorIndex!=256||e.LinetypeId!=db.ByLayerLinetype))
                    throw new InvalidDataException("部件图层或随层样式错误。");
                var lineType=(LinetypeTableRecord)tx.GetObject(openingLayer.LinetypeObjectId,OpenMode.ForRead);
                if(lineType.Name!="DASHED")throw new InvalidDataException("开启线未使用原生虚线。");
                var custom=Insert(db,tx,offsetRecord,new Point3d(7000,8000,0),0,1,new PointModel(2400,4200));
                var customBlock=(BlockReference)tx.GetObject(custom,OpenMode.ForRead);
                if(customBlock.BlockTableRecord==correctedBlock.BlockTableRecord)throw new InvalidDataException("不同基点错误复用了块定义。");
            }
            document.Editor.WriteMessage("\nCAD_COMPONENT_LIBRARY_OK nativeBlock reuse curves units rotatedAxis code resourceId standardLayers temporaryDatabase decorationReferenceCode localBase offsetRecovery customBase partColors openingDashed");
        }
    }
}
