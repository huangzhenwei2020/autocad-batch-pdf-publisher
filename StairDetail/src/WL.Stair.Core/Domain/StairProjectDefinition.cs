using System;
using System.Collections.Generic;

namespace WL.Stair.Core.Domain
{
    public static class StairOpeningDefaults
    {
        public const double DoorWidth = 900.0;
        public const double DoorHeight = 2200.0;
        public const double WindowWidth = 1200.0;
        public const double WindowHeight = 1500.0;
        public const double WindowSillHeight = 900.0;
        public const double PlatformAxisOffset = 150.0;
    }

    public sealed class StairProjectDefinition
    {
        public StairProjectDefinition()
        {
            SchemaVersion = 21;
            Name = "楼梯大样";
            ProjectName = "未命名项目";
            SubprojectName = string.Empty;
            BuildingNumber = "1#";
            StairNumber = "LT-01";
            DrawingScale = 30;
            InsertComponentSchedule = false;
            ShowBold = true;
            ShowFill = true;
            Construction = StairConstructionDefaults.CreateDefault();
            Floors = new List<StairFloorDefinition>();
            Storeys = new List<StairStoreyDefinition>();
            WallOpenings = new List<StairWallOpeningDefinition>();
            PlanSources = new List<StairPlanSourceDefinition>();
            CombinedLayoutColumnRatios = new List<double>();
            CombinedLayoutRowRatios = new List<double>();
            CombinedLayoutItemOrder = new List<string>();
            CombinedLayoutPlacements = new List<StairLayoutPlacementDefinition>();
        }

        public int SchemaVersion { get; set; }

        public string Name { get; set; }

        public string ProjectName { get; set; }

        public string SubprojectName { get; set; }

        public string BuildingNumber { get; set; }

        public string StairNumber { get; set; }

        public int DrawingScale { get; set; }

        public bool InsertComponentSchedule { get; set; }

        public bool ShowBold { get; set; }

        public bool ShowFill { get; set; }

        public double BaseElevation { get; set; }

        public int BasementStoreyCount { get; set; }

        public StairConstructionDefaults Construction { get; set; }

        public IList<StairFloorDefinition> Floors { get; set; }

        public IList<StairStoreyDefinition> Storeys { get; set; }

        /// <summary>
        /// Optional door/window settings keyed by the stable wall-segment id
        /// emitted by the section geometry builder.  Keeping these records at
        /// project level makes the feature additive: legacy floor, flight,
        /// platform and construction parameters are not replaced or rewritten.
        /// </summary>
        public IList<StairWallOpeningDefinition> WallOpenings { get; set; }

        /// <summary>
        /// Optional, additive plan-source registrations. An empty collection
        /// preserves the complete pre-plan-capture LTDY behavior.
        /// </summary>
        public IList<StairPlanSourceDefinition> PlanSources { get; set; }

        /// <summary>
        /// User-adjusted whole-sheet grid. Ratios are portable and therefore
        /// remain valid when the complete plugin folder is moved to a new PC.
        /// A changed automatic grid count safely ignores stale ratios.
        /// </summary>
        public int CombinedLayoutGridColumns { get; set; }

        public int CombinedLayoutGridRows { get; set; }

        public IList<double> CombinedLayoutColumnRatios { get; set; }

        public IList<double> CombinedLayoutRowRatios { get; set; }

        /// <summary>
        /// User-defined order of plan and section items in the whole-sheet
        /// grid. Keys are stable floor ids plus the reserved SECTION key.
        /// </summary>
        public IList<string> CombinedLayoutItemOrder { get; set; }

        /// <summary>
        /// Explicit whole-sheet cell locations selected by the user. Missing
        /// entries continue to use the automatic packer.
        /// </summary>
        public IList<StairLayoutPlacementDefinition> CombinedLayoutPlacements { get; set; }

