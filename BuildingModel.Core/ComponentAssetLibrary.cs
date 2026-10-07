using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class ComponentAssetManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string AssetId { get; set; } = Guid.NewGuid().ToString("D");
        public int Revision { get; set; } = 1;
        public string Category { get; set; } = "DoorWindow";
        public string GeometryMode { get; set; } = "Parametric";
        public string Units { get; set; } = "mm";
        public string Name { get; set; }
        public OpeningTypeModel OpeningType { get; set; }
        public ExternalComponentDefinition External { get; set; }
    }

    public sealed class ComponentAsset
    {
        public ComponentAssetManifest Manifest { get; internal set; }
        public string PackagePath { get; internal set; }
        public string Sha256 { get; internal set; }
        public ExternalComponentContent ExternalContent { get; internal set; }
        public bool IsExternal => Manifest.SchemaVersion==2;
    }

    public sealed class ComponentLibrarySnapshot
    {
        public List<ComponentAsset> Assets { get; } = new List<ComponentAsset>();
        public List<string> Errors { get; } = new List<string>();
    }

    public sealed partial class ComponentAssetLibrary
    {
        public const int MaxManifestBytes = 1024 * 1024;
        public const int MaxPackageBytes = 36 * 1024 * 1024;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, MaxDepth = 32 };
        public string Root { get; }
        public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanLuo", "ComponentLibrary");

        public ComponentAssetLibrary(string root) { Root = Path.GetFullPath(root); }

        public ComponentLibrarySnapshot Load(CancellationToken cancellation = default,bool includeExternal=true)
        {
            var result = new ComponentLibrarySnapshot();
            if (!Directory.Exists(Root)) return result;
            foreach (var path in Directory.EnumerateFiles(Root, "*.wlopkg", SearchOption.TopDirectoryOnly))
            {
                cancellation.ThrowIfCancellationRequested();
                try {
                    if(!includeExternal) {
                        using var file=File.OpenRead(path);
                        if(file.Length>MaxPackageBytes)throw new InvalidDataException("资源包过大。");
                        using var zip=new ZipArchive(file,ZipArchiveMode.Read);
                        var json=ReadEntry(zip.GetEntry("manifest.json"),MaxManifestBytes,cancellation);CheckJson(json);
                        var manifest=JsonSerializer.Deserialize<ComponentAssetManifest>(json,Json);
                        // MM must not decode external meshes it cannot place yet.
                        if(manifest?.SchemaVersion==2)continue;
                    }
                    result.Assets.Add(Read(path, cancellation));
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException || ex is UnauthorizedAccessException || ex is ArgumentException)
                { result.Errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            return result;
        }

        public ComponentAsset Save(string name, OpeningTypeModel type, CancellationToken cancellation = default)
        {
            var manifest = new ComponentAssetManifest { Name = name, OpeningType = OpeningConstruction.Copy(type) };
            Validate(manifest);
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            if (json.Length > MaxManifestBytes) throw new InvalidDataException("参数数据超过资源包限制。");
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                using var entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
                entry.Write(json);
            }
            return Store(memory.ToArray(), manifest, cancellation);
        }

        public ComponentAsset Import(string path, CancellationToken cancellation = default)
        {
            var bytes = ReadBoundedFile(path, cancellation);
            var asset = ParseAsset(bytes, cancellation);
            return Store(bytes, asset, cancellation);
        }

        public void Export(ComponentAsset asset, string destination, CancellationToken cancellation = default)
        {
            var bytes = ReadBoundedFile(asset.PackagePath, cancellation);
            var manifest = ParseAsset(bytes, cancellation).Manifest;
            if (manifest.AssetId != asset.Manifest.AssetId || manifest.Revision != asset.Manifest.Revision || Hash(bytes) != asset.Sha256)
                throw new InvalidDataException("资源包已被修改，请重新载入图库。");
            var target = Path.GetFullPath(destination);
            if (target.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("导出位置不能覆盖公共库文件。");
            if (!string.Equals(Path.GetExtension(target), ".wlopkg", StringComparison.OrdinalIgnoreCase))
                throw new IOException("导出文件必须使用 .wlopkg 扩展名。");
            AtomicWrite(destination, bytes, true, cancellation);
        }

        private ComponentAsset Store(byte[] bytes, ComponentAssetManifest manifest, CancellationToken cancellation)
            => Store(bytes,new ComponentAsset {Manifest=manifest},cancellation);

        private ComponentAsset Store(byte[] bytes, ComponentAsset asset, CancellationToken cancellation)
        {
            var manifest=asset.Manifest;
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, Guid.Parse(manifest.AssetId).ToString("D") + "-v" + manifest.Revision + ".wlopkg");
            var hash = Hash(bytes);
            if (File.Exists(path))
            {
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("资源包不能是文件链接。");
                if (Hash(ReadBoundedFile(path, cancellation)) != hash)
                    throw new InvalidDataException("同一资源 ID 和修订已存在不同内容，不能覆盖。");
            }
            else AtomicWrite(path, bytes, false, cancellation);
            asset.PackagePath=path;asset.Sha256=hash;return asset;
        }

        public static ComponentAsset Read(string path, CancellationToken cancellation = default)
        {
            var bytes = ReadBoundedFile(path, cancellation);
            var asset=ParseAsset(bytes,cancellation);asset.PackagePath=Path.GetFullPath(path);asset.Sha256=Hash(bytes);return asset;
        }

        private static byte[] ReadBoundedFile(string path, CancellationToken cancellation)
        {
            using var file = File.OpenRead(path);
            if (file.Length > MaxPackageBytes) throw new InvalidDataException("资源包过大。");
            var bytes = new byte[checked((int)file.Length)];
            cancellation.ThrowIfCancellationRequested();
            file.ReadExactly(bytes);
            cancellation.ThrowIfCancellationRequested();
            return bytes;
        }

        private static ComponentAsset ParseAsset(byte[] bytes, CancellationToken cancellation)
        {
            using var memory = new MemoryStream(bytes, false);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            if(zip.Entries.Count<1||zip.Entries.Count>3||zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=zip.Entries.Count)
                throw new InvalidDataException("资源包条目或重复路径无效。");
            var entry=zip.Entries.SingleOrDefault(e=>e.FullName=="manifest.json")??throw new InvalidDataException("缺少资源清单。");
            var json=ReadEntry(entry,MaxManifestBytes,cancellation);CheckJson(json);
            var manifest = JsonSerializer.Deserialize<ComponentAssetManifest>(json, Json);
            if (manifest == null) throw new InvalidDataException("缺少资源清单。");
            if(manifest.SchemaVersion==2)return ReadExternal(zip,manifest,cancellation);
            if(zip.Entries.Count!=1||bytes.Length>2*1024*1024||manifest.External!=null)
                throw new InvalidDataException("v1 参数资源包不能包含外部文件或外部规则。");
            Validate(manifest);return new ComponentAsset {Manifest=manifest};
        }

        private static void Validate(ComponentAssetManifest manifest)
        {
            if (manifest.SchemaVersion != 1 || manifest.Category != "DoorWindow" || manifest.GeometryMode != "Parametric" || manifest.Units != "mm")
                throw new InvalidDataException("不支持该资源版本、类型、几何模式或单位。");
            if (!Guid.TryParseExact(manifest.AssetId, "D", out _) || manifest.Revision < 1)
                throw new InvalidDataException("资源 ID 或修订无效。");
            if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 || manifest.Name.Any(char.IsControl))
                throw new InvalidDataException("资源名称应为 1～100 个非控制字符。");
            var type = manifest.OpeningType;
            if (type == null || string.IsNullOrWhiteSpace(type.Code) || type.Code.Length > 64 || type.Code.Any(char.IsControl))
                throw new InvalidDataException("缺少有效门窗编号。");
            if (!double.IsFinite(type.Width) || !double.IsFinite(type.Height) || type.Width <= 0 || type.Height <= 0 || type.Width > 100000 || type.Height > 100000)
                throw new InvalidDataException("洞口尺寸无效或超限。");
            if (!double.IsFinite(type.PlanOpenAngle) || type.PlanOpenAngle < 0 || type.PlanOpenAngle > 180)
                throw new InvalidDataException("平面开启角度应为 0～180°。");
            foreach (var property in typeof(OpeningTypeModel).GetProperties())
            {
                var value = property.GetValue(type);
                if (value is double number && !double.IsFinite(number)) throw new InvalidDataException("门窗参数包含无效数值。");
                if (value is string text && text.Length > 128000) throw new InvalidDataException("分格参数超限。");
            }
            var cells = DoorWindowElevationGeometryBuilder.ParseCellLayout(type.CustomCellLayout);
            var columns = DoorWindowElevationGeometryBuilder.ParseRatios(type.CustomColumnRatios);
            var rows = DoorWindowElevationGeometryBuilder.ParseRatios(type.CustomRowRatios);
            if (cells.Count > 128 || columns.Count > 32 || rows.Count > 32 || columns.Count * rows.Count > 128)
                throw new InvalidDataException("分格数量超限。");
            if (cells.SelectMany(c => new[] { c.Left, c.Right, c.Bottom, c.Top }).Any(n => !double.IsFinite(n))
                || columns.Concat(rows).Any(n => !double.IsFinite(n)))
                throw new InvalidDataException("分格坐标或比例无效。");
            var opening = PlanEditing.CreateOpening(type.Kind, "validation", type.Width / 2);
            PlanEditing.ApplyType(opening, type); opening.Sill = type.Sill; opening.ThresholdHeight = type.ThresholdHeight; opening.PlanOpenAngle = type.PlanOpenAngle;
            var error = OpeningConstruction.Validate(opening, type);
            if (error != null) throw new InvalidDataException(error);
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static void AtomicWrite(string path, byte[] bytes, bool overwrite, CancellationToken cancellation)
        {
            path = Path.GetFullPath(path);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { cancellation.ThrowIfCancellationRequested(); file.Write(bytes); file.Flush(true); }
                cancellation.ThrowIfCancellationRequested(); File.Move(temp, path, overwrite);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
