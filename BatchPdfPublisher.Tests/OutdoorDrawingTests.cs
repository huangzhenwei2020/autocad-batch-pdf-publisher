using BatchPdfPublisher.BuildingModel;
using BuildingModelStudio.AvaloniaProbe;

static class OutdoorDrawingTests
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static BuildingModelDocument Empty()
    {
        var m=new BuildingModelDocument {Name="室外构件回归"};
        m.Storeys.Add(new(){Id="roof",Name="屋面",Kind=StoreyKind.Roof,Elevation=9000,Height=3000});return m;
    }
    static void Main()
    {
        var model=Empty();var session=new BuildingModelEditSession(model);
        var wall=new WallDrawingChoice(Thickness:200,Height:1100,Purpose:WallPurpose.Parapet,BaseOffset:120);
        var draft=new OutdoorDrawingDraft(false,"roof",wall);
        draft.Add(new(0,0));draft.Add(new(4000,0));draft.Add(new(4000,3000));
        var json=BuildingModelJson.ToJson(session.Model);
        Check(draft.Geometry(model).Walls.Count==2&&session.Revision==0,"Preview changed model");
        Check(session.TryAddWallPath(draft.Geometry(model).Walls,out var id,out var error),error);
        Check(session.Revision==1&&session.Model.Walls.Count==2,"Path not atomic");
        Check(session.Model.Walls.All(w=>session.Model.BaseElevationOf(w)==9120&&w.Purpose==WallPurpose.Parapet),"Bottom/purpose lost");
        var volume=BuildingVolumeBuilder.Build(session.Model);
        Check(Math.Abs(volume.MinZ-9120)<.01&&Math.Abs(volume.MaxZ-10220)<.01,"3D bounds incorrect");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==json,"Path undo failed");
        Check(session.Redo()&&session.Model.Walls.Count==2,"Path redo failed");
        var valid=draft.Geometry(model).Walls;valid.Add(new(){StoreyId="roof",X1=0,Y1=0,X2=1,Y2=1,Height=1100,Purpose=WallPurpose.Parapet});
        var before=BuildingModelJson.ToJson(session.Model);var revision=session.Revision;
        Check(!session.TryAddWallPath(valid,out _,out _)&&revision==session.Revision&&before==BuildingModelJson.ToJson(session.Model),"Partial path committed");
        Check(session.TryTransformWall(id,5000,0,30,true,out var copyId,out error),error);
        var copy=session.Model.Walls.Single(w=>w.Id==copyId);
        Check(copy.BaseOffset==120&&copy.Purpose==WallPurpose.Parapet,"Wall copy lost fields");
        Check(!session.TrySetWallGeometryBySides(copyId,3000,100,100,0,out _),"Parapet followed floor height");
        Check(session.TrySetWallGeometryBySides(copyId,3000,100,100,900,out error,200),error);
        Check(session.Model.Walls.Single(w=>w.Id==copyId).BaseOffset==200,"Base parameter edit failed");
        var restored=BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        Check(restored.Walls.Any(w=>w.BaseOffset==200&&w.Purpose==WallPurpose.Parapet),"JSON lost fields");
        var legacy=BuildingModelJson.FromJson("{\"Storeys\":[{\"Id\":\"old\",\"Elevation\":3000}],\"Walls\":[{\"Id\":\"w\",\"StoreyId\":\"old\"}]}");
        Check(legacy.BaseElevationOf(legacy.Walls[0])==3000&&legacy.Walls[0].Purpose==WallPurpose.Building,"Legacy changed");
        var balcony=new OutdoorDrawingDraft(true,"roof",wall);
        foreach(var p in new[]{new PointModel(0,0),new(3000,0),new(3000,1500),new(0,1500)})Check(balcony.Add(p)==null,"Polygon point failed");
        Check(balcony.Close()==null,"Polygon failed to close");balcony.Previous();Check(!balcony.Closed&&balcony.Points.Count==4,"Close undo removed vertex");balcony.Close();
        var slab=balcony.Geometry(model).Slabs.Single();slab.Id=null!;
        var slabSession=new BuildingModelEditSession(model);
        Check(slabSession.TryUpsertSlab(slab,out var slabId,out error),error);
        var actual=slabSession.Model.Slabs.Single();
        Check(actual.Purpose==SlabPurpose.Balcony&&slabSession.Model.TopElevationOf(actual)==8900,"Balcony datum incorrect");
        Check(slabSession.TryTransformSlab(slabId,3000,0,true,out _,out error),"Touching balcony edges rejected: "+error);
        Check(!slabSession.TryTransformSlab(slabId,100,0,true,out _,out _),"Duplicate balcony allowed");
        Check(slabSession.Model.Slabs.Count==2,"Failed duplicate changed model");
        var saved=BuildingModelJson.FromJson(BuildingModelJson.ToJson(slabSession.Model));
        saved.Storeys[0].Kind=StoreyKind.Normal;
        saved.Storeys.Add(new(){Id="next",Name="屋面来源副本",Elevation=15000,Height=4000,TemplateStoreyId="roof"});
        saved.Walls=restored.Walls;
        var physical=StandardStoreyLayout.Materialize(saved);
        Check(physical.Walls.Where(w=>w.StoreyId=="next").All(w=>w.Purpose==WallPurpose.Parapet&&physical.BaseElevationOf(w)>=15120),"Standard floor wall fields lost");
        Check(physical.Slabs.Where(s=>s.StoreyId=="next").All(s=>s.Purpose==SlabPurpose.Balcony&&physical.TopElevationOf(s)==14900),"Standard floor balcony fields lost");
        var arc=new OutdoorDrawingDraft(false,"roof",wall){Arc=true};arc.Add(new(0,0));arc.Add(new(3000,0));
        Check(arc.ArcEnd!=null&&arc.Points.Count==1,"Arc end committed early");
        Check(arc.Add(new(1500,-1000))==null&&Math.Abs(arc.Points[0].Bulge)>.01,"Three point arc failed");
        Check(BuildingVolumeBuilder.Build(arc.Geometry(model)).Faces.Count>0,"Arc not rendered");
        arc.Previous();Check(arc.Points.Count==1&&arc.Points[0].Bulge==0,"Arc undo failed");
        var a=new SlabModel {Thickness=100,Outline=new(){new(0,0),new(4000,0),new(4000,4000),new(0,4000)},
            Openings=new(){new(){Id="hole",Outline=new(){new(1000,1000),new(2000,1000),new(2000,2000),new(1000,2000)}}}};
        var b=new SlabModel {Outline=new(){new(1200,1200),new(1800,1200),new(1800,1800),new(1200,1800)}};
        Check(!SlabGeometry.InteriorOverlaps(a,b),"Void counted as slab overlap");
        b.Outline[0].X=500;Check(SlabGeometry.InteriorOverlaps(a,b),"Partial overlap not detected");
        var bow=new OutdoorDrawingDraft(true,"roof",wall);
        foreach(var p in new[]{new PointModel(0,0),new(3000,2000),new(0,2000),new(3000,0)})bow.Add(p);
        try{bow.Geometry(model);throw new Exception("Self intersecting outline accepted");}catch(ArgumentException){ }
        ReferenceAndEditChecks();
        Console.WriteLine("OUTDOOR_CORE_OK atomicPath failedRollback legacy baseOffset copy standardFloors JSON arcs slabOverlap voids selfIntersection");
    }

    static void ReferenceAndEditChecks()
    {
        var model=Empty();model.Storeys[0].Kind=StoreyKind.Normal;
        var slab=new SlabModel {Id="balcony",Code="YT-1",StoreyId="roof",Purpose=SlabPurpose.Balcony,Thickness=140,TopOffset=0,
            Outline=new(){new(0,0){Bulge=.2},new(4000,0),new(4000,3000),new(0,3000)},
            Openings=new(){new(){Id="well",Name="保留板洞",Outline=new(){new(1000,1000),new(2000,1000),new(2000,2000),new(1000,2000)}}}};
        model.Slabs.Add(slab);
        model.Walls.Add(new(){Id="wall",Code="W-1",StoreyId="roof",X1=6000,X2=9000,Thickness=200,AxisPlacement=WallAxisPlacement.LeftFace,Height=1000});
        model.Walls.Add(new(){Id="open-top",Code="W-2",StoreyId="roof",X1=6000,Y1=2000,X2=9000,Y2=2000,Height=2000});
        model.Openings.Add(new(){Id="door",HostWallId="open-top",Width=900,Height=2000,Offset=1500,Kind="门"});
        model.Storeys.Add(new(){Id="copy",Name="标准层副本",TemplateStoreyId="roof",Elevation=15000,Height=3000});
        var before=BuildingModelJson.ToJson(model);
        var rings=OutdoorReferenceEdges.Build(model,"roof");
        Check(rings.Count==3&&rings.Count(r=>r.Closed)==2,"Wrong semantic reference rings");
        Check(rings.Single(r=>r.ElementId=="wall").Points.All(p=>Math.Abs(p.Y+100)<.001),"Eccentric wall top used positioning axis instead of body centre");
        Check(!rings.Any(r=>r.ElementId=="open-top"),"Interrupted wall top became continuous reference");
        Check(OutdoorReferenceEdges.Build(model,"copy").Count==3,"Standard floor references absent");
        Check(before==BuildingModelJson.ToJson(model),"Reference extraction mutated source");
        var outer=rings.Single(r=>r.Key.EndsWith("/outer"));var edges=outer.Edges().ToArray();
        var wallChoice=new WallDrawingChoice(Purpose:WallPurpose.Parapet,Height:1100);
        var path=new OutdoorDrawingDraft(false,"roof",wallChoice);
        Check(path.TakeReference(edges[0].Path(false),false)==null&&path.Points.Count==2&&path.Points[0].Bulge==.2,"Arc copied as chord");
        Check(path.TakeReference(OutdoorReferenceEdges.Reverse(edges[1].Path(false),false),false)==null,"Reversed adjacent edge not connected");
        var snapshot=string.Join(";",path.Points.Select(p=>$"{p.X},{p.Y},{p.Bulge}"));
        Check(path.TakeReference(new[]{new PointModel(9000,9000),new(10000,9000)},false)!=null
            &&snapshot==string.Join(";",path.Points.Select(p=>$"{p.X},{p.Y},{p.Bulge}")),"Gap bridged or rejected edge changed draft");
        path.TakeReference(edges[2].Path(false),false);path.TakeReference(edges[3].Path(false),false);
        Check(path.Closed&&path.Points.Count==4,"Closing selected edges duplicated start node");
        path.Previous();Check(!path.Closed&&path.Points.Count==4,"Selected closing edge undo incorrect");
        path.Previous();Check(path.Points.Count==3,"Selected edge undo not one action");
        var whole=new OutdoorDrawingDraft(false,"roof",wallChoice);
        whole.TakeReference(edges[1].Path(true),true);var original=whole.Points.Select(OutdoorDrawingDraft.Copy).ToList();
        whole.Reverse();whole.Reverse();
        Check(original.Zip(whole.Points).All(p=>p.First.X==p.Second.X&&p.First.Y==p.Second.Y&&p.First.Bulge==p.Second.Bulge),"Reverse lost bulges");
        whole.Previous();whole.Previous();whole.Previous();Check(!whole.HasDraft,"Whole ring selection was not one undo action");
        var circle=new OutdoorDrawingDraft(true,"roof",wallChoice);
        Check(circle.TakeReference(new[]{new PointModel(0,0){Bulge=1},new PointModel(2000,0){Bulge=1}},true)==null,"Two-arc boundary rejected");
        Check(BuildingVolumeBuilder.Build(circle.Geometry(model)).Faces.Count>0,"Two-arc slab has no volume");
        var editable=new OutdoorDrawingDraft(true,"roof",wallChoice){OriginalSlab=slab,Offset=80,Thickness=180};
        editable.TakeReference(slab.Outline,true);var geometry=editable.Geometry(model);var updated=geometry.Slabs.Single();
        Check(updated.Code==slab.Code&&updated.Openings.Single().Id=="well"&&updated.Thickness==180
            &&geometry.TopElevationOf(updated)==9080,"Edit draft lost code/void/datum");
        var preview=BuildingVolumeBuilder.Build(geometry);
        Check(preview.Faces.Any(f=>f.StoreyId=="copy")&&preview.Faces.Any(f=>f.StoreyId=="roof"),"Preview omitted standard floor instances");
        Check(before==BuildingModelJson.ToJson(model),"Draft changed original slab");
        updated.Id=slab.Id;var editSession=new BuildingModelEditSession(model);var old=BuildingModelJson.ToJson(editSession.Model);
        Check(editSession.TryUpsertSlab(updated,out var id,out var error),error);
        Check(id==slab.Id&&editSession.Model.Slabs.Count==1&&editSession.Model.Slabs.Single().Openings.Single().Id=="well","Edit replaced identity or duplicated slab");
        Check(editSession.Undo()&&BuildingModelJson.ToJson(editSession.Model)==old,"Edit did not undo atomically");
        var saved=BuildingModelJson.FromJson(BuildingModelJson.ToJson(editSession.Model));
        Check(saved.Slabs.Single().Openings.Single().Id=="well","Edit/undo JSON lost void");
        Console.WriteLine("OUTDOOR_REFERENCE_CORE_OK semanticRings exactArcs wallBodyPath gaps reverse edgeUndo circle standardFloorPreview redrawIdentity preservedVoids rollback");
    }
}
