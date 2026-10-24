using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Features.BuildingModel.Services;

// Separately compiled diagnostic assembly. Only a private copy is opened, and
// neither the source database nor the active drawing is ever saved or edited.
public sealed class CadBuildingObjectInventory
{
    [CommandMethod("WLOUTDOORINVENTORY")]
    public void Run()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var dbmod = Application.GetSystemVariable("DBMOD");
        try
        {
            var input = Environment.GetEnvironmentVariable("WANLUO_INVENTORY_INPUT");
            var output = Environment.GetEnvironmentVariable("WANLUO_INVENTORY_OUTPUT");
            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("Explicit diagnostic input and output required.");
            var rows = new List<CadBuildingProbeEntity>(); var counts = new Dictionary<string, int>();
            var seen = new HashSet<ObjectId>(); var sampled = new Dictionary<string, int>();
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(input, FileOpenMode.OpenForReadAndAllShare, true, ""); db.CloseInput(true);
                using (var tx = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var table = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId blockId in table)
                    {
                        var block = (BlockTableRecord)tx.GetObject(blockId, OpenMode.ForRead);
                        if (block.IsFromExternalReference || block.IsFromOverlayReference) continue;
                        foreach (ObjectId id in block)
                        {
                            if (!seen.Add(id)) continue;
                            var entity = tx.GetObject(id, OpenMode.ForRead) as Entity;
                            if (entity == null) continue;
                            var proxy = entity as ProxyEntity;
                            var dxf = proxy == null ? entity.GetRXClass().DxfName : proxy.OriginalDxfName;
                            var rx = proxy == null ? entity.GetRXClass().Name : proxy.OriginalClassName;
                            var key = (proxy == null ? "native " : "proxy ") + dxf + " / " + rx;
                            counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
                            if (!(dxf ?? "").StartsWith("TCH_", StringComparison.OrdinalIgnoreCase)) continue;
                            var r = new CadBuildingProbeEntity { Handle = id.Handle.ToString(), DxfName = dxf, RxClassName = rx,
                                ManagedType = entity.GetType().FullName, Layer = entity.Layer, Category = proxy == null ? "待核验" : "仅代理对象",
                                Note = "只读几何清单，未调用 COM 属性；块定义样本，不代表实际实例数量。" };
                            try
                            {
                                var bounds = entity.GeometricExtents;
                                r.BoundsMin = new CadProbePoint { X = bounds.MinPoint.X, Y = bounds.MinPoint.Y, Z = bounds.MinPoint.Z };
                                r.BoundsMax = new CadProbePoint { X = bounds.MaxPoint.X, Y = bounds.MaxPoint.Y, Z = bounds.MaxPoint.Z };
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception) { r.Note += "包围范围不可读。"; }
                            var count = sampled.ContainsKey(key) ? sampled[key] : 0;
                            if (count < 3 && rows.Count < 500)
                            {
                                if (proxy == null) CadBuildingDiagnosticGeometry.Read(entity, r);
                                sampled[key] = count + 1; rows.Add(r);
                            }
                        }
                    }
                }
                using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                    new DataContractJsonSerializer(typeof(CadBuildingProbeDocument)).WriteObject(file,
                        new CadBuildingProbeDocument { DrawingPath = input, DrawingFingerprint = db.FingerprintGuid.ToString(),
                            CadVersion = Convert.ToString(Application.GetSystemVariable("ACADVER")), DrawingUnits = db.Insunits.ToString(),
                            CapturedAt = DateTimeOffset.Now.ToString("O"), Entities = rows });
            }
            File.WriteAllLines(output + ".classes.txt", counts.OrderBy(p => p.Key).Select(p => p.Key + " = " + p.Value));
            if (!Equals(dbmod, Application.GetSystemVariable("DBMOD"))) throw new InvalidDataException("Active drawing changed.");
            document.Editor.WriteMessage("\nCAD_BUILDING_INVENTORY_OK samples=" + rows.Count + " definitions=" + seen.Count + " unchangedDrawing");
        }
        catch (System.Exception ex) { document.Editor.WriteMessage("\nCAD_BUILDING_INVENTORY_FAILED " + ex); }
    }
}
