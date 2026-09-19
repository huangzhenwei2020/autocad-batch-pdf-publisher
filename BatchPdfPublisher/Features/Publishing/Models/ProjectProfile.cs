using System.Collections.Generic;

namespace BatchPdfPublisher.Models
{
    public sealed class ProjectProfile
    {
        public string Name { get; set; }
        /// <summary>
        /// 云同步里的项目身份：同步目录 <c>项目配置\同步项目\&lt;CloudId&gt;</c> 与同步路径
        /// <c>项目文件/&lt;CloudId&gt;/…</c> 都用它。首次由项目名派生
        /// （<see cref="ProjectSyncProjectionStore.StableProjectId"/>），之后**改项目名不再重算**——
        /// 重算会让同步引擎把整个项目当成新项目全量重传，云端还会留下同名旧目录。
        /// 旧配置没有这个字段，载入时会按项目名补齐，取值与旧版算法完全一致。
        /// </summary>
        public string CloudId { get; set; }
        // Empty means the standard per-user project directory is used.
        public string ProjectFolder { get; set; }
        public List<FrameDefinition> Frames { get; set; } = new List<FrameDefinition>();
        public string PlotStyle { get; set; } = "monochrome.ctb";
        public string MarginMode { get; set; } = "自动适配";
        public string OutputDirectory { get; set; }
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
