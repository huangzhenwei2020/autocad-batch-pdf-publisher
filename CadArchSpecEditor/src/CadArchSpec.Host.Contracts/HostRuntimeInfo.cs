using System;

namespace CadArchSpec.Host.Contracts
{
    public sealed class HostRuntimeInfo
    {
        public string ProductName { get; set; } = string.Empty;
        public string ProductVersion { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public string WebView2Version { get; set; } = string.Empty;
        public string WebAssetsPath { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; } = DateTime.Now;
    }
}
