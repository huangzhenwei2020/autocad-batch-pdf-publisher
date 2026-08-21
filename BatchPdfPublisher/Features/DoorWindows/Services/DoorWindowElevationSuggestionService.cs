using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Linq;
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
            ApplyConstructionDefaults(item);
            item.Material = IsWindowType(type) ? "玻璃" : "无";
            if (IsWindowType(type) && item.SillHeight <= 0d && !item.SillHeightSuppressed) item.SillHeight = 900d;
            if (!IsWindowType(type)) { item.SillHeight = 0d; item.SillHeightSuppressed = false; }
            item.AtlasName = string.IsNullOrWhiteSpace(item.AtlasName) ? InferAtlas(code, type, item.SourceNote) : NormalizeAtlasName(item.AtlasName);
            if (string.IsNullOrWhiteSpace(item.Remarks)) item.Remarks = InferFireRating(code);

            if (type == "推拉门")
            {
                ApplyDoorDefaults(item, type);
                return;
            }
            if (type.Contains("门") && type != "门联窗")
            {
                ApplyDoorDefaults(item, type);
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
                ApplyBayWindowDefaults(item); return;
            }
            if (type == "拱形窗" || code.StartsWith("GXC", StringComparison.OrdinalIgnoreCase) || code.StartsWith("YX", StringComparison.OrdinalIgnoreCase))
            {
                item.ElevationType = "拱形窗"; item.DivisionPreset = "拱形亮子"; item.OpeningMode = "固定"; return;
            }
            ApplyWindowDefaults(item, code, type);
        }

        public static string InferTypeFromCode(string code, string fallback)
        {
            var value = Regex.Replace((code ?? string.Empty).Trim().ToUpperInvariant(), @"\s+", string.Empty);
            if (value.StartsWith("RFM")) return "人防门";
            if (value.StartsWith("FM") || value.StartsWith("FHM")) return FireType(value, "防火门");
            if (value.StartsWith("TLM")) return "推拉门";
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

        public static void ApplyConstructionDefaults(DoorWindowScheduleItem item)
        {
            if (item == null) return;
            var type = (item.ElevationType ?? string.Empty).Trim();
            if (type.Contains("门"))
            {
                item.HasOuterFrame = true;
                item.HasMullion = true;
                if (item.OuterFrameWidth <= 0d) item.OuterFrameWidth = 50d;
                if (item.MullionWidth <= 0d) item.MullionWidth = 50d;
                item.DoorFrameType = "N型";
                item.DoorFrameWidth = 50d;
            }
            else if (IsWindowType(type))
            {
                item.HasOuterFrame = true;
                item.HasMullion = true;
                if (item.OuterFrameWidth <= 0d) item.OuterFrameWidth = 50d;
                if (item.MullionWidth <= 0d) item.MullionWidth = 50d;
                item.DoorFrameType = "口型";
                item.DoorFrameWidth = 50d;
            }
        }

        /// <summary>
        /// 新建门窗立面统一采用可编辑的实际尺寸分格：亮子和扇宽不会因为洞口加宽而超过做法上限。
        /// 坐标均以扣除安装缝后的框口左下角为原点，和分格编辑器保存的格式完全一致。
        /// </summary>
        private static void ApplyWindowDefaults(DoorWindowScheduleItem item, string code, string type)
        {
            var width = ClearWidth(item); var height = ClearHeight(item);
            var fixedWindow = type == "带形窗" || type == "转角窗" || type == "高窗" || code.StartsWith("GC", StringComparison.OrdinalIgnoreCase) || code.StartsWith("DXC", StringComparison.OrdinalIgnoreCase) || code.StartsWith("ZJC", StringComparison.OrdinalIgnoreCase);
            var hasTopLight = item.Height > 1800d && height > 500d;
            var lowerHeight = hasTopLight ? height - 500d : height;
            var leaves = PanelCount(width, 1000d);
            var cells = new List<DoorWindowLayoutCell>();
            AddRow(cells, width, leaves, 0d, lowerHeight, index => fixedWindow ? "固定" : WindowLeafOpening(index, leaves), "玻璃", false);
            if (hasTopLight)
                AddRow(cells, width, PanelCount(width, 1500d), lowerHeight, height, index => "固定", "玻璃", false);
            ApplyCustomLayout(item, cells, fixedWindow ? "固定" : leaves == 1 ? "左平开" : "双扇平开");
            item.Material = "玻璃";
        }

        /// <summary>凸窗为三面展开：正面三等分，下部均为 500 高亮子，右上扇可开启；两侧转折面均为不竖分的窗面。</summary>
        private static void ApplyBayWindowDefaults(DoorWindowScheduleItem item)
        {
            item.ElevationType = "凸窗";
            item.HasInstallationGap = false;
            item.InstallationGap = 0d;
            item.BayLeftSide = "窗";
            item.BayRightSide = "窗";
            if (item.BayLeftDepth <= 0d) item.BayLeftDepth = 600d;
            if (item.BayRightDepth <= 0d) item.BayRightDepth = 600d;

            var width = ClearWidth(item); var height = ClearHeight(item); var lowerHeight = height > 500d ? 500d : height;
            var cells = new List<DoorWindowLayoutCell>();
            AddRow(cells, width, 3, 0d, lowerHeight, index => "固定", "玻璃", false);
            if (height > lowerHeight)
                AddRow(cells, width, 3, lowerHeight, height, index => index == 2 ? "右平开" : "固定", "玻璃", false);
            ApplyCustomLayout(item, cells, "自定义");
            item.BayLeftCellLayout = CreateBayReturnLayout(item.BayLeftDepth, height);
            item.BayRightCellLayout = CreateBayReturnLayout(item.BayRightDepth, height);
            item.Material = "玻璃";
        }

        /// <summary>门扇净宽不超过 1200；洞口高于 2400 时保留 2200 高门扇并在顶部设置玻璃亮子。</summary>
        private static void ApplyDoorDefaults(DoorWindowScheduleItem item, string type)
        {
            var width = ClearWidth(item); var height = ClearHeight(item);
            var hasTopLight = item.Height > 2400d && height > 2200d;
            var doorHeight = hasTopLight ? 2200d : height;
            var doors = PanelCount(width, 1200d);
            var cells = new List<DoorWindowLayoutCell>();
            AddRow(cells, width, doors, 0d, doorHeight, index => DoorLeafOpening(type, index, doors), hasTopLight ? "玻璃" : "无", true);
            if (hasTopLight)
            {
                AddRow(cells, width, PanelCount(width, 1500d), doorHeight, height, index => "固定", "玻璃", false);
                // 带亮子的门整套按窗的做法表达：门扇和亮子均为玻璃，出图时使用窗图层。
                item.Material = "玻璃";
            }
            else item.Material = "无";
            ApplyCustomLayout(item, cells, type == "推拉门" ? "双向推拉" : doors == 1 ? "左平开" : "双扇平开");
        }

        private static void ApplyCustomLayout(DoorWindowScheduleItem item, IList<DoorWindowLayoutCell> cells, string openingMode)
        {
            item.DivisionPreset = "自定义";
            item.OpeningMode = openingMode;
            item.CustomCellLayout = DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells);
            item.CellOpeningModes = string.Join("|", cells.Select(x => string.IsNullOrWhiteSpace(x.Opening) ? "固定" : x.Opening));
            item.CustomColumnRatios = item.CustomRowRatios = "1";
            item.CustomColumnWidths = item.CustomRowHeights = null;
        }

        private static void AddRow(ICollection<DoorWindowLayoutCell> cells, double width, int count, double bottom, double top, Func<int, string> opening, string material, bool isDoor)
        {
            if (top <= bottom || width <= 0d) return;
            count = Math.Max(1, count);
            for (var index = 0; index < count; index++)
            {
                var left = width * index / count; var right = width * (index + 1) / count;
                cells.Add(new DoorWindowLayoutCell { Left = left, Bottom = bottom, Right = right, Top = top, Opening = opening(index), Material = material, IsDoor = isDoor });
            }
        }

        private static string CreateBayReturnLayout(double width, double height)
        {
            var cells = new List<DoorWindowLayoutCell>(); var lowerHeight = height > 500d ? 500d : height;
            AddRow(cells, Math.Max(1d, width), 1, 0d, lowerHeight, index => "固定", "玻璃", false);
            if (height > lowerHeight) AddRow(cells, Math.Max(1d, width), 1, lowerHeight, height, index => "固定", "玻璃", false);
            return DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells);
        }

        private static int PanelCount(double width, double maximumWidth)
        { return Math.Max(1, (int)Math.Ceiling(Math.Max(1d, width) / maximumWidth)); }

        private static double ClearWidth(DoorWindowScheduleItem item)
        { return Math.Max(1d, item.Width - (item.HasInstallationGap ? Math.Max(0d, item.InstallationGap) * 2d : 0d)); }

        private static double ClearHeight(DoorWindowScheduleItem item)
        { return Math.Max(1d, item.Height - (item.HasInstallationGap ? Math.Max(0d, item.InstallationGap) * 2d : 0d)); }

        private static string WindowLeafOpening(int index, int count)
        {
            if (count <= 1) return "左平开";
            return index * 2 < count ? "左平开" : "右平开";
        }

        private static string DoorLeafOpening(string type, int index, int count)
        {
            if (type == "推拉门") return count <= 1 ? "右推拉" : "双向推拉";
            if (count <= 1) return "左平开";
            return index * 2 < count ? "右平开" : "左平开";
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
