using System.Numerics;
using System.Text;
using BatchPdfPublisher.BuildingModel;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;

internal static class ComponentSourceTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private static void Reject(Action action,string message){try{action();}catch(Exception ex) when(ex is InvalidDataException or ArgumentException){return;}throw new Exception(message);}
    private static int Main(string[] args)
    {
        try {
            if(args.Length==2&&args[0]=="--inspect") {
                var result=ComponentGlbInspector.Inspect(args[1]);
                Check(Math.Abs(result.Volume.Width-1200)<.1&&Math.Abs(result.Volume.Height-1600)<.1&&Math.Abs(result.Volume.Depth-100)<.1,"3ds Max exported dimensions do not match millimetres");
                Console.WriteLine($"MAX_GLB_INSPECTION_OK {result.Volume.Width:0.###}x{result.Volume.Depth:0.###}x{result.Volume.Height:0.###}mm nodes={result.Nodes.Count} triangles={result.Triangles}");return 0;
            }
            var folder=Path.GetFullPath(".artifacts/component-sources");Directory.CreateDirectory(folder);
            var symbol=new ComponentPlanSymbol {Name="Fixed window",Code="C1216",Width=1200,Height=1600};
            var outline=new[]{new PointModel(0,-50),new PointModel(1200,-50),new PointModel(1200,50),new PointModel(0,50)};
            for(var i=0;i<4;i++){var a=outline[i];var b=outline[(i+1)%4];symbol.Primitives.Add(new ComponentPlanPrimitive {Kind="Line",X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y});}
            symbol.Primitives.Add(new ComponentPlanPrimitive {Kind="Circle",X1=600,Radius=25,SweepDegrees=360});
            symbol.Primitives.Add(new ComponentPlanPrimitive {Kind="Arc",X1=50,Radius=100,StartDegrees=0,SweepDegrees=-90});
            symbol.TextCandidates.Add(new ComponentPlanTextCandidate {Text="C1216",X=600,Y=150});
            var planPath=Path.Combine(folder,"fixed-window.wlplan.json");ComponentPlanSymbols.Save(planPath,symbol);
            var restored=ComponentPlanSymbols.Load(planPath);Check(restored.Primitives.Count==6&&restored.Primitives.Last().SweepDegrees==-90&&restored.TextCandidates[0].Text=="C1216","CAD native arc/text contract roundtrip");
            Check(ComponentPlanSymbols.Preview(restored).Circles.Count==1&&ComponentPlanSymbols.Preview(restored).Texts.Count==0,"Preview flattened circle or hardcoded numbering");
            var frame=new ComponentPlanFrame(100,200,0,0,1,1000);var point=frame.Point(101,202,0);Check(point.X==2000&&point.Y==-1000,"CAD units/rotation normalization");
            var mirror=frame.Arc(new PointModel(0,0),new PointModel(100,0),new PointModel(0,-100),Math.PI/2,false);Check(mirror.SweepDegrees==-90,"Mirrored arc direction");
            Reject(()=>frame.Arc(new PointModel(0,0),new PointModel(100,0),new PointModel(0,200),Math.PI/2,false),"Nonuniform circle accepted");
            Reject(()=>frame.Point(1,2,.01),"Nonplanar geometry accepted");
            symbol.Primitives[0].X1=double.NaN;Reject(()=>ComponentPlanSymbols.Save(planPath,symbol),"NaN accepted");symbol.Primitives[0].X1=0;
            Check(ComponentPlanSymbols.Load(planPath).Primitives[0].X1==0,"Rejected save damaged old symbol");
            var glb=Path.Combine(folder,"fixed-window.glb");WriteMesh(glb,false);
            var inspection=ComponentGlbInspector.Inspect(glb);Check(inspection.Triangles==12&&inspection.Nodes.Count==1,"Static GLB mesh decode");
            Check(Math.Abs(inspection.Volume.Width-1200)<.001&&Math.Abs(inspection.Volume.Height-1600)<.001&&Math.Abs(inspection.Volume.Depth-100)<.001,"GLB metre/Y-up conversion");
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(glb)));
            var transformed=Path.Combine(folder,"mirrored-hierarchy.glb");WriteMesh(transformed,true);var mirrored=ComponentGlbInspector.Inspect(transformed);
            Check(mirrored.Nodes.Single().Mirrored&&Math.Abs(mirrored.Volume.MinX+1100)<.001&&Math.Abs(mirrored.Volume.MinZ-200)<.001,"GLB nested translation/negative scale");
            Check(mirrored.Volume.Faces.All(f=>double.IsFinite(f.NormalX)&&Math.Abs(f.NormalX*f.NormalX+f.NormalY*f.NormalY+f.NormalZ*f.NormalZ-1)<.000001),"Mirror normals invalid");
            using(var cancellation=new CancellationTokenSource()){cancellation.Cancel();try{ComponentGlbInspector.Inspect(glb,cancellation.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}}
            foreach(var json in new[]{"{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":12,\"uri\":\"../outside.bin\"}]}",
                "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"http://example.invalid/image.png\"}]}",
                "{\"asset\":{\"version\":\"2.0\"},\"extensionsRequired\":[\"UNKNOWN\"]}"}) {
                var bad=Path.Combine(folder,"bad.glb");WriteJsonGlb(bad,json);Reject(()=>ComponentGlbInspector.Inspect(bad),"External resource/required extension accepted");
            }
            Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(glb)))==hash,"Inspection changed source GLB");
            Console.WriteLine("COMPONENT_SOURCE_OK nativePlan arcs circles mirror units textCandidates atomicSave GLB hierarchy mm Zup normals cancellation externalURI extension rejection unchangedSources");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static void WriteMesh(string path,bool mirror)
    {
        var mesh=new MeshBuilder<VertexPositionNormal,VertexEmpty,VertexEmpty>("frame");var primitive=mesh.UsePrimitive(new MaterialBuilder("neutral").WithMetallicRoughnessShader());
        var p=new[]{new Vector3(0,0,0),new Vector3(1.2f,0,0),new Vector3(1.2f,1.6f,0),new Vector3(0,1.6f,0),new Vector3(0,0,.1f),new Vector3(1.2f,0,.1f),new Vector3(1.2f,1.6f,.1f),new Vector3(0,1.6f,.1f)};
        foreach(var face in new[]{new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{3,7,6,2},new[]{0,4,7,3},new[]{1,2,6,5}}) {
            var n=Vector3.Normalize(Vector3.Cross(p[face[1]]-p[face[0]],p[face[2]]-p[face[0]]));
            primitive.AddTriangle(new VertexPositionNormal(p[face[0]],n),new VertexPositionNormal(p[face[1]],n),new VertexPositionNormal(p[face[2]],n));
            primitive.AddTriangle(new VertexPositionNormal(p[face[0]],n),new VertexPositionNormal(p[face[2]],n),new VertexPositionNormal(p[face[3]],n));
        }
        var root=ModelRoot.CreateModel();root.CreateMeshes(mesh);var parent=root.UseScene(0).CreateNode("assembly");var node=parent.CreateNode("frame");node.Mesh=root.LogicalMeshes[0];
        if(mirror){parent.LocalMatrix=Matrix4x4.CreateTranslation(.1f,.2f,.3f);node.LocalMatrix=Matrix4x4.CreateScale(-1,1,1);}
        root.SaveGLB(path);
    }
    private static void WriteJsonGlb(string path,string json)
    {
        var data=Encoding.UTF8.GetBytes(json);var length=(data.Length+3)&~3;
        using var file=File.Create(path);using var writer=new BinaryWriter(file);writer.Write(0x46546C67u);writer.Write(2u);writer.Write((uint)(20+length));writer.Write((uint)length);writer.Write(0x4E4F534Au);writer.Write(data);
        for(var i=data.Length;i<length;i++)writer.Write((byte)32);
    }
}