        public static StairProjectDefinition CreateDefault()
        {
            var project = new StairProjectDefinition();
            project.Floors.Add(StairFloorDefinition.CreateDefault("LB-01", "一层楼板"));
            project.Floors.Add(StairFloorDefinition.CreateDefault("LB-02", "二层楼板"));
            project.Floors.Add(StairFloorDefinition.CreateDefault("LB-03", "三层楼板"));
            project.Storeys.Add(StairStoreyDefinition.CreateDoubleFlight(
                "LC-01", "一至二层", "LB-01", "LB-02", 3000.0, 1));
            project.Storeys.Add(StairStoreyDefinition.CreateDoubleFlight(
                "LC-02", "二至三层", "LB-02", "LB-03", 3200.0, 2));
            return project;
        }
    }

    public sealed class StairLayoutPlacementDefinition
    {
        public string Key { get; set; }

        public int Page { get; set; }

        public int Row { get; set; }

        public int Column { get; set; }
    }

    public sealed class StairConstructionDefaults
    {
        public double StairwellWidth { get; set; }

        public double StairwellDepth { get; set; }

        public double FlightSlabThickness { get; set; }

        public double LandingSlabThickness { get; set; }

        public double FloorSlabThickness { get; set; }

        public BeamDefaults FloorBeam { get; set; }

        public BeamDefaults LandingBeam { get; set; }

        public bool OppositeSupportsEnabled { get; set; }

        public double SlabOverhang { get; set; }

        public bool CloseSlabOverhangEdge { get; set; }

        public RailingDefaults Railing { get; set; }

        public WallDefaults Wall { get; set; }

        public SectionHatchDefaults SectionHatch { get; set; }

        public SectionHatchDefaults WallHatch { get; set; }

        public OpeningDefaults Door { get; set; }

        public OpeningDefaults Window { get; set; }

        public static StairConstructionDefaults CreateDefault()
        {
            return new StairConstructionDefaults
            {
                StairwellWidth = 2500.0,
                StairwellDepth = 4640.0,
                FlightSlabThickness = 120.0,
                LandingSlabThickness = 100.0,
                FloorSlabThickness = 100.0,
                FloorBeam = new BeamDefaults { Width = 200.0, Depth = 400.0 },
                LandingBeam = new BeamDefaults { Width = 200.0, Depth = 400.0 },
                OppositeSupportsEnabled = true,
                SlabOverhang = 300.0,
                CloseSlabOverhangEdge = false,
                Railing = new RailingDefaults { Enabled = true, Height = 900.0, EdgeOffset = 50.0 },
                Wall = new WallDefaults { Enabled = true, Thickness = 200.0 },
                SectionHatch = new SectionHatchDefaults { Enabled = true, PatternName = "WL_RC_CONCRETE_V2", PatternScale = 200.0 },
                WallHatch = new SectionHatchDefaults { Enabled = true, PatternName = "ANSI311", PatternScale = 20.0 },
                Door = new OpeningDefaults
                {
                    Enabled = false,
                    Width = StairOpeningDefaults.DoorWidth,
                    Height = StairOpeningDefaults.DoorHeight,
                    SillHeight = 0.0
                },
                Window = new OpeningDefaults
                {
                    Enabled = false,
                    Width = StairOpeningDefaults.WindowWidth,
                    Height = StairOpeningDefaults.WindowHeight,
                    SillHeight = StairOpeningDefaults.WindowSillHeight
                }
            };
        }
    }

    public sealed class BeamDefaults
    {
        public double Width { get; set; }

        public double Depth { get; set; }
    }

    public sealed class RailingDefaults
    {
        public bool Enabled { get; set; }

        public double Height { get; set; }

        public double EdgeOffset { get; set; }
    }

    public sealed class WallDefaults
    {
        public bool Enabled { get; set; }

        public double Thickness { get; set; }
    }

    public enum StairPlanSourceMode
    {
        None = 0,
        TianzhengStair = 1,
        ManualPolyline = 2,
        TianzhengStairWithManualBoundary = 3
    }

