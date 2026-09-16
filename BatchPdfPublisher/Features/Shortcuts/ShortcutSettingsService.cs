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
        // 连字符必须允许：用户很自然地会填 WL-GC、GC-1 这类键，AutoLISP 的 c: 别名
        // 本身也支持它们。此前只允许字母/数字/下划线，会把这类输入直接判为非法。
        private static readonly Regex ValidShortcut = new Regex("^[A-Z][A-Z0-9_-]{1,15}$", RegexOptions.Compiled);

        public static string SettingsPath { get { return UserDataPaths.SettingsFile("shortcuts.ini", "BatchPdfPublisher.shortcuts.ini"); } }

        /// <summary>
        /// 固定功能（不含每图层直达命令）。图层命令的快捷键存在 layer-shortcuts.ini，
        /// 两套命名空间各自校验唯一性，不能让图层键混进来造成"不在本文件里却占位"的假冲突。
        /// </summary>
        private static IEnumerable<FeatureDefinition> FixedFeatures
        {
            get { return FeatureRegistry.All.Where(x => !IsLayerFeature(x)); }
        }

        private static bool IsLayerFeature(FeatureDefinition feature)
        {
            string layerKey;
            return feature != null && FeatureRegistry.TryGetLayerKey(feature.Id, out layerKey);
        }

        public static IDictionary<string, string> Load()
        {
            var values = FixedFeatures.ToDictionary(x => x.Id, x => x.DefaultShortcut, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in LoadFileValues())
                if (values.ContainsKey(pair.Key) && IsValid(pair.Value)) values[pair.Key] = pair.Value;
            MigrateOldDefaults(values);
            return values;
        }

        // ------------------------------------------------------------------
        // 快捷键文件的解析结果缓存。
        //
        // 为什么需要：Ribbon 的 Idle 检查（判断标签上的快捷键文字是否过期）每秒会调
        // Load() 好几次，Load() 又会经 FeatureRegistry.All 去读制图标准。空闲时反复
        // 读盘会让 CAD 界面发滞，所以按文件时间戳缓存解析结果。
        // ------------------------------------------------------------------
        private static Dictionary<string, string> _fileCache;
        private static DateTime _fileCacheStampUtc = DateTime.MinValue;

        private static Dictionary<string, string> LoadFileValues()
        {
            DateTime stamp;
            try { stamp = File.Exists(SettingsPath) ? File.GetLastWriteTimeUtc(SettingsPath) : DateTime.MinValue; }
            catch { stamp = DateTime.MinValue; }
            var cached = _fileCache;
            if (cached != null && stamp == _fileCacheStampUtc) return cached;
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(SettingsPath))
                    foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                    {
                        var split = line.IndexOf('=');
                        if (split <= 0) continue;
                        result[line.Substring(0, split).Trim()] = Normalize(line.Substring(split + 1));
                    }
            }
            catch { }
            _fileCache = result;
            _fileCacheStampUtc = stamp;
            return result;
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
            foreach (var feature in FixedFeatures)
            {
                string value;
                var shortcut = Normalize(values != null && values.TryGetValue(feature.Id, out value) ? value : feature.DefaultShortcut);
                if (!IsValid(shortcut)) throw new InvalidOperationException("“" + feature.Name + "”的快捷键格式无效。快捷键必须以字母开头，只能包含大写字母、数字、连字符或下划线，长度为 2–16 位（例如 GC、WL-GC、GC_1）。");
                normalized[feature.Id] = shortcut;
            }
            var duplicate = normalized.GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
            if (duplicate != null)
            {
                var names = duplicate.Select(x => FeatureRegistry.Find(x.Key).Name);
                throw new InvalidOperationException("快捷键“" + duplicate.Key + "”被重复使用：" + string.Join("、", names) + "。");
            }
            var commands = new HashSet<string>(FixedFeatures.Select(x => x.Command), StringComparer.OrdinalIgnoreCase);
            foreach (var native in FixedFeatures.Select(x => x.NativeCommand).Where(x => !string.IsNullOrWhiteSpace(x))) commands.Add(native);
            foreach (var feature in FixedFeatures)
            {
                var shortcut = normalized[feature.Id];
                if (commands.Contains(shortcut) && !string.Equals(shortcut, feature.Command, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(shortcut, feature.NativeCommand, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("快捷键“" + shortcut + "”与另一个功能的内部命令重名，请换一个快捷键。");
            }
            var lines = FixedFeatures.Select(x => x.Id + "=" + normalized[x.Id]).ToArray();
            File.WriteAllLines(SettingsPath, lines, new UTF8Encoding(false));
            _fileCache = null;
            _fileCacheStampUtc = DateTime.MinValue;
        }

        public static IDictionary<string, string> Defaults()
        {
            return FixedFeatures.ToDictionary(x => x.Id, x => x.DefaultShortcut, StringComparer.OrdinalIgnoreCase);
        }

        public static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToUpperInvariant(), "\\s+", string.Empty);
        }

        public static bool IsValid(string value) { return ValidShortcut.IsMatch(Normalize(value)); }

        private static void MigrateOldDefaults(IDictionary<string, string> values)
        {
            string shortcut;
            if (values.TryGetValue("line_vision", out shortcut) && string.Equals(shortcut, "TXZCAD", StringComparison.OrdinalIgnoreCase)
                && !values.Any(item => !string.Equals(item.Key, "line_vision", StringComparison.OrdinalIgnoreCase) && string.Equals(item.Value, "TXC", StringComparison.OrdinalIgnoreCase)))
                values["line_vision"] = "TXC";
            if (values.TryGetValue("cad_table_xlsx", out shortcut) && string.Equals(shortcut, "CAD2XLSX", StringComparison.OrdinalIgnoreCase)
                && !values.Any(item => !string.Equals(item.Key, "cad_table_xlsx", StringComparison.OrdinalIgnoreCase) && string.Equals(item.Value, "CE", StringComparison.OrdinalIgnoreCase)))
                values["cad_table_xlsx"] = "CE";
        }
    }
}
