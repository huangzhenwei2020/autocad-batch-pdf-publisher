using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Wanluo.LineVision.VectorWorker
{
    [DataContract] internal sealed class VectorResult
    {
        [DataMember(Name = "success", Order = 1)] public bool Success { get; set; }
        [DataMember(Name = "error", Order = 2)] public string Error { get; set; }
        [DataMember(Name = "mode", Order = 3)] public string Mode { get; set; }
        [DataMember(Name = "width", Order = 4)] public int Width { get; set; }
        [DataMember(Name = "height", Order = 5)] public int Height { get; set; }
        [DataMember(Name = "centerlines", Order = 6)] public List<VectorPolyline> Centerlines { get; set; } = new List<VectorPolyline>();
        [DataMember(Name = "outlines", Order = 7)] public List<VectorPolyline> Outlines { get; set; } = new List<VectorPolyline>();
    }

    [DataContract] internal sealed class VectorPolyline
    {
        [DataMember(Name = "points", Order = 1)] public List<VectorPoint> Points { get; set; } = new List<VectorPoint>();
        [DataMember(Name = "closed", Order = 2)] public bool Closed { get; set; }
        [DataMember(Name = "confidence", Order = 3)] public double Confidence { get; set; }
    }

    [DataContract] internal sealed class VectorPoint
    {
        public VectorPoint() { }
        public VectorPoint(double x, double y) { X = x; Y = y; }
        [DataMember(Name = "x", Order = 1)] public double X { get; set; }
        [DataMember(Name = "y", Order = 2)] public double Y { get; set; }
    }
}