    public sealed class StairPlanSourceDefinition
    {
        public StairPlanSourceDefinition()
        {
            CropOffset = 300.0;
            FloorLabel = string.Empty;
            RepeatCount = 1;
            BoundaryPoints = new List<StairPlanPointDefinition>();
            CropBoundaryPoints = new List<StairPlanPointDefinition>();
            WallAxes = new List<StairPlanWallAxisDefinition>();
        }

        public string StoreyId { get; set; }

        /// <summary>
        /// Stable floor datum represented by this captured plan.  StoreyId is
        /// retained as a compatibility mirror for projects saved before the
        /// N-storeys/N+1-plans model was introduced.
        /// </summary>
        public string FloorId { get; set; }

        public string DisplayName { get; set; }

        public StairPlanSourceMode Mode { get; set; }

        public string SourceDrawing { get; set; }

        public string SourceDrawingFingerprint { get; set; }

        public string SourceHandle { get; set; }

        public string BoundarySourceHandle { get; set; }

        public string SourceDxfName { get; set; }

        public string SourceComType { get; set; }

        public int SourceScale { get; set; }

        public int TargetScale { get; set; }

        /// <summary>
        /// User-facing floor name or range represented by this source, for
        /// example "首层" or "3~10层".  This is metadata only and never
        /// changes the source drawing or the stair-section storey geometry.
        /// </summary>
        public string FloorLabel { get; set; }

        /// <summary>
        /// Number of physical floors represented by this source. Standard
        /// floors share one captured plan and use a value greater than one.
        /// </summary>
        public int RepeatCount { get; set; }

        public double StairWidth { get; set; }

        public double CropOffset { get; set; }

        public string RecognitionSummary { get; set; }

        /// <summary>
        /// Portable path below the plug-in's 用户配置文件 directory.  The DWG
        /// contains the already cropped native plan used by preview/layout and
        /// final insertion, so a combined insertion never scans the source
        /// drawing or runs TRIM again.
        /// </summary>
        public string CacheRelativePath { get; set; }

        public string CacheFingerprint { get; set; }

        public double CacheWidth { get; set; }

        public double CacheHeight { get; set; }

        /// <summary>
        /// The layout rectangle is the visible crop boundary expanded by
        /// 25 paper millimetres. Offsets are measured from the normalized
        /// cache extents to that rectangle, so preview and final insertion use
        /// exactly the same occupied range.
        /// </summary>
        public double CacheLayoutOffsetX { get; set; }

        public double CacheLayoutOffsetY { get; set; }

        public double CacheLayoutWidth { get; set; }

        public double CacheLayoutHeight { get; set; }

        public int CacheObjectCount { get; set; }

        public string CachedUtc { get; set; }

        public IList<StairPlanPointDefinition> BoundaryPoints { get; set; }

        public IList<StairPlanPointDefinition> CropBoundaryPoints { get; set; }

        public IList<StairPlanWallAxisDefinition> WallAxes { get; set; }
    }

    public sealed class StairPlanPointDefinition
    {
        public StairPlanPointDefinition()
        {
        }

        public StairPlanPointDefinition(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; set; }

        public double Y { get; set; }
    }

    public sealed class StairPlanWallAxisDefinition
    {
        public string Handle { get; set; }

        public double StartX { get; set; }

        public double StartY { get; set; }

        public double EndX { get; set; }

        public double EndY { get; set; }

        public double LeftWidth { get; set; }

        public double RightWidth { get; set; }

        public double Thickness { get; set; }
    }

    public sealed class StairWallOpeningDefinition
    {
        public string SegmentId { get; set; }

        public WallOpeningType Type { get; set; }

        public double Height { get; set; }

        /// <summary>
        /// Height above the supporting floor or landing at the bottom of this
        /// wall segment. Doors always use zero; windows use the configured sill.
        /// </summary>
        public double SillHeight { get; set; }

        public static StairWallOpeningDefinition CreateDefault(string segmentId)
        {
            return new StairWallOpeningDefinition
            {
                SegmentId = segmentId,
                Type = WallOpeningType.None,
                Height = StairOpeningDefaults.DoorHeight,
                SillHeight = StairOpeningDefaults.WindowSillHeight
            };
        }
    }

