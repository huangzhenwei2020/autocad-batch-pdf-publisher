using System;
using System.IO;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

// Test-only assembly: load separately, never included in the product release.
public static class CadOpeningPlacementNativeTest
{
    [CommandMethod("WL_PLACEMENT_NATIVE_QA2")]
    public static void Run()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var results = new List<string>();
        using (var tx = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (var handle in new[] { "7C1AB", "6513F", "64B1B" })
            {
                try
                {
                    var id = doc.Database.GetObjectId(false, new Handle(Convert.ToInt64(handle,16)),0);
                    var curve = tx.GetObject(id,OpenMode.ForRead) as Curve;
                    if (curve == null) throw new InvalidOperationException("No curve interface");
                    if (curve.GetRXClass().DxfName != "TCH_WALL") throw new InvalidOperationException("Wrong native type");
                    var length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
                    var chord = curve.StartPoint.DistanceTo(curve.EndPoint);
                    results.Add(handle + " length=" + length.ToString("R") + " chord=" + chord.ToString("R"));
                }
                catch (System.Exception e) { results.Add(handle + " ERROR " + e.GetType().Name + " " + e.Message); }
            }
        }
        var path = @"H:\CAD建筑插件\.artifacts\cad-opening-placement-20261001\native-geometry.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllLines(path,results);
        doc.Editor.WriteMessage("\nNative placement QA complete: " + results.Count + " read-only checks.\n");
    }
}
