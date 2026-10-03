using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

/// <summary>Mesh boundary/crease edges in world coordinates, without coplanar grid subdivisions.</summary>
internal static class ModelMeshEdges
{
    private sealed class Edge
    {
        public required Point3DModel A,B;
        public string? ElementId,StoreyId;
        public double NX,NY,NZ;
        public int Count;
        public bool Crease;
    }

    internal static List<VolumeGuideLine> Build(BuildingVolume volume)
    {
        static (long X,long Y,long Z) Key(Point3DModel p) => ((long)Math.Round(p.X*100),(long)Math.Round(p.Y*100),(long)Math.Round(p.Z*100));
        var edges=new Dictionary<((long,long,long),(long,long,long)),Edge>();
        foreach(var face in volume.Faces)
        for(var i=0;i<face.Points.Count;i++) {
            var a=face.Points[i];var b=face.Points[(i+1)%face.Points.Count];
            var ka=Key(a);var kb=Key(b);if(ka==kb)continue;
            var key=ka.CompareTo(kb)<=0 ? (ka,kb) : (kb,ka);
            if(!edges.TryGetValue(key,out var edge)) {
                edges[key]=new Edge { A=a,B=b,ElementId=face.ElementId,StoreyId=face.StoreyId,
                    NX=face.NormalX,NY=face.NormalY,NZ=face.NormalZ,Count=1 };
            } else {
                edge.Count++;
                var dot=edge.NX*face.NormalX+edge.NY*face.NormalY+edge.NZ*face.NormalZ;
                if(Math.Abs(dot)<.99999 || edge.StoreyId!=face.StoreyId)edge.Crease=true;
            }
        }
        return edges.Values.Where(e=>e.Count==1 || e.Crease).Select(e=>new VolumeGuideLine {
            Start=e.A,End=e.B,ElementId=e.ElementId }).ToList();
    }
}
