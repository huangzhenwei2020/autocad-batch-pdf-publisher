using System.Collections.Generic;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public sealed class PublishPlanStore
    {
        public List<ProjectProfile> LoadProjects() { return new List<ProjectProfile>(); }
    }

    public static class CloudProjectWorkspaceService
    {
        public static void ValidateForProjectSync(CloudSyncSettings settings, IEnumerable<ProjectProfile> projects) { }
        public static string GetWorkspaceRoot(CloudSyncSettings settings = null)
        {
            return System.IO.Path.Combine(UserDataPaths.RootDirectory, "workspace");
        }
    }

    public static class ProjectSyncProjectionStore
    {
        public static bool IsCloudProjectArchived(string cloudId) { return false; }
    }
}
