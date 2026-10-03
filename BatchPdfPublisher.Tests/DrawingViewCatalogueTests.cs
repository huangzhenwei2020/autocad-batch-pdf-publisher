using System;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;

internal static class DrawingViewCatalogueTests
{
    private static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    public static void Run()
    {
        var model=SampleModelFactory.CreateTwoStoreyHouse();
        var rangeModel=new BuildingModelDocument();
        rangeModel.Storeys=StoreyElevationLayout.Resolve(StandardStoreyLayout.ExpandRanges(new[] {
            new StoreyModel { Id="1F",Name="一层",Height=3000 },
            new StoreyModel { Id="2F",Name="2～14层",Height=3000 },
            new StoreyModel { Id="RF",Name="屋顶层",Height=3000,Kind=StoreyKind.Roof } }),"1F",0);
        var plans=DrawingViewCatalogue.Resolve(rangeModel).Where(v=>v.Kind==ViewKind.Plan).ToList();
        Check(plans.Count==3 && plans.Single(v=>v.Id=="plan-2F").Title=="2～14层 平面图",
            "A standard floor group must produce one named plan, while keeping actual storeys for quantities and elevations.");
        var rangeOutput=BuildingModelViewPublisher.Generate(rangeModel);
        Check(rangeOutput.Count(v=>v.Kind==ViewKind.Plan)==3 && rangeOutput.Count(v=>v.Kind==ViewKind.Sheet && v.Id.StartsWith("sheet-plan-"))==3,
            "Publishing must not re-expand standard-floor plans or create duplicate plan sheets.");
        var original=BuildingModelJson.ToJson(model);
        var catalogue=DrawingViewCatalogue.Resolve(model);
        Check(catalogue.Count(v=>v.Kind==ViewKind.Plan)==model.Storeys.Count,"Every storey must have a default plan.");
        Check(catalogue.Count(v=>v.Kind==ViewKind.Elevation)==4,"Default catalogue must contain all four elevations.");
        foreach(var kind in new[] { ViewKind.Section,ViewKind.Axonometric,ViewKind.Schedule,ViewKind.OpeningElevation })
            Check(catalogue.Any(v=>v.Kind==kind),"Missing default drawing kind: "+kind);
        var generated=BuildingModelViewPublisher.Generate(model);
        Check(catalogue.All(v=>generated.Any(g=>g.Id==v.Id && g.Kind==v.Kind)),"CAD generation must use the same drawing catalogue.");
        Check(generated.Any(v=>v.Kind==ViewKind.Sheet && v.Id=="sheet-opening-elevations"),"Opening elevations must be included in CAD output sheets.");
        Check(BuildingModelJson.ToJson(model)==original,"Drawing previews must not mutate the model.");

        var opening=model.Openings.First();
        var one=BuildingModelJson.FromJson(original);one.Openings.Clear();one.Openings.Add(opening);
        var definition=catalogue.First(v=>v.Kind==ViewKind.OpeningElevation);
        var detail=OrthographicProjector.Project(one,definition,null);
        var expected=DoorWindowElevationGeometryBuilder.Build(OpeningElevationAdapter.ToScheduleItem(opening,null,opening.Width,opening.Height));
        Check(detail.Lines.Count==expected.Lines.Count && detail.Dimensions.Count==2,"Opening drawings must use the existing elevation algorithm and actual sizes.");
        one.Openings.Add(new OpeningModel { Id="duplicate",HostWallId=opening.HostWallId,Code=opening.Code,Kind=opening.Kind,
            Width=opening.Width,Height=opening.Height,Sill=opening.Sill });
        var merged=OrthographicProjector.Project(one,definition,null);
        Check(merged.Lines.Count==detail.Lines.Count && merged.Texts.Any(t=>t.Text.Contains("2 樘")),"Repeated openings must share one elevation and show their count.");
        one.Openings.Last().Width+=100;
        Check(OrthographicProjector.Project(one,definition,null).Dimensions.Count==4,"Different sizes must never disappear from opening elevations.");

        var session=new BuildingModelEditSession(model);string error;
        var sessionOriginal=BuildingModelJson.ToJson(session.Model);
        var extra=new ViewDefinitionModel { Id="custom-section",Title="横剖面",Kind=ViewKind.Section,CutAxis=SectionAxis.CutY,CutPosition=2400,ViewSign=-1,Scale=75 };
        catalogue.Add(extra);
        Check(session.TryReplaceDrawingViews(catalogue,out error),error);
        extra.Title="external mutation";
        Check(session.Model.DrawingViews.Last().Title=="横剖面","Editing inputs must not retain mutable references in the model.");
        var roundTrip=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        Check(roundTrip.DrawingViews.Last().CutPosition==2400 && roundTrip.DrawingViews.Last().Scale==75,"Saved drawing definitions must retain their settings.");
        Check(BuildingModelViewPublisher.Generate(roundTrip).Any(v=>v.Id=="custom-section"),"Added drawings must participate in CAD generation.");
        Check(session.Undo() && BuildingModelJson.ToJson(session.Model)==sessionOriginal && session.Redo(),"Drawing catalogue edits must undo and redo atomically.");
        var standard=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        standard.Storeys[1].TemplateStoreyId=standard.Storeys[0].Id;
        var sourceWalls=standard.Walls.Where(w=>w.StoreyId==standard.Storeys[0].Id).ToList();
        standard.Walls=sourceWalls;standard.Openings=standard.Openings.Where(o=>sourceWalls.Any(w=>w.Id==o.HostWallId)).ToList();
        Check(BuildingModelViewPublisher.Generate(standard).Any(v=>v.Id=="custom-section"),"Standard-floor materialization must preserve custom drawing definitions.");
        var revision=session.Revision;var invalid=DrawingViewCatalogue.Resolve(session.Model);invalid[0].Id="../unsafe";
        Check(!session.TryReplaceDrawingViews(invalid,out error) && session.Revision==revision,"Unsafe drawing IDs must not be saved as output paths.");
        Check(session.TryReplaceDrawingViews(Array.Empty<ViewDefinitionModel>(),out error)
            && DrawingViewCatalogue.Resolve(session.Model).Count==0 && BuildingModelViewPublisher.Generate(session.Model).Count==0,
            "An intentionally empty catalogue must remain empty after reopening.");
        var folder=Path.GetFullPath(".artifacts/drawing-views/tests");Directory.CreateDirectory(folder);
        BuildingModelJson.SaveModel(Path.Combine(folder,"model.json"),roundTrip);
        foreach(var view in generated)BuildingModelJson.SaveView(Path.Combine(folder,view.Id+".json"),view);
        var publishFolder=Path.Combine(folder,"publish-"+Guid.NewGuid().ToString("N"),StudioLaunch.ModelFolderName,"模型");
        Directory.CreateDirectory(publishFolder);
        var publishModel=Path.Combine(publishFolder,"model.json");
        BuildingModelJson.SaveModel(publishModel,roundTrip);
        BuildingModelViewPublisher.Publish(publishModel,roundTrip);
        var cache=Path.Combine(publishFolder,StudioLaunch.ViewsFolderName);
        Check(File.Exists(Path.Combine(cache,"custom-section.json")),"Added drawings must be written to the CAD cache.");
        var unrelated=OrthographicProjector.Project(model,definition,null);unrelated.Id="unrelated";
        BuildingModelJson.SaveView(Path.Combine(cache,"unrelated.json"),unrelated);
        roundTrip.DrawingViews.RemoveAll(v=>v.Id=="custom-section");
        var published=BuildingModelViewPublisher.PublishToCad(publishModel,roundTrip);
        Check(!File.Exists(Path.Combine(cache,"custom-section.json")) && !File.Exists(Path.Combine(cache,"sheet-custom-section.json")),
            "Deleted drawings and their sheets must be removed from the generated cache.");
        Check(File.Exists(Path.Combine(cache,"unrelated.json")) && published.ViewCount==BuildingModelViewPublisher.Generate(roundTrip).Count,
            "Publishing must preserve independent view files and count only current drawings.");
        roundTrip.DrawingViews.Clear();
        var empty=BuildingModelViewPublisher.PublishToCad(publishModel,roundTrip);
        Check(empty.ViewCount==0 && empty.PendingCount==0,"Old cached views must not enter the pending CAD list for an empty catalogue.");
        Console.WriteLine("PASS: drawing defaults, shared opening elevations, count/size grouping, custom CAD output, persistence, undo/redo and invalid inputs.");
    }
}
