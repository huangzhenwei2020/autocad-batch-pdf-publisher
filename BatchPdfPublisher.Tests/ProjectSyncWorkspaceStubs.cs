using System;
using System.IO;

namespace BatchPdfPublisher.Services
{
    public sealed class CloudProjectInfo
    {
        public string ProjectName { get; set; }
        public string CloudId { get; set; }
        public bool IsArchived { get; set; }
    }

    public static class CloudProjectWorkspaceService
    {
        public static string GetWorkspaceRoot(CloudSyncSettings settings = null)
        {
            return Path.Combine(UserDataPaths.RootDirectory, "工作项目");
        }

        public static string ProjectFolderFor(CloudSyncSettings settings, string projectName)
        {
            return Path.Combine(GetWorkspaceRoot(settings), projectName ?? "默认项目");
        }

        public static bool IsUnderWorkspace(string folder, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(workspaceRoot)) return false;
            var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
