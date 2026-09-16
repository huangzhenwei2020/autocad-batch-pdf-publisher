using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 每图层独立命令的快捷键存储。
    ///
    /// 与主快捷键文件（shortcuts.ini，功能 id → 键）不同，这里的 id 是**图层键**（系统标签），
    /// 键值就是该图层的直达快捷键，例如 StairOutline=GC。
    /// 只有**填了值**的图层才会生成命令与别名：留空即不生成，
    /// 从根上避免与现有功能抢占用同一组快捷键。
    ///
    /// 与 DraftingLayerRoles 一样保持**无 AutoCAD 依赖**，以便进纯逻辑单元测试工程。
    /// </summary>
    public static class LayerShortcutStore
    {
        private static readonly object Sync = new object();
        private static Dictionary<string, string> _cache;

        public static string SettingsPath
        {
            get { return Path.Combine(UserDataPaths.SettingsDirectory, "layer-shortcuts.ini"); }
        }

        /// <summary>返回"图层键 → 快捷键"的快照；未配置时为空表。</summary>
        public static IDictionary<string, string> Load()
        {
            lock (Sync)
            {
                if (_cache == null) _cache = ReadFromDisk();
                return new Dictionary<string, string>(_cache, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>丢弃缓存，下次 Load 重新读盘。设置界面保存后应调用。</summary>
        public static void Reload()
        {
            lock (Sync) { _cache = null; }
        }

        public static void Save(IDictionary<string, string> values)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (values != null)
            {
                foreach (var pair in values)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                    var shortcut = ShortcutSettingsService.Normalize(pair.Value);
                    if (shortcut.Length == 0) continue;                  // 留空 = 不生成命令
                    if (!ShortcutSettingsService.IsValid(shortcut)) continue;
                    normalized[pair.Key.Trim()] = shortcut;
                }
            }

            var duplicate = normalized
                .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                throw new InvalidOperationException("图层快捷键“" + duplicate.Key + "”被多个图层重复使用："
                    + string.Join("、", new List<string>(duplicate.Select(x => x.Key)).ToArray()) + "。");

            // 与主功能快捷键、内部命令重名会互相覆盖或递归调用，一律拒绝。
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in FeatureRegistry.Items)
            {
                taken.Add(feature.Command);
                if (!string.IsNullOrWhiteSpace(feature.NativeCommand)) taken.Add(feature.NativeCommand);
            }
            foreach (var feature in FeatureRegistry.Items)
            {
                string configured;
                if (ShortcutSettingsService.Load().TryGetValue(feature.Id, out configured) && !string.IsNullOrWhiteSpace(configured))
                    taken.Add(ShortcutSettingsService.Normalize(configured));
            }
            foreach (var pair in normalized)
            {
                if (!taken.Contains(pair.Value)) continue;
                throw new InvalidOperationException("图层“" + LayerDisplayName(pair.Key) + "”的快捷键“" + pair.Value
                    + "”与现有功能或内部命令重名，请换一个。");
            }

            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var lines = new List<string>();
            foreach (var pair in normalized) lines.Add(pair.Key + "=" + pair.Value);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllLines(temporary, lines.ToArray(), new UTF8Encoding(false));
            if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, null);
            else File.Move(temporary, SettingsPath);
            lock (Sync) { _cache = null; }
        }

        private static Dictionary<string, string> ReadFromDisk()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(SettingsPath)) return result;
                foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    var split = line.IndexOf('=');
                    if (split <= 0) continue;
                    var key = line.Substring(0, split).Trim();
                    var shortcut = ShortcutSettingsService.Normalize(line.Substring(split + 1));
                    if (key.Length == 0 || !ShortcutSettingsService.IsValid(shortcut)) continue;
                    result[key] = shortcut;
                }
            }
            catch { }
            return result;
        }

        internal static string LayerDisplayName(string layerKey)
        {
            var role = DraftingLayerRoles.Find(layerKey);
            return role == null ? layerKey : layerKey + "（" + role.Group + "）";
        }
    }
}
