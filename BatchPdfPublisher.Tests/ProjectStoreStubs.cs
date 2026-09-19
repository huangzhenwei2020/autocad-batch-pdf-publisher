using System.Collections.Generic;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public static class ProjectSyncProjectionStore
    {
        public static bool MergeInto(IList<ProjectProfile> projects) { return false; }
        public static void Export(IEnumerable<ProjectProfile> projects) { }
        public static void RefreshMappings(IEnumerable<ProjectProfile> projects) { }
        /// <summary>真实实现会从旧映射/项目名派生云同步身份；这里只保证“已有值不被抹掉”。</summary>
        public static bool AssignIdentities(IEnumerable<ProjectProfile> projects)
        {
            var changed = false;
            foreach (var project in projects ?? new ProjectProfile[0])
            {
                if (project == null || !string.IsNullOrWhiteSpace(project.CloudId)) continue;
                project.CloudId = StableProjectId(project.Name);
                changed = true;
            }
            return changed;
        }

        public static string StableProjectId(string name) { return (name ?? "项目").Trim(); }
        public static string ProjectId(ProjectProfile project) { return string.IsNullOrWhiteSpace(project?.CloudId) ? StableProjectId(project?.Name) : project.CloudId; }
        public static List<CloudSyncProjectMapping> BuildMappings(IEnumerable<ProjectProfile> projects,
            IEnumerable<CloudSyncProjectMapping> previous)
        {
            return BuildMappings(projects, previous, null);
        }

        public static List<CloudSyncProjectMapping> BuildMappings(IEnumerable<ProjectProfile> projects,
            IEnumerable<CloudSyncProjectMapping> previous, string workspaceRoot)
        {
            var result = new List<CloudSyncProjectMapping>();
            foreach (var project in projects)
                result.Add(new CloudSyncProjectMapping { ProjectName = project.Name, CloudId = project.Name, LocalFolder = project.ProjectFolder });
            return result;
        }
    }

    public static class FrameTemplateStore
    {
        public static bool MakePathsReadable(IEnumerable<ProjectProfile> projects) { return false; }
    }

}
