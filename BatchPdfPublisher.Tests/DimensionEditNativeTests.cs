using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.Services;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

public static class DimensionEditNativeTests
{
    private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
    private static ObjectId Add(Database db, Point3d a, Point3d b, Point3d line, double angle, string text = "")
    {
        using (var tx = db.TransactionManager.StartTransaction())
        {
            var d = new RotatedDimension(angle, a, b, line, text, db.Dimstyle) { Dimtxt = 123, Dimdec = 0, ColorIndex = 2 };
            ((BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite)).AppendEntity(d); tx.AddNewlyCreatedDBObject(d, true); d.RecomputeDimensionBlock(true);
            var id = d.ObjectId; tx.Commit(); return id;
        }
    }
    private static int Count(Database db) { using (var tx = db.TransactionManager.StartTransaction()) return ((BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>().Count(id => tx.GetObject(id, OpenMode.ForRead) is Dimension); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid operation was accepted"); }
    [CommandMethod("WLDIMCHAINQA")]
    public static void Run()
    {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;
        var old = HostApplicationServices.WorkingDatabase;
        try
        {
            using (var db = new Database(true, true))
            {
                HostApplicationServices.WorkingDatabase = db; db.Insunits = UnitsValue.Millimeters;
                var angle = Math.PI / 7; var u = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0); var v = Vector3d.ZAxis.CrossProduct(u); var o = new Point3d(15000, 31000, 1200);
                var id = Add(db, o + u * 6000, o, o + v * 800, angle); // reversed endpoints
                var chain = CadDimensionChain.Load(db, id);
                Check(chain.Segments.Count == 1 && Math.Abs(chain.Stations.Last() - 6000) < 1e-6, "oblique reverse read");
                chain.Apply(db, "add", 2400, 0, false); chain = CadDimensionChain.Load(db, id);
                Check(Count(db) == 2 && Math.Abs(chain.Segments[0].B - 2400) < 1e-6 && Math.Abs(chain.Segments[1].B - 6000) < 1e-6, "6000 split 2400+3600");
                Check(chain.Segments.All(s => s.Reversed), "reverse origins not retained");
                chain.Apply(db, "add", 2400, 0, false); Check(Count(db) == 2, "duplicate zero inserted");
                chain.Apply(db, "line", 1500, 0, false); chain = CadDimensionChain.Load(db, id);
                Check(Math.Abs(chain.Offset - 1500) < 1e-6 && Math.Abs(chain.Segments.Last().B - 6000) < 1e-6, "line shift changed measured points");
                using (var tx = db.TransactionManager.StartTransaction()) foreach (var s in chain.Segments)
                { var d = (Dimension)tx.GetObject(s.Id, OpenMode.ForRead); Check(d.ColorIndex == 2 && Math.Abs(d.Dimtxt - 123) < 1e-8 && d.Dimdec == 0, "style override lost"); }
                var prior = Count(db); Reject(() => chain.Apply(db, "node", 8000, 1, false)); Check(Count(db) == prior, "failed edit was partial");
                chain.Apply(db, "merge", 0, 1, false); chain = CadDimensionChain.Load(db, id); Check(Count(db) == 1 && Math.Abs(chain.Stations.Last() - 6000) < 1e-6, "merge changed span");
                chain.Apply(db, "add", 2000, 0, false); chain = CadDimensionChain.Load(db, id);
                var surviving = chain.Apply(db, "node", 0, 1, false); chain = CadDimensionChain.Load(db, surviving);
                Check(Count(db) == 1 && Math.Abs(chain.Stations.Last() - 6000) < 1e-6, "zero node collapse failed");
                id = surviving; chain.Apply(db, "add", -1000, 0, false); chain = CadDimensionChain.Load(db, id);
                Check(Count(db) == 2 && Math.Abs(chain.Stations.Last() - chain.Stations.First() - 7000) < 1e-6, "extend chain");
                var tag = chain.Segments[0].Chain;
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    foreach (var s in chain.Segments) { var clone = (Dimension)tx.GetObject(s.Id, OpenMode.ForRead).Clone(); clone.TransformBy(Matrix3d.Displacement(v * 5000)); space.AppendEntity(clone); tx.AddNewlyCreatedDBObject(clone, true); CadDimensionChain.Tag(clone, tx, tag); }
                    tx.Commit();
                }
                chain.Apply(db, "line", 1800, 0, false); chain = CadDimensionChain.Load(db, id);
                Check(chain.Segments.Count == 2 && chain.Segments[0].Chain != tag, "copy still joined to original");
                var save = Path.Combine(Path.GetTempPath(), "WanLuoDimensionChain-" + Guid.NewGuid().ToString("N") + ".dwg"); var handle = id.Handle;
                db.SaveAs(save, DwgVersion.Current);
                using (var reopen = new Database(false, true)) { reopen.ReadDwgFile(save, FileOpenMode.OpenForReadAndAllShare, true, null); HostApplicationServices.WorkingDatabase = reopen; var reopenedId = reopen.GetObjectId(false, handle, 0); Check(CadDimensionChain.Load(reopen, reopenedId).Segments.Count == 2, "save reopen chain"); }
                HostApplicationServices.WorkingDatabase = db;
                chain.Apply(db, "delete", 0, 0, false); Check(Count(db) == 3, "delete segment did not leave gap");
            }
            using (var db = new Database(true, true))
            {
                HostApplicationServices.WorkingDatabase = db;
                var id = Add(db, Point3d.Origin, new Point3d(.01, 0, 0), new Point3d(0, 20, 0), 0, "0");
                var chain = CadDimensionChain.Load(db, id); Check(chain.Stations.Length == 2, "formatted zero erased positive geometry");
                Reject(() => chain.Apply(db, "add", .005, 0, false)); Check(Count(db) == 1, "fixed text rejection not atomic");
                chain.Apply(db, "add", .005, 0, true); Check(Count(db) == 2, "explicit text clearing rejected");
            }
            using (var db = new Database(true, true))
            {
                HostApplicationServices.WorkingDatabase = db;
                var id = Add(db, Point3d.Origin, new Point3d(2000, 0, 0), new Point3d(0, 500, 0), 0);
                Add(db, new Point3d(2000, 0, 0), new Point3d(6000, 0, 0), new Point3d(0, 500, 0), 0);
                Add(db, Point3d.Origin, new Point3d(6000, 0, 0), new Point3d(0, 500, 0), 0);
                Reject(() => CadDimensionChain.Load(db, id)); Check(Count(db) == 3, "ambiguous totals changed");
            }
            editor.WriteMessage("\nDIMENSION_CHAIN_NATIVE_OK oblique reverse split merge zero positiveRounding duplicate lineShift style fixedText rollback copyIsolation saveReopen deletion overlapGuard\n");
        }
        catch (System.Exception ex) { editor.WriteMessage("\nDIMENSION_CHAIN_NATIVE_FAILED " + ex + "\n"); }
        finally { HostApplicationServices.WorkingDatabase = old; }
    }
    [CommandMethod("WLDIMPOINTQA")]
    public static void Point()
    {
        var ed = Application.DocumentManager.MdiActiveDocument.Editor; var p = ed.GetPoint("\nProbe point:");
        if (p.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK) ed.WriteMessage("\nPOINT_QA_VALUE " + p.Value.ToString() + "\n");
    }
    [CommandMethod("WLDIMUISETUP")]
    public static void UiSetup()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc.Database.TransactionManager.TopTransaction != null) throw new InvalidOperationException("Unexpected transaction");
        var root = Environment.GetEnvironmentVariable("WANLUO_DIM_QA_ROOT");
        if (string.IsNullOrEmpty(root) || !root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Isolated QA root required");
        File.WriteAllText(Path.Combine(root, "loaded-plugin.txt"), typeof(DimensionEditService).Assembly.Location);
        using (var tx = doc.Database.TransactionManager.StartTransaction())
        {
            var model = (BlockTableRecord)tx.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            Check(!model.Cast<ObjectId>().Any(id => tx.GetObject(id, OpenMode.ForRead) is Entity), "Fixture requires an empty drawing");
            var d = new RotatedDimension(0, Point3d.Origin, new Point3d(6000, 0, 0), new Point3d(0, 1000, 0), "", doc.Database.Dimstyle) { Dimtxt = 120, Dimasz = 100, Dimdec = 0, Dimscale = 1, Dimtad = 1 };
            model.AppendEntity(d); tx.AddNewlyCreatedDBObject(d, true); d.RecomputeDimensionBlock(true); tx.Commit();
        }
        using (var view = doc.Editor.GetCurrentView()) { view.CenterPoint = new Point2d(3000, 500); view.Width = 9000; view.Height = 5000; doc.Editor.SetCurrentView(view); }
        doc.Database.SaveAs(Path.Combine(root, "dimension-ui.dwg"), DwgVersion.Current);
        DimensionEditService.Show(doc);
        doc.Editor.WriteMessage("\nDIMENSION_UI_READY isolated fixture\n");
    }
    [CommandMethod("WLDIMUIREPORT")]
    public static void UiReport()
    {
        var doc = Application.DocumentManager.MdiActiveDocument; var root = Environment.GetEnvironmentVariable("WANLUO_DIM_QA_ROOT");
        if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("Isolated QA root required");
        using (var tx = doc.Database.TransactionManager.StartTransaction())
        {
            var rows = ((BlockTableRecord)tx.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>().Select(id => tx.GetObject(id, OpenMode.ForRead)).OfType<RotatedDimension>()
                .Select(d => d.XLine1Point + "|" + d.XLine2Point + "|" + d.DimLinePoint + "|" + d.Measurement + "|" + CadDimensionChain.ReadTag(d, tx)).ToArray();
            File.WriteAllLines(Path.Combine(root, "ui-result.txt"), rows); doc.Editor.WriteMessage("\nDIMENSION_UI_REPORT count=" + rows.Length + "\n");
        }
    }
}
