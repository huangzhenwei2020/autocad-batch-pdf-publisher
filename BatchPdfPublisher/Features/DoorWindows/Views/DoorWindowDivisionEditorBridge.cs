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
            var item = CreateItem(code, openingType, width, height, customCellLayout,
                cellOpeningModes, hasInstallationGap, installationGap, hasOuterFrame,
                outerFrameWidth, hasMullion, mullionWidth, doorFrameType,
                doorFrameWidth, material);

            using (var dialog = new CustomDoorWindowDivisionForm(item))
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
            }

            return BuildResult(item);
        }

        public static DoorWindowDivisionEditResult Build(
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
            return BuildResult(CreateItem(code, openingType, width, height,
                customCellLayout, cellOpeningModes, hasInstallationGap,
                installationGap, hasOuterFrame, outerFrameWidth, hasMullion,
                mullionWidth, doorFrameType, doorFrameWidth, material));
        }

        private static DoorWindowScheduleItem CreateItem(
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
            var normalizedWidth = Math.Max(1.0, width);
            var normalizedHeight = Math.Max(1.0, height);
            var normalizedGap = hasInstallationGap ? Math.Max(0.0, installationGap) : 0.0;
            var clearWidth = Math.Max(1.0, normalizedWidth - normalizedGap * 2.0);
            var clearHeight = Math.Max(1.0, normalizedHeight - normalizedGap * 2.0);
            var normalizedMaterial = string.IsNullOrWhiteSpace(material)
                ? (openingType == 1 ? "无" : "玻璃")
                : material;
            if (string.IsNullOrWhiteSpace(customCellLayout))
            {
                customCellLayout = CreateDefaultCellLayout(
                    openingType, clearWidth, clearHeight, normalizedMaterial);
                cellOpeningModes = openingType == 1 ? "左平开" : "右平开|左平开";
            }
            return new DoorWindowScheduleItem
            {
                Code = string.IsNullOrWhiteSpace(code) ? "楼梯门窗" : code,
                Width = normalizedWidth,
                Height = normalizedHeight,
                ElevationType = openingType == 1 ? "普通门" : "普通窗",
                DivisionPreset = "自定义",
                OpeningMode = "自定义",
                CustomCellLayout = customCellLayout,
                CellOpeningModes = cellOpeningModes,
                HasInstallationGap = hasInstallationGap,
                InstallationGap = normalizedGap,
                HasOuterFrame = hasOuterFrame,
                OuterFrameWidth = Math.Max(0.0, outerFrameWidth),
                HasMullion = hasMullion,
                MullionWidth = Math.Max(0.0, mullionWidth),
                DoorFrameType = string.IsNullOrWhiteSpace(doorFrameType) ? "N型" : doorFrameType,
                DoorFrameWidth = Math.Max(0.0, doorFrameWidth),
                Material = normalizedMaterial
            };
        }

        private static string CreateDefaultCellLayout(
            int openingType,
            double width,
            double height,
            string material)
        {
            Func<double, string> number = value => value.ToString(
                "0.###", CultureInfo.InvariantCulture);
            if (openingType == 1)
                return "0,0," + number(width) + "," + number(height)
                    + ",左平开,1,0," + material;
            var middle = width / 2.0;
            return "0,0," + number(middle) + "," + number(height)
                + ",右平开,0,0," + material + "|"
                + number(middle) + ",0," + number(width) + "," + number(height)
                + ",左平开,0,0," + material;
        }

        private static DoorWindowDivisionEditResult BuildResult(DoorWindowScheduleItem item)
        {
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
