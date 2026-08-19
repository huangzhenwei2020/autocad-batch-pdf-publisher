using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    public static class ShortcutSettingsService
    {
        private static readonly Regex ValidShortcut = new Regex("^[A-Z][A-Z0-9_]{1,15}$", RegexOptions.Compiled);

        public static string SettingsPath { get { return UserDataPaths.SettingsFile("shortcuts.ini", "BatchPdfPublisher.shortcuts.ini"); } }

        public static IDictionary<string, string> Load()
        {
            var values = FeatureRegistry.All.ToDictionary(x => x.Id, x => x.DefaultShortcut, StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(SettingsPath)) return values;
                foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    var split = line.IndexOf('=');
                    if (split <= 0) continue;
                    var id = line.Substring(0, split).Trim();
                    var shortcut = Normalize(line.Substring(split + 1));
                    if (values.ContainsKey(id) && IsValid(shortcut)) values[id] = shortcut;
                }
            }
            catch { }
            return values;
        }

        public static string ShortcutFor(FeatureDefinition feature)
        {
            if (feature == null) return string.Empty;
            string value;
            return Load().TryGetValue(feature.Id, out value) ? value : feature.DefaultShortcut;
        }

        public static void Save(IDictionary<string, string> values)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in FeatureRegistry.All)
            {
                string value;
                var shortcut = Normalize(values != null && values.TryGetValue(feature.Id, out value) ? value : feature.DefaultShortcut);
                if (!IsValid(shortcut)) throw new InvalidOperationException("“" + feature.Name + "”的快捷键格式无效。快捷键必须以字母开头，只能包含大写字母、数字或下划线，长度为 2–16 位。");
                normalized[feature.Id] = shortcut;
            }
            var duplicate = normalized.GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
            if (duplicate != null)
            {
                var names = duplicate.Select(x => FeatureRegistry.Find(x.Key).Name);
                throw new InvalidOperationException("快捷键“" + duplicate.Key + "”被重复使用：" + string.Join("、", names) + "。");
            }
            var commands = new HashSet<string>(FeatureRegistry.All.Select(x => x.Command), StringComparer.OrdinalIgnoreCase);
            foreach (var native in FeatureRegistry.All.Select(x => x.NativeCommand).Where(x => !string.IsNullOrWhiteSpace(x))) commands.Add(native);
            foreach (var feature in FeatureRegistry.All)
            {
                var shortcut = normalized[feature.Id];
                if (commands.Contains(shortcut) && !string.Equals(shortcut, feature.Command, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(shortcut, feature.NativeCommand, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("快捷键“" + shortcut + "”与另一个功能的内部命令重名，请换一个快捷键。");
            }
            var lines = FeatureRegistry.All.Select(x => x.Id + "=" + normalized[x.Id]).ToArray();
            File.WriteAllLines(SettingsPath, lines, new UTF8Encoding(false));
        }

        public static IDictionary<string, string> Defaults()
        {
            return FeatureRegistry.All.ToDictionary(x => x.Id, x => x.DefaultShortcut, StringComparer.OrdinalIgnoreCase);
        }

        public static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToUpperInvariant(), "\\s+", string.Empty);
        }

        public static bool IsValid(string value) { return ValidShortcut.IsMatch(Normalize(value)); }
    }
}
