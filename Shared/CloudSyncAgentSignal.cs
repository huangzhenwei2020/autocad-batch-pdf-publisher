using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    public static class CloudSyncAgentSignal
    {
        public static string EventName
        {
            get
            {
                using (var hash = SHA256.Create())
                    return @"Local\WanluoArchitectureTools.SyncRequest." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(UserDataPaths.RootDirectory.ToUpperInvariant()))).Replace("-", "").Substring(0, 24);
            }
        }

        public static void Request()
        {
            try { using (var signal = EventWaitHandle.OpenExisting(EventName)) signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
        }
    }
}
