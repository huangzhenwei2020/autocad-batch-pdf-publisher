using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Features.BuildingModel.Services;

public sealed class CadBuildingDiagnosticNativeTests
{
    [CommandMethod("WLDIAGNOSTICGEOMETRYQA")]
    public void Run()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var dbmod = Application.GetSystemVariable("DBMOD");
        try
        {
            using (var poly = new Polyline())
            {
                poly.AddVertexAt(0, new Point2d(1000, 2000), -.414213562373095, 0, 0);
                poly.AddVertexAt(1, new Point2d(4000, 2000), 0, 0, 0);
                poly.AddVertexAt(2, new Point2d(4000, 3500), 0, 0, 0);
                poly.AddVertexAt(3, new Point2d(1000, 3500), 0, 0, 0);
                poly.Closed = true; poly.Elevation = 3300;
                var r = Read(poly); var path = r.GeometryPaths.Single();
                Check(path.Kind == "Polyline" && path.Closed && path.Vertices.Count == 4 && path.Bulges[0] < 0
                    && path.PreviewPoints.Count > 4 && path.Vertices.All(p => p.Z == 3300), "Polyline arc/elevation lost.");
                Check(!r.GeometryTruncated && r.UnsupportedGeometry == 0 && poly.NumberOfVertices == 4 && poly.Elevation == 3300,
                    "Polyline changed during read.");
            }
            using (var arc = new Arc(new Point3d(1000, 2000, 3000), Vector3d.YAxis, 500, 0, Math.PI * 1.5))
            {
                var p = Read(arc).GeometryPaths.Single();
                Check(p.Kind == "Arc" && p.Normal.Y == 1 && p.PreviewPoints.Select(q => q.Z).Distinct().Count() > 2,
                    "Tilted arc flattened.");
            }
            using (var circle = new Circle(new Point3d(1, 2, 3), -Vector3d.ZAxis, 600))
                Check(Read(circle).GeometryPaths.Single().Closed, "Full circle not closed.");
            using (var ellipse = new Ellipse(Point3d.Origin, Vector3d.ZAxis, new Vector3d(1000, 0, 0), .5, 0, Math.PI * 2))
                Check(Read(ellipse).UnsupportedGeometry == 1, "Unsupported curve silently accepted.");
            using (var database = new Database(true, true))
            {
                ObjectId blockId;
                using (var tx = database.TransactionManager.StartTransaction())
                {
                    var table = (BlockTable)tx.GetObject(database.BlockTableId, OpenMode.ForWrite);
                    var definition = new BlockTableRecord { Name = "DiagnosticFixture" };
                    blockId = table.Add(definition); tx.AddNewlyCreatedDBObject(definition, true);
                    var line = new Line(Point3d.Origin, new Point3d(1000, 0, 0));
                    definition.AppendEntity(line); tx.AddNewlyCreatedDBObject(line, true); tx.Commit();
                }
                using (var block = new BlockReference(new Point3d(10000, 20000, 3000), blockId))
                {
                    block.ScaleFactors = new Scale3d(-2, 2, 2); block.Rotation = Math.PI / 2;
                    var p = Read(block).GeometryPaths.Single();
                    Check(Math.Abs(p.PreviewPoints[1].X - 10000) < .001 && Math.Abs(p.PreviewPoints[1].Y - 18000) < .001
                        && p.PreviewPoints.All(q => q.Z == 3000) && p.SourcePath.Contains("/")
                        && p.Vertices[1].X == 1000 && p.SourceTransform.Count == 16, "Block transform/path lost.");
                }
                ObjectId nestedId; var childTransform = Matrix3d.Identity;
                using (var tx = database.TransactionManager.StartTransaction())
                {
                    var table = (BlockTable)tx.GetObject(database.BlockTableId, OpenMode.ForWrite);
                    var nested = new BlockTableRecord { Name = "NestedDiagnosticFixture" };
                    nestedId = table.Add(nested); tx.AddNewlyCreatedDBObject(nested, true);
                    var child = new BlockReference(new Point3d(100, 200, 300), blockId) { Rotation = Math.PI / 6 };
                    childTransform = child.BlockTransform;
                    nested.AppendEntity(child); tx.AddNewlyCreatedDBObject(child, true); tx.Commit();
                }
                using (var block = new BlockReference(new Point3d(10000, 20000, 3000), nestedId))
                {
                    block.ScaleFactors = new Scale3d(2, -2, 1); block.Rotation = Math.PI / 2;
                    var p = Read(block).GeometryPaths.Single();
                    var expected = new Point3d(1000, 0, 0).TransformBy(childTransform).TransformBy(block.BlockTransform);
                    var actual = p.PreviewPoints[1];
                    Check(new Point3d(actual.X, actual.Y, actual.Z).DistanceTo(expected) < .00001
                        && p.SourcePath.Split('/').Length == 3, "Nested block transform order/path lost.");
                }
                using (var tx = database.TransactionManager.StartTransaction())
                {
                    var block = (BlockTableRecord)tx.GetObject(blockId, OpenMode.ForWrite);
                    for (var i = 0; i < 2100; i++)
                    {
                        var line = new Line(new Point3d(i, 0, 0), new Point3d(i, 100, 0));
                        block.AppendEntity(line); tx.AddNewlyCreatedDBObject(line, true);
                    }
                    tx.Commit();
                }
                using (var block = new BlockReference(Point3d.Origin, blockId))
                {
                    var r = Read(block);
                    Check(r.GeometryTruncated && r.GeometryPaths.Count == CadBuildingDiagnosticGeometry.MaximumPaths,
                        "Geometry budget not reported as partial.");
                }
            }
            Check(Equals(dbmod, Application.GetSystemVariable("DBMOD")), "Active drawing was modified.");
            document.Editor.WriteMessage("\nCAD_DIAGNOSTIC_GEOMETRY_NATIVE_OK polyline bulge elevation tiltedArc circle unsupported nestedMirrorRotation budget unchangedDrawing");
        }
        catch (System.Exception ex) { document.Editor.WriteMessage("\nCAD_DIAGNOSTIC_GEOMETRY_NATIVE_FAILED " + ex); }
    }
    private static CadBuildingProbeEntity Read(Entity source)
    {
        var r = new CadBuildingProbeEntity { Handle = "fixture" };
        CadBuildingDiagnosticGeometry.Read(source, r); return r;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
}
