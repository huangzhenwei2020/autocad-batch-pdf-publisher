using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

internal static class DrawingLineworkTests
{
    public static void Run()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        var model=SampleModelFactory.CreateTwoStoreyHouse();
        var definition=SampleModelFactory.CreatePlanView(model.Storeys[0]);
        var original=OrthographicProjector.Project(model,definition);
        Check(original.Lines.All(l=>l.LineWeight==null),"Legacy views must remain ByLayer");
        Check(original.StrokeAreas.Count>0&&original.Lines.Where(l=>l.Layer==ViewLayers.Cut).All(l=>l.StrokeAreaId!=null),
            "Physical wall boundaries need inward ink regions");
        Check(original.Lines.Where(l=>l.Layer==ViewLayers.Axis||l.Layer==ViewLayers.Dimension).All(l=>l.StrokeAreaId==null),
            "Reference lines must not acquire physical interiors");
        foreach(var composed in new[] {SheetComposer.Compose(new[]{original},new SheetDefinitionModel {ViewIds=new List<string>{original.Id}}),
            SheetComposer.ComposeModelSpace(new[]{original},new SheetDefinitionModel {ViewIds=new List<string>{original.Id}})}) {
            Check(composed.Lines.Any(l=>l.StrokeAreaId!=null),"Sheet lost inward outlines");
            Check(composed.Lines.Where(l=>l.StrokeAreaId!=null).All(l=>composed.StrokeAreas.Any(a=>a.Id==l.StrokeAreaId)),
                "Sheet inward outline references are invalid");
            var arcs=composed.Lines.Where(l=>l.OpeningArcId!=null).ToArray();
            Check(arcs.Length>0&&arcs.All(l=>l.LineType=="DASHED")&&arcs.Where(l=>l.OpeningArcId.Contains("swing-")).GroupBy(l=>l.OpeningArcId).All(g=>g.Count()==OpeningPlanGeometry.SwingArcSegments),
                "Sheet lost continuous opening arcs or mixed arc identities");
        }
        definition.LineWeights=ViewLayers.All.ToDictionary(s=>s.Name,s=>70);
        var weighted=OrthographicProjector.Project(model,definition);
        Check(weighted.Lines.All(l=>l.LineWeight==70)&&weighted.Circles.All(c=>c.LineWeight==70)
            &&weighted.Dimensions.All(d=>d.LineWeight==70)&&weighted.Texts.All(t=>t.LineWeight==70),"Weights missing from primitives");
        Check(original.Lines.Zip(weighted.Lines,(a,b)=>a.X1==b.X1&&a.Y1==b.Y1&&a.X2==b.X2&&a.Y2==b.Y2).All(b=>b),
            "Changing lineweight changed real-size geometry");
        var session=new BuildingModelEditSession(model);var before=BuildingModelJson.ToJson(session.Model);
        Check(session.TryReplaceDrawingViews(new[]{definition},out _),"Valid CAD weights rejected");
        Check(BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model)).DrawingViews[0].LineWeights[ViewLayers.Cut]==70,"Weights not saved");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==before,"Weight undo is not atomic");
        foreach(var invalid in new[]{-1,14,212}) {
            definition.LineWeights[ViewLayers.Cut]=invalid;
            Check(!session.TryReplaceDrawingViews(new[]{definition},out _)&&BuildingModelJson.ToJson(session.Model)==before,"Non-CAD weight accepted");
        }
        definition.LineWeights[ViewLayers.Cut]=0;
        Check(session.TryReplaceDrawingViews(new[]{definition},out _),"CAD hairline rejected");
        var second=new ViewDocument {Id="thin",Scale=50,Lines=new List<ViewLine> {new ViewLine {Layer=ViewLayers.Cut,LineWeight=13,X2=100}}};
        var first=new ViewDocument {Id="thick",Scale=100,Lines=new List<ViewLine> {new ViewLine {Layer=ViewLayers.Cut,LineWeight=70,X2=100}}};
        var sheet=new SheetDefinitionModel {Id="mixed",ViewIds=new List<string> {"thin","thick"}};
        foreach(var composed in new[]{SheetComposer.Compose(new[]{first,second},sheet),SheetComposer.ComposeModelSpace(new[]{first,second},sheet)})
            Check(composed.Lines.Any(l=>l.LineWeight==13)&&composed.Lines.Any(l=>l.LineWeight==70),"Sheet lost per-view weights");

        var slabModel=SampleModelFactory.CreateEmptyModel("Linework fixture");
        slabModel.Walls.Add(new WallModel {Id="W-1",StoreyId="1F",X2=4000,Thickness=200});
        slabModel.Slabs.Add(new SlabModel {Id="slab",StoreyId="1F",Outline=new List<PointModel> {
            new PointModel(-1000,-100),new PointModel(6000,-100),new PointModel(6000,2000),new PointModel(-1000,2000)}});
        var slabView=OrthographicProjector.Project(slabModel,SampleModelFactory.CreatePlanView(slabModel.Storeys[0]));
        var slabLines=slabView.Lines.Where(l=>l.Layer==ViewLayers.Slab).ToArray();
        var length=slabLines.Sum(l=>Math.Sqrt(Math.Pow(l.X2-l.X1,2)+Math.Pow(l.Y2-l.Y1,2)));
        Check(Math.Abs(length-14200)<.001&&slabLines.Length==5,"Wall contact removed exposed slab edges or failed partial clipping");
        slabModel.Walls[0].AxisOffset=300;
        slabView=OrthographicProjector.Project(slabModel,SampleModelFactory.CreatePlanView(slabModel.Storeys[0]));
        Check(Math.Abs(slabView.Lines.Where(l=>l.Layer==ViewLayers.Slab).Sum(l=>Math.Sqrt(Math.Pow(l.X2-l.X1,2)+Math.Pow(l.Y2-l.Y1,2)))-18200)<.001,
            "Offset wall hid a non-contact slab edge");

        var corner=SampleModelFactory.CreateEmptyModel("Corner fixture");
        corner.Walls.Add(new WallModel {Id="h",StoreyId="1F",X2=4000,Y2=.005,Thickness=200});
        corner.Walls.Add(new WallModel {Id="v",StoreyId="1F",Y2=3000,Thickness=200});
        var seams=WallJunctionLines.Resolve(corner,corner.Walls,1200);
        Check(seams.Count==1,"Microscopic orthogonal drift lost the miter seam");
        var projected=OrthographicProjector.Project(corner,SampleModelFactory.CreatePlanView(corner.Storeys[0]));
        Check(!projected.Lines.Any(l=>l.Layer==ViewLayers.Elevation&&Math.Abs(l.X2-l.X1)>1&&Math.Abs(l.Y2-l.Y1)>1),"Drawing must omit wall ownership seams");
        var junction=SampleModelFactory.CreateEmptyModel("T junction drawing fixture");
        junction.Walls.Add(new WallModel {Id="up",StoreyId="1F",Y2=2000,Thickness=200});
        junction.Walls.Add(new WallModel {Id="down",StoreyId="1F",Y2=-2000,Thickness=200});
        junction.Walls.Add(new WallModel {Id="right",StoreyId="1F",X2=3000,Thickness=200});
        junction.Openings.Add(new OpeningModel {Id="door",Code="M1022",Kind="门",HostWallId="up",Offset=1000,Width=1000,Height=2200});
        junction.Openings.Add(new OpeningModel {Id="window",Code="C1121",Kind="窗",HostWallId="down",Offset=1000,Width=1100,Height=2100,Sill=0});
        var junctionBefore=BuildingModelJson.ToJson(junction);
        Check(WallJunctionLines.Resolve(junction,junction.Walls,1200).Count==2,"T junction editor seams disappeared");
        var junctionDefinition=SampleModelFactory.CreatePlanView(junction.Storeys[0]);
        var junctionView=OrthographicProjector.Project(junction,junctionDefinition);
        Check(junctionView.Lines.Where(l=>l.Layer==ViewLayers.Cut||l.Layer==ViewLayers.Elevation)
            .All(l=>Math.Abs(l.X2-l.X1)<.01||Math.Abs(l.Y2-l.Y1)<.01),"T junction drawing contains diagonal seams");
        Check(BuildingModelJson.ToJson(junction)==junctionBefore,"Drawing cleanup changed the editing model");
        junction.Walls.Add(new WallModel {Id="oblique",StoreyId="1F",X1=5000,Y1=3000,X2=6500,Y2=4500,Thickness=200});
        Check(OrthographicProjector.Project(junction,junctionDefinition).Lines.Any(l=>l.Layer==ViewLayers.Cut
            &&Math.Abs(l.X2-l.X1)>1&&Math.Abs(l.Y2-l.Y1)>1),"Actual oblique wall contours were hidden");
        var folder=Path.GetFullPath(".artifacts/drawing-linework");Directory.CreateDirectory(folder);
        var inwardFile=Path.Combine(folder,"inward-plan.view.json");BuildingModelJson.SaveView(inwardFile,weighted);
        var reloaded=BuildingModelJson.LoadView(inwardFile);
        Check(reloaded.StrokeAreas.Count==weighted.StrokeAreas.Count&&reloaded.Lines.Zip(weighted.Lines,
            (a,b)=>a.StrokeAreaId==b.StrokeAreaId).All(b=>b),"View JSON lost inward contour metadata");
        Check(reloaded.Lines.Zip(weighted.Lines,(a,b)=>a.OpeningArcId==b.OpeningArcId&&a.LineType==b.LineType).All(b=>b),"View JSON lost dashed opening arcs");
        BuildingModelJson.SaveModel(Path.Combine(folder,"corner.model.json"),corner);
        BuildingModelJson.SaveModel(Path.Combine(folder,"slab.model.json"),slabModel);
        BuildingModelJson.SaveModel(Path.Combine(folder,"junction.model.json"),BuildingModelJson.FromJson(junctionBefore));
        Console.WriteLine("PASS drawing linework CAD weights/hairline invalid rollback persistence undo mixed-view sheets unchanged geometry slab partial-contact clipping exposed edges editorSeamsRetained drawingL-TSeamsOmitted obliqueContoursRetained");
        WallColumnPriority();
    }
    private static void WallColumnPriority()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        var model=SampleModelFactory.CreateEmptyModel("Wall column priority");
        model.Storeys.RemoveAll(s=>s.Id!="1F");
        model.Walls.Add(new WallModel {Id="wall",StoreyId="1F",X1=-2000,X2=2000,Thickness=800});
        model.Columns.Add(new ColumnModel {Id="column",StoreyId="1F",Width=400,Depth=400});
        var original=BuildingModelJson.ToJson(model);
        var without=BuildingModelJson.FromJson(original);without.Columns.Clear();
        foreach(var definition in new[] {SampleModelFactory.CreatePlanView(model.Storeys[0]),
            new ViewDefinitionModel {Id="south",Kind=ViewKind.Elevation,Direction=ElevationDirection.South},
            new ViewDefinitionModel {Id="north",Kind=ViewKind.Elevation,Direction=ElevationDirection.North},
            new ViewDefinitionModel {Id="section-x",Kind=ViewKind.Section,CutAxis=SectionAxis.CutX,CutPosition=0},
            new ViewDefinitionModel {Id="section-y",Kind=ViewKind.Section,CutAxis=SectionAxis.CutY,CutPosition=0},
            new ViewDefinitionModel {Id="axon",Kind=ViewKind.Axonometric}}) {
            var view=OrthographicProjector.Project(model,definition);var reference=OrthographicProjector.Project(without,definition);
            Check(view.Lines.Count==reference.Lines.Count && view.Lines.Zip(reference.Lines,(a,b)=>a.Layer==b.Layer
                && Math.Abs(a.X1-b.X1)<.001 && Math.Abs(a.Y1-b.Y1)<.001 && Math.Abs(a.X2-b.X2)<.001 && Math.Abs(a.Y2-b.Y2)<.001).All(b=>b),
                "Covered column changed drawing outline: "+definition.Id);
            Check(view.Hatches.Count==reference.Hatches.Count,"Covered column duplicated section hatching.");
        }
        Check(BuildingVolumeBuilder.Build(model).Faces.Any(f=>f.Kind=="column") && !BuildingVolumeBuilder.BuildForDrawing(model).Faces.Any(f=>f.Kind=="column"),
            "Drawing cleanup must not delete the semantic/3D column.");
        Check(BuildingModelJson.ToJson(model)==original,"Projection mutated model instances.");
        model.Walls[0].Thickness=200;
        var band=ColumnDrawingGeometry.At(model,model.Columns[0],1200);
        double Area(IEnumerable<List<PointModel>> triangles)=>triangles.Sum(t=>Math.Abs((t[1].X-t[0].X)*(t[2].Y-t[0].Y)-(t[1].Y-t[0].Y)*(t[2].X-t[0].X))/2);
        Check(Math.Abs(Area(band.Triangles)-80000)<.001,"Partial overlap failed its real-size area check.");
        var plan=OrthographicProjector.Project(model,SampleModelFactory.CreatePlanView(model.Storeys[0]));
        var columnLines=plan.Lines.Where(l=>l.StrokeAreaId=="column-column").ToList();
        Check(Math.Abs(columnLines.Sum(l=>Math.Sqrt(Math.Pow(l.X2-l.X1,2)+Math.Pow(l.Y2-l.Y1,2)))-1200)<.001,
            "Column overlap edges remain, or exposed edges disappeared.");
        Check(columnLines.All(l=>Math.Abs((l.Y1+l.Y2)/2+plan.OriginY)>=100-.001),"Column line crosses the wall interior.");
        foreach(var sheet in new[] {SheetComposer.Compose(new[]{plan},new SheetDefinitionModel {ViewIds=new List<string>{plan.Id}}),
            SheetComposer.ComposeModelSpace(new[]{plan},new SheetDefinitionModel {ViewIds=new List<string>{plan.Id}})})
            Check(sheet.StrokeAreas.Any(a=>a.Contours.Count==2),"Sheet lost split column ink regions.");
        model.Columns[0].X=2050;
        band=ColumnDrawingGeometry.At(model,model.Columns[0],1200);
        Check(Math.Abs(Area(band.Triangles)-130000)<.001,"Column beyond wall end was overclipped.");
        var section=OrthographicProjector.Project(model,new ViewDefinitionModel {Id="partial-section",Kind=ViewKind.Section,CutAxis=SectionAxis.CutY,CutPosition=0});
        Check(Math.Abs(section.Hatches.Sum(h=>Math.Abs((h.Boundary.Max(p=>p.X)-h.Boundary.Min(p=>p.X))*(h.Boundary.Max(p=>p.Y)-h.Boundary.Min(p=>p.Y))))
            -4250*model.Storeys[0].Height)<1,"Section has duplicate wall/column area or lost exposed column.");
        var elevation=OrthographicProjector.Project(model,new ViewDefinitionModel {Id="partial-elevation",Kind=ViewKind.Elevation,Direction=ElevationDirection.South});
        Check(elevation.Lines.Any(l=>Math.Abs(l.X1+elevation.OriginX-2250)<.001 && Math.Abs(l.X2+elevation.OriginX-2250)<.001),"Exposed column outside wall end disappeared in elevation.");
        model.Columns[0].X=0;model.Columns[0].RotationDegrees=45;model.Walls[0].Thickness=800;
        Check(ColumnDrawingGeometry.At(model,model.Columns[0],1200).Contours.Count==0,"Rotated covered column survived.");
        model.Columns[0].RotationDegrees=0;model.Walls[0].Height=1000;model.Columns[0].Height=2000;
        var drawing=BuildingVolumeBuilder.BuildForDrawing(model);
        Check(drawing.Faces.Where(f=>f.Kind=="column").All(f=>f.Points.All(p=>p.Z>=1000-.001))
            && drawing.Faces.Any(f=>f.Kind=="column"),"Column above low wall was hidden or low overlap survived.");
        model.Columns[0].X=2050;model.Walls[0].Thickness=200;
        var stepped=OrthographicProjector.Project(model,new ViewDefinitionModel {Id="stepped-section",Kind=ViewKind.Section,CutAxis=SectionAxis.CutY,CutPosition=0});
        Check(!stepped.Lines.Any(l=>l.Layer==ViewLayers.Cut && Math.Abs(l.Y1+stepped.OriginY-1000)<.001
            && Math.Abs(l.Y2+stepped.OriginY-1000)<.001 && Math.Min(l.X1,l.X2)+stepped.OriginX<2100
            && Math.Max(l.X1,l.X2)+stepped.OriginX>2100),"Height-band splitting introduced a horizontal column seam.");
        model.Columns[0].X=0;model.Walls[0].Thickness=800;
        model.Walls[0].Height=0;model.Columns[0].Height=0;
        model.Walls.Add(new WallModel {Id="cross",StoreyId="1F",Y1=-2000,Y2=2000,Thickness=800});
        Check(ColumnDrawingGeometry.At(model,model.Columns[0],1200).Contours.Count==0,"Intersecting walls toggled subtraction back on.");
        model.Walls.RemoveAt(1);
        model.Openings.Add(new OpeningModel {Id="opening",HostWallId="wall",Code="M0620",Kind="门",Offset=2000,Width=600,Height=2000});
        Check(Math.Abs(Area(ColumnDrawingGeometry.At(model,model.Columns[0],1200).Triangles)-160000)<.001,"A wall hole incorrectly masked its column.");
        model.Openings.Clear();model.Walls.Clear();
        model.Walls.Add(new WallModel {Id="arc",StoreyId="1F",X1=2000,Y1=0,X2=0,Y2=2000,Bulge=Math.Tan(Math.PI/8),Thickness=400});
        model.Columns[0].X=model.Columns[0].Y=2000/Math.Sqrt(2);model.Columns[0].Width=model.Columns[0].Depth=50;
        Check(ColumnDrawingGeometry.At(model,model.Columns[0],1200).Contours.Count==0,"Arc wall did not hide a covered column.");
        model.Storeys.Add(new StoreyModel {Id="2F",Name="2F",TemplateStoreyId="1F",Height=3300,Elevation=3300});
        Check(BuildingVolumeBuilder.Build(model).Faces.Count(f=>f.Kind=="column")==12
            && !BuildingVolumeBuilder.BuildForDrawing(model).Faces.Any(f=>f.Kind=="column"),"Standard-storey columns lost source semantics or retained covered drawing faces.");
        model.Walls.Clear();
        Check(BuildingVolumeBuilder.BuildForDrawing(model).Faces.Count(f=>f.Kind=="column")==12,"Freestanding columns disappeared.");
        model.Storeys.RemoveAt(1);model.Columns[0].X=200;model.Columns[0].Y=200;model.Columns[0].Width=model.Columns[0].Depth=400;model.Columns[0].RotationDegrees=45;
        model.Walls.Add(new WallModel {Id="near-but-disjoint",StoreyId="1F",X1=-2000,X2=0,Thickness=200});
        Check(!ColumnDrawingGeometry.At(model,model.Columns[0],1200).HasOverlap
            && BuildingVolumeBuilder.BuildForDrawing(model).Faces.Count(f=>f.Kind=="column")==6,"A bounding-box false positive hid an actually disjoint column.");
        Console.WriteLine("PASS wall-column drawing priority full partial endExposure sectionHatch rotated arc crossedWalls holes heightBands standardFloors sheets semantic3DUnchanged");
    }
}
