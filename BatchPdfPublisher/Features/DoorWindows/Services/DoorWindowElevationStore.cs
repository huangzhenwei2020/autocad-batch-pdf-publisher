using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace BatchPdfPublisher.Services
{
    internal sealed class DoorWindowElevationStore
    {
        private const string FileName = "door-window-elevation-settings.json";
        private static string PathName { get { return UserDataPaths.SettingsFile(FileName); } }

        public List<DoorWindowElevationPreference> LoadForActiveProject()
        {
            var projectName = new PublishPlanStore().GetActiveProject()?.Name ?? "默认项目";
            return LoadAll().Where(x => string.Equals(x.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public void SaveForActiveProject(IEnumerable<DoorWindowScheduleItem> items, int drawingScale)
        {
            var projectName = new PublishPlanStore().GetActiveProject()?.Name ?? "默认项目";
            var all = LoadAll();
            all.RemoveAll(x => string.Equals(x.ProjectName, projectName, StringComparison.OrdinalIgnoreCase));
            foreach (var item in items.Where(x => !string.IsNullOrWhiteSpace(x.Code)))
            {
                all.Add(new DoorWindowElevationPreference
                {
                    ProjectName = projectName,
                    Code = item.Code.Trim(),
                    Width = item.Width,
                    Height = item.Height,
                    ElevationType = item.ElevationType,
                    DivisionPreset = item.DivisionPreset,
                    OpeningMode = item.OpeningMode,
                    HasInstallationGap = item.HasInstallationGap,
                    InstallationGap = item.InstallationGap,
                    HasOuterFrame = item.HasOuterFrame,
                    OuterFrameWidth = item.OuterFrameWidth,
                    HasMullion = item.HasMullion,
                    MullionWidth = item.MullionWidth,
                    DoorFrameType = item.DoorFrameType,
                    DrawingScale = Math.Max(1, drawingScale),
                    CustomColumnRatios = item.CustomColumnRatios,
                    CustomRowRatios = item.CustomRowRatios,
                    CustomColumnWidths = item.CustomColumnWidths,
                    CustomRowHeights = item.CustomRowHeights,
                    CustomCellLayout = item.CustomCellLayout,
                    CellOpeningModes = item.CellOpeningModes,
                    DoorPlacement = item.DoorPlacement,
                    DoorEdgeDistance = item.DoorEdgeDistance,
                    Material = item.Material,
                    AtlasName = item.AtlasName,
                    Remarks = item.Remarks,
                    SillHeight = item.SillHeight,
                    HasSillHeight = true,
                    SillHeightSuppressed = item.SillHeightSuppressed
                });
            }
            using (var stream = File.Create(PathName))
                new DataContractJsonSerializer(typeof(List<DoorWindowElevationPreference>)).WriteObject(stream, all);
        }

        private static List<DoorWindowElevationPreference> LoadAll()
        {
            try
            {
                if (!File.Exists(PathName)) return new List<DoorWindowElevationPreference>();
                using (var stream = File.OpenRead(PathName))
                    return (List<DoorWindowElevationPreference>)new DataContractJsonSerializer(typeof(List<DoorWindowElevationPreference>)).ReadObject(stream) ?? new List<DoorWindowElevationPreference>();
            }
            catch { return new List<DoorWindowElevationPreference>(); }
        }
    }
}
