using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class CadRegistryModelMismatchException : IOException
    {
        public string RegisteredPath { get; private set; }
        public string CurrentPath { get; private set; }
        public CadRegistryModelMismatchException(string registered,string current)
            : base("登记记录来自其他模型位置，请在建筑模型工作台确认关联或重新登记。")
        { RegisteredPath=registered;CurrentPath=current; }
    }
    public sealed class CadRegistryRecoveryRow
    {
        public string SourceStoreyId { get; set; }
        public string SourceName { get; set; }
        public string DrawingPath { get; set; }
        public string TargetStoreyId { get; set; }
    }
    // A read-only recovery snapshot. Neither opening the workspace nor choosing
    // a target modifies the model, the old source path or the registration file.
    public sealed class CadRegistryRecovery
    {
        private readonly byte[] _registryBytes;
        private readonly string _modelHash;
        private readonly CadFloorPlanRegistry _source;
        public string ModelPath { get; private set; }
        public string RegisteredPath => _source.ModelPath;
        private CadRegistryRecovery(string path,byte[] bytes,CadFloorPlanRegistry source)
        { ModelPath=Path.GetFullPath(path);_registryBytes=bytes;_source=source;_modelHash=Hash(File.ReadAllBytes(ModelPath)); }
        public static CadRegistryRecovery Read(string modelPath)
        {
            var bytes=File.ReadAllBytes(CadFloorPlanRegistry.FilePath(modelPath));
            using(var stream=new MemoryStream(bytes))
            {
                var source=(CadFloorPlanRegistry)new DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).ReadObject(stream);
                if(source==null||source.Floors==null)throw new InvalidDataException("登记记录无法读取，请恢复备份。");
                return new CadRegistryRecovery(modelPath,bytes,source);
            }
        }
        public List<CadRegistryRecoveryRow> Rows(BuildingModelDocument model)
        {
            return _source.Floors.Select(f=>f.Floor).Concat(_source.Datums??new List<CadFloorRegistrationContext>())
                .Where(c=>c?.Storey!=null).GroupBy(c=>c.Storey.Id,StringComparer.OrdinalIgnoreCase).Select(g=>
                {
                    var c=g.First();var independent=model.Storeys.Where(s=>string.IsNullOrWhiteSpace(s.TemplateStoreyId)).ToList();
                    var target=independent.FirstOrDefault(s=>string.Equals(s.Id,c.Storey.Id,StringComparison.OrdinalIgnoreCase));
                    if(target==null){var named=independent.Where(s=>s.Name==c.Storey.Name).ToList();if(named.Count==1)target=named[0];}
                    return new CadRegistryRecoveryRow {SourceStoreyId=c.Storey.Id,SourceName=c.Storey.Name,
                        DrawingPath=_source.Floors.FirstOrDefault(f=>f.Floor.Storey.Id==c.Storey.Id)?.Probe?.DrawingPath,
                        TargetStoreyId=target?.Id};
                }).ToList();
        }
        public CadFloorPlanCapture Preview(string sourceStoreyId)=>_source.Floors.FirstOrDefault(f=>f.Floor?.Storey?.Id==sourceStoreyId);
        public string Apply(IList<CadRegistryRecoveryRow> rows,bool startFresh)
        {
            CheckUnchanged();
            var model=BuildingModelJson.LoadModel(ModelPath);
            var draft=startFresh?new CadFloorPlanRegistry {ModelPath=ModelPath,ReplaceRegisteredConstruction=true,SlabThickness=_source.SlabThickness}:_source.Clone();
            if(!startFresh)
            {
                var expected=Rows(model).Select(r=>r.SourceStoreyId).ToList();
                if(rows==null||rows.Count!=expected.Count||rows.Any(r=>!expected.Contains(r.SourceStoreyId))||rows.Select(r=>r.SourceStoreyId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rows.Count)
                    throw new InvalidDataException("登记楼层已变化，请刷新后重新选择。");
                if(rows.Any(r=>r.TargetStoreyId==null))throw new InvalidDataException("有登记楼层尚未选择对应关系，请选择模型楼层或明确选择“暂不关联”。");
                var selected=rows.Where(r=>!string.IsNullOrWhiteSpace(r.TargetStoreyId)).ToList();
                if(selected.Count==0)throw new InvalidDataException("请选择至少一个对应楼层，或使用“备份并重新登记”。");
                if(selected.Select(r=>r.TargetStoreyId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=selected.Count)
                    throw new InvalidDataException("每个模型楼层只能对应一组登记，请调整重复的楼层选择。");
                var map=selected.ToDictionary(r=>r.SourceStoreyId,r=>r.TargetStoreyId,StringComparer.OrdinalIgnoreCase);
                draft.Floors.RemoveAll(f=>!map.ContainsKey(f.Floor.Storey.Id));
                if(draft.Datums==null)draft.Datums=new List<CadFloorRegistrationContext>();
                draft.Datums.RemoveAll(c=>!map.ContainsKey(c.Storey.Id));
                foreach(var c in draft.Floors.Select(f=>f.Floor).Concat(draft.Datums))
                {
                    var target=map[c.Storey.Id];
                    var context=CadFloorRegistrationContext.Create(ModelPath,model,target,c.Alignment.ModelBase,c.Alignment.MillimetresPerCadUnit);
                    c.ModelPath=ModelPath;c.Storey=context.Storey;c.ReferenceStoreys=context.ReferenceStoreys;c.Alignment.StoreyId=target;
                }
                draft.ModelPath=ModelPath;
            }
            CheckUnchanged();
            var file=CadFloorPlanRegistry.FilePath(ModelPath);
            var backup=file+".before-association-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".bak";
            var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=File.Create(temp))new DataContractJsonSerializer(typeof(CadFloorPlanRegistry)).WriteObject(stream,draft);
                using(var stream=new FileStream(backup,FileMode.CreateNew,FileAccess.Write))stream.Write(_registryBytes,0,_registryBytes.Length);
                CheckUnchanged();
                File.Replace(temp,file,null);
                return backup;
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        private void CheckUnchanged()
        {
            if(!File.ReadAllBytes(CadFloorPlanRegistry.FilePath(ModelPath)).SequenceEqual(_registryBytes)||Hash(File.ReadAllBytes(ModelPath))!=_modelHash)
                throw new IOException("模型或登记已在其他窗口修改，请刷新后重新确认关联；当前文件未覆盖。");
        }
        private static string Hash(byte[] bytes){using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(bytes));}
    }
}
