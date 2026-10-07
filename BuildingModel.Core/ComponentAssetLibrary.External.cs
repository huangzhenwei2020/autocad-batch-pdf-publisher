using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class ComponentSourceFrame
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string AlongAxis { get; set; } = "X";
    }
    public sealed class ComponentPartDefinition
    {
        public string PartId { get; set; } = Guid.NewGuid().ToString("D");
        public string Name { get; set; }
        public string Role { get; set; } = "Fixed";
        public List<int> MeshNodes { get; set; } = new List<int>();
        public List<int> PlanPrimitives { get; set; } = new List<int>();
        // Capability gates stay explicit until the joint size/motion solver is implemented.
        public string SizeMode { get; set; } = "Fixed";
        public string MotionKind { get; set; } = "Fixed";
    }
    public sealed class ExternalComponentDefinition
    {
        public string Code { get; set; }
        public string PlanSha256 { get; set; }
        public string ModelSha256 { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
        public ComponentSourceFrame ModelFrame { get; set; } = new ComponentSourceFrame();
        public List<ComponentPartDefinition> Parts { get; set; } = new List<ComponentPartDefinition>();
    }
    public sealed class ExternalComponentContent
    {
        public ComponentPlanSymbol Plan { get; internal set; }
        public ComponentGlbInspection Mesh { get; internal set; }
        public BuildingVolume Volume { get; internal set; }
        public BuildingVolume PartVolume(ComponentPartDefinition part)
        {
            var ids=new HashSet<string>(part.MeshNodes.Select(n=>"node-"+n));
            return ComponentAssetLibrary.Bounds(new BuildingVolume {Faces=Volume.Faces.Where(f=>ids.Contains(f.ElementId)).ToList()});
        }
        public ViewDocument PartPlan(ComponentPartDefinition part)
        {
            if(part.PlanPrimitives.Count==0)return new ViewDocument();
            return ComponentPlanSymbols.Preview(new ComponentPlanSymbol {SchemaVersion=Plan.SchemaVersion,Category=Plan.Category,
                Code=Plan.Code,Name=Plan.Name,Width=Plan.Width,Height=Plan.Height,Depth=Plan.Depth,
                Primitives=part.PlanPrimitives.Select(i=>Plan.Primitives[i]).ToList()});
        }
    }
    public sealed partial class ComponentAssetLibrary
    {
        private const string PlanEntry="plan.wlplan.json",ModelEntry="model.glb";

        public ComponentAsset SaveExternal(string name,ComponentPlanSymbol plan,byte[] model,ComponentSourceFrame frame,
            IEnumerable<ComponentPartDefinition> parts,OpeningTypeModel fallback=null,CancellationToken cancellation=default,string assetId=null,int revision=1)
        {
            var planBytes=ComponentPlanSymbols.Bytes(plan);
            var inspection=ComponentGlbInspector.Inspect(model,cancellation);
            var volume=Align(inspection.Volume,frame);
            var manifest=new ComponentAssetManifest {SchemaVersion=2,Name=name,Category=plan.Category=="Furniture"?"Furniture":"DoorWindow",
                AssetId=assetId??Guid.NewGuid().ToString("D"),Revision=revision,GeometryMode="ExternalStatic",OpeningType=fallback,
                External=new ExternalComponentDefinition {Code=plan.Code,PlanSha256=Hash(planBytes),ModelSha256=Hash(model),
                    Width=plan.Width,Height=plan.Height,Depth=volume.Depth,ModelFrame=frame,Parts=parts.ToList()}};
            var json=JsonSerializer.SerializeToUtf8Bytes(manifest,Json);
            if(json.Length>MaxManifestBytes)throw new InvalidDataException("资源清单超过限制。");
            using var memory=new MemoryStream();
            using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)) {
                void Add(string path,byte[] bytes){using var stream=zip.CreateEntry(path,CompressionLevel.NoCompression).Open();stream.Write(bytes);}
                Add("manifest.json",json);Add(PlanEntry,planBytes);Add(ModelEntry,model);
            }
            var bytes=memory.ToArray();if(bytes.Length>MaxPackageBytes)throw new InvalidDataException("资源包超过 36 MiB。");
            // Validate exactly the bytes to be stored, not a mutable caller-owned graph.
            return Store(bytes,ParseAsset(bytes,cancellation),cancellation);
        }
        private static ComponentAsset ReadExternal(ZipArchive zip,ComponentAssetManifest manifest,CancellationToken cancellation)
        {
            if(zip.Entries.Count!=3||zip.Entries.Any(e=>e.FullName!="manifest.json"&&e.FullName!=PlanEntry&&e.FullName!=ModelEntry))
                throw new InvalidDataException("v2 资源包只能包含清单、平面符号和自包含 GLB；不允许路径、脚本或额外附件。");
            if(manifest.GeometryMode!="ExternalStatic"||manifest.Units!="mm"||(manifest.Category!="DoorWindow"&&manifest.Category!="Furniture")
                ||!Guid.TryParseExact(manifest.AssetId,"D",out _)||manifest.Revision<1||!Text(manifest.Name,100))
                throw new InvalidDataException("外部资源版本、身份、类别或名称无效。");
            var definition=manifest.External;
            if(definition==null||!Text(definition.Code,64)||definition.Parts==null||definition.Parts.Count==0||definition.Parts.Count>256)
                throw new InvalidDataException("缺少有效编号或部件。");
            var planBytes=ReadEntry(zip.GetEntry(PlanEntry),ComponentPlanSymbols.MaxBytes,cancellation);
            var modelBytes=ReadEntry(zip.GetEntry(ModelEntry),ComponentGlbInspector.MaxBytes,cancellation);
            if(Hash(planBytes)!=definition.PlanSha256||Hash(modelBytes)!=definition.ModelSha256)
                throw new InvalidDataException("资源文件与清单哈希不一致。");
            var plan=ComponentPlanSymbols.Load(planBytes);
            if(plan.Code!=definition.Code||(manifest.Category=="Furniture")!=(plan.Category=="Furniture"))
                throw new InvalidDataException("平面类别/编号与资源清单不一致。");
            if(manifest.Category=="DoorWindow") {
                Validate(new ComponentAssetManifest {Name=manifest.Name,AssetId=manifest.AssetId,Revision=manifest.Revision,OpeningType=manifest.OpeningType});
                var type=manifest.OpeningType;
                if(type.Code!=definition.Code||Math.Abs(type.Width-plan.Width)>.01||Math.Abs(type.Height-plan.Height)>.01
                    ||(plan.Category=="Door")!=((type.Kind??"").Contains("门")))
                    throw new InvalidDataException("参数回退的编号、类别和洞口尺寸不一致。");
            } else if(manifest.OpeningType!=null)throw new InvalidDataException("家具不能附带门窗类型或开洞参数。");
            var mesh=ComponentGlbInspector.Inspect(modelBytes,cancellation);var volume=Align(mesh.Volume,definition.ModelFrame);
            if(!new[]{definition.Width,definition.Height,definition.Depth}.All(n=>double.IsFinite(n)&&n>0&&n<=100000)
                ||Math.Abs(definition.Width-plan.Width)>.01||Math.Abs(definition.Height-plan.Height)>.01
                ||Math.Abs(volume.Width-definition.Width)>1||Math.Abs(volume.Height-definition.Height)>1||Math.Abs(volume.Depth-definition.Depth)>1
                ||(plan.Category=="Furniture"&&Math.Abs(plan.Depth-definition.Depth)>1))
                throw new InvalidDataException("CAD 与三维尺寸不一致（容差 1 mm）；不会自动拉伸模型。");
            var nodeIds=new HashSet<int>(mesh.Nodes.Select(n=>n.Index));var bound=new HashSet<int>();var primitiveIds=new HashSet<int>();var partIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var part in definition.Parts) {
                if(part==null||!Guid.TryParseExact(part.PartId,"D",out _)||!partIds.Add(part.PartId)||!Text(part.Name,100)
                    ||!new[]{"Fixed","Frame","Panel","Hardware","Body"}.Contains(part.Role)
                    ||part.MeshNodes==null||part.MeshNodes.Count==0||part.MeshNodes.Count>256||part.PlanPrimitives==null||part.PlanPrimitives.Count>ComponentPlanSymbols.MaxPrimitives)
                    throw new InvalidDataException("部件身份、名称、角色或绑定无效。");
                if(part.SizeMode!="Fixed"||part.MotionKind!="Fixed")throw new InvalidDataException("当前仅发布固定尺寸/静态部件；可变尺寸和开闭驱动尚未开放。");
                foreach(var node in part.MeshNodes)if(!nodeIds.Contains(node)||!bound.Add(node))throw new InvalidDataException("三维节点不存在或重复绑定。");
                foreach(var index in part.PlanPrimitives)if(index<0||index>=plan.Primitives.Count||!primitiveIds.Add(index))throw new InvalidDataException("二维图元不存在或重复绑定。");
            }
            if(!bound.SetEquals(nodeIds))throw new InvalidDataException("存在未绑定的三维节点。");
            return new ComponentAsset {Manifest=manifest,ExternalContent=new ExternalComponentContent {Plan=plan,Mesh=mesh,Volume=volume}};
        }
        private static bool Text(string value,int limit)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=limit&&!value.Any(char.IsControl);
        private static void CheckJson(byte[] json)
        {
            using var document=JsonDocument.Parse(json,new JsonDocumentOptions {MaxDepth=32});
            void Visit(JsonElement element) {
                if(element.ValueKind==JsonValueKind.Object) {
                    var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach(var property in element.EnumerateObject()){if(!names.Add(property.Name))throw new InvalidDataException("清单含重复字段。");Visit(property.Value);}
                }else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())Visit(child);
            }
            Visit(document.RootElement);
        }
        private static byte[] ReadEntry(ZipArchiveEntry entry,int limit,CancellationToken cancellation)
        {
            if(entry==null||entry.Length>limit||((entry.ExternalAttributes>>16)&0xF000)==0xA000)
                throw new InvalidDataException("资源文件缺失、超限或是符号链接。");
            using var stream=entry.Open();using var memory=new MemoryStream();var buffer=new byte[8192];int count;
            while((count=stream.Read(buffer,0,buffer.Length))>0) {
                cancellation.ThrowIfCancellationRequested();if(memory.Length+count>limit)throw new InvalidDataException("资源解压数据超限。");memory.Write(buffer,0,count);
            }
            return memory.ToArray();
        }
        public static BuildingVolume Align(BuildingVolume volume,ComponentSourceFrame frame)
        {
            if(frame==null||!new[]{frame.X,frame.Y,frame.Z}.All(n=>double.IsFinite(n)&&Math.Abs(n)<=1000000)||(frame.AlongAxis!="X"&&frame.AlongAxis!="Y"))
                throw new InvalidDataException("三维基点/方向无效，方向只能是 X 或 Y。");
            var alongY=frame.AlongAxis=="Y";
            return Bounds(new BuildingVolume {Faces=volume.Faces.Select(f=>new VolumeFace {
                ElementId=f.ElementId,Kind=f.Kind,NormalX=alongY?f.NormalY:f.NormalX,NormalY=alongY?-f.NormalX:f.NormalY,NormalZ=f.NormalZ,
                Points=f.Points.Select(p=>new Point3DModel(alongY?p.Y-frame.Y:p.X-frame.X,alongY?-(p.X-frame.X):p.Y-frame.Y,p.Z-frame.Z)).ToList()}).ToList()});
        }
        internal static BuildingVolume Bounds(BuildingVolume volume)
        {
            if(volume.Faces.Count==0)return volume;
            var points=volume.Faces.SelectMany(f=>f.Points).ToArray();
            volume.MinX=points.Min(p=>p.X);volume.MaxX=points.Max(p=>p.X);volume.MinY=points.Min(p=>p.Y);volume.MaxY=points.Max(p=>p.Y);volume.MinZ=points.Min(p=>p.Z);volume.MaxZ=points.Max(p=>p.Z);return volume;
        }
        public static ComponentAsset RetainInProject(ComponentAsset asset,string modelPath,CancellationToken cancellation=default)
        {
            var full=Path.GetFullPath(modelPath);
            if(!File.Exists(full)||!string.Equals(Path.GetFileName(full),"model.json",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请先将建筑模型保存为 model.json，再保存项目资源副本。");
            // Reject reparse ancestors so a project resource copy cannot escape into a linked directory.
            var folder=Path.GetDirectoryName(full);var root=Path.Combine(folder,"components");
            for(var current=new DirectoryInfo(root);current!=null;current=current.Parent)
                if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("项目资源路径不能经过目录链接。");
            var bytes=ReadBoundedFile(asset.PackagePath,cancellation);var checkedAsset=ParseAsset(bytes,cancellation);
            if(checkedAsset.Manifest.AssetId!=asset.Manifest.AssetId||checkedAsset.Manifest.Revision!=asset.Manifest.Revision||Hash(bytes)!=asset.Sha256)
                throw new InvalidDataException("源资源已改变，请重新载入后再保存项目副本。");
            return new ComponentAssetLibrary(root).Store(bytes,checkedAsset,cancellation);
        }
    }
}
