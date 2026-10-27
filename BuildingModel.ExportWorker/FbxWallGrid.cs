using System.Numerics;
using Assimp;

namespace BatchPdfPublisher.BuildingModel.Export;

/// <summary>Optional export-only partition of rectilinear vertical wall surfaces.
/// Preserve the existing convex partition whenever the boundary is not suitable.</summary>
internal static class FbxWallGrid
{
    private readonly record struct Edge(int A,int B)
    { internal static Edge Of(int a,int b)=>a<b?new(a,b):new(b,a); }
    private readonly record struct D(double X,double Y,double Z)
    {
        internal D(Vector3 p):this(p.X,p.Y,p.Z){}
        public static D operator -(D a,D b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static D operator +(D a,D b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static D operator *(D a,double t)=>new(a.X*t,a.Y*t,a.Z*t);
        internal double Dot(D b)=>X*b.X+Y*b.Y+Z*b.Z;
        internal D Cross(D b)=>new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        internal double Length=>Math.Sqrt(Dot(this));
        internal D Unit=>this*(1/Length);
    }
    private sealed record Loop(Vector3[] Points,Vector3[] Normals);
    private static D Area(IReadOnlyList<Vector3> points)
    {
        var area=new D();var origin=new D(points[0]);
        for(var i=1;i+1<points.Count;i++)area+=(new D(points[i])-origin).Cross(new D(points[i+1])-origin);
        return area*.5;
    }

    internal static int Apply(Mesh mesh,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(mesh.FaceCount<2)return 0;
        var canonical=new Dictionary<Vector3,int>();var ids=new int[mesh.VertexCount];
        for(var i=0;i<ids.Length;i++)
        {if(!canonical.TryGetValue(mesh.Vertices[i],out var id))canonical.Add(mesh.Vertices[i],id=canonical.Count);ids[i]=id;}
        var points=canonical.OrderBy(p=>p.Value).Select(p=>p.Key).ToArray();
        var faces=mesh.Faces.Select(f=>f.Indices.ToArray()).ToArray();
        var normals=faces.Select(f=>Area(f.Select(i=>mesh.Vertices[i]).ToArray()).Unit).ToArray();
        var uses=new Dictionary<Edge,List<int>>();
        for(var i=0;i<faces.Length;i++)foreach(var edge in Edges(faces[i]))
        {if(!uses.TryGetValue(edge,out var list))uses.Add(edge,list=new());list.Add(i);}
        IEnumerable<Edge> Edges(int[] f)
        {for(var i=0;i<f.Length;i++)yield return Edge.Of(ids[f[i]],ids[f[(i+1)%f.Length]]);}
        bool SamePlane(int a,int b)
        {
            if(normals[a].Dot(normals[b])<1-1e-10)return false;
            var origin=new D(mesh.Vertices[faces[a][0]]);
            return faces[b].All(i=>{
                var delta=new D(mesh.Vertices[i])-origin;
                return Math.Abs(delta.Dot(normals[a]))<=Math.Max(.00001,delta.Length*2e-7);
            });
        }
        var replacements=new Dictionary<int,List<Loop>>();var removed=new HashSet<int>();var visited=new HashSet<int>();
        var changed=0;
        for(var start=0;start<faces.Length;start++)
        {
            cancellation.ThrowIfCancellationRequested();
            if(Math.Abs(normals[start].Z)>1e-7||!visited.Add(start))continue;
            var component=new List<int>();var queue=new Queue<int>();queue.Enqueue(start);
            while(queue.TryDequeue(out var current))
            {
                cancellation.ThrowIfCancellationRequested();component.Add(current);
                foreach(var edge in Edges(faces[current]))
                    if(uses[edge].Count==2)foreach(var next in uses[edge])
                        if(!visited.Contains(next)&&SamePlane(start,next)){visited.Add(next);queue.Enqueue(next);}
            }
            if(component.Count<2)continue;
            var loops=Partition(component,normals[start]);if(loops==null)continue;
            replacements.Add(start,loops);foreach(var i in component)removed.Add(i);changed++;
        }
        if(changed==0)return 0;
        List<Loop>? Partition(List<int> component,D normal)
        {
            var boundary=new Dictionary<(int A,int B),bool>();
            var u=new D(-normal.Y,normal.X,0).Unit;var hasDiagonal=false;
            foreach(var face in component)
                for(var j=0;j<faces[face].Length;j++)
                {
                    var a=ids[faces[face][j]];var b=ids[faces[face][(j+1)%faces[face].Length]];
                    if(a==b)return null;
                    if(boundary.Remove((b,a)))
                    {var d=new D(points[b])-new D(points[a]);if(Math.Abs(d.Z)>1e-6&&Math.Abs(d.Dot(u))>1e-6)hasDiagonal=true;}
                    else if(!boundary.TryAdd((a,b),true))return null;
                }
            if(!hasDiagonal||boundary.Count<4||boundary.Count>4096)return null;
            // Closed, non-branching loops, with holes retained by the even/odd sweep.
            if(boundary.Keys.GroupBy(e=>e.A).Any(g=>g.Count()!=1)||boundary.Keys.GroupBy(e=>e.B).Any(g=>g.Count()!=1)
                ||!boundary.Keys.Select(e=>e.A).ToHashSet().SetEquals(boundary.Keys.Select(e=>e.B)))return null;
            var vertical=new List<(Vector3 A,Vector3 B,double U)>();
            var origin=new D(points[boundary.First().Key.A]);
            double U(Vector3 p)=>(new D(p)-origin).Dot(u);
            foreach(var edge in boundary.Keys)
            {
                var a=points[edge.A];var b=points[edge.B];
                if(a.Z==b.Z)continue;
                // Exact XY equality avoids flattening authored slopes or thin details.
                if(a.X!=b.X||a.Y!=b.Y)return null;
                vertical.Add((a,b,U(a)));
            }
            var levels=boundary.Keys.SelectMany(e=>new[]{points[e.A].Z,points[e.B].Z}).Distinct().OrderBy(z=>z).ToArray();
            if(levels.Length>1024)return null;
            var result=new List<Loop>();var normalValue=mesh.Normals[faces[component[0]][0]];
            for(var i=1;i<levels.Length;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var low=levels[i-1];var high=levels[i];var mid=((double)low+high)/2;
                var intersections=vertical.Where(e=>mid>Math.Min(e.A.Z,e.B.Z)&&mid<Math.Max(e.A.Z,e.B.Z)).OrderBy(e=>e.U).ToArray();
                if(intersections.Length%2!=0)return null;
                for(var j=0;j<intersections.Length;j+=2)
                {
                    var left=intersections[j];var right=intersections[j+1];
                    if(right.U<=left.U)return null;
                    var quad=new[]{new Vector3(left.A.X,left.A.Y,low),new Vector3(right.A.X,right.A.Y,low),
                        new Vector3(right.A.X,right.A.Y,high),new Vector3(left.A.X,left.A.Y,high)};
                    var area=Area(quad);if(!double.IsFinite(area.Length)||area.Length<=0)return null;
                    if(area.Dot(normal)<0)Array.Reverse(quad);
                    result.Add(new(quad,Enumerable.Repeat(normalValue,4).ToArray()));
                    if(result.Count>Math.Min(20000,component.Count*8+16))return null;
                }
            }
            var before=component.Sum(i=>Area(faces[i].Select(v=>mesh.Vertices[v]).ToArray()).Length);
            var after=result.Sum(l=>Area(l.Points).Length);
            if(result.Count==0||Math.Abs(before-after)>Math.Max(.00001,before*2e-7))return null;
            return result;
        }

        var output=new List<Loop>();
        for(var i=0;i<faces.Length;i++)
            if(replacements.TryGetValue(i,out var replacement))output.AddRange(replacement);
            else if(!removed.Contains(i))output.Add(new(faces[i].Select(v=>mesh.Vertices[v]).ToArray(),faces[i].Select(v=>mesh.Normals[v]).ToArray()));
        // A band may meet a jamb or another band at a T junction. Insert the same
        // control point in every incident polygon, including unreplaced end caps.
        // No position welding across gaps or different elements is performed.
        var candidates=output.SelectMany(l=>l.Points).Concat(mesh.Vertices).Distinct().ToArray();
        var byZ=candidates.GroupBy(p=>p.Z).ToDictionary(g=>g.Key,g=>g.ToArray());
        var byXY=candidates.GroupBy(p=>(p.X,p.Y)).ToDictionary(g=>g.Key,g=>g.ToArray());
        var stitched=new List<Loop>();long checks=0;
        foreach(var loop in output)
        {
            cancellation.ThrowIfCancellationRequested();var p=new List<Vector3>();var n=new List<Vector3>();
            for(var i=0;i<loop.Points.Length;i++)
            {
                var a=loop.Points[i];var b=loop.Points[(i+1)%loop.Points.Length];
                var na=loop.Normals[i];var nb=loop.Normals[(i+1)%loop.Points.Length];
                p.Add(a);n.Add(na);var delta=new D(b)-new D(a);var length2=delta.Dot(delta);if(length2<=0)return 0;
                var possible=a.Z==b.Z?byZ[a.Z]:a.X==b.X&&a.Y==b.Y?byXY[(a.X,a.Y)]:candidates;
                var inside=new List<(double T,Vector3 P)>();
                foreach(var candidate in possible)
                {
                    if(++checks>20_000_000)return 0;
                    if((checks&4095)==0)cancellation.ThrowIfCancellationRequested();
                    if(candidate==a||candidate==b)continue;
                    var rel=new D(candidate)-new D(a);var t=rel.Dot(delta)/length2;
                    if(t<=0||t>=1)continue;
                    var tolerance=Math.Min(.001,Math.Max(.000001,Math.Sqrt(length2)*2e-7));
                    if((rel-delta*t).Length<=tolerance)inside.Add((t,candidate));
                }
                foreach(var item in inside.OrderBy(c=>c.T))
                {p.Add(item.P);n.Add(Vector3.Normalize(Vector3.Lerp(na,nb,(float)item.T)));}
            }
            if(p.Count>512)return 0;
            // Do not trade the previous convex guarantee for new target-DCC n-gons.
            var normal=Area(p).Unit;var reflex=false;
            for(var i=0;i<p.Count;i++)
            {
                var a=new D(p[i])-new D(p[(i+p.Count-1)%p.Count]);var b=new D(p[(i+1)%p.Count])-new D(p[i]);
                // Inserting an existing float vertex on a rotated edge can leave
                // a few float ULPs of collinearity noise. Match independent FBX
                // surface validation; a real reflex corner still keeps old cuts.
                var turn=a.Cross(b).Dot(normal);
                if(turn<-Math.Max(1e-8,a.Length*b.Length*1e-7))
                {
                    // A horizontal cap shares the facade's new boundary points.
                    // Float XY collinearity noise must not turn it into a concave
                    // n-gon. Its convex source interior supports a bounded fan;
                    // facade bands themselves keep their horizontal/vertical cuts.
                    if(Math.Abs(normal.Z)<.999999||-turn/Math.Max(1,a.Length+b.Length)>.001)
                    return 0;
                    reflex=true;
                }
            }
            if(!reflex)stitched.Add(new(p.ToArray(),n.ToArray()));
            else
            {
                var center=new Vector3((float)p.Average(v=>(double)v.X),(float)p.Average(v=>(double)v.Y),(float)p.Average(v=>(double)v.Z));
                var centerNormal=Vector3.Normalize(n.Aggregate(Vector3.Zero,(a,b)=>a+b));
                var fan=new List<Loop>();
                for(var i=0;i<p.Count;i++)
                {
                    var triangle=new[]{p[i],p[(i+1)%p.Count],center};
                    if(Area(triangle).Dot(normal)<=0)return 0;
                    fan.Add(new(triangle,new[]{n[i],n[(i+1)%p.Count],centerNormal}));
                }
                if(Math.Abs(fan.Sum(l=>Area(l.Points).Length)-Area(p).Length)>Math.Max(.00001,Area(p).Length*2e-7))return 0;
                stitched.AddRange(fan);
            }
        }
        var vertexMap=new Dictionary<(Vector3,Vector3),int>();var vertices=new List<Vector3>();var vertexNormals=new List<Vector3>();var finalFaces=new List<Face>();
        foreach(var loop in stitched)
        {
            var indices=new int[loop.Points.Length];
            for(var i=0;i<indices.Length;i++)
            {
                var key=(loop.Points[i],loop.Normals[i]);
                if(!vertexMap.TryGetValue(key,out var v)){vertexMap.Add(key,v=vertices.Count);vertices.Add(key.Item1);vertexNormals.Add(key.Item2);}
                indices[i]=v;
            }
            finalFaces.Add(new(indices));
        }
        mesh.Vertices.Clear();mesh.Vertices.AddRange(vertices);mesh.Normals.Clear();mesh.Normals.AddRange(vertexNormals);
        mesh.Faces.Clear();mesh.Faces.AddRange(finalFaces);
        mesh.PrimitiveType=(finalFaces.Any(f=>f.IndexCount==3)?PrimitiveType.Triangle:0)|(finalFaces.Any(f=>f.IndexCount>3)?PrimitiveType.Polygon:0);
        return changed;
    }
}
