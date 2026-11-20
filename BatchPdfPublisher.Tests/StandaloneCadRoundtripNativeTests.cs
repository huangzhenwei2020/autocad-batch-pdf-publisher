using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;

public static class StandaloneCadRoundtripNativeTests
{
    private static string Root { get { return Environment.GetEnvironmentVariable("WANLUO_CAD_ROUNDTRIP_QA"); } }
    [CommandMethod("WLCADPREPAREQA")]
    public static void Prepare()
    {
        try
        {
            if (string.IsNullOrEmpty(Root)) throw new InvalidOperationException("Private QA directory required");
            Directory.CreateDirectory(Root);
            using (var db = new Database(true, true))
            {
                db.Insunits = UnitsValue.Meters;
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var layers = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForWrite);
                    var layer = new LayerTableRecord { Name = "独立图层", Color = Color.FromColorIndex(ColorMethod.ByAci, 3) };
                    var layerId = layers.Add(layer); tx.AddNewlyCreatedDBObject(layer, true);
                    var reg = (RegAppTable)tx.GetObject(db.RegAppTableId, OpenMode.ForWrite);
                    var app = new RegAppTableRecord { Name = "WANLUO_QA" }; reg.Add(app); tx.AddNewlyCreatedDBObject(app, true);
                    var blocks = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForWrite);
                    var model = (BlockTableRecord)tx.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var line = new Line(new Point3d(10, 20, 0), new Point3d(1010, 20, 0)) { LayerId = layerId };
                    model.AppendEntity(line); tx.AddNewlyCreatedDBObject(line, true);
                    line.XData = new ResultBuffer(new TypedValue(1001, "WANLUO_QA"), new TypedValue(1000, "保留附加数据"));
                    var circle = new Circle(new Point3d(2000, 1000, 0), Vector3d.ZAxis, 300) { LayerId = layerId };
                    model.AppendEntity(circle); tx.AddNewlyCreatedDBObject(circle, true);
                    var text = new DBText { Position = new Point3d(0, 1500, 0), TextString = "独立 CAD 中文", Height = 150, Rotation = Math.PI / 6 };
                    model.AppendEntity(text); tx.AddNewlyCreatedDBObject(text, true);
                    var block = new BlockTableRecord { Name = "测试块" }; blocks.Add(block); tx.AddNewlyCreatedDBObject(block, true);
                    var blockLine = new Line(Point3d.Origin, new Point3d(500, 0, 0)); block.AppendEntity(blockLine); tx.AddNewlyCreatedDBObject(blockLine, true);
                    var attribute = new AttributeDefinition(Point3d.Origin, "A1", "编号", "编号", db.Textstyle) { Height = 100 };
                    block.AppendEntity(attribute); tx.AddNewlyCreatedDBObject(attribute, true);
                    var instance = new BlockReference(new Point3d(5000, 2000, 0), block.ObjectId);
                    model.AppendEntity(instance); tx.AddNewlyCreatedDBObject(instance, true);
                    var reference = new AttributeReference(); reference.SetAttributeFromBlock(attribute, instance.BlockTransform); reference.TextString = "测试-01";
                    instance.AttributeCollection.AppendAttribute(reference); tx.AddNewlyCreatedDBObject(reference, true);
                    var dictionary = (DBDictionary)tx.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
                    var record = new Xrecord { Data = new ResultBuffer(new TypedValue(1, "自定义数据保留")) };
                    dictionary.SetAt("WANLUO_QA_RECORD", record); tx.AddNewlyCreatedDBObject(record, true);
                    tx.Commit();
                }
                db.SaveAs(Path.Combine(Root, "CAD2022 源图.dwg"), DwgVersion.AC1032);
            }
            File.WriteAllText(Path.Combine(Root, "prepare-ok.txt"), "CAD_FIXTURE_OK");
        }
        catch (System.Exception error) { File.WriteAllText(Path.Combine(Root, "failure.txt"), error.ToString()); }
    }

    [CommandMethod("WLCADROUNDTRIPQA")]
    public static void Verify()
    {
        try
        {
            var output = Environment.GetEnvironmentVariable("WANLUO_CAD_ROUNDTRIP_OUTPUT");
            if (string.IsNullOrEmpty(Root) || string.IsNullOrEmpty(output)) throw new InvalidOperationException("Private QA paths required");
            foreach (var extension in new[] { ".dwg", ".dxf" })
                using (var db = Open(Path.Combine(output, "原生编辑 与 保存" + extension)))
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var entities = Model(db, tx);
                    Check(entities.Count == 2, "Unexpected model-space entity count " + extension);
                    var line = entities.OfType<Line>().Single();
                    Check(line.StartPoint.DistanceTo(new Point3d(500, 250, 0)) < .01 && line.EndPoint.DistanceTo(new Point3d(1500, 250, 0)) < .01, "Line coordinates changed " + extension);
                    var circle = entities.OfType<Circle>().Single();
                    Check(circle.Center.DistanceTo(new Point3d(2500, 1250, 0)) < .01 && Math.Abs(circle.Radius - 300) < .01, "Circle dimensions changed " + extension);
                    Check(db.Insunits == UnitsValue.Millimeters, "New drawing millimetre units lost " + extension);
                }
            using (var db = Open(Path.Combine(output, "source-preserved.dwg")))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var entities = Model(db, tx);
                Check(entities.Count == 5, "Original entities or new circle lost");
                Check(db.Insunits == UnitsValue.Meters, "Original source units changed");
                var line = entities.OfType<Line>().Single();
                Check(line.StartPoint.DistanceTo(new Point3d(10, 20, 0)) < .01, "Original line moved");
                Check(line.Layer == "独立图层", "Layer changed");
                var layer = (LayerTableRecord)tx.GetObject(line.LayerId, OpenMode.ForRead);
                Check(layer.Color.ColorIndex == 3, "Layer color changed");
                Check(line.XData != null && line.XData.AsArray().Any(item => Convert.ToString(item.Value) == "保留附加数据"), "XData lost");
                var text = entities.OfType<DBText>().Single();
                Check(text.TextString == "独立 CAD 中文" && Math.Abs(text.Rotation - Math.PI / 6) < .001, "Text content or rotation changed");
                var block = entities.OfType<BlockReference>().Single();
                var attributes = block.AttributeCollection.Cast<ObjectId>().Select(id => (AttributeReference)tx.GetObject(id, OpenMode.ForRead)).ToArray();
                Check(attributes.Any(item => item.Tag == "编号" && item.TextString == "测试-01"), "Block attribute lost");
                var dictionary = (DBDictionary)tx.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                Check(dictionary.Contains("WANLUO_QA_RECORD"), "Custom dictionary entry lost");
                var record = (Xrecord)tx.GetObject(dictionary.GetAt("WANLUO_QA_RECORD"), OpenMode.ForRead);
                Check(record.Data.AsArray().Any(item => Convert.ToString(item.Value) == "自定义数据保留"), "Custom dictionary value changed");
            }
            VerifyExtended(output);
            VerifyStyles(output);
            VerifyManagement(output);
            VerifyLayouts(output);
            VerifyCurveClipping(output);
            VerifyViewportEditing(output);
            VerifyInlineViewportEditing(output);
            var projectionPath = Path.Combine(Directory.GetParent(output).FullName, "native-fixtures", "spatial-projection.txt");
            var projectedPoints = File.ReadAllLines(projectionPath).Select(line => line.Split(' ')
                .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
            foreach (var extension in new[] { ".dwg", ".dxf" })
            using (var db = Open(Path.Combine(output, "非俯视视口" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var blocks=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead);
                var directions=new[]{new Vector3d(0,-2,0),new Vector3d(1,2,3),new Vector3d(0,0,-1),new Vector3d(.01,.005,1)};
                var found=0;
                foreach (ObjectId blockId in blocks)
                {
                    var block=(BlockTableRecord)tx.GetObject(blockId,OpenMode.ForRead);
                    foreach(ObjectId id in block)
                    {
                        var viewport=tx.GetObject(id,OpenMode.ForRead) as Viewport;
                        if(viewport==null || Math.Abs(viewport.Width-300)>.001) continue;
                        var index=(int)Math.Round((viewport.CenterPoint.X-150)/300);
                        Check(viewport.ViewDirection.GetNormal().IsEqualTo(directions[index].GetNormal(),new Tolerance(1e-8,1e-8)),"Spatial direction changed");
                        Check(viewport.ViewTarget.DistanceTo(new Point3d(100,200,300))<.001 && viewport.ViewCenter.GetDistanceTo(new Point2d(50,40))<.001,"Spatial target or DCS changed");
                        Check(Math.Abs(viewport.TwistAngle-index*.3)<1e-8 && Math.Abs(viewport.CustomScale-.1)<1e-8,"Spatial twist or scale changed");
                        var toWorld = Matrix3d.Displacement(viewport.ViewTarget.GetAsVector()) *
                            Matrix3d.PlaneToWorld(viewport.ViewDirection);
                        toWorld = Matrix3d.Rotation(-viewport.TwistAngle, viewport.ViewDirection, viewport.ViewTarget) * toWorld;
                        var displayPoint = new Point3d(200,200,400).TransformBy(toWorld.Inverse());
                        var paperX = viewport.CenterPoint.X + (displayPoint.X - viewport.ViewCenter.X) * viewport.CustomScale;
                        var paperY = viewport.CenterPoint.Y + (displayPoint.Y - viewport.ViewCenter.Y) * viewport.CustomScale;
                        Check(Math.Abs(paperX - projectedPoints[index][0]) < 1e-8 &&
                            Math.Abs(paperY - projectedPoints[index][1]) < 1e-8, "Spatial projection differs from AutoCAD matrices");
                        ++found;
                    }
                }
                Check(found==8,"Spatial viewport native count changed");
            }
            VerifySpatialNavigation(output);
            VerifySpatialMaximizedEditing(output);
            VerifySpatialInlineEditing(output);
            VerifyLayoutPerspective(output);
            VerifyPerspectiveTransfer(output);
            VerifyWorkingPlane(output);
            VerifySpatialCurves(output);
            VerifyWorkingPolylines(output);
            VerifyWorkingEllipses(output);
            VerifyWorkingHatches(output);
            VerifyWorkingText(output);
            VerifyMediaClipping(output);
            File.WriteAllText(Path.Combine(Root, "roundtrip-ok.txt"), "CAD_ROUNDTRIP_OK dwg dxf dimensions units layers attributes text xdata dictionary extendedCurves hatch dimensions arrays blocks layouts");
        }
        catch (System.Exception error) { File.WriteAllText(Path.Combine(Root, "failure.txt"), error.ToString()); }
    }
    private static void VerifyWorkingText(string output)
    {
        foreach (var view in new[] { 2, 6, 1 })
        {
            var coordinates = File.ReadAllText(Path.Combine(output, "text-plane-" + view + ".txt")).Split(' ')
                .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var origin = new Point3d(coordinates[0], coordinates[1], coordinates[2]);
            var x = new Vector3d(coordinates[3], coordinates[4], coordinates[5]);
            var y = new Vector3d(coordinates[6], coordinates[7], coordinates[8]);
            var normal = x.CrossProduct(y).GetNormal();
            Func<double, Vector3d> direction = degrees =>
                x * Math.Cos(degrees * Math.PI / 180) + y * Math.Sin(degrees * Math.PI / 180);
            foreach (var incremental in new[] { false, true })
            foreach (var extension in new[] { ".dwg", ".dxf" })
            using (var db = Open(Path.Combine(output, "工作平面文字" + (incremental ? "增量" : "") + "-" + view + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var entities = Model(db, tx);
                Check(entities.Count == 4, "Expected four native text entities");
                foreach (var offset in new[] { 0d, 200d })
                {
                    var singleLocation = origin + x * (34 + offset) + y * 48 + normal * (incremental ? 20 : 10);
                    var multiLocation = origin + x * (144 + offset) + y * 88 + normal * (incremental ? 25 : 15);
                    var single = entities.OfType<DBText>().SingleOrDefault(text => text.Position.DistanceTo(singleLocation) < 1e-6);
                    var multi = entities.OfType<MText>().SingleOrDefault(text => text.Location.DistanceTo(multiLocation) < 1e-6);
                    Check(single != null, $"TEXT insertion mismatch in view {view}, {extension}, incremental={incremental}. " +
                        $"Expected {singleLocation}; found {string.Join(", ", entities.OfType<DBText>().Select(text => text.Position))}.");
                    Check(multi != null, $"MTEXT insertion mismatch in view {view}, {extension}, incremental={incremental}. " +
                        $"Expected {multiLocation}; found {string.Join(", ", entities.OfType<MText>().Select(text => text.Location))}.");
                    var singleDirection = new Vector3d(Math.Cos(single.Rotation), Math.Sin(single.Rotation), 0)
                        .TransformBy(Matrix3d.PlaneToWorld(single.Normal));
                    Check(single.Normal.DotProduct(normal) > 1 - 1e-8 && singleDirection.DotProduct(direction(25)) > 1 - 1e-8 &&
                          Math.Abs(single.Height - 6) < 1e-8 && single.TextString == "立面文字", "Single-line text plane, angle, height or content changed");
                    Check(multi.Normal.DotProduct(normal) > 1 - 1e-8 && multi.Direction.GetNormal().DotProduct(direction(-35)) > 1 - 1e-8 &&
                          Math.Abs(multi.Width - 45) < 1e-8 && Math.Abs(multi.TextHeight - 8) < 1e-8 &&
                          multi.Contents.Contains("文字一\\P文字二"), "Multiline text plane, direction, size or paragraph break changed");
                }
            }
        }
    }

    private static void VerifyWorkingHatches(string output)
    {
        foreach (var view in new[] { 2, 6, 1 })
        {
            var values = File.ReadAllText(Path.Combine(output, "hatch-plane-" + view + ".txt")).Split(' ')
                .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var origin = new Point3d(values[0], values[1], values[2]);
            var x = new Vector3d(values[3], values[4], values[5]);
            var y = new Vector3d(values[6], values[7], values[8]);
            var normal = x.CrossProduct(y).GetNormal();
            var worldToPlane = Matrix3d.WorldToPlane(normal);
            var ocsX = x.TransformBy(worldToPlane);
            double angle = Math.Atan2(ocsX.Y, ocsX.X) + 25 * Math.PI / 180;
            Func<double, double, double, Point3d> world = (a, b, c) => origin + x * a + y * b + normal * c;
            foreach (var incremental in new[] { false, true })
            foreach (var extension in new[] { ".dwg", ".dxf" })
            using (var db = Open(Path.Combine(output, "工作平面填充" + (incremental ? "增量" : "") + "-" + view + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var hatches = Model(db, tx).OfType<Hatch>().ToArray();
                double elevation = world(0,0,incremental ? 15 : 5).TransformBy(worldToPlane).Z;
                Check(hatches.Length == 4 && hatches.Count(h => h.IsSolidFill) == 2, "Expected two solid and two patterned native hatches");
                foreach (var hatch in hatches)
                {
                    Check(hatch.Normal.DotProduct(normal) > 1 - 1e-8 &&
                          Math.Abs(hatch.Elevation - elevation) < 1e-7 && hatch.NumberOfLoops == 1,
                        "Hatch plane, elevation or loop count changed");
                    var loop = hatch.GetLoopAt(0);
                    if (hatch.IsSolidFill)
                    {
                        Check(!loop.IsPolyline && loop.Curves.Count == 1, "Solid hatch lost its analytic circle");
                        continue;
                    }
                    Check(hatch.PatternName == "ANSI31" && Math.Abs(hatch.PatternScale - 4) < 1e-8 &&
                          Math.Abs(Math.IEEERemainder(hatch.PatternAngle - angle, 2 * Math.PI)) < 1e-8,
                        "Hatch pattern name, scale or angle changed");
                    Check(loop.IsPolyline && loop.Polyline.Count >= 4, "Patterned hatch lost its closed polyline boundary");
                    var vertices = loop.Polyline.Cast<BulgeVertex>().Select(vertex =>
                        new Point3d(vertex.Vertex.X, vertex.Vertex.Y, elevation).TransformBy(worldToPlane.Inverse())).ToArray();
                    var offset = vertices.Any(p => p.DistanceTo(world(10,20,incremental ? 15 : 5)) < 1e-6) ? 0 : 400;
                    foreach (var u in new[] { 10d, 130d })
                    foreach (var v in new[] { 20d, 100d })
                        Check(vertices.Any(p => p.DistanceTo(world(u + offset,v,incremental ? 15 : 5)) < 1e-6),
                            "Hatch boundary was replaced by the projected loop from another elevation");
                }
            }
        }
    }

    private static void VerifyWorkingEllipses(string output)
    {
        foreach (var view in new[] { 2, 6, 1 })
        {
            var values = File.ReadAllText(Path.Combine(output, "ellipse-plane-" + view + ".txt")).Split(' ')
                .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var origin = new Point3d(values[0], values[1], values[2]);
            var x = new Vector3d(values[3], values[4], values[5]);
            var y = new Vector3d(values[6], values[7], values[8]);
            var normal = x.CrossProduct(y).GetNormal();
            Func<double, double, double, Point3d> world = (a, b, c) => origin + x * a + y * b + normal * c;
            foreach (var incremental in new[] { false, true })
            foreach (var extension in new[] { ".dwg", ".dxf" })
            using (var db = Open(Path.Combine(output, "工作平面椭圆" + (incremental ? "增量" : "") + "-" + view + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var entities = Model(db, tx);
                var ellipses = entities.OfType<Ellipse>().ToArray();
                Check(entities.Count == 4 && ellipses.Length == 4, "Working-plane ellipse changed entity type");
                double z = incremental ? 22 : 12;
                foreach (var offset in new[] { 0d, 300d })
                {
                    var full = ellipses.Single(item => item.Center.DistanceTo(world(50 + offset, 80, z)) < 1e-6);
                    var arc = ellipses.Single(item => item.Center.DistanceTo(world(210 + offset, 120, z)) < 1e-6);
                    Check(full.Closed && !arc.Closed && Math.Abs(full.MajorRadius - 50) < 1e-8 &&
                          Math.Abs(full.MinorRadius - 20) < 1e-8 && Math.Abs(arc.MajorRadius - 40) < 1e-8 &&
                          Math.Abs(arc.MinorRadius - 20) < 1e-8, "Ellipse radii or closure changed");
                    Check(Math.Abs(full.Normal.DotProduct(normal)) > 1 - 1e-8 &&
                          Math.Abs(arc.Normal.DotProduct(normal)) > 1 - 1e-8 &&
                          Math.Abs(full.MajorAxis.GetNormal().DotProduct((x * .6 + y * .8))) > 1 - 1e-8,
                        "Ellipse normal or major axis changed");
                    var a = world(242 + offset, 96, z);
                    var b = world(222 + offset, 136, z);
                    Check(arc.StartPoint.DistanceTo(a) < 1e-6 && arc.EndPoint.DistanceTo(b) < 1e-6 ||
                          arc.StartPoint.DistanceTo(b) < 1e-6 && arc.EndPoint.DistanceTo(a) < 1e-6,
                        "Ellipse arc endpoints changed");
                    double sweep = arc.EndParam - arc.StartParam;
                    if (sweep <= 0) sweep += Math.PI * 2;
                    Check(arc.GetPointAtParameter(arc.StartParam + sweep / 2)
                          .DistanceTo(world(210 + offset + 44 / Math.Sqrt(2), 120 - 8 / Math.Sqrt(2), z)) < 1e-6,
                        "Ellipse arc direction changed after save");
                }
            }
        }
    }

    private static void VerifyWorkingPolylines(string output)
    {
        foreach (var view in new[] { 2, 6, 1 })
        {
            var values = File.ReadAllText(Path.Combine(output, "polyline-plane-" + view + ".txt")).Split(' ')
                .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var origin = new Point3d(values[0], values[1], values[2]);
            var x = new Vector3d(values[3], values[4], values[5]);
            var y = new Vector3d(values[6], values[7], values[8]);
            var normal = x.CrossProduct(y).GetNormal();
            Func<double, double, double, Point3d> world = (a, b, c) => origin + x * a + y * b + normal * c;
            foreach (var incremental in new[] { false, true })
            foreach (var extension in new[] { ".dwg", ".dxf" })
            using (var db = Open(Path.Combine(output, "工作平面多段线" + (incremental ? "增量" : "") + "-" + view + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var entities = Model(db, tx);
                var polylines = entities.OfType<Polyline>().ToArray();
                Check(entities.Count == 2 && polylines.Length == 2, "Working-plane polyline changed entity type");
                double z = incremental ? 22 : 12;
                foreach (var offset in new[] { 0d, 250d })
                {
                    var path = polylines.Single(item => item.GetPoint3dAt(0).DistanceTo(world(30 + offset, 50, z)) < 1e-6);
                    Check(path.Closed && path.NumberOfVertices == 3 && Math.Abs(path.ConstantWidth - .25) < 1e-8,
                        "Working-plane polyline closure, vertex count or width changed");
                    Check(Math.Abs(path.Normal.DotProduct(normal)) > 1 - 1e-8 &&
                          path.GetPoint3dAt(1).DistanceTo(world(130 + offset, 50, z)) < 1e-6 &&
                          path.GetPoint3dAt(2).DistanceTo(world(230 + offset, 50, z)) < 1e-6,
                        "Working-plane polyline normal or vertices changed");
                    Check(Math.Abs(Math.Abs(path.GetBulgeAt(1)) - 1) < 1e-8 &&
                          path.GetPointAtParameter(1.5).DistanceTo(world(180 + offset, 100, z)) < 1e-6,
                        "Working-plane polyline arc geometry changed");
                }
            }
        }
    }

    private static void VerifySpatialCurves(string output)
    {
        foreach (var view in new[] {2,6,1})
        {
            var values = File.ReadAllText(Path.Combine(output,"curve-plane-"+view+".txt")).Split(' ')
                .Select(value => double.Parse(value,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var origin = new Point3d(values[0],values[1],values[2]);
            var x = new Vector3d(values[3],values[4],values[5]);
            var y = new Vector3d(values[6],values[7],values[8]);
            var normal = x.CrossProduct(y).GetNormal();
            Func<double,double,double,Point3d> world = (a,b,c) => origin+x*a+y*b+normal*c;
            foreach (var incremental in new[] {false,true})
            foreach (var extension in new[] {".dwg",".dxf"})
            using (var db = Open(Path.Combine(output,"工作平面圆弧"+(incremental?"增量":"")+"-"+view+extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var entities = Model(db,tx);
                var circles = entities.OfType<Circle>().ToArray();
                var arcs = entities.OfType<Arc>().ToArray();
                Check(entities.Count==6 && circles.Length==2 && arcs.Length==4,"Spatial curve entity types changed");
                double z = incremental ? 22 : 12;
                foreach (var offset in new[] {0d,250d})
                {
                    var circle = circles.Single(item => item.Center.DistanceTo(world(40+offset,60,z))<1e-7);
                    Check(Math.Abs(circle.Radius-25)<1e-7 && Math.Abs(circle.Normal.DotProduct(normal))>1-1e-8,
                        "Circle radius or normal changed view="+view+" incremental="+incremental+extension+" radius="+circle.Radius+" normal="+circle.Normal+" expected="+normal);
                    foreach (var localX in new[] {100d,200d})
                    {
                        var arc = arcs.Single(item => item.Center.DistanceTo(world(localX+offset,60,z))<1e-7);
                        Check(Math.Abs(arc.Radius-25)<1e-7 && Math.Abs(arc.Normal.DotProduct(normal))>1-1e-8,"Arc radius or normal changed");
                        Check(arc.GetPointAtDist(arc.GetDistanceAtParameter(arc.EndParam)/2).DistanceTo(world(localX+offset,85,z))<1e-6,
                            "Three-point arc direction or OCS angles changed");
                        Check(new[] {arc.StartPoint,arc.EndPoint}.All(point =>
                            point.DistanceTo(world(localX+offset-25,60,z))<1e-6 || point.DistanceTo(world(localX+offset+25,60,z))<1e-6),"Arc endpoints changed");
                    }
                }
            }
        }
    }
    private static void VerifyWorkingPlane(string output)
    {
        using (var db = Open(Path.Combine(output, "工作平面纯Z.dwg")))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var lines = Model(db, tx).OfType<Line>().ToArray();
            Check(lines.Any(line => line.StartPoint.DistanceTo(new Point3d(110,200,325)) < 1e-7 &&
                line.EndPoint.DistanceTo(new Point3d(210,200,325)) < 1e-7), "Pure Z movement was dropped");
        }
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, "三维工作平面" + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            Check(db.Ucsorg.DistanceTo(new Point3d(100,200,300)) < 1e-8 &&
                db.Ucsxdir.IsEqualTo(Vector3d.XAxis, new Tolerance(1e-8,1e-8)) &&
                db.Ucsydir.IsEqualTo(Vector3d.ZAxis, new Tolerance(1e-8,1e-8)), "Model UCS header lost origin or axes");
            var entities = Model(db, tx);
            Check(entities.Count == 16 && entities.All(entity => !entity.IsErased), "Working plane entity count changed");
            var lines = entities.OfType<Line>().ToArray();
            var points = entities.OfType<DBPoint>().ToArray();
            Check(lines.Length == 12 && points.Length == 4, "Working plane entity types changed");
            foreach (var offset in new[] { 0d, 200d })
            {
                Func<Point3d, Point3d, bool> hasLine = (start, end) => lines.Any(line =>
                    line.StartPoint.DistanceTo(start) < 1e-7 && line.EndPoint.DistanceTo(end) < 1e-7);
                Check(hasLine(new Point3d(110+offset,200,320), new Point3d(210+offset,200,320)),
                    "UCS relative X line lost coordinates " + extension + " " + string.Join(" | ", lines.Select(line => line.StartPoint + " -> " + line.EndPoint)));
                Check(hasLine(new Point3d(210+offset,200,320), new Point3d(210+offset,200,420)), "UCS relative Y line flattened Z");
                Check(hasLine(new Point3d(210+offset,200,420), new Point3d(210+offset,190,420)), "UCS relative XYZ input lost normal displacement");
                Check(hasLine(new Point3d(110+offset,200,320), new Point3d(190+offset,200,400)), "Object snap or typed UCS endpoint changed");
                Check(points.Any(point => point.Position.DistanceTo(new Point3d(135+offset,190,345)) < 1e-7), "UCS absolute XYZ point changed");
                Check(points.Any(point => point.Position.DistanceTo(new Point3d(160+offset,200,320)) < 1e-7), "Three-dimensional midpoint snap changed");
                Check(lines.Any(line => line.StartPoint.DistanceTo(new Point3d(110+offset,200,320)) < 1e-7 &&
                    Math.Abs(line.EndPoint.X - (110+offset)) < 1e-7 && Math.Abs(line.EndPoint.Z - 370) < 2), "UCS ortho did not follow vertical Z axis");
                Check(lines.Any(line => Math.Abs(line.StartPoint.Y - 200) < 1e-7 && Math.Abs(line.EndPoint.Y - 200) < 1e-7 &&
                    line.StartPoint.DistanceTo(new Point3d(110+offset,200,320)) < 2 &&
                    line.EndPoint.DistanceTo(new Point3d(160+offset,200,380)) < 2), "Mouse drawing did not intersect the view work plane");
            }
        }
    }

    private static void VerifyLayoutPerspective(string output)
    {
        foreach (var name in new[] { "透视视口回读", "透视视口导航", "透视参数编辑" })
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, name + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layout = (Layout)tx.GetObject(dictionary.GetAt("Layout1"), OpenMode.ForRead);
            var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var viewport = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead))
                .OfType<Viewport>().Single(item => item.PerspectiveOn);
            var navigated = name == "透视视口导航";
            var editedOptics = name == "透视参数编辑";
            Check(!viewport.Locked && viewport.FrontClipOn && viewport.BackClipOn && !viewport.FrontClipAtEyeOn &&
                Math.Abs(viewport.LensLength - (editedOptics ? 63 : navigated ? 50.4 : 42)) < 1e-8 &&
                Math.Abs(viewport.FrontClipDistance - (editedOptics ? 250 : 600)) < 1e-8 &&
                Math.Abs(viewport.BackClipDistance - (editedOptics ? -300 : -400)) < 1e-8,
                "Perspective optics or depth clipping was not preserved");
            var modelPerPaper = 1000 / Math.Sqrt(300 * 300 + 200 * 200);
            var center = navigated
                ? new Point2d(10 + (-30 + 25 / 6.0) * modelPerPaper, -15 + (-20 - 15 / 6.0) * modelPerPaper)
                : new Point2d(10,-15);
            Check(viewport.ViewDirection.IsEqualTo(new Vector3d(0,0,1000)) &&
                viewport.ViewTarget.DistanceTo(new Point3d(100,200,300)) < 1e-7 &&
                viewport.ViewCenter.GetDistanceTo(center) < (navigated ? 5 : 1e-7) &&
                Math.Abs(viewport.ViewHeight - 2000 / (editedOptics ? 1.5 : navigated ? 1.2 : 1)) < 1e-7 &&
                viewport.CenterPoint.DistanceTo(new Point3d(150,100,0)) < 1e-7 &&
                Math.Abs(viewport.Width - 300) < 1e-7 && Math.Abs(viewport.Height - 200) < 1e-7 &&
                Math.Abs(viewport.TwistAngle - .2) < 1e-8,
                "Perspective camera or view field was not preserved");
            Check(Model(db, tx).OfType<Line>().Count() == 4, "Display clipping changed model geometry");
        }
        foreach (var edited in new[] { false, true })
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, (edited ? "透视视口属性" : "透视视口绘图") + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layout = (Layout)tx.GetObject(dictionary.GetAt("Layout1"), OpenMode.ForRead);
            var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var viewport = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead))
                .OfType<Viewport>().Single(item => item.PerspectiveOn);
            var entities = Model(db, tx);
            var lines = entities.OfType<Line>().ToArray();
            var drawn = lines.Single(line => Math.Abs(line.EndPoint.Z) < 1e-7);
            var near = lines.Single(line => Math.Abs(line.EndPoint.Z - (edited ? 550 : 600)) < 1e-7);
            var far = lines.Single(line => Math.Abs(line.EndPoint.Z - 100) < 1e-7);
            var midpoint = far.StartPoint + (far.EndPoint - far.StartPoint) * .5;
            if (edited) midpoint = new Point3d(midpoint.X, midpoint.Y, 150);
            var center = new Point3d(100 + 10 * Math.Cos(.2) - 15 * Math.Sin(.2),
                200 - 10 * Math.Sin(.2) - 15 * Math.Cos(.2), 0);
            Check(lines.Length == 5 && drawn.StartPoint.DistanceTo(near.StartPoint) < 1e-7 && drawn.EndPoint.DistanceTo(center) < 5 &&
                entities.OfType<DBPoint>().Single().Position.DistanceTo(midpoint) < 1e-7,
                "Perspective drawing transferred the wrong coordinates or space");
            Check(viewport.Locked && viewport.ViewDirection.IsEqualTo(new Vector3d(0,0,1000)) &&
                Math.Abs(viewport.LensLength - 42) < 1e-8 && Math.Abs(viewport.FrontClipDistance - 600) < 1e-8 &&
                Math.Abs(viewport.BackClipDistance + 400) < 1e-8, "Perspective drawing changed the viewport camera");
        }
    }

    private static void VerifyPerspectiveTransfer(string output)
    {
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, "透视视口移动复制" + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var model = Model(db, tx);
            var lines = model.OfType<Line>().ToArray();
            var points = model.OfType<DBPoint>().Select(point => point.Position).ToArray();
            var moved = lines.Single(line => Math.Abs(line.EndPoint.Z - 550) < 1e-7);
            var right = new Vector3d(Math.Cos(.2), -Math.Sin(.2), 0);
            var up = new Vector3d(Math.Sin(.2), Math.Cos(.2), 0);
            var center = new Point3d(100, 200, 300) + right * 10 - up * 15;
            var distance = 20 * 700 / Math.Sqrt(300 * 300 + 200 * 200);
            var expectedStart = center + right * (-120 + distance) + up * (50 + distance) + new Vector3d(0, 0, 300);
            Check(lines.Length == 5 && moved.ColorIndex == 1 && moved.StartPoint.DistanceTo(expectedStart) < 3 &&
                Math.Abs(moved.StartPoint.Z - 600) < 1e-7 &&
                (moved.EndPoint - moved.StartPoint - right * 240 + new Vector3d(0,0,50)).Length < 1e-7,
                "Perspective move did not preserve line geometry, elevation or color");
            var source = center - up * 80 - new Vector3d(0, 0, 150);
            Check(points.Length == 3 && new[] { new Vector3d(0,0,0), new Vector3d(70,25,50), new Vector3d(-50,80,-25) }
                .All(offset => points.Any(point => point.DistanceTo(source + offset) < 1e-7)),
                "Perspective copy lost the source or a relative XYZ destination");

            var layouts = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layout = (Layout)tx.GetObject(layouts.GetAt("Layout1"), OpenMode.ForRead);
            var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var viewport = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<Viewport>().Single(item => item.PerspectiveOn);
            Check(viewport.Locked && Math.Abs(viewport.LensLength - 42) < 1e-7 &&
                Math.Abs(viewport.FrontClipDistance - 600) < 1e-7 && Math.Abs(viewport.BackClipDistance + 400) < 1e-7 &&
                viewport.ViewDirection.IsEqualTo(new Vector3d(0,0,1000)), "Perspective move and copy changed the camera");
        }
    }

    private static void VerifySpatialInlineEditing(string output)
    {
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, "非俯视原位裁剪编辑" + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var entities = Model(db, tx);
            Check(entities.Count == 4 && entities.OfType<Line>().Count() == 4, "Unexpected spatial inline model entities");
            Check(entities.OfType<Line>().Any(line =>
                line.StartPoint.DistanceTo(new Point3d(-800,200,800)) < 1e-7 &&
                line.EndPoint.DistanceTo(new Point3d(200,180,400)) < 1e-7),
                "Inline drawing lost the model endpoint snap or working-plane coordinate");
            var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layout = (Layout)tx.GetObject(dictionary.GetAt("Layout1"), OpenMode.ForRead);
            var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var paperEntities = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).ToArray();
            Check(!paperEntities.OfType<Line>().Any(), "Spatial inline drawing was saved in paper space");
            var viewport = paperEntities.OfType<Viewport>().Single(item => item.NonRectClipOn);
            var boundary = (Polyline)tx.GetObject(viewport.NonRectClipEntityId, OpenMode.ForRead);
            Check(boundary.Closed && boundary.NumberOfVertices == 8 && boundary.OwnerId == viewport.OwnerId,
                "Non-planar viewport lost its concave boundary");
            Check(!viewport.Locked && !viewport.PerspectiveOn && Math.Abs(viewport.CustomScale - .1) < 1e-9 &&
                viewport.ViewDirection.GetNormal().DotProduct(new Vector3d(0,-1,0)) > 1 - 1e-10 &&
                viewport.ViewTarget.DistanceTo(new Point3d(100,200,300)) < 1e-8 &&
                viewport.ViewCenter.GetDistanceTo(new Point2d(0,0)) < 1e-8,
                "Spatial inline navigation undo did not preserve viewport parameters");
        }
    }

    private static void VerifySpatialMaximizedEditing(string output)
    {
        var origin = new Point3d(100, 200, 300);
        var directions = new[] { new Vector3d(0,-2,0), new Vector3d(1,2,3), new Vector3d(0,0,-1), new Vector3d(.01,.005,1) };
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, "非俯视最大化编辑" + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var lines = Model(db, tx).OfType<Line>().ToArray();
            Check(lines.Length == 5 && lines.Any(line => line.StartPoint.DistanceTo(origin) < 1e-8 &&
                line.EndPoint.DistanceTo(new Point3d(200,200,400)) < 1e-8), "Original or edited model lines missing");
            var blocks = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            var matched = new HashSet<int>();
            foreach (ObjectId blockId in blocks)
            {
                var block = (BlockTableRecord)tx.GetObject(blockId, OpenMode.ForRead);
                if (!block.IsLayout || ((Layout)tx.GetObject(block.LayoutId, OpenMode.ForRead)).LayoutName != "Layout1") continue;
                foreach (ObjectId id in block)
                {
                    var entity = tx.GetObject(id, OpenMode.ForRead);
                    Check(!(entity is Line), "Maximized editing wrote model geometry into paper space");
                    var viewport = entity as Viewport;
                    if (viewport == null || Math.Abs(viewport.Width - 300) > 1e-7) continue;
                    var index = (int)Math.Round((viewport.CenterPoint.X - 150) / 300);
                    Check(index >= 0 && index < directions.Length && matched.Add(index), "Unexpected layout viewport identity");
                    Check(viewport.Locked && !viewport.PerspectiveOn &&
                        Math.Abs(viewport.CustomScale - .1) < 1e-8 &&
                        viewport.ViewTarget.DistanceTo(origin) < 1e-8 &&
                        viewport.ViewDirection.GetNormal().DotProduct(directions[index].GetNormal()) > 1 - 1e-10 &&
                        viewport.ViewCenter.GetDistanceTo(new Point2d(50,40)) < 1e-7 &&
                        Math.Abs(viewport.TwistAngle - index * .3) < 1e-8,
                        "Maximized editing changed saved view parameters");
                    var planeToWorld = Matrix3d.Rotation(-viewport.TwistAngle, viewport.ViewDirection, origin) *
                        Matrix3d.Displacement(origin.GetAsVector()) * Matrix3d.PlaneToWorld(viewport.ViewDirection);
                    var x = 10 + 30 * index;
                    var start = new Point3d(x,20,0).TransformBy(planeToWorld);
                    var end = new Point3d(x+80,65,0).TransformBy(planeToWorld);
                    Check(lines.Any(line => line.StartPoint.DistanceTo(start) < 1e-6 && line.EndPoint.DistanceTo(end) < 1e-6),
                        "Edited line did not land on the viewport working plane");
                }
            }
            Check(matched.Count == directions.Length, "Maximized editing changed the number of viewports");
        }
    }

    private static void VerifySpatialNavigation(string output)
    {
        var expected = File.ReadAllLines(Path.Combine(output, "spatial-navigation-center.txt"))
            .Select(line => line.Split(' ').Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, "非俯视导航" + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var line = Model(db, tx).OfType<Line>().Single();
            Check(line.StartPoint.DistanceTo(new Point3d(100,200,300)) < 1e-8 &&
                line.EndPoint.DistanceTo(new Point3d(200,200,400)) < 1e-8, "Spatial navigation modified model geometry");
            var blocks = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            var found = 0;
            foreach (ObjectId blockId in blocks)
            {
                var block = (BlockTableRecord)tx.GetObject(blockId, OpenMode.ForRead);
                if (!block.IsLayout || ((Layout)tx.GetObject(block.LayoutId, OpenMode.ForRead)).LayoutName != "Layout1") continue;
                foreach (ObjectId id in block)
                {
                    var viewport = tx.GetObject(id, OpenMode.ForRead) as Viewport;
                    if (viewport == null || Math.Abs(viewport.Width - 300) > 1e-7) continue;
                    var index = (int)Math.Round((viewport.CenterPoint.X - 150) / 300);
                    var values = expected[index];
                    var worldCenter = new Point3d(values[0], values[1], values[2]);
                    var toWorld = Matrix3d.Displacement(viewport.ViewTarget.GetAsVector()) * Matrix3d.PlaneToWorld(viewport.ViewDirection);
                    toWorld = Matrix3d.Rotation(-viewport.TwistAngle, viewport.ViewDirection, viewport.ViewTarget) * toWorld;
                    var displayCenter = worldCenter.TransformBy(toWorld.Inverse());
                    Check(viewport.ViewCenter.GetDistanceTo(new Point2d(displayCenter.X, displayCenter.Y)) < 1e-7,
                        "Spatial navigation DCS differs from AutoCAD matrices");
                    Check(Math.Abs(viewport.CustomScale - 1 / values[3]) < 1e-8 && viewport.Locked && !viewport.PerspectiveOn,
                        "Spatial navigation lost scale, lock or projection type");
                    Check(viewport.ViewTarget.DistanceTo(new Point3d(100,200,300)) < 1e-8 && Math.Abs(viewport.TwistAngle - index * .3) < 1e-8,
                        "Spatial navigation changed target or twist");
                    ++found;
                }
            }
            Check(found == 4, "Spatial navigation viewport count changed");
        }
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, "非俯视裁剪" + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var blocks = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            var found = 0;
            foreach (ObjectId blockId in blocks)
            foreach (ObjectId id in (BlockTableRecord)tx.GetObject(blockId, OpenMode.ForRead))
            {
                var viewport = tx.GetObject(id, OpenMode.ForRead) as Viewport;
                if (viewport == null || Math.Abs(viewport.Width - 170) > 1e-7) continue;
                var values = expected[0];
                var worldCenter = new Point3d(values[0] - 35 * values[3], values[1], values[2] - 15 * values[3]);
                var toWorld = Matrix3d.Displacement(viewport.ViewTarget.GetAsVector()) * Matrix3d.PlaneToWorld(viewport.ViewDirection);
                var displayCenter = worldCenter.TransformBy(toWorld.Inverse());
                Check(viewport.ViewCenter.GetDistanceTo(new Point2d(displayCenter.X, displayCenter.Y)) < 1e-7 &&
                    viewport.NonRectClipOn && Math.Abs(viewport.CustomScale - 1 / values[3]) < 1e-8,
                    "Spatial clip changed projection center or scale");
                ++found;
            }
            Check(found == 1, "Spatial clipping viewport missing");
        }
        using (var db = Open(Path.Combine(output, "透视视口保护.dwg")))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var blocks = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            var count = 0;
            foreach (ObjectId blockId in blocks)
            foreach (ObjectId id in (BlockTableRecord)tx.GetObject(blockId, OpenMode.ForRead))
            {
                var viewport = tx.GetObject(id, OpenMode.ForRead) as Viewport;
                if (viewport == null || !viewport.PerspectiveOn) continue;
                Check(viewport.ViewCenter.GetDistanceTo(new Point2d(50,40)) < 1e-8 && Math.Abs(viewport.ViewHeight - 2000) < 1e-8,
                    "Perspective viewport camera changed");
                ++count;
            }
            Check(count == 1, "Perspective viewport flag lost");
        }
    }
    private static Database Open(string path)
    {
        var db = new Database(false, true);
        try
        {
            if (Path.GetExtension(path) == ".dxf") db.DxfIn(path, Path.ChangeExtension(path, ".dxf.log"));
            else { db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null); db.CloseInput(true); }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static void VerifyMediaClipping(string output)
    {
        foreach (var name in new[] { "viewport-media", "viewport-media-outside", "viewport-media-text" })
        foreach (var extension in new[] { ".dwg", ".dxf" })
        {
            File.WriteAllText(Path.Combine(output,"native-media-phase.txt"),name+extension+" opening");
            using (var db = Open(Path.Combine(output, name + "-saved" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                File.WriteAllText(Path.Combine(output,"native-media-phase.txt"),name+extension+" opened");
                var model = Model(db, tx);
                File.WriteAllText(Path.Combine(output,"native-media-phase.txt"),name+extension+" model");
                if (name == "viewport-media-text")
                {
                    var text = model.OfType<DBText>().Single();
                    Check(text.TextString == "MMMM" && Math.Abs(text.Height - 40) < .001 &&
                          text.Position.DistanceTo(new Point3d(120,135,0)) < .001, "Viewport clipping altered source text");
                }
                else
                {
                    var image = model.OfType<RasterImage>().Single();
                    File.WriteAllText(Path.Combine(output,"native-media-phase.txt"),name+extension+" image");
                    var definition = (RasterImageDef)tx.GetObject(image.ImageDefId, OpenMode.ForRead);
                    File.WriteAllText(Path.Combine(output,"native-media-phase.txt"),name+extension+" definition");
                    Check(definition.SourceFileName.EndsWith("detailed-checker.png", StringComparison.OrdinalIgnoreCase) &&
                          File.Exists(definition.SourceFileName), "Viewport image reference was lost");
                    Check(Math.Abs(image.Orientation.Xaxis.Length - (name == "viewport-media" ? 500 : 120)) < .001 &&
                          Math.Abs(image.Orientation.Yaxis.Length - (name == "viewport-media" ? 400 : 60)) < .001,
                          "CAD image display dimensions changed after loading pixel data");
                }
                var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var layout = (Layout)tx.GetObject(dictionary.GetAt("媒体裁剪"), OpenMode.ForRead);
                var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var viewport = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<Viewport>().Single(item => Math.Abs(item.Width-300)<.001);
                Check(viewport.NonRectClipOn && Math.Abs(viewport.CustomScale-1)<.000001,
                    "Media viewport clip or 1:1 scale was lost");
                Check(((Polyline)tx.GetObject(viewport.NonRectClipEntityId, OpenMode.ForRead)).NumberOfVertices==6,
                    "Media viewport concave boundary was lost");
            }
        }
        File.WriteAllText(Path.Combine(output,"native-media-result.txt"),"CAD_MEDIA_ROUNDTRIP_OK text image reference clip 1to1");
    }

    private static void VerifyInlineViewportEditing(string output)
    {
        foreach (var name in new[] { "原位模型编辑", "原位导航", "原位凹形裁剪", "原位圆形裁剪", "原位原图编辑" })
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, name + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var model = Model(db, tx);
            var expectedCount = name == "原位圆形裁剪" ? 6 : name == "原位凹形裁剪" || name == "原位原图编辑" ? 5 : 4;
            Check(model.Count == expectedCount, "Inline edits lost model entities or changed ownership " + name + extension);
            var moved = model.OfType<Line>().Single(item => Math.Abs(item.StartPoint.Y-1250) < .001);
            Check(moved.StartPoint.DistanceTo(new Point3d(100, 1250, 0)) < .001 && moved.EndPoint.DistanceTo(new Point3d(3100, 1250, 0)) < .001,
                "Inline move used paper coordinates " + name + extension);
            Check(model.OfType<Circle>().Any(item => item.Center.DistanceTo(new Point3d(500, 700, 0)) < .001 && Math.Abs(item.Radius - 100) < .001),
                "Inline drawing dimensions changed " + name + extension);
            var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layout = (Layout)tx.GetObject(dictionary.GetAt("原位编辑布局"), OpenMode.ForRead);
            var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var entities = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).ToArray();
            var paperLines = entities.OfType<Line>().ToArray();
            Check(paperLines.Length == 2 && paperLines.Any(item => item.StartPoint.DistanceTo(new Point3d(120,60,0)) < .001 &&
                item.EndPoint.DistanceTo(new Point3d(180,60,0)) < .001), "Inline editing changed paper annotations " + name + extension);
            var first = entities.OfType<Viewport>().Single(item => item.CenterPoint.DistanceTo(new Point3d(150,100,0)) < .001);
            var second = entities.OfType<Viewport>().Single(item => item.CenterPoint.DistanceTo(new Point3d(500,100,0)) < .001);
            Check(Math.Abs(second.CustomScale-.1) < 1e-8 && second.Locked && !second.NonRectClipOn,
                "Editing one viewport changed another viewport " + name + extension);
            var navigation = name == "原位导航";
            Check(first.Locked != navigation && Math.Abs(first.CustomScale-(navigation ? .11 : .1)) < 1e-8,
                "Inline viewport scale or locking changed " + name + extension);
            Check(first.GetFrozenLayers().Cast<ObjectId>().Count() == 1 && second.GetFrozenLayers().Cast<ObjectId>().Count() == 0,
                "Inline frozen layers changed " + name + extension);
            if (navigation)
            {
                var values = File.ReadAllLines(Path.Combine(output, "inline-navigation-center.txt"));
                var x = double.Parse(values[0], System.Globalization.CultureInfo.InvariantCulture);
                var y = double.Parse(values[1], System.Globalization.CultureInfo.InvariantCulture);
                var angle = Math.PI / 6;
                Check(first.ViewCenter.GetDistanceTo(new Point2d(x*Math.Cos(angle)-y*Math.Sin(angle),x*Math.Sin(angle)+y*Math.Cos(angle))) < .001,
                    "Inline navigation center changed " + extension);
            }
            if (name == "原位凹形裁剪")
            {
                var boundary = (Polyline)tx.GetObject(first.NonRectClipEntityId, OpenMode.ForRead);
                Check(first.NonRectClipOn && boundary.Closed && boundary.NumberOfVertices == 6 && boundary.OwnerId == first.OwnerId,
                    "Inline concave boundary changed " + extension);
            }
            if (name == "原位圆形裁剪")
            {
                var boundary = (Circle)tx.GetObject(first.NonRectClipEntityId, OpenMode.ForRead);
                Check(first.NonRectClipOn && Math.Abs(boundary.Radius-90) < .001 && boundary.OwnerId == first.OwnerId,
                    "Inline circular boundary changed " + extension);
            }
        }
    }

    private static void VerifyViewportEditing(string output)
    {
        foreach (var name in new[] { "视口内编辑.dwg", "视口内编辑.dxf", "视口编辑返回.dwg", "视口原图编辑.dwg", "视口原图编辑.dxf" })
        using (var db = Open(Path.Combine(output, name)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var model = Model(db, tx);
            Check(model.Count == (name.StartsWith("视口原图编辑") ? 4 : 3), "Viewport edits changed model-space ownership " + name);
            var moved = model.OfType<Line>().Single(item => Math.Abs(item.StartPoint.Y - 250) < .001);
            if (name.StartsWith("视口原图编辑"))
                Check(model.OfType<Line>().Any(item => item.StartPoint.DistanceTo(new Point3d(100, 1200, 0)) < .001 && item.EndPoint.DistanceTo(new Point3d(1000, 1200, 0)) < .001),
                    "Source-based viewport addition was lost " + name);
            Check(moved.StartPoint.DistanceTo(new Point3d(500, 250, 0)) < .001 && moved.EndPoint.DistanceTo(new Point3d(1500, 250, 0)) < .001,
                "Viewport move used paper units " + name);
            var frozen = model.OfType<Line>().Single(item => item.Layer == "编辑视口冻结层");
            Check(frozen.StartPoint.DistanceTo(new Point3d(0, 1000, 0)) < .001 && frozen.EndPoint.DistanceTo(new Point3d(1000, 1000, 0)) < .001,
                "Frozen model geometry was changed " + name);
            var circle = model.OfType<Circle>().Single();
            Check(circle.Center.DistanceTo(new Point3d(500, 700, 0)) < .001 && Math.Abs(circle.Radius - 100) < .001,
                "Viewport drawing used paper units " + name);
            var layers = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
            Check(!((LayerTableRecord)tx.GetObject(layers["编辑视口冻结层"], OpenMode.ForRead)).IsOff, "Viewport freeze became a global layer switch " + name);
            var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layout = (Layout)tx.GetObject(dictionary.GetAt("视口模型编辑"), OpenMode.ForRead);
            var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var entities = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).ToArray();
            var paperLine = entities.OfType<Line>().Single();
            Check(paperLine.StartPoint.DistanceTo(Point3d.Origin) < .001 && paperLine.EndPoint.DistanceTo(new Point3d(200, 0, 0)) < .001,
                "Paper-space line changed while editing model " + name);
            var viewport = entities.OfType<Viewport>().Single(item => Math.Abs(item.Width - 300) < .001);
            Check(viewport.Locked && Math.Abs(viewport.CustomScale - .1) < 1e-8 && Math.Abs(viewport.Height - 200) < .001 &&
                Math.Abs(viewport.TwistAngle - Math.PI / 6) < 1e-8 && viewport.CenterPoint.DistanceTo(new Point3d(150, 100, 0)) < .001,
                "Maximized viewport changed native layout parameters " + name);
            Check(viewport.GetFrozenLayers().Cast<ObjectId>().Single() == layers["编辑视口冻结层"], "Viewport frozen layer changed " + name);
        }
    }

    private static void VerifyCurveClipping(string output)
    {
        foreach (var extension in new[] { ".dwg", ".dxf" })
        foreach (var name in new[] { "圆形视口", "椭圆视口", "竖向椭圆视口", "椭圆复制", "曲线恢复矩形", "旋转椭圆视口", "旋转椭圆布局复制" })
        using (var db = Open(Path.Combine(output, name + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var rotated = name.StartsWith("旋转椭圆", StringComparison.Ordinal);
            var layouts = name == "椭圆复制" ? new[] { "裁剪平面", "曲线副本" } :
                name == "旋转椭圆布局复制" ? new[] { "裁剪平面", "旋转椭圆副本" } : new[] { "裁剪平面" };
            var boundaries = new List<ObjectId>();
            foreach (var layoutName in layouts)
            {
                var layout = (Layout)tx.GetObject(dictionary.GetAt(layoutName), OpenMode.ForRead);
                var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var entities = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).ToArray();
                var width = rotated ? 2 * Math.Sqrt(11600) : name == "圆形视口" || name == "竖向椭圆视口" ? 160 : 280;
                var viewport = entities.OfType<Viewport>().Single(item => Math.Abs(item.Width - width) < .001);
                Check(Math.Abs(viewport.CustomScale - .02) < 1e-8, "Curve viewport scale changed " + name + extension);
                if (name == "曲线恢复矩形")
                {
                    Check(!viewport.NonRectClipOn && !entities.OfType<Curve>().Any(), "Restored curve left an orphan boundary " + extension);
                    continue;
                }
                Check(viewport.NonRectClipOn && !viewport.NonRectClipEntityId.IsNull, "Native curve clip missing " + name + extension);
                var boundary = (Curve)tx.GetObject(viewport.NonRectClipEntityId, OpenMode.ForRead);
                Check(boundary.OwnerId == paper.ObjectId && entities.OfType<Curve>().Count() == 1, "Curve owner or duplicate changed " + name + extension);
                boundaries.Add(boundary.ObjectId);
                if (name == "圆形视口")
                {
                    var circle = boundary as Circle;
                    Check(circle != null && Math.Abs(circle.Radius - 80) < .001 && circle.Center.DistanceTo(new Point3d(150, 100, 0)) < .001, "Native circle dimensions changed " + extension);
                }
                else if (rotated)
                {
                    var ellipse = boundary as Ellipse;
                    Check(ellipse != null && ellipse.Center.DistanceTo(new Point3d(170,110,0)) < 1e-8 &&
                        Math.Abs(ellipse.MajorRadius - Math.Sqrt(20000)) < 1e-8 && Math.Abs(ellipse.MinorRadius - Math.Sqrt(3200)) < 1e-8 &&
                        Math.Abs(Math.Abs(ellipse.MajorAxis.GetNormal().DotProduct(new Vector3d(1,1,0).GetNormal())) - 1) < 1e-8,
                        "Rotated ellipse dimensions or axis changed " + extension);
                }
                else
                {
                    var ellipse = boundary as Ellipse;
                    Check(ellipse != null && Math.Abs(ellipse.MajorRadius - 140) < .001 && Math.Abs(ellipse.MinorRadius - 80) < .001 && ellipse.Center.DistanceTo(new Point3d(150, 100, 0)) < .001, "Native ellipse dimensions changed " + extension);
                }
            }
            Check(boundaries.Distinct().Count() == boundaries.Count, "Copied curve layouts share one boundary " + extension);
        }
    }

    private static void VerifyLayouts(string output)
    {
        foreach (var extension in new[] { ".dwg", ".dxf" })
        {
            File.WriteAllText(Path.Combine(output,"native-layout-phase.txt"),"多边形视口"+extension);
            using (var db = Open(Path.Combine(output, "多边形视口" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var layout = (Layout)tx.GetObject(dictionary.GetAt("裁剪平面"), OpenMode.ForRead);
                var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var entities = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).ToArray();
                var viewport = entities.OfType<Viewport>().Single(item => Math.Abs(item.Width - 300) < .001);
                Check(viewport.NonRectClipOn && !viewport.NonRectClipEntityId.IsNull && Math.Abs(viewport.CustomScale - .02) < 1e-8, "Native viewport polygon clip/scale missing");
                var boundary = (Polyline)tx.GetObject(viewport.NonRectClipEntityId, OpenMode.ForRead);
                Check(boundary.Closed && boundary.NumberOfVertices == 6 && boundary.OwnerId == viewport.OwnerId && entities.OfType<Polyline>().Count() == 1, "Clip boundary ownership/duplicates changed");
                Check(boundary.GetPoint2dAt(4).GetDistanceTo(new Point2d(150,100)) < .001, "Concave clip vertex changed");
            }
            File.WriteAllText(Path.Combine(output,"native-layout-phase.txt"),"恢复矩形视口"+extension);
            using (var db = Open(Path.Combine(output, "恢复矩形视口" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var layout = (Layout)tx.GetObject(dictionary.GetAt("裁剪平面"), OpenMode.ForRead);
                var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var entities = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).ToArray();
                Check(!entities.OfType<Viewport>().Single(item => Math.Abs(item.Width - 300) < .001).NonRectClipOn && !entities.OfType<Polyline>().Any(), "Restoring rectangle left an orphan boundary");
            }
            File.WriteAllText(Path.Combine(output,"native-layout-phase.txt"),"裁剪重画"+extension);
            using (var db = Open(Path.Combine(output, "裁剪重画" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var dictionary=(DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
                var layout=(Layout)tx.GetObject(dictionary.GetAt("裁剪平面"),OpenMode.ForRead);
                var paper=(BlockTableRecord)tx.GetObject(layout.BlockTableRecordId,OpenMode.ForRead);
                var entities=paper.Cast<ObjectId>().Select(id=>tx.GetObject(id,OpenMode.ForRead)).ToArray();
                var boundary=entities.OfType<Polyline>().Single();
                Check(boundary.NumberOfVertices==4 && boundary.GetPoint2dAt(0).GetDistanceTo(new Point2d(10,20))<.001,"Redrawn clip was not updated");
                Check(Math.Abs(entities.OfType<Viewport>().Single(item=>Math.Abs(item.Width-190)<.001).CustomScale-.02)<1e-8,"Redrawn viewport scale changed");
            }
            File.WriteAllText(Path.Combine(output,"native-layout-phase.txt"),"裁剪布局复制"+extension);
            using (var db = Open(Path.Combine(output, "裁剪布局复制" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var dictionary=(DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
                var boundaries=new List<ObjectId>();
                foreach(var name in new[]{"裁剪平面","裁剪副本"})
                {
                    var layout=(Layout)tx.GetObject(dictionary.GetAt(name),OpenMode.ForRead);
                    var paper=(BlockTableRecord)tx.GetObject(layout.BlockTableRecordId,OpenMode.ForRead);
                    var candidates=paper.Cast<ObjectId>().Select(id=>tx.GetObject(id,OpenMode.ForRead)).OfType<Viewport>().ToArray();
                    File.AppendAllLines(Path.Combine(output,"clip-owner-debug.txt"),new[]{name+": "+string.Join(",",candidates.Select(item=>item.Width+"/"+item.Height+" id "+item.Handle.ToString()))});
                    var viewport=candidates.Single(item=>Math.Abs(item.Width-300)<.001);
                    boundaries.Add(viewport.NonRectClipEntityId);
                    Check(((Polyline)tx.GetObject(viewport.NonRectClipEntityId,OpenMode.ForRead)).OwnerId==layout.BlockTableRecordId,"Copied clip owner changed");
                }
                Check(boundaries.Distinct().Count()==2,"Copied layouts share one clip boundary");
            }
            File.WriteAllText(Path.Combine(output,"native-layout-phase.txt"),"视口内容调整"+extension);
            using (var db = Open(Path.Combine(output, "视口内容调整" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var layout = (Layout)tx.GetObject(dictionary.GetAt("平面布置"), OpenMode.ForRead);
                var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var viewport = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<Viewport>().Single(item => Math.Abs(item.Width - 330) < .001);
                Check(Math.Abs(viewport.CustomScale - 1d / 62.5) < .000001 && !viewport.Locked, "Navigated viewport scale/lock changed");
                Check(viewport.CenterPoint.DistanceTo(new Point3d(210, 148, 0)) < .001 && Math.Abs(viewport.Height - 200) < .001, "Content navigation moved paper frame");
                var expected = File.ReadAllLines(Path.Combine(output, "viewport-navigation-center.txt"));
                var modelX = double.Parse(expected[0], System.Globalization.CultureInfo.InvariantCulture);
                var modelY = double.Parse(expected[1], System.Globalization.CultureInfo.InvariantCulture);
                Check(viewport.ViewCenter.GetDistanceTo(new Point2d(-modelY, modelX)) < .001 && Math.Abs(viewport.TwistAngle - Math.PI / 2) < .000001, "Content pan/twist did not persist");
            }
            File.WriteAllText(Path.Combine(output,"native-layout-phase.txt"),"布局视口"+extension);
            using (var db = Open(Path.Combine(output, "布局视口" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var layout = (Layout)tx.GetObject(dictionary.GetAt("平面布置"), OpenMode.ForRead);
                Check(Math.Abs(layout.PlotPaperSize.X - 594) < .001 && Math.Abs(layout.PlotPaperSize.Y - 420) < .001, "Physical layout page size changed");
                var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var viewport = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<Viewport>().Single(item => Math.Abs(item.Width - 300) < .001);
                Check(Math.Abs(viewport.Height - 180) < .001 && Math.Abs(viewport.CustomScale - .02) < .000001, "Viewport dimensions/scale changed");
                Check(viewport.CenterPoint.DistanceTo(new Point3d(210, 148, 0)) < .001 && Math.Abs(viewport.TwistAngle - Math.PI / 6) < .000001, "Viewport paper location/twist changed");
                Check(viewport.Locked && viewport.GetFrozenLayers().Cast<ObjectId>().Any(id => ((LayerTableRecord)tx.GetObject(id, OpenMode.ForRead)).Name == "视口冻结层"), "Viewport lock/frozen layers changed");
            }
            File.WriteAllText(Path.Combine(output,"native-layout-phase.txt"),"布局修改复制"+extension);
            using (var db = Open(Path.Combine(output, "布局修改复制" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var ownerIds = new List<ObjectId>();
                foreach (var name in new[] { "平面布置", "复制布置" })
                {
                    var layout = (Layout)tx.GetObject(dictionary.GetAt(name), OpenMode.ForRead);
                    ownerIds.Add(layout.BlockTableRecordId);
                    if (name == "复制布置") Check(layout.TabOrder == 1, "Copied layout tab order changed");
                    var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    var viewports = paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<Viewport>().Where(item => item.Width > 1 && item.ViewHeight > 0).ToArray();
                    Check(viewports.Length == 2, "Copied layout viewport ownership changed");
                    var edited = viewports.Single(item => item.Width > 300);
                    Check(Math.Abs(edited.CustomScale - 1d / 75) < .000001 && !edited.Locked && Math.Abs(edited.TwistAngle - Math.PI / 2) < .000001, "Edited/gripped viewport parameters changed");
                }
                Check(ownerIds.Distinct().Count() == 2, "Two layouts share one paper-space record");
                Check(Model(db, tx).OfType<Line>().Count() == 2, "Layout copying duplicated model-space content");
            }
            File.WriteAllText(Path.Combine(output,"native-layout-phase.txt"),"layout-metadata"+extension);
            using (var db = Open(Path.Combine(output, "layout-metadata" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var inchPage = (Layout)tx.GetObject(dictionary.GetAt("英制页面"), OpenMode.ForRead);
                Check(inchPage.PlotPaperUnits == PlotPaperUnit.Inches && Math.Abs(inchPage.PlotPaperSize.X - 609.6) < .001, "Inch layout physical paper millimetres changed");
                foreach (var name in new[] { "改名页面", "复制页面" })
                {
                    var layout = (Layout)tx.GetObject(dictionary.GetAt(name), OpenMode.ForRead);
                    Check(layout.PlotConfigurationName == "DWG To PDF.pc3" && layout.CurrentStyleSheet == "monochrome.ctb", "Original/copied layout plot device or style changed");
                    Check(layout.PlotRotation == PlotRotation.Degrees090 && Math.Abs(layout.PlotPaperSize.X - 594) < .001 && Math.Abs(layout.PlotPaperSize.Y - 420) < .001, "Original page rotation/size changed");
                    Check(Math.Abs(layout.CustomPrintScale.Denominator - 2) < .001 && layout.PlotOrigin.GetDistanceTo(new Point2d(7, 9)) < .001, "Original page custom scale/origin changed");
                }
            }
            File.WriteAllText(Path.Combine(output,"native-layout-phase.txt"),"布局删除"+extension);
            using (var db = Open(Path.Combine(output, "布局删除" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                Check(!dictionary.Contains("复制布置") && dictionary.Count == 4, "Deleted layout was recreated or other pages changed");
            }
        }
        File.WriteAllText(Path.Combine(Root, "layouts-ok.txt"), "CAD_LAYOUTS_ROUNDTRIP_OK page size rotation order copy separateOwners modelCount viewport dimensions scale twist lock frozenLayers sourceDeviceStyleScaleOrigin");
    }
    private static void VerifyManagement(string output)
    {
        foreach (var extension in new[] { ".dwg", ".dxf" })
        {
            using (var db = Open(Path.Combine(output, "drawing-settings-edited" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var table = (LinetypeTable)tx.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                var type = (LinetypeTableRecord)tx.GetObject(table["工程点划线"], OpenMode.ForRead);
                Check(type.NumDashes == 4 && Math.Abs(type.DashLengthAt(0) - 24) < .001, "Edited source line type pattern was lost");
                Check(Math.Abs(db.Ltscale - 20) < .001 && !db.Psltscale, "Global or paper line type scale changed");
                Check(db.Pdmode == 35 && Math.Abs(db.Pdsize + 3) < .001, "Point display mode/size changed");
                Check(((LinetypeTableRecord)tx.GetObject(db.Celtype, OpenMode.ForRead)).Name == "工程点划线", "Current line type changed");
                var layers = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
                Check(((LayerTableRecord)tx.GetObject(layers["线型验证"], OpenMode.ForRead)).LinetypeObjectId == type.ObjectId, "Layer line type changed");
            }
            using (var db = Open(Path.Combine(output, "clipboard-transfer" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var objects = Model(db, tx);
                Check(objects.Count == 3, "Clipboard model entity count changed");
                var source = objects.OfType<BlockReference>().Single(item => ((BlockTableRecord)tx.GetObject(item.BlockTableRecord, OpenMode.ForRead)).Name == "源块");
                Check(source.Position.DistanceTo(new Point3d(10000, 1000, 0)) < .001 && source.Layer == "源图层", "Clipboard block location or layer remapping failed");
                var block = (BlockTableRecord)tx.GetObject(source.BlockTableRecord, OpenMode.ForRead);
                var nested = block.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<BlockReference>().Single();
                var inner = (BlockTableRecord)tx.GetObject(nested.BlockTableRecord, OpenMode.ForRead);
                Check(inner.Name == "内部块" && inner.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<Line>().Single().Length == 1000, "Clipboard nested definition remapping failed");
                var text = objects.OfType<DBText>().Single();
                Check(text.Position.DistanceTo(new Point3d(10000, 1300, 0)) < .001 && text.Layer == "源图层", "Clipboard text location/layer changed");
                Check(((TextStyleTableRecord)tx.GetObject(text.TextStyleId, OpenMode.ForRead)).Name == "源文字样式", "Clipboard text style remapping failed");
                Check(objects.OfType<BlockReference>().Any(item => ((BlockTableRecord)tx.GetObject(item.BlockTableRecord, OpenMode.ForRead)).Name == "目标块"), "Existing target block was overwritten");
            }
            using (var db = Open(Path.Combine(output, "xref-located" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var reference = Model(db, tx).OfType<BlockReference>().Single();
                var block = (BlockTableRecord)tx.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                Check(block.IsFromExternalReference && File.Exists(block.PathName) && reference.Position.DistanceTo(new Point3d(100, 200, 0)) < .001, "Located external reference identity/path/position changed");
            }
            using (var db = Open(Path.Combine(output, "named-style-management" + extension)))
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var textStyles = (TextStyleTable)tx.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                Check(textStyles.Has("已改名文字样式") && !textStyles.Has("待改名文字样式") && !textStyles.Has("待删文字样式"), "Text style rename/delete failed");
                var mlineStyles = (DBDictionary)tx.GetObject(db.MLStyleDictionaryId, OpenMode.ForRead);
                Check(mlineStyles.Contains("已改名多线样式") && !mlineStyles.Contains("待改名多线样式") && !mlineStyles.Contains("待删多线样式"), "MLine style rename/delete failed");
                var objects = Model(db, tx);
                Check(objects.OfType<DBText>().Single().TextStyleId == textStyles["已改名文字样式"], "Renamed text style reference changed");
                Check(objects.OfType<Mline>().Single().Style == mlineStyles.GetAt("已改名多线样式"), "Renamed MLine style reference changed");
            }
        }
        using (var db = Open(Path.Combine(output, "clipboard-xref.dwg")))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var reference = Model(db, tx).OfType<BlockReference>().Single();
            var block = (BlockTableRecord)tx.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
            Check(block.IsFromExternalReference && File.Exists(block.PathName) && reference.Position.DistanceTo(new Point3d(1100, 1200, 0)) < .001, "Clipboard external reference path/position changed");
        }
        using (var db = Open(Path.Combine(output, "clipboard-paper.dwg")))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layout = (Layout)tx.GetObject(layouts.GetAt("剪贴板布局"), OpenMode.ForRead);
            var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            Check(paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<DBText>().Any(item => item.TextString == "跨图纸文字"), "Clipboard paper-space ownership changed");
        }
        File.WriteAllText(Path.Combine(Root, "management-ok.txt"), "CAD_MANAGEMENT_ROUNDTRIP_OK DWG DXF lineType pattern current scales pointMode layer clipboard names nestedBlocks textStyles positions paperSpace externalReference");
    }
    private static void VerifyStyles(string output)
    {
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, "cad-styles-edited" + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var table = (TextStyleTable)tx.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            Check(table.Has("工程正文") && table.Has("未用文字样式"), "Named or unused text style lost");
            var style = (TextStyleTableRecord)tx.GetObject(table["工程正文"], OpenMode.ForRead);
            Check(Math.Abs(style.TextSize - 250) < .001 && Math.Abs(style.XScale - .8) < .001 && Math.Abs(style.ObliquingAngle - Math.PI / 15) < .001, "Text style parameters changed");
            Check(((TextStyleTableRecord)tx.GetObject(db.Textstyle, OpenMode.ForRead)).Name == "工程正文", "Current text style changed");
            var entities = Model(db, tx);
            Check(entities.OfType<DBText>().Single().TextStyleId == style.ObjectId, "Text entity references the wrong style");
            var lines = entities.OfType<Mline>().OrderBy(item => item.VertexAt(0).Y).ToArray();
            Check(lines.Length == 2, "MLine count changed");
            var oldStyle = (MlineStyle)tx.GetObject(lines[0].Style, OpenMode.ForRead);
            var newStyle = (MlineStyle)tx.GetObject(lines[1].Style, OpenMode.ForRead);
            Check(oldStyle.Elements.Count == 3 && newStyle.Elements.Count == 3, "MLine element count changed");
            Check(Math.Abs(oldStyle.Elements[0].Offset - .5) < .001 && Math.Abs(newStyle.Elements[0].Offset - .75) < .001, "Old/new MLine geometry did not retain separate styles");
            Check(newStyle.StartSquareCap && newStyle.EndSquareCap, "MLine square cap flags changed");
            File.AppendAllText(Path.Combine(Root, "styles-colors.txt"), extension + " old=" + oldStyle.Elements[1].Color.ToString() + " new=" + newStyle.Elements[1].Color.ToString() + " oldRGB=" + oldStyle.Elements[1].Color.ColorValue.ToString() + " newRGB=" + newStyle.Elements[1].Color.ColorValue.ToString() + Environment.NewLine);
            Check(newStyle.Elements[1].Color.ColorValue.G == 255 && oldStyle.Elements[1].Color.ColorValue.R == 255, "MLine element color changed");
            Check(Math.Abs(lines[0].Scale - 200) < .001, "MLine scale changed");
        }
        File.WriteAllText(Path.Combine(Root, "styles-ok.txt"), "CAD_STYLES_ROUNDTRIP_OK DWG DXF names current fixedHeight width oblique unusedStyle snapshots colors capFlags");
    }
    private static void VerifyExtended(string output)
    {
        foreach (var extension in new[] { ".dwg", ".dxf" })
        using (var db = Open(Path.Combine(output, "扩展二维绘图" + extension)))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var entities = Model(db, tx);
            File.AppendAllText(Path.Combine(Root, "extended-types.txt"), extension + Environment.NewLine + string.Join(Environment.NewLine, entities.GroupBy(item => item.GetType().Name).Select(group => group.Key + "=" + group.Count())) + Environment.NewLine);
            Check(entities.Count == 26, "Extended drawing entity count changed " + extension);
            Check(entities.OfType<Ellipse>().Count() == 2 && entities.OfType<Spline>().Count() == 1, "Curves lost " + extension);
            Check(entities.OfType<DBPoint>().Count() == 1 && entities.OfType<Ray>().Count() == 1 && entities.OfType<Xline>().Count() == 1, "Point or construction lines lost " + extension);
            var text = entities.OfType<DBText>().Single();
            Check(text.TextString == "独立单行文字" && Math.Abs(text.Height - 150) < .001 && Math.Abs(text.Rotation - Math.PI / 12) < .001, "New text properties changed " + extension);
            Check(entities.OfType<MText>().Single().Contents.Contains("第二行"), "MText content lost " + extension);
            var hatch = entities.OfType<Hatch>().Single();
            Check(hatch.PatternName == "ANSI31" && Math.Abs(hatch.PatternScale - 10) < .001 && hatch.NumberOfLoops > 0, "Hatch parameters lost " + extension);
            Check(entities.OfType<Dimension>().Count() == 7, "Editable dimensions lost " + extension);
            Check(entities.OfType<Dimension>().Select(item => item.DimensionStyle).Distinct().Count() == 1, "Identical dimension styles were duplicated " + extension);
            var dimensionStyle = (DimStyleTableRecord)tx.GetObject(entities.OfType<Dimension>().First().DimensionStyle, OpenMode.ForRead);
            Check(Math.Abs(dimensionStyle.Dimtxt - 100) < .001 && Math.Abs(dimensionStyle.Dimasz - 80) < .001, "Dimension parameters changed " + extension);
            Check(Math.Abs(entities.OfType<RotatedDimension>().Single().Measurement - 1000) < .01, "Linear dimension measurement changed " + extension);
            Check(Math.Abs(entities.OfType<AlignedDimension>().Single().Measurement - Math.Sqrt(1250000)) < .01, "Aligned dimension measurement changed " + extension);
            Check(Math.Abs(entities.OfType<RadialDimension>().Single().Measurement - 500) < .01, "Radius measurement changed " + extension);
            Check(Math.Abs(entities.OfType<DiametricDimension>().Single().Measurement - 1000) < .01, "Diameter measurement changed " + extension);
            Check(entities.OfType<Leader>().Single().NumVertices == 2 && entities.OfType<MLeader>().Count() == 1, "Leader geometry lost " + extension);
            var blocks = entities.OfType<BlockReference>().ToArray();
            Check(blocks.Any(item => ((BlockTableRecord)tx.GetObject(item.BlockTableRecord, OpenMode.ForRead)).Comments.StartsWith("WanluoJoggedV1 ", StringComparison.Ordinal)), "Jogged dimension display block lost " + extension);
            var tableBlock = blocks.Select(item => (BlockTableRecord)tx.GetObject(item.BlockTableRecord, OpenMode.ForRead)).Single(item => item.Comments.StartsWith("WanluoTableV1 ", StringComparison.Ordinal));
            var cells = tableBlock.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).ToArray();
            Check(cells.OfType<Line>().Count() >= 6 && cells.OfType<MText>().Any(item => item.Contents.Contains("表格单元")), "Table grid or cell text lost " + extension);
        }
        using (var db = Open(Path.Combine(output, "二维阵列.dwg")))
        using (var tx = db.TransactionManager.StartTransaction())
            Check(Model(db, tx).OfType<Line>().Count() == 6, "Rectangular array copy count changed");
        using (var db = Open(Path.Combine(output, "二维块.dwg")))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var references = Model(db, tx).OfType<BlockReference>().ToArray();
            Check(references.Length == 2 && references.All(item => ((BlockTableRecord)tx.GetObject(item.BlockTableRecord, OpenMode.ForRead)).Name == "独立验证块"), "Named blocks lost");
            Check(references.Any(item => item.Position.DistanceTo(new Point3d(5000, 0, 0)) < .01), "Block insertion moved");
        }
        using (var db = Open(Path.Combine(output, "图层布局.dwg")))
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var layers = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
            var layer = (LayerTableRecord)tx.GetObject(layers["修改验证层"], OpenMode.ForRead);
            Check(layer.Color.Green == 255 && (int)layer.LineWeight == 35, "Layer color or weight lost");
            var dictionary = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            Check(dictionary.Contains("打印验证"), "Added layout lost");
            var layout = (Layout)tx.GetObject(dictionary.GetAt("打印验证"), OpenMode.ForRead);
            var paper = (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            Check(paper.Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<DBText>().Any(item => item.TextString == "图纸空间文字"), "Paper-space text lost");
        }
    }
    private static List<Entity> Model(Database db, Transaction tx)
    {
        var table = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
        return ((BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead)).Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<Entity>().ToList();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
