using System.Collections.Generic;

namespace BatchPdfPublisher.Models
{
    public sealed class ProjectProfile
    {
        public string Name { get; set; }
        public List<FrameDefinition> Frames { get; set; } = new List<FrameDefinition>();
        public string PlotStyle { get; set; } = "monochrome.ctb";
        public string MarginMode { get; set; } = "自动适配";
        public string OutputDirectory { get; set; } = "D:\\PDF输出";
        public bool MergeByBuilding { get; set; } = true;
        public bool PreviewEnabled { get; set; } = true;
        public List<string> FavoritePlotStyles { get; set; } = new List<string>();
        public List<SheetCatalogItem> SavedSheets { get; set; } = new List<SheetCatalogItem>();
    }
}
