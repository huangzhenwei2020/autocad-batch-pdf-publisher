using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class CadStoreyUpdateRequest
    {
        public string Id { get; set; }
        public string CreatedUtc { get; set; }
        public string ExpectedStoreysHash { get; set; }
        public List<StoreyModel> Storeys { get; set; }
    }

    public static class BuildingModelProjectWorkflow
    {
        public static BuildingModelDocument Empty(string name)
        {
            ValidateName(name);
            return new BuildingModelDocument { Name = name.Trim(), Storeys = new List<StoreyModel> {
                new StoreyModel { Id = "1F", Name = "一层", Elevation = 0, Height = 3300 } } };
        }
        public static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.Trim().TrimEnd('.') != name.Trim() || name.Trim() == "." || name.Trim() == "..")
                throw new ArgumentException("请输入有效模型名称（最多 80 字，不能包含路径符号或以点结尾）。");
        }

        public static void ValidateStoreys(List<StoreyModel> floors)
        {
            if (floors == null || floors.Count == 0 || floors.Count > 500 || floors.Any(s => s == null
                || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name)
                || double.IsNaN(s.Height) || double.IsInfinity(s.Height) || s.Height <= 0
                || double.IsNaN(s.Elevation) || double.IsInfinity(s.Elevation)
                || !Enum.IsDefined(typeof(StoreyKind), s.Kind)))
                throw new ArgumentException("请保留有效楼层，填写名称及大于 0 的层高（最多 500 层）。");
            if (floors.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new ArgumentException("楼层 ID 重复。");
            foreach (var s in floors.Where(s => !string.IsNullOrWhiteSpace(s.TemplateStoreyId)))
            {
                var source = floors.FirstOrDefault(f => string.Equals(f.Id, s.TemplateStoreyId, StringComparison.OrdinalIgnoreCase));
                if (source == null || source == s || source.Kind != StoreyKind.Normal || s.Kind != StoreyKind.Normal
                    || !string.IsNullOrWhiteSpace(source.TemplateStoreyId))
                    throw new ArgumentException("标准层应引用另一独立普通层，不能形成引用链。");
            }
        }

        public static void CreateNew(string path, BuildingModelDocument model)
        {
            ValidateStoreys(model.Storeys);
            ValidateName(model.Name);
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WithGate(path, () => {
                if (File.Exists(path) || StudioLaunch.HasLiveSession(path))
                    throw new IOException("此模型已存在，请换一个名称或路径；新建不会覆盖已有模型。");
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, BuildingModelJson.ToJson(model), new UTF8Encoding(false));
                    File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            });
        }

        public static string StoreysHash(BuildingModelDocument model)
        {
            var json = BuildingModelJson.ToJson(new BuildingModelDocument { Storeys = model.Storeys });
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", "");
        }
        public static string ModelFileHash(string path)
        {
            using(var stream=File.OpenRead(path))using(var hash=SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");
        }
        public static string ReplaceExisting(string path,BuildingModelDocument model,string expectedHash)
        {
            ValidateName(model.Name);ValidateStoreys(model.Storeys);
            path=Path.GetFullPath(path);
            var project=BuildingModelCatalog.InferProject(path);
            if(project==null||!string.Equals(path,BuildingModelJson.ModelFilePath(project,model.Name),StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("替换模型须使用项目内同名的独立模型目录。");
            if(string.IsNullOrWhiteSpace(expectedHash))throw new ArgumentException("替换前必须核对原模型。");
            var folder=Path.GetDirectoryName(path);var parent=Path.GetDirectoryName(folder);
            var suffix=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N");
            var backup=Path.Combine(parent,".model-replaced-"+model.Name.Trim()+"-"+suffix);
            var stage=Path.Combine(parent,".model-new-"+suffix);
            // All directory moves stay among verified siblings in the model root.
            if(Path.GetDirectoryName(backup)!=parent||Path.GetDirectoryName(stage)!=parent)
                throw new IOException("模型备份路径无效。");
            WithGate(path,()=>
            {
                if(StudioLaunch.HasLiveSession(path))throw new IOException("旧模型仍在三维窗口中打开，请保存并关闭该模型后再替换。");
                if(ModelFileHash(path)!=expectedHash)throw new IOException("旧模型已在其他窗口修改，请重新确认替换；当前草稿保留。");
                if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0
                    ||Directory.EnumerateFileSystemEntries(folder,"*",SearchOption.AllDirectories)
                        .Any(item=>(File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0))
                    throw new IOException("模型目录包含链接，未替换；请另建模型或先检查目录。");
                Directory.CreateDirectory(stage);
                try
                {
                    File.WriteAllText(Path.Combine(stage,"model.json"),BuildingModelJson.ToJson(model),new UTF8Encoding(false));
                    if(StudioLaunch.HasLiveSession(path)||ModelFileHash(path)!=expectedHash)
                        throw new IOException("旧模型的状态已改变，请重新确认替换。");
                    Directory.Move(folder,backup);
                    try{Directory.Move(stage,folder);}
                    catch(Exception commitError)
                    {
                        try
                        {
                            if(Directory.Exists(folder))throw new IOException("目标目录在替换期间发生变化。");
                            Directory.Move(backup,folder);
                        }
                        catch(Exception restoreError)
                        {throw new IOException("替换未完成，旧模型完整保留在备份目录："+backup,new AggregateException(commitError,restoreError));}
                        throw;
                    }
                }
                finally
                {
                    if(Directory.Exists(stage))
                    {
                        File.Delete(Path.Combine(stage,"model.json"));
                        Directory.Delete(stage,false);
                    }
                }
            });
            return backup;
        }
        public static void CreateCopy(string sourcePath,string targetPath,BuildingModelDocument snapshot,string name)
        {
            ValidateName(name);ValidateStoreys(snapshot.Storeys);
            targetPath=Path.GetFullPath(targetPath);
            if(!string.Equals(Path.GetFileName(targetPath),"model.json",StringComparison.OrdinalIgnoreCase)
                ||BuildingModelCatalog.InferProject(targetPath)==null)throw new ArgumentException("另存模型须使用独立的 建筑模型/<名称>/model.json 目录。");
            var folder=Path.GetDirectoryName(targetPath);var parent=Path.GetDirectoryName(folder);
            if(!string.Equals(Path.GetFileName(folder),name.Trim(),StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("另存目录必须与模型名称一致。");
            var stage=Path.Combine(parent,".model-copy-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(parent);
            WithGate(targetPath,()=> {
                if(Directory.Exists(folder)||StudioLaunch.HasLiveSession(targetPath))throw new IOException("目标模型已存在，请使用其他名称。");
                try
                {
                    Directory.CreateDirectory(stage);
                    var copy=BuildingModelJson.FromJson(BuildingModelJson.ToJson(snapshot));copy.Name=name.Trim();
                    BuildingModelJson.SaveModel(Path.Combine(stage,"model.json"),copy);
                    if(!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        var sourceRegistry=CadFloorPlanRegistry.FilePath(sourcePath);
                        if(File.Exists(sourceRegistry))
                        {
                            var registry=CadFloorPlanRegistry.Load(sourcePath).Clone();registry.ModelPath=targetPath;
                            foreach(var capture in registry.Floors)capture.Floor.ModelPath=targetPath;
                            foreach(var datum in registry.Datums??new List<CadFloorRegistrationContext>())datum.ModelPath=targetPath;
                            Write(Path.Combine(stage,"model.json.cad-floors.json"),registry,typeof(CadFloorPlanRegistry));
                        }
                        var library=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath)),"openings.json");
                        if(File.Exists(library)) {BuildingModelJson.LoadOpeningLibrary(library);File.Copy(library,Path.Combine(stage,"openings.json"));}
                    }
                    Directory.Move(stage,folder);
                }
                finally
                {
                    if(Directory.Exists(stage)&&Path.GetDirectoryName(stage)==parent&&Path.GetFileName(stage).StartsWith(".model-copy-",StringComparison.Ordinal))Directory.Delete(stage,true);
                }
            });
        }
        public static string RequestPath(string path) => path + ".cad-storeys.json";
        public static CadStoreyUpdateRequest Queue(string path, string expectedHash, List<StoreyModel> floors)
        {
            ValidateStoreys(floors);
            var request = new CadStoreyUpdateRequest { Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTime.UtcNow.ToString("O"),
                ExpectedStoreysHash = expectedHash, Storeys = floors };
            WithGate(path, () => {
                if (StoreysHash(BuildingModelJson.LoadModel(path)) != expectedHash)
                    throw new IOException("楼层已在其他窗口修改，请刷新楼层后重试。");
                if (File.Exists(RequestPath(path)))
                {
                    var pending=Load(path);DateTime created;
                    if(DateTime.TryParse(pending.CreatedUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out created)
                        && (DateTime.UtcNow-created.ToUniversalTime()).TotalMinutes>2)File.Delete(RequestPath(path));
                    else throw new IOException("已有楼层更新正在处理，请稍后重试。");
                }
                Write(RequestPath(path), request, typeof(CadStoreyUpdateRequest));
            });
            return request;
        }
        public static CadStoreyUpdateRequest Load(string path)
        {
            var file = RequestPath(path);
            if (new FileInfo(file).Length > 512 * 1024) throw new InvalidDataException("楼层更新请求过大。");
            using (var stream = File.OpenRead(file)) return (CadStoreyUpdateRequest)new DataContractJsonSerializer(typeof(CadStoreyUpdateRequest)).ReadObject(stream);
        }
        public static void ValidateRequest(CadStoreyUpdateRequest request, BuildingModelDocument model)
        {
            DateTime created;
            if (request == null || string.IsNullOrWhiteSpace(request.Id)
                || !DateTime.TryParse(request.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out created)
                || (DateTime.UtcNow - created.ToUniversalTime()).TotalMinutes > 2
                || created.ToUniversalTime() > DateTime.UtcNow.AddMinutes(1))
                throw new InvalidDataException("楼层更新请求已过期，请在 CAD 中重新应用。");
            ValidateStoreys(request.Storeys);
            if (StoreysHash(model) != request.ExpectedStoreysHash)
                throw new InvalidDataException("模型窗口中的楼层已修改，请先保存并在 CAD 刷新后重试。");
        }
        public static CadModelGenerationResult Result(string path)
        {
            var file = path + ".cad-storeys-result.json";
            if (!File.Exists(file)) return null;
            using (var stream = File.OpenRead(file)) return (CadModelGenerationResult)new DataContractJsonSerializer(typeof(CadModelGenerationResult)).ReadObject(stream);
        }
        public static void Complete(string path, string id, string error)
        {
            WithGate(path, () => {
                Write(path + ".cad-storeys-result.json", new CadModelGenerationResult { RequestId = id, Succeeded = error == null, Error = error }, typeof(CadModelGenerationResult));
                if (File.Exists(RequestPath(path)) && Load(path).Id == id) File.Delete(RequestPath(path));
            });
        }
        public static void Cancel(string path, string id)
        {
            WithGate(path, () => { if (File.Exists(RequestPath(path)) && Load(path).Id == id) File.Delete(RequestPath(path)); });
        }
        private static void WithGate(string path, Action action)
        {
            string name;
            using (var hash = SHA256.Create()) name = "WanLuoProjectWorkflow-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "").Substring(0, 32);
            using (var gate = new Mutex(false, name))
            {
                try { if (!gate.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("模型更新正忙，请稍后重试。"); }
                catch (AbandonedMutexException) { }
                try { action(); } finally { gate.ReleaseMutex(); }
            }
        }
        private static void Write(string path, object value, Type type)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temp)) new DataContractJsonSerializer(type).WriteObject(stream, value);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
