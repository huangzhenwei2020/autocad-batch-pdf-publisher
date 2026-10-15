using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class BuildingModelFileRequest
    {
        public string Id {get;set;}
        public string Operation {get;set;}
        public string ModelPath {get;set;}
        public string TargetPath {get;set;}
        public string TargetName {get;set;}
        public string ProjectFolder {get;set;}
        public string DrawingFingerprint {get;set;}
        public string DrawingPath {get;set;}
        public string SessionToken {get;set;}
        public DateTime CreatedUtc {get;set;}
    }
    public sealed class BuildingModelFileResult
    {
        public string Id {get;set;}
        public bool Succeeded {get;set;}
        public string ModelPath {get;set;}
        public string Error {get;set;}
    }
    public static class BuildingModelFileRequests
    {
        public static string RequestPath(string path)=>path+".model-command.json";
        public static BuildingModelFileRequest Queue(string path,string operation,string project,string fingerprint=null,
            string drawingPath=null,string targetPath=null,string targetName=null)
        {
            if(operation!="Save"&&operation!="SaveAs"&&operation!="Activate")throw new ArgumentException("不支持的模型操作。");
            if(!StudioLaunch.HasLiveSession(path))throw new IOException("模型窗口尚未打开，不能从磁盘旧版本代替保存。");
            var request=new BuildingModelFileRequest {Id=Guid.NewGuid().ToString("N"),Operation=operation,ModelPath=Path.GetFullPath(path),
                TargetPath=targetPath,TargetName=targetName,ProjectFolder=project,DrawingFingerprint=fingerprint,DrawingPath=drawingPath,
                SessionToken=StudioLaunch.ReadMetadata(path+".studio-session.txt"),CreatedUtc=DateTime.UtcNow};
            Gate(path,()=> {
                if(File.Exists(RequestPath(path)))
                {
                    var old=Load(path);
                    if((DateTime.UtcNow-old.CreatedUtc).TotalMinutes<=2)throw new IOException("模型已有文件操作待处理，请稍后重试。");
                }
                Write(RequestPath(path),request,typeof(BuildingModelFileRequest));
            });return request;
        }
        public static BuildingModelFileRequest Load(string path)
        {
            var file=RequestPath(path);
            if(new FileInfo(file).Length>32768)throw new InvalidDataException("模型文件请求过大。");
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))return (BuildingModelFileRequest)new DataContractJsonSerializer(typeof(BuildingModelFileRequest)).ReadObject(stream);
        }
        public static void Validate(string path,BuildingModelFileRequest request)
        {
            if(request==null||string.IsNullOrWhiteSpace(request.Id)||!string.Equals(Path.GetFullPath(path),request.ModelPath,StringComparison.OrdinalIgnoreCase)
                ||request.CreatedUtc>DateTime.UtcNow.AddMinutes(1)||(DateTime.UtcNow-request.CreatedUtc).TotalMinutes>2
                ||!StudioLaunch.IsCurrentSession(path)||StudioLaunch.ReadMetadata(path+".studio-session.txt")!=request.SessionToken)
                throw new InvalidDataException("模型请求已过期或窗口已切换，请重新操作。");
            if(!File.Exists(RequestPath(path))||Load(path).Id!=request.Id)throw new InvalidDataException("模型请求已取消或替换。");
            if(request.Operation!="Save"&&request.Operation!="SaveAs"&&request.Operation!="Activate")throw new InvalidDataException("模型请求操作无效。");
            if(request.Operation=="SaveAs" && (string.IsNullOrWhiteSpace(request.TargetPath)||string.IsNullOrWhiteSpace(request.TargetName)
                ||string.Equals(Path.GetFullPath(request.TargetPath),Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("另存模型需要新的名称和独立路径。");
        }
        public static BuildingModelFileResult Result(string path)
        {
            var file=path+".model-command-result.json";if(!File.Exists(file))return null;
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))return (BuildingModelFileResult)new DataContractJsonSerializer(typeof(BuildingModelFileResult)).ReadObject(stream);
        }
        public static void Complete(string path,string id,string resultPath,string error)
        {
            Gate(path,()=> {
                Write(path+".model-command-result.json",new BuildingModelFileResult {Id=id,Succeeded=error==null,ModelPath=resultPath,Error=error},typeof(BuildingModelFileResult));
                if(File.Exists(RequestPath(path))&&Load(path).Id==id)File.Delete(RequestPath(path));
            });
        }
        public static void Cancel(string path,string id)
        { Gate(path,()=> {if(File.Exists(RequestPath(path))&&Load(path).Id==id)File.Delete(RequestPath(path));}); }
        private static void Gate(string path,Action action)
        {
            using(var gate=new Mutex(false,"WanLuoModelFile-"+BuildingModelCatalog.Hash(Path.GetFullPath(path).ToUpperInvariant())))
            {
                try {if(!gate.WaitOne(TimeSpan.FromSeconds(5)))throw new IOException("模型文件操作正忙。");}catch(AbandonedMutexException){}
                try {action();}finally {gate.ReleaseMutex();}
            }
        }
        private static void Write(string path,object value,Type type)
        {
            var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {using(var stream=File.Create(temp))new DataContractJsonSerializer(type).WriteObject(stream,value);
                StudioLaunch.ReplaceMetadata(temp,path);}
            finally {if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
