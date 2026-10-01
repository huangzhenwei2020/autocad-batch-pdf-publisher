using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

internal static class CadOpeningScheduleRulesTests
{
    private static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    public static void Run()
    {
        var items=new[] {
            new CadFloorOpeningItem { Code="C01",Width=1200,Height=1500 },
            new CadFloorOpeningItem { Code="c01",Width=900,Height=1500 },
            new CadFloorOpeningItem { Code="C01",Width=900,Height=1500,Sill=0 },
            new CadFloorOpeningItem { Code="C01A",Width=700,Height=1500 },
            new CadFloorOpeningItem { Kind="门",Width=1500,Height=2200 },
            new CadFloorOpeningItem { Kind="门",Width=1499,Height=2200 } };
        var codes=CadFloorPlanRegistry.ResolveCodes(items,i=>i.ModelCode,i=>i.Width.Value,i=>i.Height.Value);
        Check(codes[items[0]]=="C01C" && codes[items[1]]=="C01B" && codes[items[2]]=="C01B","Different sizes need suffixes, identical sizes share codes; explicit suffixes cannot collide.");
        Check(codes[items[4]]=="M1522B" && codes[items[5]]=="M1522A","Automatically sized labels must retain exact size variants.");
        var reversed=CadFloorPlanRegistry.ResolveCodes(items.Reverse(),i=>i.ModelCode,i=>i.Width.Value,i=>i.Height.Value);
        Check(items.All(i=>codes[i]==reversed[i]),"Naming must be stable across floor ordering.");
        Check(CadOpeningDefaults.Code(new CadFloorOpeningItem { Kind="门",Width=1500,Height=2200 })=="M1522","Requested compact door code.");
        Check(CadOpeningDefaults.AlphabeticSuffix(26)=="AA","More than 26 variants need alphabetic continuation.");

        var folder=Path.GetFullPath(".artifacts/cad-floor-workflow-20261001/table-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"model.json");
        var model=new BuildingModelDocument { Storeys=new List<StoreyModel> { new StoreyModel { Id="1F",Name="一层",Height=3000 } } };
        BuildingModelJson.SaveModel(path,model);
        var floor=CadFloorRegistrationContext.Create(path,model,"1F",new PointModel(),1);floor.SetSourceDatum(new PointModel(),new PointModel(10,0));
        floor.RegionMin=new PointModel();floor.RegionMax=new PointModel(4000,4000);floor.RegionPolygon=new List<PointModel> { new PointModel(),new PointModel(4000,0),new PointModel(4000,4000),new PointModel(0,4000) };
        var capture=new CadFloorPlanCapture { Floor=floor,Probe=new CadBuildingProbeDocument(),Openings=new List<CadFloorOpeningItem> {
            new CadFloorOpeningItem { SourceHandle="A1",Code="C01",Width=900,Height=1500 },
            new CadFloorOpeningItem { SourceHandle="A2",Code="C01",Width=1200,Height=1500 } } };
        var registry=new CadFloorPlanRegistry { ModelPath=path };registry.SaveFloor(capture);
        var before=File.ReadAllText(CadFloorPlanRegistry.FilePath(path));
        var rows=registry.OpeningRows(model);
        Check(rows.Select(r=>r.Code).OrderBy(c=>c).SequenceEqual(new[] { "C01A","C01B" }),"Table and schedule must share size suffixes.");
        rows[0].Code="M01";rows[0].Width=1500;rows[0].Height=2200;
        var stamp=File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(path)).Ticks;
        var draft=registry.Clone();draft.SaveOpeningRows(rows,stamp,false);
        Check(File.ReadAllText(CadFloorPlanRegistry.FilePath(path))==before && registry.Floors[0].Openings[0].Width==900,"Staged table edits and failed reads must not modify saved data.");
        registry.ReplaceFrom(draft,stamp);
        Check(CadFloorPlanRegistry.Load(path).Floors[0].Openings[0].Width==1500,"Saving edits writes native-instance dimensions atomically.");
        rows=registry.OpeningRows(model);rows[0].Height=double.NaN;
        before=File.ReadAllText(CadFloorPlanRegistry.FilePath(path));stamp=File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(path)).Ticks;
        try { registry.SaveOpeningRows(rows,stamp);throw new Exception("Invalid size accepted."); } catch(InvalidDataException) { }
        Check(File.ReadAllText(CadFloorPlanRegistry.FilePath(path))==before,"Invalid edits must preserve saved tables.");
        var window=new CadRegisteredOpeningTableWindow(registry,model);
        var root=(FrameworkElement)window.Content;root.Measure(new Size(980,580));root.Arrange(new Rect(new Size(980,580)));root.UpdateLayout();
        var grid=((Grid)root).Children.OfType<DataGrid>().Single();
        Check(grid.Columns.All(c=>!new[] { "SourceHandle","Handle","CodeSource" }.Contains((c as DataGridBoundColumn)?.Binding is System.Windows.Data.Binding b ? b.Path.Path : "")),"Internal object identifiers must be hidden.");
        window.Close();
        Console.WriteLine("PASS: shared compact codes, stable size suffixes, collision protection, transactional door/window table edits.");
    }
}
