using System;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.Services;

public static class BuildingModelCadScaleTest
{
    [CommandMethod("WL_MODEL_BATCH_QA")]
    public static void Batch()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        try
        {
            var bridge = typeof(DraftingStandardService).Assembly.GetType("BatchPdfPublisher.Services.BuildingModelCadBridge");
            var cursorType = bridge.GetNestedType("BatchPlacementCursor", BindingFlags.NonPublic);
            var cursor = Activator.CreateInstance(cursorType, true);
            var method = bridge.GetMethod("PlaceBatchEntry", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var path in new[] { Environment.GetEnvironmentVariable("WANLUO_CAD_SCALE_VIEW"),
                Environment.GetEnvironmentVariable("WANLUO_CAD_BATCH_VIEW") })
                if (!(bool)method.Invoke(null, new object[] { document, path, null, null, null, cursor }))
                    throw new InvalidOperationException("Batch placement failed.");
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead);
                var frames = space.Cast<ObjectId>().Select(id => transaction.GetObject(id, OpenMode.ForRead))
                    .OfType<Line>().Where(l => Math.Abs(l.Length - 42000) < 0.001
                        && l.GetXDataForApplication("WL_BUILDING_VIEW") != null).ToArray();
                var starts = frames.Select(l => Math.Min(l.StartPoint.X, l.EndPoint.X)).Distinct().OrderBy(x => x).ToArray();
                if (starts.Length != 2 || starts[1] - starts[0] <= 42000)
                    throw new InvalidOperationException("Batch sheets missing or overlapping.");
                document.Editor.WriteMessage("\nCAD_MODEL_BATCH_QA_OK sheets=2 onePoint=true separated=true\n");
            }
        }
        catch (System.Exception error)
        { document.Editor.WriteMessage("\nCAD_MODEL_BATCH_QA_FAIL " + error + "\n"); }
    }

    [CommandMethod("WL_MODEL_SCALE_QA")]
    public static void Run()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        try
        {
            var root = Environment.GetEnvironmentVariable("WANLUO_USER_DATA_ROOT");
            if (string.IsNullOrEmpty(root) || !root.Contains("modelspace-qa"))
                throw new InvalidOperationException("Use an isolated modelspace-qa user-data directory.");
            var profile = DraftingStandardService.LoadProfile();
            foreach (var layer in profile.Layers) layer.Name = "QA-" + layer.Key;
            DraftingStandardService.SaveProfile(profile);
            var bridge = typeof(DraftingStandardService).Assembly.GetType("BatchPdfPublisher.Services.BuildingModelCadBridge");
            var method = bridge.GetMethod("PlaceOneView", BindingFlags.NonPublic | BindingFlags.Static);
            var success = (bool)method.Invoke(null, new object[] { document,
                Environment.GetEnvironmentVariable("WANLUO_CAD_SCALE_VIEW"), "qa-sheet", null, null });
            if (!success) throw new InvalidOperationException("Placement failed.");
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead);
                var entities = space.Cast<ObjectId>().Select(id => transaction.GetObject(id, OpenMode.ForRead))
                    .OfType<Entity>().Where(e => e.GetXDataForApplication("WL_BUILDING_VIEW") != null).ToArray();
                var lines = entities.OfType<Line>().ToArray();
                if (!lines.Any(l => l.Layer == "QA-Structure" && Math.Abs(l.Length - 6000) < 0.001))
                    throw new InvalidOperationException("Wall is not 6000 mm or ignored the custom structure layer.");
                if (!lines.Any(l => l.Layer == "QA-Frame" && Math.Abs(l.Length - 42000) < 0.001))
                    throw new InvalidOperationException("A3 frame is not 100x paper width.");
                if (!lines.Any(l => l.Layer == "QA-Fine" && Math.Abs(l.Length - 1000) < 0.001))
                    throw new InvalidOperationException("Opening ignored the custom fine layer.");
                if (!entities.OfType<Dimension>().Any(d => d.Layer == "QA-AnnotationDimensionLayer"
                    && Math.Abs(d.Measurement - 6000) < 0.001 && string.IsNullOrEmpty(d.DimensionText)))
                    throw new InvalidOperationException("Dimension is not a real 6000 mm measurement.");
                if (entities.Any(e => !e.Layer.StartsWith("QA-", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Generated entity bypassed drafting standards.");
                document.Editor.WriteMessage("\nCAD_MODEL_SCALE_QA_OK wall=6000 frame=42000 dimension=6000 customLayers=true\n");
            }
        }
        catch (System.Exception error)
        {
            document.Editor.WriteMessage("\nCAD_MODEL_SCALE_QA_FAIL " + error + "\n");
        }
    }
}
