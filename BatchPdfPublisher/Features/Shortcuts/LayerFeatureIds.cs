using System;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 每图层直达归层命令的功能 id 约定。
    ///
    /// 这些 id 只在本进程内使用（Ribbon、菜单栏、快捷键设置页用来区分"固定功能"和
    /// "按图层动态生成的功能"），图层快捷键本身是按**图层键**存在
    /// layer-shortcuts.ini 里的，所以这里的格式可以自由调整。
    ///
    /// **前缀为什么不是 "layer_"**：固定功能里有一个 id 就叫 <c>layer_assignment</c>
    /// （归层 GL）。用 "layer_" 当前缀时，它会被 TryParse 认成"图层命令 assignment"，
    /// 后果是：
    ///   - 快捷键设置窗口把它当成图层命令过滤掉，归层的快捷键在窗口里根本改不了；
    ///   - 它被当成图层命令统计，日志里多出一条莫名其妙的 "GL→归层"。
    /// 换成不会与固定 id 撞车的前缀，从根上消掉这类误判。
    /// </summary>
    public static class LayerFeatureIds
    {
        /// <summary>图层命令功能 id 的前缀。</summary>
        public const string Prefix = "layerrole_";

        public static string For(string layerKey)
        {
            return Prefix + (layerKey ?? string.Empty).Trim();
        }

        /// <summary>判断功能 id 是不是图层命令；是则输出图层键。</summary>
        public static bool TryParse(string featureId, out string layerKey)
        {
            layerKey = null;
            if (string.IsNullOrWhiteSpace(featureId)) return false;
            if (!featureId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
            layerKey = featureId.Substring(Prefix.Length);
            return layerKey.Length > 0;
        }
    }
}
