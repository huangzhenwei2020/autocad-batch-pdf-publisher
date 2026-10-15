using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;

internal static class OpeningElevationStyleTests
{
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    public static void Run()
    {
        var model=SampleModelFactory.CreateTwoStoreyHouse();var library=SampleModelFactory.CreateDemoOpeningLibrary();
        var view=OrthographicProjector.Project(model,new ViewDefinitionModel {Id="style",Kind=ViewKind.Elevation,Direction=ElevationDirection.South,Scale=50},library);
        var details=OrthographicProjector.ProjectOpeningElevations(model,new ViewDefinitionModel {Id="details",Kind=ViewKind.OpeningElevation,Scale=50},library);
        foreach(var drawing in new[]{view,details})
        {
            var typed=drawing.Lines.Where(l=>l.OpeningRole!=null).ToList();Check(typed.Count>0,"Elevation roles lost.");
            Check(typed.Any(l=>l.OpeningRole=="Opening")&&typed.Where(l=>l.OpeningRole=="Opening"||l.OpeningRole=="Hole").All(l=>l.LineType=="DASHED"),"Opening marks must remain dashed.");
            Check(typed.Where(l=>l.OpeningRole=="Frame"||l.OpeningRole=="Mullion"||l.OpeningRole=="SashFrame").All(l=>l.LineType==null),"Physical frames became dashed.");
            Check(typed.Any(l=>OpeningElevationAdapter.LayerKey(l)==OpeningElevationAdapter.WindowLayerKey)
                &&typed.Any(l=>OpeningElevationAdapter.LayerKey(l)==OpeningElevationAdapter.DoorLayerKey),"Door/window appearance was collapsed.");
            var file=System.IO.Path.GetFullPath(".artifacts/elevation-style-"+Guid.NewGuid().ToString("N")+".json");
            BuildingModelJson.SaveView(file,drawing);var reloaded=BuildingModelJson.LoadView(file);
            Check(reloaded.Lines.Count(l=>l.OpeningRole=="Opening"&&l.LineType=="DASHED")==typed.Count(l=>l.OpeningRole=="Opening"),"View JSON lost opening role/style.");
        }
        var sheet=SheetComposer.ComposeModelSpace(new[]{view},new SheetDefinitionModel {Id="sheet",ViewIds=new List<string>{view.Id}});
        Check(sheet.Lines.Count(l=>l.OpeningRole=="Opening")==view.Lines.Count(l=>l.OpeningRole=="Opening")
            &&sheet.Lines.Where(l=>l.OpeningRole=="Opening").All(l=>l.LineType=="DASHED"),"Sheet placement lost elevation roles.");
        ViewLine Line(double a,double b,double y=0,string role=null,string type=null)=>new ViewLine {Layer=ViewLayers.Opening,OpeningRole=role,LineType=type,X1=a,Y1=y,X2=b,Y2=y};
        var clean=OpeningElevationAdapter.CleanLines(new[]{Line(0,10),Line(10,0),Line(4,14),Line(0,10,2),Line(0,10,0,null,"DASHED"),
            new ViewLine {Layer=ViewLayers.Opening,X1=5,Y1=-5,X2=5,Y2=5}});
        Check(clean.Count==5&&Math.Abs(clean.Where(l=>l.Y1==0&&l.Y2==0&&l.LineType==null).Sum(l=>Math.Abs(l.X2-l.X1))-14)<1e-6,
            "Reverse duplicates/partial overlaps were not removed or distinct lines were lost.");
        clean=OpeningElevationAdapter.CleanLines(new[]{Line(0,10,0,"Frame"),Line(10,20,0,"Frame"),Line(20,0,0,"Hole","DASHED"),Line(0,20,0,"Opening","DASHED")});
        Check(clean.Count==2&&clean.Any(l=>l.OpeningRole=="Frame"&&Math.Abs(l.X2-l.X1-20)<1e-6)
            &&clean.Any(l=>l.OpeningRole=="Opening"),"Frame merge deletes opening indication or keeps coincident hole.");
        var diagonal=new[]{new ViewLine {Layer=ViewLayers.Elevation,X2=10,Y2=10},new ViewLine {Layer=ViewLayers.Elevation,X1=15,Y1=15,X2=5,Y2=5}};
        clean=OpeningElevationAdapter.CleanLines(diagonal);
        Check(Math.Abs(clean.Sum(l=>Math.Sqrt(Math.Pow(l.X2-l.X1,2)+Math.Pow(l.Y2-l.Y1,2)))-15*Math.Sqrt(2))<1e-6,"Diagonal overlap trimming inaccurate.");
        var transom=new DoorWindowScheduleItem {ElevationType="普通门",CustomCellLayout="0,0,900,2100,左平开,1,0,玻璃|0,2100,900,2400,固定,0,0,玻璃"};
        Check(OpeningElevationAdapter.UsesWindowAppearance(transom)&&!OpeningElevationAdapter.UsesWindowAppearance(new DoorWindowScheduleItem {ElevationType="普通门"}),"Door transom classification differs from CAD elevation.");
        var boundary=OrthographicProjector.Project(model,new ViewDefinitionModel {Id="boundary",Kind=ViewKind.Elevation,Direction=ElevationDirection.South,Scale=100},library);
        Check(boundary.Lines.Where(l=>l.OpeningRole=="Frame"&&l.StrokeAreaId!=null).Any(),"Opening outer-frame inward clip lost.");
        Console.WriteLine("PASS elevation presentation door/window layers role dash physicalSolid transom JSON sheets reverse partial diagonal frameMerge distinctParallel symbolPreserved inwardClip");
    }
}