    public enum WallOpeningType
    {
        None = 0,
        Door = 1,
        Window = 2
    }

    public sealed class SectionHatchDefaults
    {
        public bool Enabled { get; set; }

        public string PatternName { get; set; }

        public double PatternScale { get; set; }
    }

    public sealed class OpeningDefaults
    {
        public bool Enabled { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public double SillHeight { get; set; }
    }

    public sealed class StairFloorDefinition
    {
        public StairFloorDefinition()
        {
            PlanFloorLabel = string.Empty;
            PlanRepeatCount = 1;
        }

        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>
        /// User-facing logical floor or standard-floor range represented by
        /// the plan at this physical slab elevation.
        /// </summary>
        public string PlanFloorLabel { get; set; }

        public int PlanRepeatCount { get; set; }

        public double DepthToUpFlight { get; set; }

        public double DepthToDownFlight { get; set; }

        public PlatformLayoutType PlatformType { get; set; }

        public double PlatformWidth { get; set; }

        public bool PlatformWidthLocked { get; set; }

        public int ProjectionDirection { get; set; }

        public bool DirectionLinked { get; set; }

        public bool AllowLowerFlightClosure { get; set; }

        public bool AllowUpperFlightClosure { get; set; }

        public double? SlabThicknessOverride { get; set; }

        public double? BeamWidthOverride { get; set; }

        public double? BeamDepthOverride { get; set; }

        public OppositeSupportType OppositeSupportType { get; set; }

        public double? OppositeSlabThicknessOverride { get; set; }

        public double? OppositeBeamWidthOverride { get; set; }

        public double? OppositeBeamDepthOverride { get; set; }

        public double? SlabOverhangOverride { get; set; }

        public bool? CloseSlabOverhangEdgeOverride { get; set; }

        public string BeamId { get; set; }

        /// <summary>Optional door/window elevation placed above this floor.</summary>
        public StairPlatformOpeningDefinition DoorWindowElevation { get; set; }

        public static StairFloorDefinition CreateDefault(string id, string name)
        {
            return new StairFloorDefinition
            {
                Id = id,
                Name = name,
                BeamId = id.StartsWith("LB-", StringComparison.OrdinalIgnoreCase)
                    ? "LL-" + id.Substring(3)
                    : id + "-L",
                DepthToUpFlight = 1200.0,
                DepthToDownFlight = 1200.0,
                PlatformType = PlatformLayoutType.Platform3,
                PlatformWidth = 1200.0,
                PlatformWidthLocked = false,
                ProjectionDirection = -1,
                DirectionLinked = true,
                AllowLowerFlightClosure = false,
                AllowUpperFlightClosure = false,
                OppositeSupportType = OppositeSupportType.Beam
            };
        }
    }

    public sealed class StairStoreyDefinition
    {
        public StairStoreyDefinition()
        {
            PlanFloorLabel = string.Empty;
            PlanRepeatCount = 1;
            Flights = new List<StairFlightDefinition>();
            Landings = new List<StairLandingDefinition>();
        }

        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>
        /// User-facing logical floor or standard-floor range represented by
        /// this storey record (for example "1层" or "4~18层").  It belongs
        /// to the storey rather than to an optional captured plan, so it can
        /// be edited before a plan source is registered.
        /// </summary>
        public string PlanFloorLabel { get; set; }

        /// <summary>
        /// Physical floor count derived from <see cref="PlanFloorLabel"/>.
        /// </summary>
        public int PlanRepeatCount { get; set; }

        public string LowerFloorId { get; set; }

        public string UpperFloorId { get; set; }

        public double Height { get; set; }

        public int TotalRiserCount { get; set; }

        public bool StairwellConstraintLocked { get; set; }

        public bool TreadDepthLinked { get; set; }

        public bool PlatformWidthsEqual { get; set; }

        public bool AllowUpperClosureGap { get; set; }

        public IList<StairFlightDefinition> Flights { get; set; }

