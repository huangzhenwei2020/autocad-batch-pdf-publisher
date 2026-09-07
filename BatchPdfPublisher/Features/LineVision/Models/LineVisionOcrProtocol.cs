using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BatchPdfPublisher.Models
{
    internal static class LineVisionOcrProtocol
    {
        public const int CurrentVersion = 1;
    }

    [DataContract]
    internal sealed class LineVisionOcrEngineCapabilities
    {
        [DataMember(Order = 1)] public string EngineId { get; set; }
        [DataMember(Order = 2)] public string DisplayName { get; set; }
        [DataMember(Order = 3)] public string EngineVersion { get; set; }
        [DataMember(Order = 4)] public int ProtocolVersion { get; set; }
        [DataMember(Order = 5)] public bool SupportsPolygon { get; set; }
        [DataMember(Order = 6)] public bool SupportsConfidence { get; set; }
        [DataMember(Order = 7)] public bool SupportsRotation { get; set; }
        [DataMember(Order = 8)] public List<string> Languages { get; set; } = new List<string>();
    }

    [DataContract]
    internal sealed class LineVisionOcrWorkerRequest
    {
        [DataMember(Order = 1)] public int ProtocolVersion { get; set; } = LineVisionOcrProtocol.CurrentVersion;
        [DataMember(Order = 2)] public string RequestId { get; set; }
        [DataMember(Order = 3)] public string ImagePath { get; set; }
        [DataMember(Order = 4)] public string Language { get; set; }
    }

    [DataContract]
    internal sealed class LineVisionOcrWorkerResult
    {
        [DataMember(Order = 1)] public int ProtocolVersion { get; set; }
        [DataMember(Order = 2)] public string RequestId { get; set; }
        [DataMember(Order = 3)] public string EngineId { get; set; }
        [DataMember(Order = 4)] public string EngineVersion { get; set; }
        [DataMember(Order = 5)] public bool Success { get; set; }
        [DataMember(Order = 6)] public string Error { get; set; }
        [DataMember(Order = 7)] public string Language { get; set; }
        [DataMember(Order = 8)] public int ImageWidth { get; set; }
        [DataMember(Order = 9)] public int ImageHeight { get; set; }
        [DataMember(Order = 10)] public List<LineVisionOcrWorkerTextRegion> TextRegions { get; set; } = new List<LineVisionOcrWorkerTextRegion>();
    }

    [DataContract]
    internal sealed class LineVisionOcrWorkerTextRegion
    {
        [DataMember(Order = 1)] public string Text { get; set; }
        [DataMember(Order = 2)] public double X { get; set; }
        [DataMember(Order = 3)] public double Y { get; set; }
        [DataMember(Order = 4)] public double Width { get; set; }
        [DataMember(Order = 5)] public double Height { get; set; }
        [DataMember(Order = 6)] public double RotationDegrees { get; set; }
        [DataMember(Order = 7)] public double Confidence { get; set; }
    }
}
