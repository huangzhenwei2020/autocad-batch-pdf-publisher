using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using SharpGLTF.Schema2;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class ComponentGlbNode
    {
        public int Index { get; internal set; }
        public string Name { get; internal set; }
        public int Triangles { get; internal set; }
        public bool Mirrored { get; internal set; }
    }
    public sealed class ComponentGlbInspection
    {
        public BuildingVolume Volume { get; internal set; }
        public List<ComponentGlbNode> Nodes { get; } = new List<ComponentGlbNode>();
        public int Triangles { get; internal set; }
        public int MaterialCount { get; internal set; }
    }
    // Read-only static mesh inspection, not a project resource importer or a MAX reader.
    public static class ComponentGlbInspector
    {
        public const int MaxBytes=32*1024*1024;
        public const int MaxTriangles=100000;
        public const int MaxVertices=300000;
        public static ComponentGlbInspection Inspect(string path,CancellationToken cancellation=default)
        {
            if(!string.Equals(Path.GetExtension(path),".glb",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请选择自包含静态 .glb 文件。");
            using var file=File.OpenRead(path);if(file.Length>MaxBytes)throw new InvalidDataException("GLB 超过 32 MiB。");
            return Inspect(file,cancellation);
        }
        public static ComponentGlbInspection Inspect(byte[] bytes,CancellationToken cancellation=default)
        {
            if(bytes==null||bytes.Length>MaxBytes)throw new InvalidDataException("GLB 超过 32 MiB 或内容为空。");
            using var stream=new MemoryStream(bytes,false);return Inspect(stream,cancellation);
        }
        private static ComponentGlbInspection Inspect(Stream file,CancellationToken cancellation)
        {
            try{return InspectCore(file,cancellation);}
            catch(Exception ex) when(ex is SharpGLTF.Validation.ModelException||ex is ArgumentException||ex is KeyNotFoundException||ex is InvalidOperationException||ex is JsonException)
            {throw new InvalidDataException("GLB 数据无效："+ex.Message,ex);}
        }
        private static ComponentGlbInspection InspectCore(Stream file,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var settings=new ReadSettings {JsonPreprocessor=json=>{ValidateJson(json);cancellation.ThrowIfCancellationRequested();return json;}};
            var root=ModelRoot.ReadGLB(file,settings);
            if(root.LogicalAnimations.Count>0||root.LogicalSkins.Count>0||root.LogicalNodes.Count>256)throw new InvalidDataException("首阶段不支持动画、蒙皮或超过 256 个节点。");
            var scene=root.DefaultScene??root.LogicalScenes.FirstOrDefault();if(scene==null)throw new InvalidDataException("GLB 没有场景。");
            var result=new ComponentGlbInspection {Volume=new BuildingVolume(),MaterialCount=root.LogicalMaterials.Count};var vertices=0;
            void Visit(Node node,int depth)
            {
                if(depth>32)throw new InvalidDataException("GLB 节点嵌套过深。");
                cancellation.ThrowIfCancellationRequested();
                if(node.Mesh!=null) {
                    var transform=node.WorldMatrix;var determinant=transform.GetDeterminant();
                    if(!float.IsFinite(determinant)||Math.Abs(determinant)<.000000001)throw new InvalidDataException("GLB 节点包含奇异或非法变换。");
                    var info=new ComponentGlbNode {Index=node.LogicalIndex,Name=node.Name??"node_"+node.LogicalIndex,Mirrored=determinant<0};
                    foreach(var primitive in node.Mesh.Primitives) {
                        if(primitive.DrawPrimitiveType!=PrimitiveType.TRIANGLES||primitive.MorphTargetsCount>0)throw new InvalidDataException("首阶段仅支持静态三角网格。");
                        var accessor=primitive.GetVertexAccessor("POSITION");if(accessor==null)throw new InvalidDataException("网格缺少坐标。");
                        vertices+=accessor.Count;if(vertices>MaxVertices)throw new InvalidDataException("GLB 实例顶点超过 300000。");
                        var positions=accessor.AsVector3Array();
                        Vector3 Point(int index) {
                            var p=Vector3.Transform(positions[index],transform);
                            // glTF metres, Y-up -> architectural millimetres, Z-up (right-handed).
                            var value=new Vector3(p.X*1000,-p.Z*1000,p.Y*1000);
                            if(!float.IsFinite(value.X)||!float.IsFinite(value.Y)||!float.IsFinite(value.Z)||Math.Max(Math.Max(Math.Abs(value.X),Math.Abs(value.Y)),Math.Abs(value.Z))>1000000)
                                throw new InvalidDataException("GLB 坐标非法或超出构件范围。");return value;
                        }
                        for(var i=0;i<positions.Count;i++){if(i%1024==0)cancellation.ThrowIfCancellationRequested();Point(i);}
                        foreach(var triangle in primitive.GetTriangleIndices()) {
                            if(++result.Triangles>MaxTriangles)throw new InvalidDataException("GLB 超过 100000 个三角面。");
                            if(result.Triangles%1024==0)cancellation.ThrowIfCancellationRequested();
                            var a=Point(triangle.A);var b=Point(info.Mirrored?triangle.C:triangle.B);var c=Point(info.Mirrored?triangle.B:triangle.C);
                            var normal=Vector3.Cross(b-a,c-a);if(normal.LengthSquared()<.00000001)throw new InvalidDataException("GLB 含退化面。");normal=Vector3.Normalize(normal);
                            result.Volume.Faces.Add(new VolumeFace {ElementId="node-"+info.Index,Kind="asset",Points=new List<Point3DModel>{new Point3DModel(a.X,a.Y,a.Z),new Point3DModel(b.X,b.Y,b.Z),new Point3DModel(c.X,c.Y,c.Z)},NormalX=normal.X,NormalY=normal.Y,NormalZ=normal.Z});info.Triangles++;
                        }
                    }
                    result.Nodes.Add(info);
                }
                foreach(var child in node.VisualChildren)Visit(child,depth+1);
            }
            foreach(var node in scene.VisualChildren)Visit(node,0);
            if(result.Volume.Faces.Count==0)throw new InvalidDataException("场景没有有效静态网格。");
            var points=result.Volume.Faces.SelectMany(f=>f.Points).ToArray();var volume=result.Volume;
            volume.MinX=points.Min(p=>p.X);volume.MaxX=points.Max(p=>p.X);volume.MinY=points.Min(p=>p.Y);volume.MaxY=points.Max(p=>p.Y);volume.MinZ=points.Min(p=>p.Z);volume.MaxZ=points.Max(p=>p.Z);
            return result;
        }
        private static void ValidateJson(string json)
        {
            if(json.Length>2*1024*1024)throw new InvalidDataException("GLB 清单过大。");
            using var document=JsonDocument.Parse(json,new JsonDocumentOptions {MaxDepth=64});var root=document.RootElement;
            if(root.TryGetProperty("extensionsRequired",out var required)&&required.GetArrayLength()>0)throw new InvalidDataException("首阶段不支持必需扩展。");
            if(root.TryGetProperty("images",out var images)&&images.GetArrayLength()>0)throw new InvalidDataException("本阶段先校验无贴图静态模型；请导出基础色版本。");
            if(root.TryGetProperty("nodes",out var nodes)) {
                if(nodes.GetArrayLength()>256)throw new InvalidDataException("节点数量超限。");
                foreach(var node in nodes.EnumerateArray())if(node.TryGetProperty("name",out var name)&&(name.GetString()?.Length??0)>256)throw new InvalidDataException("节点名称过长。");
            }
            if(root.TryGetProperty("buffers",out var buffers)) {
                if(buffers.GetArrayLength()>1)throw new InvalidDataException("只支持一个内嵌二进制缓冲区。");
                foreach(var buffer in buffers.EnumerateArray()){var bytes=buffer.GetProperty("byteLength").GetInt64();if(bytes<0||bytes>MaxBytes)throw new InvalidDataException("二进制缓冲区超限。");}
            }
            if(root.TryGetProperty("accessors",out var accessors)) {
                if(accessors.GetArrayLength()>1024)throw new InvalidDataException("访问器数量超限。");
                foreach(var accessor in accessors.EnumerateArray()){var count=accessor.GetProperty("count").GetInt64();if(count<0||count>MaxVertices)throw new InvalidDataException("访问器数据超限。");}
            }
            void Check(JsonElement element) {
                if(element.ValueKind==JsonValueKind.Object)foreach(var property in element.EnumerateObject()) {
                    if(property.Name=="uri")throw new InvalidDataException("不允许外部文件、网络或 data URI；请选择完全自包含 GLB。");Check(property.Value);
                }
                else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())Check(child);
            }
            Check(root);
        }
    }
}
