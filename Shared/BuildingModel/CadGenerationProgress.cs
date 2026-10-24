using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class CadGenerationProgress
    {
        public string RequestId { get; set; }
        public string Stage { get; set; }
        public string State { get; set; } = "running";
        public int Completed { get; set; }
        public int Total { get; set; }
        public string UpdatedUtc { get; set; }
    }

    public sealed partial class CadModelGenerationRequest
    {
        public static string ProgressPath(string modelPath) => modelPath+".cad-generate-progress.json";
        public static CadGenerationProgress LoadProgress(string modelPath,string requestId)
        {
            var file=ProgressPath(modelPath);
            if(!File.Exists(file))return null;
            using(var stream=File.OpenRead(file))
            {
                if(stream.Length>16384)throw new InvalidDataException("生成进度记录过大。");
                var result=(CadGenerationProgress)new DataContractJsonSerializer(typeof(CadGenerationProgress)).ReadObject(stream);
                return result?.RequestId==requestId?result:null;
            }
        }
        public static void ReportProgress(string modelPath,CadGenerationProgress progress)
        {
            if(progress==null||string.IsNullOrWhiteSpace(progress.RequestId))return;
            WithGate(modelPath,()=>
            {
                // An old worker/heartbeat must never replace a newer request's state.
                if(!File.Exists(FilePath(modelPath))||Load(modelPath).Id!=progress.RequestId)return;
                Write(ProgressPath(modelPath),progress,typeof(CadGenerationProgress));
            });
        }
        private static void WriteProgressOptional(string modelPath,CadGenerationProgress progress)
        {
            try{Write(ProgressPath(modelPath),progress,typeof(CadGenerationProgress));}
            catch(IOException){}
            catch(UnauthorizedAccessException){}
        }
    }
}
