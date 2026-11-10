using Assimp;
using System.Text.Json;
using System.Numerics;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.BuildingModel.Export;

// Invoked only in an isolated, memory/time limited Studio child process.
public static class ComponentImportWorker
{
    public static int Run(string[] args)
    {
        try
        {
            if(args.Length!=4&&args.Length!=5&&args.Length!=8)throw new ArgumentException("Expected source, output GLB, units, up axis, rotation and optional XYZ scales.");
            var source=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);
            if(!new[]{".fbx",".glb"}.Contains(Path.GetExtension(source).ToLowerInvariant())||Path.GetExtension(output)!=".glb"||source==output||File.Exists(output))throw new IOException("请选择 FBX/GLB 来源和独立的新输出路径。");
            var unit=double.Parse(args[2],System.Globalization.CultureInfo.InvariantCulture);
            var yaw=args.Length>=5?double.Parse(args[4],System.Globalization.CultureInfo.InvariantCulture):0;
            var scales=args.Length==8?args.Skip(5).Select(s=>double.Parse(s,System.Globalization.CultureInfo.InvariantCulture)).ToArray():new[]{1d,1d,1d};
            if(scales.Any(s=>!double.IsFinite(s)||s<=0||s>10000))throw new ArgumentException("尺寸倍率无效。");
            if(!double.IsFinite(yaw))throw new ArgumentException("旋转角度无效。");
            if(!double.IsFinite(unit)||unit<=0||unit>100000||!new[]{"Z","Y"}.Contains(args[3]))throw new ArgumentException("单位或向上方向无效。");
            if(new FileInfo(source).Length>256L*1024*1024)throw new IOException("模型超过 256 MB，请先分件或简化后载入。");
            if(Path.GetExtension(source).Equals(".glb",StringComparison.OrdinalIgnoreCase))
            {
                ComponentGlbInspector.Inspect(source);
                using var stream=File.OpenRead(source);var model=SharpGLTF.Schema2.ModelRoot.ReadGLB(stream);
                var axis=args[3]=="Z"?Matrix4x4.CreateRotationX(-MathF.PI/2):Matrix4x4.Identity;
                var matrix=axis*Matrix4x4.CreateScale((float)(unit*scales[0]/1000),(float)(unit*scales[2]/1000),(float)(unit*scales[1]/1000))*Matrix4x4.CreateRotationY((float)(yaw*Math.PI/180));
                foreach(var node in model.DefaultScene.VisualChildren)node.LocalMatrix=node.LocalMatrix*matrix;
                model.SaveGLB(output);ComponentGlbInspector.Inspect(output);return 0;
            }
            using var context=new AssimpContext();
            var imported=context.ImportFile(source,PostProcessSteps.Triangulate|PostProcessSteps.PreTransformVertices|PostProcessSteps.GenerateNormals);
            var scene=new Scene {RootNode=new Node("Imported model")};
            foreach(var material in imported.Materials)scene.Materials.Add(new Material {Name=material.Name,ColorDiffuse=material.ColorDiffuse,Opacity=material.Opacity});
            if(scene.MaterialCount==0)scene.Materials.Add(new Material {Name="Default"});
            var count=0;var skipped=0;
            foreach(var mesh in imported.Meshes)
            {
                if(mesh.HasBones)throw new IOException("请先将蒙皮模型转换为当前姿态的静态网格。");
                var clean=new Mesh(mesh.Name,PrimitiveType.Triangle) {MaterialIndex=Math.Clamp(mesh.MaterialIndex,0,scene.MaterialCount-1)};
                foreach(var p in mesh.Vertices)
                {
                    var ax=p.X*unit*scales[0];var ay=(args[3]=="Z"?p.Y:-p.Z)*unit*scales[1];var az=(args[3]=="Z"?p.Z:p.Y)*unit*scales[2];
                    var theta=yaw*Math.PI/180;var x=(float)((ax*Math.Cos(theta)-ay*Math.Sin(theta))/1000);var y=(float)(az/1000);var z=(float)(-(ax*Math.Sin(theta)+ay*Math.Cos(theta))/1000);
                    if(!float.IsFinite(x)||!float.IsFinite(y)||!float.IsFinite(z))throw new IOException("模型包含无效坐标。");
                    clean.Vertices.Add(new Vector3(x,y,z));
                }
                foreach(var f in mesh.Faces)
                {
                    if(f.IndexCount!=3)continue;
                    var a=clean.Vertices[f.Indices[0]];var b=clean.Vertices[f.Indices[1]];var c=clean.Vertices[f.Indices[2]];
                    var cross=Vector3.Cross(b-a,c-a);
                    if(cross.LengthSquared()*1e12<1e-8){skipped++;continue;}
                    clean.Faces.Add(new Face(f.Indices.ToArray()));count++;
                    if(count>ComponentGlbInspector.MaxTriangles)throw new IOException("静态图库当前支持十万面以内，请先分件或简化模型。");
                }
                if(clean.FaceCount==0)continue;
                var node=new Node(string.IsNullOrWhiteSpace(mesh.Name)?"部件 "+(scene.MeshCount+1):mesh.Name);node.MeshIndices.Add(scene.MeshCount);scene.RootNode.Children.Add(node);scene.Meshes.Add(clean);
            }
            if(scene.MeshCount==0)throw new IOException("没有可导入的静态网格。");
            if(!context.ExportFile(scene,output,"glb2",PostProcessSteps.GenerateNormals))throw new IOException("模型转换未完成。");
            ComponentGlbInspector.Inspect(output);
            Console.WriteLine(JsonSerializer.Serialize(new {Triangles=count,RemovedDegenerate=skipped,Notice="FBX 静态几何与基础材质颜色；外部纹理及动画未导入。单位、向上方向请在预览中核对。"}));return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
    }
    public static int Views(string[] args)
    {
        try
        {
            if(args.Length!=3||File.Exists(args[1]))throw new ArgumentException("Expected GLB, new projection output and name.");
            var volume=ComponentGlbInspector.Inspect(args[0]).Volume;
            var views=ComponentElevationProjection.Four(volume,args[2],progress:Console.WriteLine);
            var plan=ComponentElevationProjection.Project(volume,args[2]+" · 平面",0,90);
            File.WriteAllText(args[1],JsonSerializer.Serialize(new {Views=views,Plan=plan}));return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
    }
}
