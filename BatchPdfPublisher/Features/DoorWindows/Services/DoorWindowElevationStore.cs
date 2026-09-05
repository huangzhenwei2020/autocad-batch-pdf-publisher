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
        private const string SessionFileName = "door-window-elevation-session.json";
        private static string PathName { get { return UserDataPaths.SettingsFile(FileName); } }
        private static string SessionPathName { get { return UserDataPaths.SettingsFile(SessionFileName); } }

        public DoorWindowElevationSession LoadSession()
        {
            try
            {
                if (!File.Exists(SessionPathName)) return null;
                using (var stream = File.OpenRead(SessionPathName))
                {
                    var session = (DoorWindowElevationSession)new DataContractJsonSerializer(typeof(DoorWindowElevationSession)).ReadObject(stream);
                    var projectName = new PublishPlanStore().GetActiveProject()?.Name ?? "默认项目";
                    return session != null && string.Equals(session.ProjectName, projectName, StringComparison.OrdinalIgnoreCase) ? session : null;
                }
            }
            catch { return null; }
        }

        public void SaveSession(bool floorStatistics, string baseSourceHandle, IEnumerable<DoorWindowScheduleItem> baseItems, IEnumerable<DoorWindowFloorSourcePreference> floorSources)
        {
            var projectName = new PublishPlanStore().GetActiveProject()?.Name ?? "默认项目";
            var items = (baseItems ?? Enumerable.Empty<DoorWindowScheduleItem>()).Where(x => x != null).ToList();
            var existing = LoadSession();
            if (items.Count == 0 && existing != null && existing.BaseItems != null && existing.BaseItems.Count > 0)
                items = existing.BaseItems;
            var session = new DoorWindowElevationSession
            {
                ProjectName = projectName,
                FloorStatistics = floorStatistics,
                BaseSourceHandle = baseSourceHandle,
                BaseItems = items,
                FloorSources = (floorSources ?? Enumerable.Empty<DoorWindowFloorSourcePreference>()).ToList()
            };
            using (var stream = File.Create(SessionPathName)) new DataContractJsonSerializer(typeof(DoorWindowElevationSession)).WriteObject(stream, session);
        }

        public List<DoorWindowElevationPreference> LoadForActiveProject()
        {
            var projectName = new PublishPlanStore().GetActiveProject()?.Name ?? "默认项目";
            return LoadAll().Where(x => string.Equals(x.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public void SaveForActiveProject(IEnumerable<DoorWindowScheduleItem> items, int drawingScale)
        {
            var projectName = new PublishPlanStore().GetActiveProject()?.Name ?? "默认项目";
            var all = LoadAll();
            foreach (var item in items.Where(x => !string.IsNullOrWhiteSpace(x.Code)))
            {
                var preference = all.FirstOrDefault(x => string.Equals(x.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Code, item.Code.Trim(), StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(x.Width - item.Width) < .01d && Math.Abs(x.Height - item.Height) < .01d);
                if (preference == null)
                {
                    preference = new DoorWindowElevationPreference { ProjectName = projectName, Code = item.Code.Trim(), Width = item.Width, Height = item.Height };
                    all.Add(preference);
                }
                preference.ElevationType = item.ElevationType;
                preference.DivisionPreset = item.DivisionPreset;
                preference.OpeningMode = item.OpeningMode;
                preference.HasInstallationGap = item.HasInstallationGap;
                preference.InstallationGap = item.InstallationGap;
                preference.HasOuterFrame = item.HasOuterFrame;
                preference.OuterFrameWidth = item.OuterFrameWidth;
                preference.HasMullion = item.HasMullion;
                preference.MullionWidth = item.MullionWidth;
                preference.DoorFrameType = item.DoorFrameType;
                preference.DoorFrameWidth = item.DoorFrameWidth;
                preference.DrawingScale = Math.Max(1, drawingScale);
                preference.CustomColumnRatios = item.CustomColumnRatios;
                preference.CustomRowRatios = item.CustomRowRatios;
                preference.CustomColumnWidths = item.CustomColumnWidths;
                preference.CustomRowHeights = item.CustomRowHeights;
                preference.CustomCellLayout = item.CustomCellLayout;
                preference.CellOpeningModes = item.CellOpeningModes;
                preference.DoorPlacement = item.DoorPlacement;
                preference.DoorEdgeDistance = item.DoorEdgeDistance;
                preference.BayLeftSide = item.BayLeftSide;
                preference.BayRightSide = item.BayRightSide;
                preference.BayLeftDepth = item.BayLeftDepth;
                preference.BayRightDepth = item.BayRightDepth;
                preference.BayLeftCellLayout = item.BayLeftCellLayout;
                preference.BayRightCellLayout = item.BayRightCellLayout;
                preference.Material = item.Material;
                preference.AtlasName = item.AtlasName;
                preference.Remarks = item.Remarks;
                preference.SillHeight = item.SillHeight;
                preference.HasSillHeight = true;
                preference.SillHeightSuppressed = item.SillHeightSuppressed;
                /*
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
                    DoorFrameWidth = item.DoorFrameWidth,
                    DrawingScale = Math.Max(1, drawingScale),
                    CustomColumnRatios = item.CustomColumnRatios,
                    CustomRowRatios = item.CustomRowRatios,
                    CustomColumnWidths = item.CustomColumnWidths,
                    CustomRowHeights = item.CustomRowHeights,
                    CustomCellLayout = item.CustomCellLayout,
                    CellOpeningModes = item.CellOpeningModes,
                    DoorPlacement = item.DoorPlacement,
                    DoorEdgeDistance = item.DoorEdgeDistance,
                    BayLeftSide = item.BayLeftSide,
                    BayRightSide = item.BayRightSide,
                    BayLeftDepth = item.BayLeftDepth,
                    BayRightDepth = item.BayRightDepth,
                    BayLeftCellLayout = item.BayLeftCellLayout,
                    BayRightCellLayout = item.BayRightCellLayout,
                    Material = item.Material,
                    AtlasName = item.AtlasName,
                    Remarks = item.Remarks,
                    SillHeight = item.SillHeight,
                    HasSillHeight = true,
                    SillHeightSuppressed = item.SillHeightSuppressed
                });*/
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
