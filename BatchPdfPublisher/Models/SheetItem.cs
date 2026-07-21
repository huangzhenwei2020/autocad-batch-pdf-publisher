using Autodesk.AutoCAD.DatabaseServices;

namespace BatchPdfPublisher.Models
{
    public sealed class SheetItem
    {
        public ObjectId BlockId { get; set; }
        public int Order { get; set; }
        public string Building { get; set; }
        public string SheetNumber { get; set; }
        public string SheetName { get; set; }
        public string Frame { get; set; }
        public string Extension { get; set; }
        public string FrameDisplay => string.IsNullOrWhiteSpace(Extension) ? Frame : Frame + "+" + Extension;
        public string PaperOrientation { get; set; }
        public string PrintScale { get; set; }
        public string PlotStyle { get; set; }
        public string SourceFile { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }
}