        public IList<StairLandingDefinition> Landings { get; set; }

        public static StairStoreyDefinition CreateDoubleFlight(
            string id,
            string name,
            string lowerFloorId,
            string upperFloorId,
            double height,
            int index)
        {
            var storey = new StairStoreyDefinition
            {
                Id = id,
                Name = name,
                PlanFloorLabel = index + "层",
                PlanRepeatCount = 1,
                LowerFloorId = lowerFloorId,
                UpperFloorId = upperFloorId,
                Height = height,
                TotalRiserCount = 18,
                StairwellConstraintLocked = true,
                TreadDepthLinked = true
                ,PlatformWidthsEqual = false,
                AllowUpperClosureGap = false
            };
            storey.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-" + index + "-1", "第" + index + "层第一跑", 9, StairFlightDirection.Right, StairSectionRepresentation.Rear));
            storey.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-" + index + "-2", "第" + index + "层第二跑", 9, StairFlightDirection.Left, StairSectionRepresentation.Cut));
            storey.Landings.Add(StairLandingDefinition.CreateDefault(
                "PT-" + index + "-1", "第" + index + "层休息平台", storey.Flights[0].Id, storey.Flights[1].Id));
            return storey;
        }
    }

    public sealed class StairFlightDefinition
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public int RiserCount { get; set; }

        public double TreadDepth { get; set; }

        public double Width { get; set; }

        public double? SlabThicknessOverride { get; set; }

        public StairFlightDirection Direction { get; set; }

        public bool DirectionLinked { get; set; }

        public StairSectionRepresentation SectionRepresentation { get; set; }

        public bool SectionRepresentationLinked { get; set; }

        public bool RiserCountLocked { get; set; }

        public static StairFlightDefinition CreateDefault(
            string id,
            string name,
            int riserCount,
            StairFlightDirection direction,
            StairSectionRepresentation representation)
        {
            return new StairFlightDefinition
            {
                Id = id,
                Name = name,
                RiserCount = riserCount,
                TreadDepth = 280.0,
                Width = 1150.0,
                Direction = direction,
                DirectionLinked = true,
                SectionRepresentation = representation,
                SectionRepresentationLinked = true,
                RiserCountLocked = false
            };
        }
    }

    public sealed class StairLandingDefinition
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string IncomingFlightId { get; set; }

        public string OutgoingFlightId { get; set; }

        public double DepthToIncomingFlight { get; set; }

        public double DepthToOutgoingFlight { get; set; }

        public PlatformLayoutType PlatformType { get; set; }

        public double PlatformWidth { get; set; }

        public bool PlatformWidthLocked { get; set; }

        public int ProjectionDirection { get; set; }

        public bool DirectionLinked { get; set; }

        public bool AllowLowerFlightClosure { get; set; }

        public bool AllowUpperFlightClosure { get; set; }

        public double? SlabThicknessOverride { get; set; }

        public double? BeamWidthOverride { get; set; }

        public double? BeamDepthOverride { get; set; }

        public OppositeSupportType OppositeSupportType { get; set; }

        public double? OppositeSlabThicknessOverride { get; set; }

        public double? OppositeBeamWidthOverride { get; set; }

        public double? OppositeBeamDepthOverride { get; set; }

        public double? SlabOverhangOverride { get; set; }

        public bool? CloseSlabOverhangEdgeOverride { get; set; }

        public string BeamId { get; set; }

        /// <summary>Optional door/window elevation placed above this landing.</summary>
        public StairPlatformOpeningDefinition DoorWindowElevation { get; set; }

