using System;
using System.IO;

namespace BatchPdfPublisher.Services
{
    public static class WanluoCloudBrokerConfiguration
    {
        // Set only after the production HTTPS endpoint has been deployed and verified.
        public const string DefaultBrokerUrl = "";

        public static string Resolve(CloudSyncSettings settings)
        {
            var configured = settings == null ? null : settings.ProviderBrokerUrl;
            if (IsHttps(configured)) return configured.TrimEnd('/');
            var environment = Environment.GetEnvironmentVariable("WANLUO_BAIDU_BROKER_URL");
            if (IsHttps(environment)) return environment.TrimEnd('/');
            var file = UserDataPaths.SettingsFile("cloud-auth-broker.url");
            try { if (File.Exists(file)) { var value = File.ReadAllText(file).Trim(); if (IsHttps(value)) return value.TrimEnd('/'); } } catch { }
            return IsHttps(DefaultBrokerUrl) ? DefaultBrokerUrl.TrimEnd('/') : string.Empty;
        }

        private static bool IsHttps(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
        }
    }
}
