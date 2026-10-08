using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class ComponentAuthorDraft
    {
        public int SchemaVersion { get; set; } = 1;
        public string Name { get; set; } = "";
        public string OriginX { get; set; } = "0";
        public string OriginY { get; set; } = "0";
        public string OriginZ { get; set; } = "0";
        public string AlongAxis { get; set; } = "X";
        public string BasedOnAssetId { get; set; }
        public int BasedOnRevision { get; set; }
        public List<ComponentPartDefinition> Parts { get; set; } = new List<ComponentPartDefinition>();
        [JsonIgnore] public ComponentPlanSymbol Plan { get; set; }
        [JsonIgnore] public byte[] ModelBytes { get; set; }
    }
    public sealed partial class ComponentAssetLibrary
    {
        private static void ValidateDraft(ComponentAuthorDraft draft)
        {
            if(draft==null||draft.SchemaVersion!=1||draft.Name==null||draft.Name.Length>100||draft.Name.Any(char.IsControl)
                ||new[]{draft.OriginX,draft.OriginY,draft.OriginZ}.Any(t=>t==null||t.Length>64||t.Any(char.IsControl))
                ||(draft.AlongAxis!="X"&&draft.AlongAxis!="Y")||draft.Parts==null||draft.Parts.Count>256
                ||draft.BasedOnRevision<0||(draft.BasedOnAssetId==null?draft.BasedOnRevision!=0:!Guid.TryParseExact(draft.BasedOnAssetId,"D",out _)))
                throw new InvalidDataException("草稿版本或字段无效。");
            foreach(var part in draft.Parts)
                if(part==null||part.Name==null||part.Name.Length>100||part.Name.Any(char.IsControl)||part.PartId?.Length>64||part.Role?.Length>64
                    ||part.PlanPartId?.Length>64||part.SizeMode?.Length>64||part.MotionKind?.Length>64||part.MeshNodes==null||part.MeshNodes.Count>256||part.PlanPrimitives==null||part.PlanPrimitives.Count>ComponentPlanSymbols.MaxPrimitives)
                    throw new InvalidDataException("草稿部件数量或字段超限。");
        }
        public static void SaveDraft(string path,ComponentAuthorDraft draft,CancellationToken cancellation=default)
        {
            if(!string.Equals(Path.GetExtension(path),".wlodraft",StringComparison.OrdinalIgnoreCase))throw new IOException("草稿须保存为 .wlodraft，不能覆盖源模型或资源包。");
            ValidateDraft(draft);var json=JsonSerializer.SerializeToUtf8Bytes(draft,Json);
            if(json.Length>MaxManifestBytes)throw new InvalidDataException("草稿清单超过限制。");
            byte[] plan=null;if(draft.Plan!=null)plan=ComponentPlanSymbols.Bytes(draft.Plan);
            if(draft.ModelBytes!=null)ComponentGlbInspector.Inspect(draft.ModelBytes,cancellation);
            using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)) {
                void Add(string name,byte[] bytes){using var stream=zip.CreateEntry(name,CompressionLevel.NoCompression).Open();stream.Write(bytes);}
                Add("draft.json",json);if(plan!=null)Add(PlanEntry,plan);if(draft.ModelBytes!=null)Add(ModelEntry,draft.ModelBytes);
            }
            if(memory.Length>MaxPackageBytes)throw new InvalidDataException("草稿超过 36 MiB。");
            AtomicWrite(path,memory.ToArray(),true,cancellation);
        }
        public static ComponentAuthorDraft LoadDraft(string path,CancellationToken cancellation=default)
        {
            if(!string.Equals(Path.GetExtension(path),".wlodraft",StringComparison.OrdinalIgnoreCase))throw new IOException("请选择 .wlodraft 草稿。");
            var bytes=ReadBoundedFile(path,cancellation);using var memory=new MemoryStream(bytes,false);using var zip=new ZipArchive(memory,ZipArchiveMode.Read);
            if(zip.Entries.Count<1||zip.Entries.Count>3||zip.Entries.Any(e=>e.FullName!="draft.json"&&e.FullName!=PlanEntry&&e.FullName!=ModelEntry)
                ||zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=zip.Entries.Count)
                throw new InvalidDataException("草稿条目无效或包含额外文件。");
            var json=ReadEntry(zip.GetEntry("draft.json"),MaxManifestBytes,cancellation);CheckJson(json);
            var draft=JsonSerializer.Deserialize<ComponentAuthorDraft>(json,Json);ValidateDraft(draft);
            var plan=zip.GetEntry(PlanEntry);if(plan!=null)draft.Plan=ComponentPlanSymbols.Load(ReadEntry(plan,ComponentPlanSymbols.MaxBytes,cancellation));
            var model=zip.GetEntry(ModelEntry);if(model!=null){draft.ModelBytes=ReadEntry(model,ComponentGlbInspector.MaxBytes,cancellation);ComponentGlbInspector.Inspect(draft.ModelBytes,cancellation);}
            return draft;
        }
    }
}
