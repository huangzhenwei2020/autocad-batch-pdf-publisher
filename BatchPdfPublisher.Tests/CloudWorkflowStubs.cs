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
            return settings != null && !string.IsNullOrWhiteSpace(settings.ProjectWorkspaceRoot)
                ? settings.ProjectWorkspaceRoot : System.IO.Path.Combine(UserDataPaths.RootDirectory, "workspace");
        }
        public static bool IsUnderWorkspace(string folder, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(workspaceRoot)) return false;
            var root = System.IO.Path.GetFullPath(workspaceRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            return System.IO.Path.GetFullPath(folder).StartsWith(root, System.StringComparison.OrdinalIgnoreCase);
        }
        public static string ProjectFolderFor(CloudSyncSettings settings, string projectName)
        { return System.IO.Path.Combine(GetWorkspaceRoot(settings), projectName); }
    }

    public static class ProjectSyncProjectionStore
    {
        public static bool IsCloudProjectArchived(string cloudId) { return false; }
    }
}
