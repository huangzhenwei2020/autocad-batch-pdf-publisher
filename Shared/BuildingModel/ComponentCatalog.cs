using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class ComponentCatalogRecord
    {
        public int SchemaVersion { get; set; } = 1;
        public string AssetId { get; set; }
        public int Version { get; set; }
        public ComponentPlanSymbol Plan { get; set; }
        public string PlanHash { get; set; }
        public int ModelRevision { get; set; }
        public string ModelHash { get; set; }
        public string ModelPlanHash { get; set; }
        public bool IsModelCurrent => ModelRevision > 0 && ModelPlanHash == PlanHash;
        public override string ToString() => Plan.Code + " · " + Plan.Name;
    }

    public sealed class ComponentCatalogSnapshot
    {
        public List<ComponentCatalogRecord> Records { get; } = new List<ComponentCatalogRecord>();
        public List<string> Errors { get; } = new List<string>();
    }

    // Both CAD (.NET Framework) and Studio use this exact contract and atomic head records.
    public sealed class ComponentCatalog
    {
        public string Root { get; }
        public string RecordsPath => Path.Combine(Root, "records");
        public static string DefaultRoot => Environment.GetEnvironmentVariable("WANLUO_COMPONENT_LIBRARY_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanLuo", "ComponentLibrary");
        public ComponentCatalog(string root = null) { Root = Path.GetFullPath(root ?? DefaultRoot); }
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private string PathFor(string id)
        {
            Guid guid;
            if (!Guid.TryParseExact(id, "D", out guid)) throw new InvalidDataException("图库资源标识无效。");
            return Path.Combine(RecordsPath, guid.ToString("D") + ".json");
        }
        private static void SafeDirectory(string path)
        {
            for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("图库路径不允许包含链接目录。");
        }
        public ComponentCatalogSnapshot Load()
        {
            SafeDirectory(RecordsPath);
            var result = new ComponentCatalogSnapshot();
            if (!Directory.Exists(RecordsPath)) return result;
            foreach (var path in Directory.EnumerateFiles(RecordsPath, "*.json")) {
                try { var record = Read(path); if (Path.GetFileName(path) != record.AssetId + ".json") throw new InvalidDataException("资源标识与文件不一致。"); result.Records.Add(record); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException || ex is ArgumentException)
                { result.Errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            return result;
        }
        public ComponentCatalogRecord Get(string id) { SafeDirectory(RecordsPath); return Read(PathFor(id)); }
        private static bool Digest(string value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
        private static void Validate(ComponentCatalogRecord record)
        {
            Guid id;
            if (record == null || record.SchemaVersion != 1 || record.Version < 1 || !Guid.TryParseExact(record.AssetId, "D", out id))
                throw new InvalidDataException("图库记录版本或标识无效。");
            ComponentPlanSymbols.Validate(record.Plan);
            if (record.PlanHash != Hash(ComponentPlanSymbols.Bytes(record.Plan)) || record.ModelRevision < 0
                || (record.ModelRevision > 0 && (!Digest(record.ModelHash) || !Digest(record.ModelPlanHash))))
                throw new InvalidDataException("图库平面或模型关联校验失败。");
        }
        private static ComponentCatalogRecord Read(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("图库记录不能是文件链接。");
            using (var file = File.OpenRead(path)) {
                if (file.Length > 3 * 1024 * 1024) throw new InvalidDataException("图库记录过大。");
                var record = (ComponentCatalogRecord)Serializer().ReadObject(file);
                Validate(record); return record;
            }
        }
        private FileStream Lock()
        {
            SafeDirectory(RecordsPath); Directory.CreateDirectory(RecordsPath);
            var path = Path.Combine(Root, ".catalog.lock");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("图库锁文件不能是链接。");
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new IOException("图库正在被另一窗口更新，请稍后重试。"); }
        }
        public ComponentCatalogRecord SavePlan(ComponentPlanSymbol plan, string id = null, int expectedVersion = 0)
            => SavePlanCore(plan,id,expectedVersion,0,null);
        public ComponentCatalogRecord ImportPair(ComponentPlanSymbol plan,string id,int revision,string modelHash)
        {
            if(revision<1||!Digest(modelHash))throw new InvalidDataException("旧资源的模型关联无效。");
            return SavePlanCore(plan,id,0,revision,modelHash);
        }
        private ComponentCatalogRecord SavePlanCore(ComponentPlanSymbol plan,string id,int expectedVersion,int modelRevision,string modelHash)
        {
            plan = ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(plan));
            var hash = Hash(ComponentPlanSymbols.Bytes(plan));
            using (Lock()) {
                var records = Load();
                if (records.Errors.Count > 0) throw new InvalidDataException("请先处理图库损坏记录：" + records.Errors[0]);
                var existing = id == null ? null : records.Records.FirstOrDefault(r => r.AssetId == id);
                if(modelRevision>0&&existing!=null)return existing;
                if ((existing?.Version ?? 0) != expectedVersion) throw new IOException("图库已被修改，请刷新后重试。");
                if (records.Records.Any(r => r.AssetId != id && r.Plan.Category == plan.Category && string.Equals(r.Plan.Code, plan.Code, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("该类别已有此编号，请选择原记录更新，或使用新编号。");
                if (existing != null && existing.PlanHash == hash) return existing;
                var record = existing ?? new ComponentCatalogRecord { AssetId = id ?? Guid.NewGuid().ToString("D") };
                record.Plan = plan; record.PlanHash = hash; record.Version = checked(record.Version + 1);
                if(modelRevision>0){record.ModelRevision=modelRevision;record.ModelHash=modelHash;record.ModelPlanHash=hash;}
                Write(record); return record;
            }
        }
        public ComponentCatalogRecord AttachModel(ComponentCatalogRecord expected, int revision, string packageHash)
        {
            using (Lock()) {
                var record = Get(expected.AssetId);
                if (record.Version != expected.Version || record.PlanHash != expected.PlanHash)
                    throw new IOException("CAD 平面已更新，请重新打开该资源后补充模型。");
                if (revision < 1 || !Digest(packageHash)) throw new InvalidDataException("模型关联无效。");
                record.ModelRevision = revision; record.ModelHash = packageHash; record.ModelPlanHash = record.PlanHash;
                record.Version = checked(record.Version + 1); Write(record); return record;
            }
        }
        private void Write(ComponentCatalogRecord record)
        {
            Validate(record); var path = PathFor(record.AssetId);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    Serializer().WriteObject(file, record); file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static DataContractJsonSerializer Serializer()=>new DataContractJsonSerializer(typeof(ComponentCatalogRecord),new DataContractJsonSerializerSettings {MaxItemsInObjectGraph=200000});
    }
}
