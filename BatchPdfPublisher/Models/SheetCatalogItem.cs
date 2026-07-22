namespace BatchPdfPublisher.Models
{
    // 仅保存可序列化的目录字段；ObjectId 只在当前 CAD 会话中有效，不能写入项目文件。
    public sealed class SheetCatalogItem
    {
        public int Order { get; set; }
        public string BlockHandle { get; set; }
        public string Building { get; set; }
        public string SheetNumber { get; set; }
        public string SheetName { get; set; }
        public string Frame { get; set; }
        public string Extension { get; set; }
        public string FrameNote { get; set; }
        public string PaperOrientation { get; set; }
        public string PrintScale { get; set; }
        public string PlotStyle { get; set; }
        public string SourceFile { get; set; }
        public string SourceLayout { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }
}
