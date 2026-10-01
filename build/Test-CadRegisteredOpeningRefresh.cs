using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;

// Separately loaded QA assembly. Reads a copied source DWG; all output is in artifacts.
public static class CadRegisteredOpeningRefreshNativeTest
{
    [CommandMethod("WL_OPENING_REFRESH_NATIVE_QA6")]
    public static void Run()
    {
        var folder=@"H:\CAD建筑插件\.artifacts\cad-floor-workflow-20261001\native-refresh";
        var output=new List<string>();
        try {
            var doc=Application.DocumentManager.MdiActiveDocument;
            var path=Path.Combine(folder,"model.json");var registry=CadFloorPlanRegistry.Load(path);
            var service=typeof(CadFloorPlanRegistry).Assembly.GetType("BatchPdfPublisher.Features.BuildingModel.Services.TianzhengBuildingProbeService",true);
            var allWalls=new List<CadBuildingProbeEntity>();var nearby=new List<CadBuildingProbeEntity>();var classes=new Dictionary<string,int>();
            var nativeOpenings=registry.Floors.SelectMany(f=>f.Probe.Entities).Where(e=>e.DxfName=="TCH_OPENING" && e.BoundsMin!=null).ToList();
            using(var tx=doc.Database.TransactionManager.StartOpenCloseTransaction()) {
                var space=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database),OpenMode.ForRead);
                foreach(ObjectId id in space) {
                    var cls=id.ObjectClass.DxfName;classes[cls]=classes.ContainsKey(cls) ? classes[cls]+1 : 1;
                    var entity=tx.GetObject(id,OpenMode.ForRead,false) as Entity;
                    if(entity==null)continue;
                    if(entity.GetRXClass().DxfName!="TCH_WALL") {
                        if(cls!="TCH_CURTAIN_WALL" && cls!="LINE" && cls!="LWPOLYLINE")continue;
                        Extents3d bounds;try { bounds=entity.GeometricExtents; } catch { continue; }
                        if(!nativeOpenings.Any(o=>bounds.MaxPoint.X>=o.BoundsMin.X-500 && bounds.MinPoint.X<=o.BoundsMax.X+500
                            && bounds.MaxPoint.Y>=o.BoundsMin.Y-500 && bounds.MinPoint.Y<=o.BoundsMax.Y+500))continue;
                        var near=new CadBuildingProbeEntity { Handle=id.Handle.ToString(),DxfName=cls,Layer=entity.Layer,
                            BoundsMin=new CadProbePoint { X=bounds.MinPoint.X,Y=bounds.MinPoint.Y,Z=bounds.MinPoint.Z },BoundsMax=new CadProbePoint { X=bounds.MaxPoint.X,Y=bounds.MaxPoint.Y,Z=bounds.MaxPoint.Z } };
                        var curve=entity as Curve;if(curve!=null)try {
                            near.CurveStart=new CadProbePoint { X=curve.StartPoint.X,Y=curve.StartPoint.Y,Z=curve.StartPoint.Z };
                            near.CurveEnd=new CadProbePoint { X=curve.EndPoint.X,Y=curve.EndPoint.Y,Z=curve.EndPoint.Z };
                            near.CurveLength=Math.Abs(curve.GetDistanceAtParameter(curve.EndParam)-curve.GetDistanceAtParameter(curve.StartParam));
                            near.StraightHorizontalLineVerified=Math.Abs(near.CurveLength.Value-curve.StartPoint.DistanceTo(curve.EndPoint))<.000001;
                        } catch { }
                        if(cls=="TCH_CURTAIN_WALL")service.GetMethod("ReadOpeningLabel",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] { entity,near });
                        nearby.Add(near);continue;
                    }
                    var record=new CadBuildingProbeEntity { Handle=id.Handle.ToString() };
                    service.GetMethod("ReadEntity",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] { entity,record,true });allWalls.Add(record);
                }
            }
            output.Add("MODELSPACE_CLASSES "+string.Join(";",classes.Select(k=>k.Key+"="+k.Value)));
            using(var stream=File.Create(Path.Combine(folder,"all-walls.json")))new DataContractJsonSerializer(typeof(List<CadBuildingProbeEntity>)).WriteObject(stream,allWalls);
            using(var stream=File.Create(Path.Combine(folder,"nearby-objects.json")))new DataContractJsonSerializer(typeof(List<CadBuildingProbeEntity>)).WriteObject(stream,nearby);
            service.GetMethod("ReadRegisteredPlan",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] { doc,registry,false });
            foreach(var capture in registry.Floors) {
                output.Add(capture.Floor.Storey.Name+" openings="+capture.Openings.Count+" missing="+capture.Openings.Count(o=>o.Include && o.Placement==null)+" walls="+capture.Floor.WallCandidates.Count);
                foreach(var item in capture.Openings.Where(o=>o.Include && o.Placement==null)) {
                    var source=capture.Probe.Entities.Single(e=>e.Handle==item.SourceHandle);
                    output.Add("MISSING "+item.SourceHandle+" "+item.ModelCode+" layer="+source.Layer+" segments="+source.DisplaySegments.Count);
                }
            }
            using(var stream=File.Create(Path.Combine(folder,"refreshed-registry.json")))new DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).WriteObject(stream,registry);
            var model=CadFloorModelGeneration.Build(BuildingModelJson.LoadModel(path),registry,"native-refresh-qa");
            BuildingModelJson.SaveModel(Path.Combine(folder,"generated-model.json"),model);
            output.Add("NATIVE_GENERATION_OK walls="+model.Walls.Count+" openings="+model.Openings.Count);
        } catch(System.Exception ex) { output.Add("ERROR "+ex); }
        File.WriteAllLines(Path.Combine(folder,"result.txt"),output);
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nNative registration refresh QA finished.\n");
    }
}
