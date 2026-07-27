using System.Collections.Generic;

namespace BatchPdfPublisher.Models
{
    public sealed class ProjectProfile
    {
        public string Name { get; set; }
        // Empty means the standard per-user project directory is used.
        public string ProjectFolder { get; set; }
        public List<FrameDefinition> Frames { get; set; } = new List<FrameDefinition>();
        public string PlotStyle { get; set; } = "monochrome.ctb";
        public string MarginMode { get; set; } = "自动适配";
        public string OutputDirectory { get; set; } = "D:\\PDF输出";
        public bool OutputNextToCadFile { get; set; }
        public bool IncludeProjectNameInFileName { get; set; } = true;
        public bool IncludeBuildingNameInFileName { get; set; } = true;
        public bool OverwriteExistingPdf { get; set; }
        public bool MergeByBuilding { get; set; } = true;
        public bool PreviewEnabled { get; set; }
        public List<string> FavoritePlotStyles { get; set; } = new List<string>();
        public List<SheetCatalogItem> SavedSheets { get; set; } = new List<SheetCatalogItem>();
        public List<string> CadFiles { get; set; } = new List<string>();
        public List<string> SelectedCadFiles { get; set; } = new List<string>();
        public List<string> SelectedPublishBuildings { get; set; } = new List<string>();
        public bool ScanModelSpace { get; set; } = true;
        public bool ScanAllLayouts { get; set; } = true;
        public List<string> SelectedLayouts { get; set; } = new List<string>();
        // Null means an older project that should follow AutoCAD SAVETIME.
        public int? AutoSaveMinutes { get; set; }
    }
}
