using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed record OutdoorReferenceRing(string ElementId,string StoreyId,string Name,string Key,
    double Elevation,IReadOnlyList<PointModel> Points,bool Closed)
{
    internal int EdgeCount=>Points.Count-(Closed?0:1);
    internal IEnumerable<OutdoorReferenceEdge> Edges()=>Enumerable.Range(0,EdgeCount).Select(i=>new OutdoorReferenceEdge(this,i));
}

internal sealed record OutdoorReferenceEdge(OutdoorReferenceRing Ring,int Index)
{
    internal PointModel Start=>Ring.Points[Index];
    internal PointModel End=>Ring.Points[(Index+1)%Ring.Points.Count];
    internal CurveGeometry Curve=>new(Start,End,Start.Bulge);
    private IReadOnlyList<PointModel>? _samples;
    internal IReadOnlyList<PointModel> Samples=>_samples??=Curve.Sample();
    internal List<PointModel> Path(bool wholeRing)=>wholeRing
        ? Enumerable.Range(0,Ring.Points.Count).Select(i=>OutdoorDrawingDraft.Copy(Ring.Points[(Index+i)%Ring.Points.Count])).ToList()
        : new(){OutdoorDrawingDraft.Copy(Start),new(End.X,End.Y)};
}

internal static class OutdoorReferenceEdges
{
    internal static List<OutdoorReferenceRing> Build(BuildingModelDocument source,string storeyId)
    {
        var model=StandardStoreyLayout.Materialize(source);var rings=new List<OutdoorReferenceRing>();
        void Add(string id,string name,string key,double z,IReadOnlyList<PointModel> points,bool closed)
        {
            if(string.IsNullOrWhiteSpace(id)||!double.IsFinite(z)||points.Count<2)return;
            try {
                var copy=points.Select(OutdoorDrawingDraft.Copy).ToArray();
                for(var i=0;i<copy.Length-(closed?0:1);i++)
                    if(new CurveGeometry(copy[i],copy[(i+1)%copy.Length],copy[i].Bulge).Length<10)return;
                rings.Add(new(id,storeyId,name,key,z,copy,closed));
            }catch(ArgumentException){ }
        }
        foreach(var slab in model.Slabs.Where(s=>s.StoreyId==storeyId))
        {
            if(SlabGeometry.Validate(slab)!=null)continue;
            var name=BuildingElementNames.Slab(slab);var z=model.TopElevationOf(slab);
            Add(slab.Id,name+" · 板顶外边",slab.Id+"/outer",z,slab.Outline,true);
            foreach(var hole in slab.Openings??new())
                Add(slab.Id,name+" · "+hole.Name,slab.Id+"/hole/"+hole.Id,z,hole.Outline,true);
        }
        foreach(var wall in model.Walls.Where(w=>w.StoreyId==storeyId))
        {
            if(PlanEditing.ValidateWall(wall)!=null||model.Openings.Any(o=>o.HostWallId==wall.Id&&o.Sill+o.Height>=model.HeightOf(wall)-.5))continue;
            var curve=CurveGeometry.ForWall(wall);var offset=WallReferenceGeometry.BodyOffset(wall);
            var a=curve.At(0,offset);a.Bulge=wall.Bulge;
            Add(wall.Id,BuildingElementNames.Wall(wall)+" · 墙顶中心路径",wall.Id+"/top",model.BaseElevationOf(wall)+model.HeightOf(wall),
                new[]{a,curve.At(1,offset)},false);
        }
        return rings;
    }

    internal static List<PointModel> Reverse(IReadOnlyList<PointModel> points,bool closed)
    {
        var count=points.Count;var result=new List<PointModel>(count);
        for(var i=0;i<count;i++)
        {
            var p=OutdoorDrawingDraft.Copy(points[count-1-i]);
            p.Bulge=closed?-points[(count-2-i+count)%count].Bulge:i<count-1?-points[count-2-i].Bulge:0;
            result.Add(p);
        }
        return result;
    }
}