        public static StairLandingDefinition CreateDefault(
            string id,
            string name,
            string incomingFlightId,
            string outgoingFlightId)
        {
            return new StairLandingDefinition
            {
                Id = id,
                Name = name,
                IncomingFlightId = incomingFlightId,
                OutgoingFlightId = outgoingFlightId,
                BeamId = id.StartsWith("PT-", StringComparison.OrdinalIgnoreCase)
                    ? "PTL-" + id.Substring(3)
                    : id + "-L",
                DepthToIncomingFlight = 1200.0,
                DepthToOutgoingFlight = 1200.0,
                PlatformType = PlatformLayoutType.Platform2,
                PlatformWidth = 1200.0,
                PlatformWidthLocked = false,
                ProjectionDirection = 1,
                DirectionLinked = true,
                AllowLowerFlightClosure = false,
                AllowUpperFlightClosure = false,
                OppositeSupportType = OppositeSupportType.None
            };
        }
    }

    public enum PlatformLayoutType
    {
        Platform1 = 1,
        Platform2 = 2,
        Platform3 = 3
    }

    public enum OppositeSupportType
    {
        None = 0,
        Beam = 1,
        BeamWithSlab = 2
    }

    public enum StairFlightDirection
    {
        Left = -1,
        Right = 1
    }

    public enum StairSectionRepresentation
    {
        Cut = 0,
        Rear = 1
    }

    /// <summary>
    /// Door/window elevation attached to a floor or landing.  It is deliberately
    /// separate from the platform geometry parameters so old projects and their
    /// platform widths, beams and closure settings remain untouched.
    /// </summary>
    public sealed class StairPlatformOpeningDefinition
    {
        public WallOpeningType Type { get; set; }

        public double DistanceFromWall { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public double SillHeight { get; set; }

        public bool HasInstallationGap { get; set; }

        public double InstallationGap { get; set; }

        public bool HasOuterFrame { get; set; }

        public double OuterFrameWidth { get; set; }

        public bool HasMullion { get; set; }

        public double MullionWidth { get; set; }

        public string DoorFrameType { get; set; }

        public double DoorFrameWidth { get; set; }

        public string Material { get; set; }

        public string CustomCellLayout { get; set; }

        public string CellOpeningModes { get; set; }

        /// <summary>
        /// Exact line geometry returned by the shared door/window division
        /// editor.  Coordinates are local to the elevation's lower-left corner.
        /// </summary>
        public string GeometryLines { get; set; }

        public static StairPlatformOpeningDefinition CreateDefault()
        {
            var opening = CreateDoorDefault();
            opening.Type = WallOpeningType.None;
            return opening;
        }

        public static StairPlatformOpeningDefinition CreateDoorDefault()
        {
            return new StairPlatformOpeningDefinition
            {
                Type = WallOpeningType.Door,
                DistanceFromWall = StairOpeningDefaults.PlatformAxisOffset,
                Width = StairOpeningDefaults.DoorWidth,
                Height = StairOpeningDefaults.DoorHeight,
                SillHeight = 0.0,
                HasInstallationGap = false,
                InstallationGap = 20.0,
                HasOuterFrame = true,
                OuterFrameWidth = 50.0,
                HasMullion = true,
                MullionWidth = 50.0,
                DoorFrameType = "N型",
                DoorFrameWidth = 0.0,
                Material = "无",
                CustomCellLayout = "0,0,900,2200,左平开,1,0,无",
                CellOpeningModes = "左平开"
            };
        }

        public static StairPlatformOpeningDefinition CreateWindowDefault()
        {
            return new StairPlatformOpeningDefinition
            {
                Type = WallOpeningType.Window,
                DistanceFromWall = StairOpeningDefaults.PlatformAxisOffset,
                Width = StairOpeningDefaults.DoorWidth,
                Height = StairOpeningDefaults.WindowHeight,
                SillHeight = StairOpeningDefaults.WindowSillHeight,
                HasInstallationGap = false,
                InstallationGap = 20.0,
                HasOuterFrame = true,
                OuterFrameWidth = 50.0,
                HasMullion = true,
                MullionWidth = 50.0,
                DoorFrameType = "N型",
                DoorFrameWidth = 0.0,
                Material = "玻璃",
                CustomCellLayout = "0,0,450,1500,右平开,0,0,玻璃|450,0,900,1500,左平开,0,0,玻璃",
                CellOpeningModes = "右平开|左平开"
            };
        }
    }
}
