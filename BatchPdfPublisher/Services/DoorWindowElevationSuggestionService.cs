using BatchPdfPublisher.Models;
using System;

namespace BatchPdfPublisher.Services
{
    internal static class DoorWindowElevationSuggestionService
    {
        public static void Apply(DoorWindowScheduleItem item)
        {
            if (item == null) return;
            var type = item.ElevationType ?? string.Empty;
            var code = (item.Code ?? string.Empty).ToUpperInvariant();
            if (type == "待确认")
            {
                if (code.StartsWith("TC", StringComparison.OrdinalIgnoreCase)) type = "凸窗";
                else if (code.StartsWith("GC", StringComparison.OrdinalIgnoreCase)) type = "窗";
            }
            item.ElevationType = type;

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
            if (type == "百叶")
            {
                item.DivisionPreset = item.Width > 1600d ? "三扇等分" : item.Width > 900d ? "双扇等分" : "单扇";
                item.OpeningMode = "百叶"; return;
            }
            if (type == "凸窗" || code.StartsWith("TC", StringComparison.OrdinalIgnoreCase))
            {
                item.ElevationType = "凸窗"; item.DivisionPreset = item.Width > 1600d ? "三扇等分" : "双扇等分"; item.OpeningMode = "固定"; return;
            }
            // Size-only inference is deliberately conservative: it proposes a
            // common shop-drawing layout but remains editable in the grid.
            item.DivisionPreset = item.Width <= 900d ? "单扇" : item.Width <= 1800d ? "双扇等分" : "三扇等分";
            item.OpeningMode = code.StartsWith("GC", StringComparison.OrdinalIgnoreCase) ? "固定" : item.Width <= 900d ? "左平开" : "双扇平开";
        }
    }
}
