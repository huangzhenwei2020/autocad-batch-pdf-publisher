using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Assimp;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.BuildingModel.Export;

internal static class ComponentImportTests
{
    private static void Check(bool value,string why){if(!value)throw new Exception(why);}
    internal static Mesh Box(float x,float y,float z,float w,float d,float h)
    {
        var mesh=new Mesh("合成柜体",PrimitiveType.Triangle);
        mesh.Vertices.AddRange(new[]{new Vector3(x,y,z),new Vector3(x+w,y,z),new Vector3(x+w,y+d,z),new Vector3(x,y+d,z),new Vector3(x,y,z+h),new Vector3(x+w,y,z+h),new Vector3(x+w,y+d,z+h),new Vector3(x,y+d,z+h)});
        foreach(var quad in new[]{new[]{0,3,2,1},new[]{4,5,6,7},new[]{0,1,5,4},new[]{1,2,6,5},new[]{2,3,7,6},new[]{3,0,4,7}})
        {mesh.Faces.Add(new Face(new[]{quad[0],quad[1],quad[2]}));mesh.Faces.Add(new Face(new[]{quad[0],quad[2],quad[3]}));}
        return mesh;
    }
    private static int Main()
    {
        var root=Path.GetFullPath(".artifacts/component-import-tests/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var source=Path.Combine(root,"合成柜体.fbx");var glb=Path.Combine(root,"cabinet.glb");
        using(var context=new AssimpContext())
        {
            var scene=new Scene {RootNode=new Node("合成柜体")};scene.Materials.Add(new Material {Name="测试材质"});scene.Meshes.Add(Box(0,0,0,1200,600,900));scene.RootNode.MeshIndices.Add(0);
            Check(context.ExportFile(scene,source,"fbx"),"Synthetic FBX creation failed.");
        }
        var sourceHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        Check(ComponentImportWorker.Run(new[]{source,glb,"1","Z"})==0,"FBX import failed.");
        var mesh=ComponentGlbInspector.Inspect(glb);var volume=mesh.Volume;
        Check(Math.Abs(volume.Width-1200)<.1&&Math.Abs(volume.Depth-600)<.1&&Math.Abs(volume.Height-900)<.1,$"Units/axes changed: {volume.Width},{volume.Depth},{volume.Height}");
        var four=ComponentElevationProjection.Four(volume,"合成柜体");
        for(var i=0;i<4;i++)
        {
            var lines=four[i].Lines;Check(lines.Count>=4,"Elevation empty.");
            Check(lines.All(l=>Math.Abs(l.X1-l.X2)<.001||Math.Abs(l.Y1-l.Y2)<.001),"Coplanar triangle diagonals survived.");
            var w=lines.SelectMany(l=>new[]{l.X1,l.X2}).Max();var h=lines.SelectMany(l=>new[]{l.Y1,l.Y2}).Max();
            Check(Math.Abs(w-(i<2?1200:600))<.1&&Math.Abs(h-900)<.1,"Elevation width/height mismatch.");
        }
        var rotated=Path.Combine(root,"rotated.glb");
        Check(ComponentImportWorker.Run(new[]{glb,rotated,"1000","Y","90","2","1",".5"})==0,"Resize/rotate failed.");
        var r=ComponentGlbInspector.Inspect(rotated).Volume;
        Check(Math.Abs(r.Width-600)<.1&&Math.Abs(r.Depth-2400)<.1&&Math.Abs(r.Height-450)<.1,"Scale/rotation was not persisted in geometry.");
        var output=Path.Combine(root,"views.json");Check(ComponentImportWorker.Views(new[]{glb,output,"柜体"})==0,"Projection worker failed.");
        using var doc=JsonDocument.Parse(File.ReadAllText(output));Check(doc.RootElement.GetProperty("Views").GetArrayLength()==4,"Four views not serialized.");
        var asymmetric=Path.Combine(root,"asymmetric.fbx");var asymmetricGlb=Path.Combine(root,"asymmetric.glb");
        using(var context=new AssimpContext())
        {
            var scene=new Scene {RootNode=new Node("非对称柜体")};scene.Materials.Add(new Material {Name="测试材质"});
            foreach(var shape in new[]{Box(0,0,0,1200,600,900),Box(200,-50,400,100,50,100),Box(1200,200,650,50,100,100)}){scene.RootNode.MeshIndices.Add(scene.Meshes.Count);scene.Meshes.Add(shape);}
            Check(context.ExportFile(scene,asymmetric,"fbx"),"Asymmetric fixture failed.");
        }
        Check(ComponentImportWorker.Run(new[]{asymmetric,asymmetricGlb,"1","Z"})==0,"Asymmetric FBX import failed.");
        var av=ComponentElevationProjection.Four(ComponentGlbInspector.Inspect(asymmetricGlb).Volume,"非对称柜体");
        bool HorizontalAt(ViewDocument view,double height)=>view.Lines.Any(l=>Math.Abs(l.Y1-height)<.1&&Math.Abs(l.Y2-height)<.1&&Math.Abs(Math.Abs(l.X2-l.X1)-100)<.1);
        Check(HorizontalAt(av[0],400)&&!HorizontalAt(av[1],400),"Front handle is missing or leaks through back.");
        Check(!HorizontalAt(av[2],650)&&HorizontalAt(av[3],650),"Left/right swapped or hidden handle leaks through body.");
        var centimetres=Path.Combine(root,"centimetres.glb");Check(ComponentImportWorker.Run(new[]{source,centimetres,"10","Z"})==0,"Centimetre conversion failed.");
        Check(Math.Abs(ComponentGlbInspector.Inspect(centimetres).Volume.Width-12000)<.1,"Centimetre factor ignored.");
        var before=File.ReadAllBytes(glb);Check(ComponentImportWorker.Run(new[]{source,glb,"1","Z"})!=0&&File.ReadAllBytes(glb).SequenceEqual(before),"Existing output overwritten.");
        using(var cancellation=new CancellationTokenSource()){cancellation.Cancel();try{ComponentElevationProjection.Four(volume,"cancel",cancellation:cancellation.Token);throw new Exception("Cancel ignored");}catch(OperationCanceledException){}}
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))==sourceHash,"Source FBX modified.");
        Console.WriteLine("COMPONENT_IMPORT_OK nativeFbx GLB mmCmZUp resizeRotatePersisted fourDirections asymmetricSides occlusion noTriangleDiagonals sourceUnchanged overwriteGuard cancel "+root);return 0;
    }
}
