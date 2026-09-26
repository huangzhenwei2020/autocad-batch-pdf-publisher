using System;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// 把"模型里的洞口 + 类型库里的做法"翻译成插件已有门窗立面生成器的输入。
    ///
    /// 这一层是**适配器**，不是另一套画法：
    /// 尺寸以模型里的洞口为准（三维开洞用的是它），做法（分格/开启/框料/凸窗）以类型库为准，
    /// 然后整份交给 <see cref="DoorWindowElevationGeometryBuilder"/> —— CAD 里画门窗立面用的是同一份代码，
    /// 所以程序出的立面门窗与插件画的不会两样。类型库改了，重算视图就跟着变（不复制参数）。
    /// </summary>
    public static class OpeningElevationAdapter
    {
        /// <summary>按编号查类型库；查不到就返回一个"单格"做法（只画洞口轮廓）。</summary>
        public static OpeningTypeModel Resolve(OpeningTypeLibraryDocument library, OpeningModel opening)
        {
            if (library == null || opening == null) return null;
            return library.FindType(opening.Code);
        }

        /// <summary>
        /// 组装生成器输入。视图里洞口的实际尺寸（宽/高）优先，做法其余取自类型库；
        /// 类型库缺失时退化成"普通窗/普通门 + 单格"。
        ///
        /// <paramref name="simplifiedDetail"/> = true 时按**1:100 及更小的立面图**简化：
        /// 不画 20mm 安装缝、不画 50mm 门扇内框、不画玻璃/百叶材料符号
        ///（这些在 1:50 的门窗立面详图里才看得清，1:100 上只有零点几毫米，纯属糊线）。
        /// 分格与开启线一律保留 —— 那正是立面图要表达的内容。
        /// </summary>
        public static DoorWindowScheduleItem ToScheduleItem(OpeningModel opening, OpeningTypeModel type,
            double width, double height, bool simplifiedDetail = false)
        {
            if (opening == null) throw new ArgumentNullException(nameof(opening));
            var kind = string.IsNullOrWhiteSpace(opening.Kind) ? "窗" : opening.Kind.Trim();
            var isDoor = kind.IndexOf("门", StringComparison.Ordinal) >= 0;
            var item = new DoorWindowScheduleItem
            {
                Code = opening.Code,
                Width = width > 0.5d ? width : opening.Width,
                Height = height > 0.5d ? height : opening.Height,
                SillHeight = opening.Sill,
                ElevationType = type == null || string.IsNullOrWhiteSpace(type.ElevationType)
                    ? (isDoor ? "普通门" : "普通窗")
                    : type.ElevationType.Trim(),
                DivisionPreset = type == null ? null : type.DivisionPreset,
                OpeningMode = type == null ? null : type.OpeningMode,
                HasInstallationGap = type != null && type.HasInstallationGap && !simplifiedDetail,
                InstallationGap = type == null || simplifiedDetail ? 0d : type.InstallationGap,
                HasOuterFrame = type == null || type.HasOuterFrame,
                OuterFrameWidth = type == null ? 50d : type.OuterFrameWidth,
                HasMullion = type == null || type.HasMullion,
                MullionWidth = type == null ? 50d : type.MullionWidth,
                DoorFrameType = type == null || string.IsNullOrWhiteSpace(type.DoorFrameType) ? "N型" : type.DoorFrameType,
                DoorFrameWidth = type == null || simplifiedDetail ? 0d : Math.Max(0d, type.DoorFrameWidth),
                CustomColumnRatios = type == null ? null : type.CustomColumnRatios,
                CustomRowRatios = type == null ? null : type.CustomRowRatios,
                CustomColumnWidths = type == null ? null : type.CustomColumnWidths,
                CustomRowHeights = type == null ? null : type.CustomRowHeights,
                CustomCellLayout = type == null ? null : type.CustomCellLayout,
                CellOpeningModes = type == null ? null : type.CellOpeningModes,
                DoorPlacement = type == null || string.IsNullOrWhiteSpace(type.DoorPlacement) ? "靠左" : type.DoorPlacement,
                DoorEdgeDistance = type == null ? 0d : type.DoorEdgeDistance,
                BayLeftSide = type == null || string.IsNullOrWhiteSpace(type.BayLeftSide) ? "墙" : type.BayLeftSide,
                BayRightSide = type == null || string.IsNullOrWhiteSpace(type.BayRightSide) ? "墙" : type.BayRightSide,
                BayLeftDepth = type == null || type.BayLeftDepth <= 0d ? 600d : type.BayLeftDepth,
                BayRightDepth = type == null || type.BayRightDepth <= 0d ? 600d : type.BayRightDepth,
                BayLeftCellLayout = type == null ? null : type.BayLeftCellLayout,
                BayRightCellLayout = type == null ? null : type.BayRightCellLayout,
                Material = type == null || string.IsNullOrWhiteSpace(type.Material) || simplifiedDetail ? "无" : type.Material,
                AtlasName = type == null ? null : type.AtlasName
            };
            return item;
        }
    }
}
