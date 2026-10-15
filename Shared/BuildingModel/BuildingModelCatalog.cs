using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class BuildingModelDrawingLink
    {
        public string Fingerprint { get; set; }
        public string Path { get; set; }
    }
    public sealed class BuildingModelCatalogRecord
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public List<BuildingModelDrawingLink> Drawings { get; set; } = new List<BuildingModelDrawingLink>();
    }
    public sealed class BuildingModelCatalogEntry
    {
        public string Id { get; set; }
        public string ModelPath { get; set; }
        public string Name { get; set; }
        public int Storeys { get; set; }
        public DateTime Modified { get; set; }
        public bool IsCurrent { get; set; }
        public bool IsOpen { get; set; }
        public string Error { get; set; }
        public List<BuildingModelDrawingLink> Drawings { get; set; } = new List<BuildingModelDrawingLink>();
        public bool Available => string.IsNullOrWhiteSpace(Error);
        public string Status => !Available ? "文件不可用" : IsCurrent ? "当前模型" : IsOpen ? "已打开" : "已保存";
        public string ModifiedText => Modified == default(DateTime) ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
        public string DrawingText => string.Join("、",Drawings.Select(d=>System.IO.Path.GetFileName(d.Path ?? "")).Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct());
    }
    public static class BuildingModelCatalog
    {
        [DataContract] private sealed class Index
        {
            [DataMember] public int SchemaVersion { get; set; } = 1;
            [DataMember] public List<BuildingModelCatalogRecord> Models { get; set; } = new List<BuildingModelCatalogRecord>();
        }
        [DataContract] private sealed class RegistryHeader { [DataMember] public List<CaptureHeader> Floors {get;set;} }
        [DataContract] private sealed class CaptureHeader { [DataMember] public DrawingHeader Probe {get;set;} }
        [DataContract] private sealed class DrawingHeader { [DataMember] public string DrawingFingerprint {get;set;} [DataMember] public string DrawingPath {get;set;} }

        public static string FilePath(string project) => System.IO.Path.Combine(System.IO.Path.GetFullPath(project),StudioLaunch.ModelFolderName,"模型目录.json");
        public static string InferProject(string modelPath)
        {
            if(string.IsNullOrWhiteSpace(modelPath))return null;
            var folder=System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(modelPath));
            var models=System.IO.Path.GetDirectoryName(folder);
            return string.Equals(System.IO.Path.GetFileName(models),StudioLaunch.ModelFolderName,StringComparison.OrdinalIgnoreCase)
                ? System.IO.Path.GetDirectoryName(models) : null;
        }
        public static List<BuildingModelCatalogEntry> List(string project,string currentPath=null)
        {
            if(string.IsNullOrWhiteSpace(project))return new List<BuildingModelCatalogEntry>();
            project=System.IO.Path.GetFullPath(project);
            var records=Read(project).Models;
            var paths=new Dictionary<string,BuildingModelCatalogRecord>(StringComparer.OrdinalIgnoreCase);
            foreach(var record in records) { var path=Resolve(project,record.Path);if(!paths.ContainsKey(path))paths.Add(path,record); }
            var models=System.IO.Path.Combine(project,StudioLaunch.ModelFolderName);
            if(Directory.Exists(models))foreach(var folder in Directory.GetDirectories(models))
            {
                if(System.IO.Path.GetFileName(folder).StartsWith(".",StringComparison.Ordinal))continue;
                var path=System.IO.Path.Combine(folder,"model.json");
                if(File.Exists(path)&&!paths.ContainsKey(path))paths.Add(path,new BuildingModelCatalogRecord { Id=Identity(project,path),Path=Store(project,path) });
            }
            if(!string.IsNullOrWhiteSpace(currentPath))
            {
                currentPath=System.IO.Path.GetFullPath(currentPath);
                if(!paths.ContainsKey(currentPath))paths.Add(currentPath,new BuildingModelCatalogRecord { Id=Identity(project,currentPath),Path=Store(project,currentPath) });
            }
            var entries=new List<BuildingModelCatalogEntry>();
            foreach(var pair in paths)
            {
                var entry=new BuildingModelCatalogEntry { Id=pair.Value.Id,ModelPath=pair.Key,
                    Name=System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(pair.Key)),
                    IsCurrent=string.Equals(pair.Key,currentPath,StringComparison.OrdinalIgnoreCase),IsOpen=StudioLaunch.HasLiveSession(pair.Key),
                    Drawings=(pair.Value.Drawings??new List<BuildingModelDrawingLink>()).ToList() };
                try
                {
                    var model=BuildingModelJson.LoadModel(pair.Key);
                    entry.Name=string.IsNullOrWhiteSpace(model.Name)?entry.Name:model.Name;
                    entry.Storeys=model.Storeys.Count;entry.Modified=File.GetLastWriteTime(pair.Key);
                    foreach(var link in RegisteredDrawings(pair.Key))AddLink(entry.Drawings,link);
                }
                catch(Exception ex) when(ex is IOException || ex is ArgumentException || ex is SerializationException || ex is InvalidOperationException)
                { entry.Error=ex.Message; }
                entries.Add(entry);
            }
            return entries.OrderByDescending(e=>e.IsCurrent).ThenBy(e=>e.Name,StringComparer.CurrentCulture).ToList();
        }
        public static void Register(string project,string path,string fingerprint=null,string drawingPath=null)
        {
            if(string.IsNullOrWhiteSpace(project))return;
            path=System.IO.Path.GetFullPath(path);
            BuildingModelJson.LoadModel(path);
            var file=FilePath(project);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            using(var gate=new Mutex(false,"WanLuoModelCatalog-"+Hash(file)))
            {
                try { if(!gate.WaitOne(TimeSpan.FromSeconds(5)))throw new IOException("模型清单正在更新，请稍后重试。"); }
                catch(AbandonedMutexException) { }
                try
                {
                    var index=Read(project);
                    var record=index.Models.FirstOrDefault(r=>string.Equals(Resolve(project,r.Path),path,StringComparison.OrdinalIgnoreCase));
                    if(record==null) { record=new BuildingModelCatalogRecord {Id=Identity(project,path),Path=Store(project,path)};index.Models.Add(record); }
                    record.Drawings=record.Drawings??new List<BuildingModelDrawingLink>();
                    foreach(var link in RegisteredDrawings(path))AddLink(record.Drawings,link);
                    if(!string.IsNullOrWhiteSpace(fingerprint))AddLink(record.Drawings,new BuildingModelDrawingLink {Fingerprint=fingerprint,Path=drawingPath});
                    var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try
                    {
                        using(var stream=File.Create(temp))new DataContractJsonSerializer(typeof(Index)).WriteObject(stream,index);
                        StudioLaunch.ReplaceMetadata(temp,file,file+".bak");
                    }
                    finally {if(File.Exists(temp))File.Delete(temp);}
                }
                finally {gate.ReleaseMutex();}
            }
        }
        public static bool LinkedTo(BuildingModelCatalogEntry entry,string fingerprint)
            => !string.IsNullOrWhiteSpace(fingerprint)&&entry.Drawings.Any(d=>string.Equals(d.Fingerprint,fingerprint,StringComparison.OrdinalIgnoreCase));
        internal static string Store(string project,string path)
        {
            var root=System.IO.Path.GetFullPath(project).TrimEnd(System.IO.Path.DirectorySeparatorChar)+System.IO.Path.DirectorySeparatorChar;
            path=System.IO.Path.GetFullPath(path);
            return path.StartsWith(root,StringComparison.OrdinalIgnoreCase)?path.Substring(root.Length):path;
        }
        internal static string Resolve(string project,string path)
        {
            if(string.IsNullOrWhiteSpace(path))throw new InvalidDataException("模型清单包含空路径。");
            if(System.IO.Path.IsPathRooted(path))return System.IO.Path.GetFullPath(path);
            var root=System.IO.Path.GetFullPath(project).TrimEnd(System.IO.Path.DirectorySeparatorChar)+System.IO.Path.DirectorySeparatorChar;
            var resolved=System.IO.Path.GetFullPath(System.IO.Path.Combine(root,path));
            if(!resolved.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("模型相对路径越出项目目录。");
            return resolved;
        }
        private static Index Read(string project)
        {
            var file=FilePath(project);if(!File.Exists(file))return new Index();
            Index ReadFile(string path)
            {
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                {
                    var index=(Index)new DataContractJsonSerializer(typeof(Index)).ReadObject(stream);
                    if(index?.SchemaVersion!=1||index.Models==null||index.Models.Any(r=>r==null||string.IsNullOrWhiteSpace(r.Id)
                        ||string.IsNullOrWhiteSpace(r.Path)||(r.Drawings!=null&&r.Drawings.Any(d=>d==null||string.IsNullOrWhiteSpace(d.Fingerprint)))))
                        throw new InvalidDataException("模型清单格式无效。");
                    return index;
                }
            }
            try {return ReadFile(file);}
            catch(Exception ex) when(ex is IOException||ex is SerializationException)
            {
                if(File.Exists(file+".bak"))return ReadFile(file+".bak");
                throw new InvalidDataException("模型清单无法读取，原文件已保留："+file,ex);
            }
        }
        private static List<BuildingModelDrawingLink> RegisteredDrawings(string modelPath)
        {
            var file=modelPath+".cad-floors.json";var result=new List<BuildingModelDrawingLink>();
            if(!File.Exists(file))return result;
            try
            {
                using(var stream=File.OpenRead(file))
                {
                    var header=(RegistryHeader)new DataContractJsonSerializer(typeof(RegistryHeader)).ReadObject(stream);
                    foreach(var floor in header?.Floors??new List<CaptureHeader>())
                        if(!string.IsNullOrWhiteSpace(floor?.Probe?.DrawingFingerprint))AddLink(result,new BuildingModelDrawingLink {Fingerprint=floor.Probe.DrawingFingerprint,Path=floor.Probe.DrawingPath});
                }
            }
            catch(Exception ex) when(ex is IOException||ex is SerializationException) { }
            return result;
        }
        private static void AddLink(List<BuildingModelDrawingLink> links,BuildingModelDrawingLink link)
        {
            var existing=links.FirstOrDefault(d=>string.Equals(d.Fingerprint,link.Fingerprint,StringComparison.OrdinalIgnoreCase));
            if(existing==null)links.Add(link);
            else if(!string.IsNullOrWhiteSpace(link.Path))existing.Path=link.Path;
        }
        private static string Identity(string project,string path)=>Hash(Store(project,path).ToUpperInvariant());
        internal static string Hash(string value)
        {
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").Substring(0,32);
        }
    }
}
