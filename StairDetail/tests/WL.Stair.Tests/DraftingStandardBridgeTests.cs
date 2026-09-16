using System;
using System.IO;
using WL.Stair.CadShared;

namespace WL.Stair.Tests
{
    /// <summary>
    /// 校验楼梯读取制图标准（BZS）的行为：命名解析、旧格式兼容、
    /// 以及标准缺失/缺失项时必须回退内置常量（保证不因标准问题而失效）。
    /// </summary>
    internal static class DraftingStandardBridgeTests
    {
        /// <summary>返回执行的测试数，供总计数汇总。</summary>
        public static int Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "WanluoStairStandardTests", Guid.NewGuid().ToString("N"));
            var settingsDirectory = Path.Combine(root, "通用设置");
            Directory.CreateDirectory(settingsDirectory);
            var standardFile = Path.Combine(settingsDirectory, "drafting-standard.ini");
            Environment.SetEnvironmentVariable(DraftingStandardBridge.SettingsDirectoryOverrideVariable, root);
            try
            {
                ResolvesCurrentFormat();
                ResolvesLegacyFormat();
                ResolvesTextStylesAndDimensionPrefix();
                FallsBackWhenKeyMissing();
                FallsBackWhenFileMissing(standardFile);
                return 5;
            }
            finally
            {
                Environment.SetEnvironmentVariable(DraftingStandardBridge.SettingsDirectoryOverrideVariable, null);
                DraftingStandardBridge.Reset();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // 主插件当前保存格式：LayerItem.N.Key + LayerItem.N.Name
        private static void ResolvesCurrentFormat()
        {
            WriteStandard("Layer.Count=2",
                "LayerItem.0.Key=StairOutline", "LayerItem.0.Name=AR-楼梯-轮廓",
                "LayerItem.1.Key=StairSide", "LayerItem.1.Name=AR-楼梯-侧面");
            DraftingStandardBridge.Reset();
            TestAssert.Equal("AR-楼梯-轮廓", DraftingStandardBridge.LayerName("StairOutline", "WL_楼梯_轮廓"),
                "标准里的图层名应被采用");
            TestAssert.Equal("AR-楼梯-侧面", DraftingStandardBridge.LayerName("StairSide", "WL-楼梯侧面"),
                "第二个图层名也应被采用");
            Console.WriteLine("PASS StairBridgeReadsStandardLayerNames");
        }

        // 旧配置格式：Layer.<Key>.Name
        private static void ResolvesLegacyFormat()
        {
            WriteStandard("Layer.StairOutline.Name=LEGACY-轮廓", "Layer.StairTread.Name=LEGACY-踏步");
            DraftingStandardBridge.Reset();
            TestAssert.Equal("LEGACY-轮廓", DraftingStandardBridge.LayerName("StairOutline", "WL_楼梯_轮廓"),
                "旧格式 Layer.<Key>.Name 也应被识别");
            TestAssert.Equal("LEGACY-踏步", DraftingStandardBridge.LayerName("StairTread", "WL_楼梯_踏步"),
                "旧格式的第二个键也应被识别");
            Console.WriteLine("PASS StairBridgeReadsLegacyFormat");
        }

        // 文字样式用的是 Text.N.Key/Name（不是 TextItem），标注前缀是 Dimension.StylePrefix。
        private static void ResolvesTextStylesAndDimensionPrefix()
        {
            WriteStandard("Text.Count=2",
                "Text.0.Key=Title", "Text.0.Name=AR-文字-标题",
                "Text.1.Key=Annotation", "Text.1.Name=AR-文字-标注",
                "Dimension.StylePrefix=AR-标注-1_");
            DraftingStandardBridge.Reset();
            TestAssert.Equal("AR-文字-标题", DraftingStandardBridge.TextStyleName("Title", "WL-文字-标题"),
                "标题文字样式名应被采用");
            TestAssert.Equal("AR-文字-标注", DraftingStandardBridge.TextStyleName("Annotation", "WL-文字-标注"),
                "标注文字样式名应被采用");
            TestAssert.Equal("WL-文字-正文", DraftingStandardBridge.TextStyleName("Body", "WL-文字-正文"),
                "标准里没有的样式键应回退内置名");
            TestAssert.Equal("AR-标注-1_", DraftingStandardBridge.DimensionStylePrefix("WL-标注-1_"),
                "标注样式前缀应被采用");
            Console.WriteLine("PASS StairBridgeReadsTextStylesAndDimensionPrefix");

            // 旧配置的 Text.<Key>.Name 也要认识
            WriteStandard("Text.Title.Name=LEGACY-标题");
            DraftingStandardBridge.Reset();
            TestAssert.Equal("LEGACY-标题", DraftingStandardBridge.TextStyleName("Title", "WL-文字-标题"),
                "旧格式 Text.<Key>.Name 也应被识别");
            Console.WriteLine("PASS StairBridgeReadsLegacyTextStyleFormat");
        }

        // 标准里没有这个键时必须回退内置常量，不能让楼梯出不了图。
        private static void FallsBackWhenKeyMissing()
        {
            WriteStandard("LayerItem.0.Key=StairOutline", "LayerItem.0.Name=AR-楼梯-轮廓");
            DraftingStandardBridge.Reset();
            TestAssert.Equal("WL_剖面墙", DraftingStandardBridge.LayerName("StairWall", "WL_剖面墙"),
                "标准缺少该键时应回退内置常量");
            TestAssert.Equal("AR-楼梯-轮廓", DraftingStandardBridge.LayerName("StairOutline", "WL_楼梯-轮廓"),
                "存在的键仍应生效");
            Console.WriteLine("PASS StairBridgeFallsBackForMissingKey");
        }

        private static void FallsBackWhenFileMissing(string standardFile)
        {
            if (File.Exists(standardFile)) File.Delete(standardFile);
            DraftingStandardBridge.Reset();
            TestAssert.Equal("WL_楼梯_轮廓", DraftingStandardBridge.LayerName("StairOutline", "WL_楼梯_轮廓"),
                "标准文件不存在时应回退内置常量");
            Console.WriteLine("PASS StairBridgeFallsBackWithoutStandardFile");
        }

        private static void WriteStandard(params string[] lines)
        {
            var directory = Path.Combine(Environment.GetEnvironmentVariable(DraftingStandardBridge.SettingsDirectoryOverrideVariable), "通用设置");
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "drafting-standard.ini"), lines);
        }
    }
}
