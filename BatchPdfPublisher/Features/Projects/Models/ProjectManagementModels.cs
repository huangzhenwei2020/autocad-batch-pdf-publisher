using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.Models
{
    /// <summary>项目管理（在 AutoCAD 之外执行）支持的四类操作。</summary>
    public enum ProjectManagementAction
    {
        CreateProject,
        RenameProject,
        RenameFile,
        DeleteProject
    }

    /// <summary>一次要执行的磁盘搬移。</summary>
    public sealed class ProjectMovePlan
    {
        /// <summary>人可读类别：项目文件夹 / 图框模板 / 楼梯方案 / 楼梯平面缓存 / 图纸文件。</summary>
        public string Kind { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        /// <summary>必需项搬移失败会整体回滚；非必需项失败只记警告（例如楼梯缓存目录）。</summary>
        public bool Required { get; set; }

        public override string ToString()
        {
            return Kind + "：" + From + " → " + To;
        }
    }

    /// <summary>要改写的登记路径（供执行前预览）。</summary>
    public sealed class ProjectReferenceChange
    {
        /// <summary>人可读位置：项目文件夹 / 输出目录 / 图纸列表 / 已扫描图纸 / 图框模板。</summary>
        public string Where { get; set; }
        public string From { get; set; }
        public string To { get; set; }
    }

    /// <summary>
    /// 执行前的完整方案：先给用户看，确认后才 <see cref="Services.ProjectManagementService.Execute"/>。
    /// <see cref="Blockers"/> 非空表示不允许执行。
    /// </summary>
    public sealed class ProjectManagementPlan
    {
        public ProjectManagementAction Action { get; set; }
        public string ProjectName { get; set; }
        public string NewName { get; set; }
        /// <summary>云同步里的项目身份，改名时保持不变。</summary>
        public string CloudId { get; set; }
        public string ProjectFolder { get; set; }
        public string NewProjectFolder { get; set; }
        /// <summary>文件改名时的项目内相对路径。</summary>
        public string FileRelativePath { get; set; }
        /// <summary>项目改名时是否连同磁盘文件夹一起改。</summary>
        public bool RenameProjectFolder { get; set; } = true;
        /// <summary>删除项目时是否把项目文件夹移到回收站。</summary>
        public bool DeleteFolderToRecycleBin { get; set; }
        public List<ProjectMovePlan> Moves { get; set; } = new List<ProjectMovePlan>();
        public List<ProjectReferenceChange> References { get; set; } = new List<ProjectReferenceChange>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Blockers { get; set; } = new List<string>();

        public bool CanExecute { get { return Blockers.Count == 0; } }

        public string ActionTitle
        {
            get
            {
                switch (Action)
                {
                    case ProjectManagementAction.CreateProject: return "新建项目";
                    case ProjectManagementAction.RenameProject: return "重命名项目";
                    case ProjectManagementAction.RenameFile: return "重命名项目文件";
                    case ProjectManagementAction.DeleteProject: return "删除项目";
                    default: return "项目管理";
                }
            }
        }

        public string Summary
        {
            get
            {
                switch (Action)
                {
                    case ProjectManagementAction.CreateProject:
                        return "新建项目“" + NewName + "”，文件夹：" + ProjectFolder;
                    case ProjectManagementAction.RenameProject:
                        return "把项目“" + ProjectName + "”改名为“" + NewName + "”"
                            + (RenameProjectFolder && !string.Equals(ProjectFolder, NewProjectFolder, StringComparison.OrdinalIgnoreCase)
                                ? "，并把文件夹改名为 " + System.IO.Path.GetFileName(NewProjectFolder ?? string.Empty) : "（文件夹不动）");
                    case ProjectManagementAction.RenameFile:
                        return "把“" + ProjectName + "”里的 " + FileRelativePath + " 改名为 " + System.IO.Path.GetFileName(NewName ?? string.Empty);
                    case ProjectManagementAction.DeleteProject:
                        return "从项目列表移除“" + ProjectName + "”"
                            + (DeleteFolderToRecycleBin ? "，并把项目文件夹移到回收站" : "（文件夹保留）");
                    default: return string.Empty;
                }
            }
        }
    }

    /// <summary>执行结果。</summary>
    public sealed class ProjectManagementResult
    {
        public bool Succeeded { get; set; }
        public string Message { get; set; }
        /// <summary>操作记录（正向 + 反向映射），出问题时据此人工恢复。</summary>
        public string JournalPath { get; set; }
        /// <summary>执行前的配置备份副本。</summary>
        public string SafetyBackupPath { get; set; }
        public List<string> Steps { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();

        public string Describe()
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(Message)) lines.Add(Message);
            foreach (var step in Steps) lines.Add("✓ " + step);
            foreach (var warning in Warnings) lines.Add("! " + warning);
            foreach (var error in Errors) lines.Add("✗ " + error);
            if (!string.IsNullOrWhiteSpace(SafetyBackupPath)) lines.Add("执行前备份：" + SafetyBackupPath);
            if (!string.IsNullOrWhiteSpace(JournalPath)) lines.Add("操作记录：" + JournalPath);
            return string.Join(Environment.NewLine, lines.ToArray());
        }
    }

    /// <summary>项目文件夹里的一个文件（供列表显示）。</summary>
    public sealed class ProjectFileEntry
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public long Size { get; set; }
        public DateTime ModifiedUtc { get; set; }
        /// <summary>是否已在项目的图纸列表（CadFiles）里登记。</summary>
        public bool Registered { get; set; }
        /// <summary>是否有已扫描的图框记录（SavedSheets）。</summary>
        public bool Scanned { get; set; }

        public string SizeText
        {
            get
            {
                if (Size < 1024) return Size + " B";
                if (Size < 1024 * 1024) return (Size / 1024d).ToString("0.#") + " KB";
                return (Size / 1024d / 1024d).ToString("0.##") + " MB";
            }
        }

        public string StateText
        {
            get
            {
                if (Scanned) return "已扫描 " + (Registered ? "· 已登记" : string.Empty);
                if (Registered) return "已登记";
                return "未登记";
            }
        }
    }
}
