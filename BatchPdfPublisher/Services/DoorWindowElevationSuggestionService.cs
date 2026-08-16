using BatchPdfPublisher.Models;
using System;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    internal static class DoorWindowElevationSuggestionService
    {
        public static void Apply(DoorWindowScheduleItem item)
        {
            if (item == null) return;
            var code = Regex.Replace((item.Code ?? string.Empty).Trim().ToUpperInvariant(), @"\s+", string.Empty);
            var type = InferTypeFromCode(code, item.ElevationType);
            item.ElevationType = type;
            item.Material = IsWindowType(type) ? "玻璃" : "无";
            if (IsWindowType(type) && item.SillHeight <= 0d && !item.SillHeightSuppressed) item.SillHeight = 900d;
            if (!IsWindowType(type)) { item.SillHeight = 0d; item.SillHeightSuppressed = false; }
            item.AtlasName = string.IsNullOrWhiteSpace(item.AtlasName) ? InferAtlas(code, type, item.SourceNote) : NormalizeAtlasName(item.AtlasName);
            if (string.IsNullOrWhiteSpace(item.Remarks)) item.Remarks = InferFireRating(code);

            if (type.Contains("门") && type != "门联窗")
            {
                item.DivisionPreset = item.Width > 1100d ? "双扇等分" : "单扇";
                item.OpeningMode = item.Width > 1100d ? "双扇平开" : "左平开";
                return;
            }
            if (type == "门联窗")
            {
                item.DivisionPreset = "门联窗"; item.OpeningMode = "双扇平开"; return;
            }
            if (type == "百叶" || type == "百叶窗" || type == "百叶门")
            {
                item.DivisionPreset = item.Width > 1600d ? "三扇等分" : item.Width > 900d ? "双扇等分" : "单扇";
                item.OpeningMode = "百叶"; return;
            }
            if (type == "凸窗" || code.StartsWith("TC", StringComparison.OrdinalIgnoreCase))
            {
                item.ElevationType = "凸窗"; item.DivisionPreset = item.Width > 1600d ? "三扇等分" : "双扇等分"; item.OpeningMode = "固定"; return;
            }
            if (type == "带形窗" || code.StartsWith("DXC", StringComparison.OrdinalIgnoreCase))
            {
                item.ElevationType = "带形窗"; item.DivisionPreset = item.Width > 3000d ? "五扇等分" : item.Width > 2200d ? "四扇等分" : "三扇等分"; item.OpeningMode = "固定"; return;
            }
            if (type == "转角窗" || code.StartsWith("ZJC", StringComparison.OrdinalIgnoreCase))
            {
                item.ElevationType = "转角窗"; item.DivisionPreset = item.Width > 2200d ? "四扇等分" : "双扇等分"; item.OpeningMode = "固定"; return;
            }
            if (type == "拱形窗" || code.StartsWith("GXC", StringComparison.OrdinalIgnoreCase) || code.StartsWith("YX", StringComparison.OrdinalIgnoreCase))
            {
                item.ElevationType = "拱形窗"; item.DivisionPreset = "拱形亮子"; item.OpeningMode = "固定"; return;
            }
            // Size-only inference is deliberately conservative: it proposes a
            // common shop-drawing layout but remains editable in the grid.
            item.DivisionPreset = item.Width <= 900d ? "单扇" : item.Width <= 1800d ? "双扇等分" : "三扇等分";
            item.OpeningMode = code.StartsWith("GC", StringComparison.OrdinalIgnoreCase) ? "固定" : item.Width <= 900d ? "左平开" : "双扇平开";
        }

        public static string InferTypeFromCode(string code, string fallback)
        {
            var value = Regex.Replace((code ?? string.Empty).Trim().ToUpperInvariant(), @"\s+", string.Empty);
            if (value.StartsWith("RFM")) return "人防门";
            if (value.StartsWith("FM") || value.StartsWith("FHM")) return FireType(value, "防火门");
            if (value.StartsWith("BM")) return "百叶门";
            if (value.StartsWith("MLC")) return "门联窗";
            if (value.StartsWith("DXC")) return "带形窗";
            if (value.StartsWith("ZJC")) return "转角窗";
            if (value.StartsWith("GXC")) return "拱形窗";
            if (value.StartsWith("TC")) return "凸窗";
            if (value.StartsWith("GC")) return "高窗";
            if (value.StartsWith("BYC") || value.StartsWith("BY")) return "百叶窗";
            if (value.StartsWith("FC") || value.StartsWith("FHC")) return FireType(value, "防火窗");
            if (value.StartsWith("C")) return "普通窗";
            if (value.StartsWith("M")) return "普通门";
            if (fallback == "门") return "普通门";
            if (fallback == "窗") return "普通窗";
            return string.IsNullOrWhiteSpace(fallback) ? "待确认" : fallback;
        }

        public static string InferAtlas(string code, string type, string note)
        {
            var value = ((code ?? string.Empty) + " " + (type ?? string.Empty) + " " + (note ?? string.Empty)).ToUpperInvariant();
            if (value.Contains("五金") || value.Contains("附件")) return "《门、窗、幕墙窗用五金附件》（04J631）";
            if (value.Contains("百叶") || value.StartsWith("BY")) return "《百叶窗》（05J624-1）";
            if (value.Contains("防火") || value.StartsWith("FM") || value.StartsWith("FHM") || value.StartsWith("FC")) return "《防火门窗》（12J609）";
            if (value.Contains("不锈钢")) return "《不锈钢门窗》（13J602-3）";
            return "《铝合金门窗》（22J603-1）";
        }

        public static string NormalizeAtlasName(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text == "22J603-1") return "《铝合金门窗》（22J603-1）";
            if (text == "12J609") return "《防火门窗》（12J609）";
            if (text == "13J602-3") return "《不锈钢门窗》（13J602-3）";
            if (text == "04J631") return "《门、窗、幕墙窗用五金附件》（04J631）";
            if (text == "05J624-1") return "《百叶窗》（05J624-1）";
            return text;
        }

        /// <summary>图集名称下拉候选：供用户在网格中选用，避免默认判断不准确时无法手动指定。</summary>
        public static string[] AtlasChoices()
        {
            return new[]
            {
                "《铝合金门窗》（22J603-1）",
                "《防火门窗》（12J609）",
                "《不锈钢门窗》（13J602-3）",
                "《门、窗、幕墙窗用五金附件》（04J631）",
                "《百叶窗》（05J624-1）"
            };
        }

        private static string FireType(string code, string suffix)
        {
            var match = Regex.Match(code ?? string.Empty, "(甲|乙|丙)");
            return match.Success ? match.Groups[1].Value + "级" + suffix : suffix + "（等级待确认）";
        }

        private static bool IsWindowType(string type)
        { return (type ?? string.Empty).Contains("窗") || type == "凸窗" || type == "高窗" || type == "带形窗" || type == "转角窗" || type == "拱形窗" || type == "百叶窗" || type == "门联窗"; }

        private static string InferFireRating(string code)
        {
            var match = Regex.Match(code ?? string.Empty, @"^(?:R?FM|FHM)(甲|乙|丙)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value + "级防火" : string.Empty;
        }
    }
}
