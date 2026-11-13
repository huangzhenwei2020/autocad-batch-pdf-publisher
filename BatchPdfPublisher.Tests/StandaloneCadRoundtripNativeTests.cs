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
            File.WriteAllText(Path.Combine(Root, "roundtrip-ok.txt"), "CAD_ROUNDTRIP_OK dwg dxf dimensions units layers attributes text xdata dictionary extendedCurves hatch dimensions arrays blocks layouts");
        }
        catch (System.Exception error) { File.WriteAllText(Path.Combine(Root, "failure.txt"), error.ToString()); }
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
