using System.Collections.Generic;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public static class ProjectSyncProjectionStore
    {
        public static bool MergeInto(IList<ProjectProfile> projects) { return false; }
        public static void Export(IEnumerable<ProjectProfile> projects) { }
        public static void RefreshMappings(IEnumerable<ProjectProfile> projects) { }
        public static List<CloudSyncProjectMapping> BuildMappings(IEnumerable<ProjectProfile> projects,
            IEnumerable<CloudSyncProjectMapping> previous)
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
