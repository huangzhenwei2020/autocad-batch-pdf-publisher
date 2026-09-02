using System.Collections.Generic;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public static class ProjectSyncProjectionStore
    {
        public static bool MergeInto(IList<ProjectProfile> projects) { return false; }
        public static void Export(IEnumerable<ProjectProfile> projects) { }
        public static void RefreshMappings(IEnumerable<ProjectProfile> projects) { }
    }

    public static class FrameTemplateStore
    {
        public static bool MakePathsReadable(IEnumerable<ProjectProfile> projects) { return false; }
    }
}
