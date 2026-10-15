using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Features.BuildingModel.Services
{
    internal static class CadPlanUnderlayCapture
    {
        public static CadPlanUnderlay Read(Database database, CadFloorRegistrationContext floor)
        {
            floor.ValidateCapture();
            var result = new CadPlanUnderlay { CapturedAt = DateTimeOffset.Now.ToString("O") };
            var visited = 0;
            using (var tx = database.TransactionManager.StartOpenCloseTransaction())
            {
                var stack = new HashSet<ObjectId>();
                Point3d Transform(Point3d p, List<Matrix3d> chain)
                { foreach (var matrix in chain) p = p.TransformBy(matrix); return p; }
                void Add(ComponentPlanPrimitive p, Entity entity, string layer, string path)
                {
                    if (result.Primitives.Count >= 50000) throw new InvalidDataException("参照底图超过 50000 个图元，请缩小登记范围。");
                    p.SourceLayer = layer; p.SourcePath = path; p.SourceLineType = entity.Linetype;
                    result.Primitives.Add(p);
                }
                void Text(string text, Point3d point, double height, double width, double rotation, List<Matrix3d> chain, string layer)
                {
                    if (string.IsNullOrWhiteSpace(text)) return;
                    if (result.Texts.Count >= 5000) throw new InvalidDataException("参照底图超过 5000 段文字，请缩小登记范围。");
                    var p = Transform(point, chain);
                    var up = Transform(point + new Vector3d(-Math.Sin(rotation), Math.Cos(rotation), 0) * height, chain) - p;
                    var along = Transform(point + new Vector3d(Math.Cos(rotation), Math.Sin(rotation), 0) * height * width, chain) - p;
                    var h = Math.Sqrt(up.X * up.X + up.Y * up.Y);
                    if (h <= .000001) { result.UnsupportedEntities++; return; }
                    result.Texts.Add(new CadUnderlayText { Text = text.Length > 2048 ? text.Substring(0, 2048) : text,
                        Layer = layer, X = p.X, Y = p.Y, Height = h,
                        WidthFactor = Math.Sqrt(along.X * along.X + along.Y * along.Y) / h,
                        RotationDegrees = Math.Atan2(along.Y, along.X) * 180 / Math.PI });
                }
                void Arc(Entity entity, Point3d center, Point3d start, Vector3d normal, double sweep, bool circle,
                    List<Matrix3d> chain, string layer, string path)
                {
                    var radial = start - center; var tangent = normal.GetNormal().CrossProduct(radial);
                    var c = Transform(center, chain); var a = Transform(start, chain); var b = Transform(center + tangent, chain);
                    var rx = a.X - c.X; var ry = a.Y - c.Y; var sx = b.X - c.X; var sy = b.Y - c.Y;
                    var radius = Math.Sqrt(rx * rx + ry * ry); var other = Math.Sqrt(sx * sx + sy * sy);
                    if (radius < .000001 || Math.Abs(radius - other) > radius * .000001
                        || Math.Abs(rx * sx + ry * sy) > radius * other * .000001)
                    { result.UnsupportedEntities++; return; }
                    Add(new ComponentPlanPrimitive { Kind = circle ? "Circle" : "Arc", X1 = c.X, Y1 = c.Y, Radius = radius,
                        StartDegrees = Math.Atan2(ry, rx) * 180 / Math.PI,
                        SweepDegrees = Math.Sign(rx * sy - ry * sx) * sweep * 180 / Math.PI }, entity, layer, path);
                }
                void Explode(Entity entity, List<Matrix3d> chain, string layer, string path, int depth)
                {
                    var items = new DBObjectCollection();
                    try
                    {
                        entity.Explode(items); var index = 0;
                        if (items.Count == 0) result.UnsupportedEntities++;
                        foreach (DBObject item in items)
                            if (item is Entity child) Visit(child, chain, layer, path + ":" + index++, depth + 1);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { result.UnsupportedEntities++; }
                    finally { foreach (DBObject item in items) item.Dispose(); items.Dispose(); }
                }
                void Visit(Entity entity, List<Matrix3d> chain, string inheritedLayer, string path, int depth)
                {
                    if (++visited > 100000 || depth > 16) throw new InvalidDataException("底图实体数量或嵌套层数超限。");
                    if (!entity.Visible) return;
                    if (!entity.LayerId.IsNull)
                    {
                        var record = (LayerTableRecord)tx.GetObject(entity.LayerId, OpenMode.ForRead);
                        if (record.IsOff || record.IsFrozen) return;
                    }
                    var layer = entity.Layer == "0" ? inheritedLayer ?? "0" : entity.Layer;
                    if (entity is BlockReference block)
                    {
                        var record = (BlockTableRecord)tx.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                        if (record.IsFromExternalReference || record.IsFromOverlayReference || !stack.Add(record.ObjectId))
                        { result.UnsupportedEntities++; return; }
                        var nested = new List<Matrix3d>(chain); nested.Insert(0, block.BlockTransform);
                        try
                        {
                            foreach (ObjectId id in record)
                                if (tx.GetObject(id, OpenMode.ForRead) is Entity child) Visit(child, nested, layer, path + "/" + id.Handle, depth + 1);
                            foreach (ObjectId id in block.AttributeCollection)
                                if (tx.GetObject(id, OpenMode.ForRead) is AttributeReference attribute && !attribute.Invisible)
                                    Text(attribute.TextString, attribute.Position, attribute.Height, attribute.WidthFactor, attribute.Rotation, chain, layer);
                        }
                        finally { stack.Remove(record.ObjectId); }
                    }
                    else if (entity is Line line)
                    {
                        var a = Transform(line.StartPoint, chain); var b = Transform(line.EndPoint, chain);
                        if (a.DistanceTo(b) > .000001)
                            Add(new ComponentPlanPrimitive { Kind = "Line", X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y }, entity, layer, path);
                    }
                    else if (entity is Arc arc) Arc(entity, arc.Center, arc.StartPoint, arc.Normal, arc.TotalAngle, false, chain, layer, path);
                    else if (entity is Circle circle)
                        Arc(entity, circle.Center, circle.Center + circle.Normal.GetPerpendicularVector().GetNormal() * circle.Radius,
                            circle.Normal, Math.PI * 2, true, chain, layer, path);
                    else if (entity is AttributeDefinition definition)
                    { if (definition.Constant && !definition.Invisible) Text(definition.TextString, definition.Position, definition.Height, definition.WidthFactor, definition.Rotation, chain, layer); }
                    else if (entity is DBText text) Text(text.TextString, text.Position, text.Height, text.WidthFactor, text.Rotation, chain, layer);
                    else if (entity is MText mtext) Text(mtext.Text, mtext.Location, mtext.TextHeight, 1, mtext.Rotation, chain, layer);
                    else if (entity is Dimension)
                    {
                        var first=result.Primitives.Count;Explode(entity,chain,layer,path,depth);
                        for(var i=first;i<result.Primitives.Count;i++)result.NonSnapPrimitiveIndices.Add(i);
                    }
                    else if (entity is Polyline || entity is Polyline2d || entity is Polyline3d
                        || (entity.GetRXClass()?.DxfName ?? "").StartsWith("TCH_", StringComparison.OrdinalIgnoreCase))
                        Explode(entity, chain, layer, path, depth);
                    else result.UnsupportedEntities++;
                }
                var space = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    var entity = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null) continue;
                    Extents3d bounds;
                    try { bounds = entity.GeometricExtents; } catch (Autodesk.AutoCAD.Runtime.Exception) { result.UnsupportedEntities++; continue; }
                    if (bounds.MaxPoint.X < floor.RegionMin.X || bounds.MinPoint.X > floor.RegionMax.X
                        || bounds.MaxPoint.Y < floor.RegionMin.Y || bounds.MinPoint.Y > floor.RegionMax.Y) continue;
                    Visit(entity, new List<Matrix3d>(), null, id.Handle.ToString(), 0);
                }
            }
            result.Validate(); return result;
        }
        public static void Update(Document document, CadFloorPlanRegistry registry)
        {
            var file = CadFloorPlanRegistry.FilePath(registry.ModelPath);
            var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0;
            var draft = registry.Clone();
            var fingerprint = document.Database.FingerprintGuid.ToString();
            foreach (var capture in draft.Floors)
            {
                if (!string.Equals(capture.Probe.DrawingFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("请打开登记时的源 CAD 图纸，再更新参照底图。");
                capture.Underlay = Read(document.Database, capture.Floor);
            }
            registry.SaveUnderlays(draft, stamp);
            var count = draft.Floors.Sum(f => f.Underlay.Primitives.Count);
            var skipped = draft.Floors.Sum(f => f.Underlay.UnsupportedEntities);
            document.Editor.WriteMessage("\n参照底图已更新：" + count + " 个矢量图元；未改动模型构件。跳过不支持对象 " + skipped + " 个。");
        }
        public static void RunNativeCheck(Document document)
        {
            var originalDbmod=Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("DBMOD"));
            using(var db=new Database(true,true))
            {
                using(var tx=db.TransactionManager.StartTransaction())
                {
                    var layers=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForWrite);
                    var layer=new LayerTableRecord {Name="QA-ordinary"};layers.Add(layer);tx.AddNewlyCreatedDBObject(layer,true);
                    var off=new LayerTableRecord {Name="QA-off",IsOff=true};layers.Add(off);tx.AddNewlyCreatedDBObject(off,true);
                    var space=(BlockTableRecord)tx.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
                    void Add(Entity entity){space.AppendEntity(entity);tx.AddNewlyCreatedDBObject(entity,true);}
                    Add(new Line(new Point3d(100,200,0),new Point3d(1100,200,0)){LayerId=layer.ObjectId});
                    Add(new Line(new Point3d(600,100,0),new Point3d(600,300,0)){LayerId=layer.ObjectId});
                    Add(new Line(new Point3d(100,210,0),new Point3d(1100,210,0)){LayerId=off.ObjectId});
                    var poly=new Polyline {LayerId=layer.ObjectId};poly.AddVertexAt(0,new Point2d(200,500),0,0,0);poly.AddVertexAt(1,new Point2d(400,500),.5,0,0);poly.AddVertexAt(2,new Point2d(700,500),0,0,0);Add(poly);
                    Add(new Circle(new Point3d(1500,1000,0),Vector3d.ZAxis,100){LayerId=layer.ObjectId});
                    Add(new DBText {TextString="QA-reference",Position=new Point3d(500,700,0),Height=30,WidthFactor=.8,LayerId=layer.ObjectId});
                    var blocks=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForWrite);
                    var definition=new BlockTableRecord {Name="QA-nested-geometry"};blocks.Add(definition);tx.AddNewlyCreatedDBObject(definition,true);
                    var arc=new Arc(new Point3d(0,0,0),100,0,Math.PI/2);definition.AppendEntity(arc);tx.AddNewlyCreatedDBObject(arc,true);
                    Add(new BlockReference(new Point3d(2000,1000,0),definition.ObjectId){LayerId=layer.ObjectId,ScaleFactors=new Scale3d(-1,1,1)});
                    Add(new BlockReference(new Point3d(2500,1000,0),definition.ObjectId){LayerId=layer.ObjectId,ScaleFactors=new Scale3d(2,1,1)});
                    Add(new Line(new Point3d(9000,9000,0),new Point3d(9500,9000,0)){LayerId=layer.ObjectId});
                    tx.Commit();
                }
                var floor=new CadFloorRegistrationContext {ModelPath=Path.Combine(Path.GetTempPath(),"underlay-qa-model.json"),Storey=new StoreyModel {Id="1F",Height=3000},
                    Alignment=new CadFloorRegistrationAlignment {StoreyId="1F",CadBase=new PointModel(100,200),ModelBase=new PointModel(1000,2000),MillimetresPerCadUnit=2},
                    DirectionPoint=new PointModel(110,200),RegionMin=new PointModel(0,0),RegionMax=new PointModel(6000,6000),
                    RegionPolygon=new List<PointModel>{new PointModel(0,0),new PointModel(6000,0),new PointModel(6000,6000),new PointModel(0,6000)}};
                var data=Read(db,floor);
                if(data.Primitives.Count!=6||data.Texts.Count!=1||data.UnsupportedEntities!=1)
                    throw new InvalidDataException("原生参照图元或跳过计数不正确："+data.Primitives.Count+" / "+data.Texts.Count+" / "+data.UnsupportedEntities);
                if(data.Primitives.Any(p=>p.SourceLayer!="QA-ordinary")||Math.Abs(data.Texts[0].WidthFactor-.8)>.000001)
                    throw new InvalidDataException("块内图层继承或文字尺寸丢失。");
                var mirrored=data.Primitives.Single(p=>p.Kind=="Arc"&&p.SourcePath.Contains("/"));
                if(mirrored.SweepDegrees>=0||Math.Abs(mirrored.Radius-100)>.000001)throw new InvalidDataException("镜像块圆弧方向错误。");
                var index=new CadUnderlayIndex(new CadFloorPlanCapture {Floor=floor,Underlay=data});
                var snap=index.Snap(2001,2002,10,null);
                if(snap.Kind!="CAD · 交点"||snap.X!=2000||snap.Y!=2000)throw new InvalidDataException("普通 CAD 线未形成毫米参照交点。");
                floor.DirectionPoint=new PointModel(100,210);floor.SetSourceDatum(floor.Alignment.CadBase,floor.DirectionPoint);
                var rotated=new CadUnderlayIndex(new CadFloorPlanCapture {Floor=floor,Underlay=data});
                if(!rotated.Snap(1001,1,10,null).Snapped)throw new InvalidDataException("旋转基点参照捕捉失败。");
            }
            if(Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("DBMOD"))!=originalDbmod)
                throw new InvalidDataException("原生底图检查修改了当前图纸。");
            document.Editor.WriteMessage("\nCAD_UNDERLAY_NATIVE_OK ordinaryLines bulgeArc circle nestedBlock mirror layerInheritance offLayer region units rotatedDatum text nonUniformRejected unchangedDrawing");
        }
    }
}
