using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Assimp;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.BuildingModel.Export;

internal static class FbxWallGridTests
{
    private static void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);}
    private static double Area(Mesh mesh)=>mesh.Faces.Sum(f=>{
        var start=mesh.Vertices[f.Indices[0]];var sum=Vector3.Zero;
        for(var i=1;i+1<f.IndexCount;i++)sum+=Vector3.Cross(mesh.Vertices[f.Indices[i]]-start,mesh.Vertices[f.Indices[i+1]]-start);
        return sum.Length()/2d;
    });
    private static int Diagonals(Mesh mesh,Vector3 u)=>mesh.Faces.Sum(f=>Enumerable.Range(0,f.IndexCount).Count(i=>{
        var d=mesh.Vertices[f.Indices[(i+1)%f.IndexCount]]-mesh.Vertices[f.Indices[i]];
        return Math.Abs(d.Z)>.001&&Math.Abs(Vector3.Dot(d,u))>.001;
    }));
    internal static void Run()
    {
        Mesh Ring(bool mirror,float yaw,float slope=0)
        {
            var points=new[]{new Vector3(0,0,0),new(4000,0,0),new(4000,0,3000),new(0,0,3000),
                new(1000,0,800),new(2500,0,800),new(2500,0,2200),new(1000,0,2200)};
            var matrix=Matrix4x4.CreateRotationZ(yaw);
            points=points.Select(p=>Vector3.Transform(p+new Vector3(0,0,p.X*slope),matrix)).ToArray();
            var faces=new[]{new[]{0,1,5},new[]{0,5,4},new[]{1,2,6},new[]{1,6,5},new[]{2,3,7},new[]{2,7,6},new[]{3,0,4},new[]{3,4,7}};
            if(mirror)foreach(var face in faces)Array.Reverse(face);
            var mesh=new Mesh("wall",PrimitiveType.Triangle);mesh.Vertices.AddRange(points);
            var normal=Vector3.TransformNormal(mirror?Vector3.UnitY:-Vector3.UnitY,matrix);
            mesh.Normals.AddRange(points.Select(_=>normal));mesh.Faces.AddRange(faces.Select(f=>new Face(f)));
            FbxPlanarFaces.Merge(mesh,Enumerable.Repeat("",faces.Length).ToArray(),default);return mesh;
        }
        foreach(var mirror in new[]{false,true})foreach(var yaw in new[]{0f,.43f,1.57f})
        {
            var mesh=Ring(mirror,yaw);var before=Area(mesh);var u=Vector3.TransformNormal(Vector3.UnitX,Matrix4x4.CreateRotationZ(yaw));
            object Capture()=>new{Vertices=mesh.Vertices.Select(p=>new[]{p.X,p.Y,p.Z}).ToArray(),Faces=mesh.Faces.Select(f=>f.Indices.ToArray()).ToArray()};
            var baseline=Capture();
            Check(Diagonals(mesh,u)>0,"Fixture had no diagonal partition to replace.");
            Check(FbxWallGrid.Apply(mesh,default)==1,"Rectangular opening did not get a grid partition.");
            Check(Diagonals(mesh,u)==0,"A diagonal crosses the rectangular wall grid.");
            Check(Math.Abs(Area(mesh)-before)<1,"Wall grid changed the surface area.");
            Check(mesh.FaceCount==4,"Single rectangular opening should use four horizontal bands/spans.");
            if(!mirror&&yaw==0)
            {Directory.CreateDirectory(".artifacts/fbx-wall-grid-tests");File.WriteAllText(".artifacts/fbx-wall-grid-tests/comparison.json",JsonSerializer.Serialize(new{Before=baseline,After=Capture()}));}
            // The opening centre must not become a polygon interior.
            var inverse=Matrix4x4.CreateRotationZ(-yaw);
            foreach(var face in mesh.Faces)
            {
                var loop=face.Indices.Select(i=>Vector3.Transform(mesh.Vertices[i],inverse)).ToArray();
                Check(!(loop.Min(p=>p.X)<1800&&loop.Max(p=>p.X)>1800&&loop.Min(p=>p.Z)<1400&&loop.Max(p=>p.Z)>1400),
                    "Wall grid capped the opening.");
            }
        }
        var sloped=Ring(false,0,.1f);var count=sloped.FaceCount;var area=Area(sloped);
        Check(FbxWallGrid.Apply(sloped,default)==0&&sloped.FaceCount==count&&Area(sloped)==area,"Sloped boundary was flattened.");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{FbxWallGrid.Apply(sloped,cancelled.Token);throw new Exception("Grid cancellation ignored.");}catch(OperationCanceledException){}

        // Exercise the actual writer, its material gate and all side/jamb caps.
        foreach(var angle in new[]{0d,.43d})foreach(var openings in new[]{1,3})
        {
            var model=new BuildingModelDocument {Name="Wall grid native fixture"};model.Storeys.Add(new(){Id="F",Height=3000});
            model.Walls.Add(new(){Id="wall-grid",StoreyId="F",X2=5000*Math.Cos(angle),Y2=5000*Math.Sin(angle),Thickness=200});
            model.Openings.Add(new(){Id="window-grid",HostWallId="wall-grid",Code="C1615",Kind="窗",Offset=2500,Width=1600,Height=1500,Sill=800});
            if(openings==3)
            {
                model.Openings.Add(new(){Id="window-low",HostWallId="wall-grid",Code="C0810",Kind="窗",Offset=900,Width=800,Height=1000,Sill=400});
                model.Openings.Add(new(){Id="opening-door",HostWallId="wall-grid",Code="M0721",Kind="门",Offset=4400,Width=700,Height=2100});
            }
            var source=BuildingExportSceneBuilder.Build(model);var before=JsonSerializer.Serialize(source);
            var scene=(Scene)typeof(BuildingFbxWriter).GetMethod("CreateScene",BindingFlags.NonPublic|BindingFlags.Static)!
                .Invoke(null,new object[]{source,CancellationToken.None,new List<string>()})!;
            var wall=scene.Meshes.Single(m=>scene.Materials[m.MaterialIndex].Name.StartsWith("WL_Material: wall [",StringComparison.Ordinal));
            var u=new Vector3((float)Math.Cos(angle),(float)Math.Sin(angle),0);
            // Horizontal top/jamb faces can have plan mitres; only test vertical faces.
            var vertical=new Mesh("vertical",PrimitiveType.Polygon);vertical.Vertices.AddRange(wall.Vertices);
            vertical.Faces.AddRange(wall.Faces.Where(f=>{
                var a=wall.Vertices[f.Indices[0]];var normal=Vector3.Zero;
                for(var i=1;i+1<f.IndexCount;i++)normal+=Vector3.Cross(wall.Vertices[f.Indices[i]]-a,wall.Vertices[f.Indices[i+1]]-a);
                return Math.Abs(Vector3.Normalize(normal).Z)<.001;
            }));
            Check(Diagonals(vertical,u)==0,"Writer integration retained diagonal facade edges.");
            var uses=wall.Faces.SelectMany(f=>Enumerable.Range(0,f.IndexCount).Select(i=>{
                var a=wall.Vertices[f.Indices[i]];var b=wall.Vertices[f.Indices[(i+1)%f.IndexCount]];
                return Compare(a,b)<0?(a,b):(b,a);
            })).GroupBy(e=>e).ToArray();
            Check(uses.All(g=>g.Count()==2),"Grid introduced an open or nonmanifold wall/cap edge.");
            Check(before==JsonSerializer.Serialize(source),"Writer grid edited the neutral/source mesh.");
            var root=Path.GetFullPath(".artifacts/fbx-wall-grid-tests");Directory.CreateDirectory(root);
            var name=(angle==0?"orthogonal":"rotated")+(openings==3?"-multiple":"");var path=Path.Combine(root,name+".fbx");
            BuildingFbxWriter.Write(source,path);File.WriteAllText(Path.Combine(root,name+".scene.json"),before);
            BuildingModelJson.SaveModel(Path.Combine(root,name+".model.json"),model);
        }
        Console.WriteLine("FBX_WALL_GRID_OK horizontalVertical mirrored rotated holeArea sideCapWeld sourceUnchanged slopeFallback cancellation nativeReadback");
    }
    private static int Compare(Vector3 a,Vector3 b)=>a.X!=b.X?a.X.CompareTo(b.X):a.Y!=b.Y?a.Y.CompareTo(b.Y):a.Z.CompareTo(b.Z);
}
