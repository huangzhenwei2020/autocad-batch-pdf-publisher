using BatchPdfPublisher.Models;
using System;
using System.Globalization;
using System.Linq;

namespace BatchPdfPublisher.Views
{
    /// <summary>
    /// Public reflection bridge used by optional modules such as StairDetail.
    /// Keeping the bridge in the main plug-in guarantees that those modules
    /// use the very same "编辑当前分格" form and geometry builder as MCLM,
    /// without introducing a compile-time circular dependency.
    /// </summary>
    public static class DoorWindowDivisionEditorBridge
    {
        public static DoorWindowDivisionEditResult Edit(
            string code,
            int openingType,
            double width,
            double height,
            string customCellLayout,
            string cellOpeningModes,
            bool hasInstallationGap,
            double installationGap,
            bool hasOuterFrame,
            double outerFrameWidth,
            bool hasMullion,
            double mullionWidth,
            string doorFrameType,
            double doorFrameWidth,
            string material)
        {
            var isDoor = openingType == 1;
            var item = new DoorWindowScheduleItem
            {
                Code = string.IsNullOrWhiteSpace(code) ? "楼梯门窗" : code,
                Width = Math.Max(1.0, width),
                Height = Math.Max(1.0, height),
                ElevationType = isDoor ? "普通门" : "普通窗",
                DivisionPreset = "自定义",
                OpeningMode = "自定义",
                CustomCellLayout = customCellLayout,
                CellOpeningModes = cellOpeningModes,
                HasInstallationGap = hasInstallationGap,
                InstallationGap = Math.Max(0.0, installationGap),
                HasOuterFrame = hasOuterFrame,
                OuterFrameWidth = Math.Max(0.0, outerFrameWidth),
                HasMullion = hasMullion,
                MullionWidth = Math.Max(0.0, mullionWidth),
                DoorFrameType = string.IsNullOrWhiteSpace(doorFrameType) ? "N型" : doorFrameType,
                DoorFrameWidth = Math.Max(0.0, doorFrameWidth),
                Material = string.IsNullOrWhiteSpace(material) ? "玻璃" : material
            };

            using (var dialog = new CustomDoorWindowDivisionForm(item))
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
            }

            var geometry = DoorWindowElevationGeometryBuilder.Build(item);
            return new DoorWindowDivisionEditResult
            {
                CustomCellLayout = item.CustomCellLayout,
                CellOpeningModes = item.CellOpeningModes,
                HasInstallationGap = item.HasInstallationGap,
                InstallationGap = item.InstallationGap,
                HasOuterFrame = item.HasOuterFrame,
                OuterFrameWidth = item.OuterFrameWidth,
                HasMullion = item.HasMullion,
                MullionWidth = item.MullionWidth,
                DoorFrameType = item.DoorFrameType,
                DoorFrameWidth = item.DoorFrameWidth,
                Material = item.Material,
                GeometryLines = string.Join("|", geometry.Lines.Select(line => string.Join(",", new[]
                {
                    line.X1.ToString("0.###", CultureInfo.InvariantCulture),
                    line.Y1.ToString("0.###", CultureInfo.InvariantCulture),
                    line.X2.ToString("0.###", CultureInfo.InvariantCulture),
                    line.Y2.ToString("0.###", CultureInfo.InvariantCulture),
                    ((int)line.Role).ToString(CultureInfo.InvariantCulture)
                })))
            };
        }
    }

    public sealed class DoorWindowDivisionEditResult
    {
        public string CustomCellLayout { get; set; }
        public string CellOpeningModes { get; set; }
        public bool HasInstallationGap { get; set; }
        public double InstallationGap { get; set; }
        public bool HasOuterFrame { get; set; }
        public double OuterFrameWidth { get; set; }
        public bool HasMullion { get; set; }
        public double MullionWidth { get; set; }
        public string DoorFrameType { get; set; }
        public double DoorFrameWidth { get; set; }
        public string Material { get; set; }
        public string GeometryLines { get; set; }
    }
}
