using System;
using System.Collections.Generic;
using System.IO;

namespace WL.Stair.CadShared
{
    /// <summary>
    /// 只读访问万落建筑工具主插件（BatchPdfPublisher）的制图标准（BZS）。
    ///
    /// 楼梯大样是独立程序集，不能引用主插件类型，也不保证主插件已加载，
    /// 因此这里直接读取标准文件（与主插件共用一个用户数据根目录）。
    /// 读不到、格式不认识、或某项缺失时一律回退到内置常量——
    /// 即行为与改造前完全一致，不会因为标准不可用而失效。
    /// </summary>
    internal static class DraftingStandardBridge
    {
        private const string SettingsRelativePath = @"通用设置\drafting-standard.ini";
        /// <summary>覆盖标准文件所在目录，供自动化测试使用，不设置时用真实用户数据目录。</summary>
        internal const string SettingsDirectoryOverrideVariable = "WANLUO_DRAFTING_STANDARD_DIR";
        private static readonly object Sync = new object();
        private static Dictionary<string, string> _layersByKey;
        private static Dictionary<string, string> _layersBySystemTag;
        private static Dictionary<string, string> _textStylesByKey;
        private static string _dimensionStylePrefix;
        private static bool _loaded;

        /// <summary>
        /// 解析图层名：优先按系统标签（主插件 LayerItem.N.Key），
        /// 其次按旧格式 Layer.&lt;Key&gt;.Name，最后回退内置常量。
        /// </summary>
        internal static string LayerName(string systemTag, string fallback)
        {
            if (string.IsNullOrWhiteSpace(systemTag)) return fallback;
            try
            {
                EnsureLoaded();
                string name;
                if (_layersBySystemTag != null && _layersBySystemTag.TryGetValue(systemTag, out name) && !string.IsNullOrWhiteSpace(name)) return name;
                if (_layersByKey != null && _layersByKey.TryGetValue(systemTag, out name) && !string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return fallback;
        }

        /// <summary>
        /// 解析文字样式名：兼容主插件当前的 Text.N.Key/Name 段与旧的 Text.&lt;Key&gt;.Name。
        /// </summary>
        internal static string TextStyleName(string systemTag, string fallback)
        {
            if (string.IsNullOrWhiteSpace(systemTag)) return fallback;
            try
            {
                EnsureLoaded();
                string name;
                if (_textStylesByKey != null && _textStylesByKey.TryGetValue(systemTag, out name) && !string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return fallback;
        }

        /// <summary>标注样式名前缀，默认 "WL-标注-1_"。空值或读取失败时回退。</summary>
        internal static string DimensionStylePrefix(string fallback)
        {
            try
            {
                EnsureLoaded();
                if (!string.IsNullOrWhiteSpace(_dimensionStylePrefix)) return _dimensionStylePrefix;
            }
            catch { }
            return fallback;
        }

        /// <summary>测试或诊断用：丢弃缓存，下次调用重新读取标准文件。</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                _layersByKey = null; _layersBySystemTag = null;
                _textStylesByKey = null; _dimensionStylePrefix = null;
                _loaded = false;
            }
        }

        private static string ResolveSettingsPath()
        {
            var overridden = Environment.GetEnvironmentVariable(SettingsDirectoryOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden)) return Path.Combine(overridden, @"通用设置\drafting-standard.ini");
#if WL_STAIR_TEST
            // 无 AutoCAD 依赖的单元测试工程只通过上面的覆盖变量提供标准文件。
            return null;
#else
            return Path.Combine(WanluoDataPaths.Root, SettingsRelativePath);
#endif
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (Sync)
            {
                if (_loaded) return;
                _layersByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _layersBySystemTag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _textStylesByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var path = ResolveSettingsPath();
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        // 主插件保存时写的是 LayerItem.N.* 段，旧配置则是 Layer.<Key>.Name。
                        // 两种都要认识：前者用 Key→Name，后者键里就带图层 Key 或系统标签。
                        var indexed = new SortedDictionary<int, string[]>( );
                        var indexedText = new SortedDictionary<int, string[]>();
                        foreach (var line in File.ReadAllLines(path))
                        {
                            var split = line.IndexOf('=');
                            if (split <= 0) continue;
                            var key = line.Substring(0, split).Trim();
                            var value = line.Substring(split + 1).Trim();
                            if (key.Length == 0 || value.Length == 0) continue;
                            if (string.Equals(key, "Dimension.StylePrefix", StringComparison.OrdinalIgnoreCase))
                            {
                                _dimensionStylePrefix = value;
                                continue;
                            }
                            if (key.StartsWith("Text.", StringComparison.OrdinalIgnoreCase))
                            {
                                // 主插件当前保存的是 Text.N.Key / Text.N.Name；
                                // 旧配置则是 Text.<Key>.Name（键里直接带样式 Key）。
                                var rest = key.Substring("Text.".Length);
                                var dot = rest.IndexOf('.');
                                int index;
                                if (dot > 0 && int.TryParse(rest.Substring(0, dot), out index))
                                {
                                    var field = rest.Substring(dot + 1);
                                    string[] slot;
                                    if (!indexedText.TryGetValue(index, out slot)) { slot = new string[2]; indexedText[index] = slot; }
                                    if (string.Equals(field, "Key", StringComparison.OrdinalIgnoreCase)) slot[0] = value;
                                    else if (string.Equals(field, "Name", StringComparison.OrdinalIgnoreCase)) slot[1] = value;
                                }
                                else if (key.EndsWith(".Name", StringComparison.OrdinalIgnoreCase) && dot > 0)
                                {
                                    _textStylesByKey[rest.Substring(0, rest.Length - ".Name".Length)] = value;
                                }
                            }
                            else if (key.StartsWith("LayerItem.", StringComparison.OrdinalIgnoreCase))
                            {
                                var rest = key.Substring("LayerItem.".Length);
                                var dot = rest.IndexOf('.');
                                if (dot <= 0) continue;
                                int index;
                                if (!int.TryParse(rest.Substring(0, dot), out index)) continue;
                                var field = rest.Substring(dot + 1);
                                string[] slot;
                                if (!indexed.TryGetValue(index, out slot)) { slot = new string[2]; indexed[index] = slot; }
                                if (string.Equals(field, "Key", StringComparison.OrdinalIgnoreCase)) slot[0] = value;
                                else if (string.Equals(field, "Name", StringComparison.OrdinalIgnoreCase)) slot[1] = value;
                            }
                            else if (key.StartsWith("SystemTag.", StringComparison.OrdinalIgnoreCase))
                            {
                                var tag = key.Substring("SystemTag.".Length);
                                var dot = tag.LastIndexOf('.');
                                if (dot > 0) tag = tag.Substring(0, dot);
                                _layersBySystemTag[tag] = value;
                            }
                            else if (key.StartsWith("Layer.", StringComparison.OrdinalIgnoreCase) &&
                                     key.EndsWith(".Name", StringComparison.OrdinalIgnoreCase))
                            {
                                var inner = key.Substring("Layer.".Length);
                                inner = inner.Substring(0, inner.Length - ".Name".Length);
                                _layersByKey[inner] = value;
                            }
                        }
                        foreach (var slot in indexed.Values)
                            if (!string.IsNullOrWhiteSpace(slot[0]) && !string.IsNullOrWhiteSpace(slot[1]))
                                _layersBySystemTag[slot[0]] = slot[1];
                        foreach (var slot in indexedText.Values)
                            if (!string.IsNullOrWhiteSpace(slot[0]) && !string.IsNullOrWhiteSpace(slot[1]))
                                _textStylesByKey[slot[0]] = slot[1];
                    }
                }
                catch { }
                _loaded = true;
            }
        }
    }
}
