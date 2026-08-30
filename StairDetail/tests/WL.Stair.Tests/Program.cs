using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WL.Stair.Core.Calculation;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Geometry;
using WL.Stair.Core.Layout;
using WL.Stair.Core.Validation;

namespace WL.Stair.Tests
{
    internal static class Program
    {
        private static readonly IList<Action> Tests = new List<Action>
        {
            CalculatesStandardDoubleFlightStair,
            UsesUpdatedHatchScaleDefaults,
            UsesUpdatedPlatformDoorWindowDefaults,
            UsesOppositeSupportAndSlabOverhangDefaults,
            MigratesLegacyProjectsWithoutChangingStairwellAxes,
            ResolvesIndependentStairwellAlignmentAndOffset,
            ResolvesOverallStairwellEnvelope,
            BuildsOppositeBeamSlabWithConfigurableOpenEnd,
            IgnoresLegacyLandingOppositeSupports,
            ExtendsTopWallsAboveTopFloorOpening,
            RecommendsBalancedEvenRiserCount,
            SplitsOddRiserCountAccordingToPreference,
            SupportsManualFlightSplit,
            RejectsInvalidGeometry,
            ReportsLandingAndWidthWarnings,
            UsesGb50352StairStepChecks,
            DetectsInsufficientFlightClearance,
            DetectsInsufficientPlatformClearance,
            AcceptsCompliantStairClearance,
            DefaultProjectHasNoFalseClearanceWarning,
            RejectsInvalidStructuralThickness,
            BuildsPlanGeometry,
            MarksUpperPlanFlightAsHidden,
            BuildsDistinctFloorPlans,
            BuildsPlanDirectionArrowsAndTitles,
            BuildsSectionToFullFloorHeight,
            BuildsContinuousMultiFloorSection,
            BuildsSecondSectionFlightInReturnDirection,
            BuildsSectionLandingsAroundSharedTurnaround,
            BuildsSectionStructuralThickness,
            BuildsFloorBeams,
            OmitsFlightSoffitLines,
            ConnectsFlightsToPlatformsWithFinalTread,
            BuildsEachRiserBeforeItsTread,
            CalculatesDifferentStoreyHeightsAndFlights,
            SupportsThreeFlightsPerStorey,
            KeepsLinkedTreadDepthWhenFinalFlightDoesNotReachFloor,
            ConnectsMixedTwoThreeTwoFlightStoreys,
            AllowsTwoFlightFinalClosureWithoutChangingUpperStorey,
            ConnectsEveryNonThreeFlightStoreyDirectly,
            SynchronizesDisplayedTotalRiserCount,
            KeepsTotalRiserCountWhileRespectingFlightLocks,
            MigratesLegacyClosureSettingsToBoundaries,
            MigratesPlanSourceTargetScaleWithoutChangingSourceScale,
            MigratesStandardFloorMetadataWithoutChangingCapturedGeometry,
            MigratesLogicalFloorRangeToStorey,
            AllowsUpperFlightClosureFromAPlatform,
            AlternatesUnifiedFloorAndLandingDirections,
            DrawsUnifiedBoundaryGeometryFromConnectionToAxis,
            KeepsPlatformBeamsCenteredOnFixedAxes,
            PropagatesNonThreeFlightConnectionsAcrossSharedFloors,
            RebalancesThreeFlightBoundariesAroundEditedLanding,
            RejectsDuplicateComponentIds,
            RejectsBrokenStoreyAndLandingOrder,
            BuildsSharedFloorOnlyOnce,
            OmitsFirstFloorSlabAndBeam,
            BuildsThreePlatformOutlinesWithoutOverlaps,
            AppliesStairwellConstraintsPerLockedStorey,
            AppliesIndependentStairwellDepthToStoreyConstraints,
            RejectsInvalidIndependentStairwellParameters,
            PreservesRiserCountsAndRecalculatesTreadDepth,
            LocksStoreyTreadDepthAndBalancesPlatforms,
            PreservesLockedPlatformWidth,
            DrawsWallsFromBeamCenterAxes,
            BuildsShiftedStoreyWallsAxesBeamsAndTransitionSlabs,
            ConnectsIndependentStairwellTransitionAsOneOutline,
            KeepsSectionGeometryWhenIndependentAxesMatchUnifiedAxes,
            BuildsWallSegmentDoorAndWindowOpenings,
            BuildsPlatformDoorWindowElevationWithoutChangingPlatform,
            ClipsOrdinaryPlanSegmentsToCropBoundary,
            SplitsSegmentsAcrossConcaveCropBoundary,
            KeepsBoundaryAlignedSegmentsAndRejectsOutsideSegments,
            KeepsSectionAxesFixedDuringEditsAndMirroring,
            BuildsHandrailsDimensionsAndOptionalComponentSchedule,
            LabelsFlightsAndLandings,
            SupportsBasementBaseElevation,
            DerivesLowestElevationFromBasementStoreys,
            AcceptsNegativeBasementElevationAsPositiveHeight,
            ExpandsStandardFloorElevationsAndTotalDimension
            ,DrawsTwoStandardFloorBreakLinesAtTwelveHundred
            ,LaysOutPlansBeforeSectionAndRetainsEveryItem
            ,PacksFivePlansAndTallSectionIntoMergedGridCells
            ,PersistsDraggedCombinedLayoutGridRatios
            ,MovesCombinedLayoutItemIntoAnEmptyCell
        };

        private static int Main()
        {
            var failed = 0;

            foreach (var test in Tests)
            {
                try
                {
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                catch (Exception exception)
                {
                    failed++;
                    Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + exception.Message);
                }
            }

            Console.WriteLine(string.Format("Executed {0} tests; {1} failed.", Tests.Count, failed));
            return failed == 0 ? 0 : 1;
        }

        private static void CalculatesStandardDoubleFlightStair()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);

            TestAssert.True(outcome.IsSuccess, "The standard stair should calculate successfully.");
            TestAssert.Equal(9, outcome.Result.FirstFlight.RiserCount, "First-flight risers are incorrect.");
            TestAssert.Equal(8, outcome.Result.FirstFlight.TreadCount, "The landing must replace the ninth tread.");
            TestAssert.NearlyEqual(166.6667, outcome.Result.RiserHeight, 0.001, "Riser height is incorrect.");
            TestAssert.NearlyEqual(2240.0, outcome.Result.FirstFlight.HorizontalRun, 0.001, "Flight run must contain eight visible treads.");
            TestAssert.NearlyEqual(4640.0, outcome.Result.PlanLength, 0.001, "Plan length must use eight visible treads per flight.");
            TestAssert.NearlyEqual(2400.0, outcome.Result.PlanWidth, 0.001, "Plan width is incorrect.");
            TestAssert.NearlyEqual(3000.0, outcome.Result.FloorElevation, 0.001, "Floor elevation is incorrect.");
        }

        private static void SupportsBasementBaseElevation()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.BaseElevation = -3000.0;
            project.BasementStoreyCount = 1;
            project.InsertComponentSchedule = true;
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);

            TestAssert.True(outcome.IsSuccess, "A negative basement base elevation must be supported.");
            TestAssert.NearlyEqual(-3000.0, outcome.Result.Storeys[0].LowerElevation, 0.001,
                "The basement base elevation was lost.");
            TestAssert.NearlyEqual(6200.0, outcome.Result.TotalHeight, 0.001,
                "Total height must measure from the lowest floor to the highest floor.");
            TestAssert.True(section.Texts.Any(text => text.Content.Contains("-3.000 (1F)")),
                "The lowest floor label must use its logical floor name and elevation.");
        }

        private static void DetectsInsufficientFlightClearance()
        {
            var project = StairProjectDefinition.CreateDefault();
            var flightId = project.Storeys[0].Flights[0].Id;
            var view = new DrawingView("Clearance", new[]
            {
                new DrawingLine(new Point2D(0, 0), new Point2D(260, 0),
                    StairLineRole.CutFlightProfile, false, flightId),
                new DrawingLine(new Point2D(0, -160), new Point2D(0, 0),
                    StairLineRole.CutFlightProfile, false, flightId),
                new DrawingLine(new Point2D(-100, 2100), new Point2D(400, 2100),
                    StairLineRole.CutBoundary, false, "OVERHEAD")
            });

            var issues = new StairClearanceValidator().Validate(project, view);

            TestAssert.True(issues.Any(issue => issue.Code == "WL-GB-CLR-2200"
                && issue.ParameterName == flightId
                && issue.Message.Contains("2100mm")),
                "A flight with only 2100mm clear height must be reported against 2200mm.");
        }

        private static void DetectsInsufficientPlatformClearance()
        {
            var project = StairProjectDefinition.CreateDefault();
            var landingId = project.Storeys[0].Landings[0].Id;
            var view = new DrawingView("Clearance", new[]
            {
                new DrawingLine(new Point2D(0, 0), new Point2D(1200, 0),
                    StairLineRole.CutBoundary, false, landingId),
                new DrawingLine(new Point2D(-100, 1950), new Point2D(1300, 1950),
                    StairLineRole.CutBoundary, false, "PT-OVERHEAD")
            });

            var issues = new StairClearanceValidator().Validate(project, view);

            TestAssert.True(issues.Any(issue => issue.Code == "WL-GB-CLR-2000"
                && issue.ParameterName == landingId
                && issue.Message.Contains("1950mm")),
                "A platform with only 1950mm clear height must be reported against 2000mm.");
        }

        private static void AcceptsCompliantStairClearance()
        {
            var project = StairProjectDefinition.CreateDefault();
            var flightId = project.Storeys[0].Flights[0].Id;
            var landingId = project.Storeys[0].Landings[0].Id;
            var view = new DrawingView("Clearance", new[]
            {
                new DrawingLine(new Point2D(0, 0), new Point2D(260, 0),
                    StairLineRole.CutFlightProfile, false, flightId),
                new DrawingLine(new Point2D(0, -160), new Point2D(0, 0),
                    StairLineRole.CutFlightProfile, false, flightId),
                new DrawingLine(new Point2D(1000, 0), new Point2D(2200, 0),
                    StairLineRole.CutBoundary, false, landingId),
                new DrawingLine(new Point2D(-400, 2200), new Point2D(700, 2200),
                    StairLineRole.CutBoundary, false, "OVERHEAD-FLIGHT"),
                new DrawingLine(new Point2D(900, 2000), new Point2D(2300, 2000),
                    StairLineRole.CutBoundary, false, "PT-OVERHEAD")
            });

            var issues = new StairClearanceValidator().Validate(project, view);

            TestAssert.True(!issues.Any(issue => issue.Code.StartsWith("WL-GB-CLR-")),
                "Clear heights exactly at 2200mm and 2000mm must comply.");
        }

        private static void DefaultProjectHasNoFalseClearanceWarning()
        {
            var project = StairProjectDefinition.CreateDefault();
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The default project must calculate before clearance validation.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);

            var issues = new StairClearanceValidator().Validate(project, section);

            TestAssert.True(!issues.Any(), "The default section must not produce false clear-height warnings: "
                + string.Join("; ", issues.Select(issue => issue.ParameterName + " " + issue.Message)));
        }

        private static void DerivesLowestElevationFromBasementStoreys()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.BasementStoreyCount = 2;
            project.BaseElevation = 12345.0;

            var outcome = new StairProjectCalculator().Calculate(project);

            TestAssert.True(outcome.IsSuccess,
                "A project with two basement storeys must calculate successfully.");
            TestAssert.NearlyEqual(-6200.0, project.BaseElevation, 0.001,
                "The lowest elevation must equal the negative sum of all basement heights.");
            TestAssert.NearlyEqual(-6200.0, outcome.Result.Storeys[0].LowerElevation, 0.001,
                "The calculated section must start at the automatically derived basement elevation.");
        }

        private static void AcceptsNegativeBasementElevationAsPositiveHeight()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.BasementStoreyCount = 1;
            project.Storeys[0].Height = -4500.0;

            new StairProjectConstraintService().Normalize(project);

            TestAssert.NearlyEqual(4500.0, project.Storeys[0].Height, 0.001,
                "A negative basement elevation entry must be interpreted as a positive storey height.");
            TestAssert.NearlyEqual(-4500.0, project.BaseElevation, 0.001,
                "The automatic lowest elevation must retain the basement's negative direction.");
        }

        private static void ExpandsStandardFloorElevationsAndTotalDimension()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys[1].Height = 3000.0;
            project.Floors[0].PlanFloorLabel = "1层";
            project.Floors[1].PlanFloorLabel = "2~15层";
            project.Floors[1].PlanRepeatCount = 14;
            project.Floors[2].PlanFloorLabel = "16层";
            project.Storeys[1].PlanFloorLabel = "2~15层";
            project.Storeys[1].PlanRepeatCount = 14;

            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The standard-floor project must calculate.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var rightDimensionX = section.Dimensions
                .Where(dimension => project.Storeys.Any(storey =>
                    storey.Id == dimension.ComponentId))
                .Max(dimension => dimension.DimensionLinePoint.X);

            TestAssert.True(section.Texts.Any(text => text.Content == "42.000 (15F)"),
                "Every logical standard floor must have its real physical elevation.");
            TestAssert.True(section.Texts.Any(text => text.Content == "45.000 (16F)"),
                "The floor above a standard range must include all repeated storey heights.");
            TestAssert.True(section.Dimensions.Any(dimension =>
                    dimension.ComponentId == project.Storeys[1].Id
                    && dimension.TextOverride == "3000×14=42000"),
                "A standard-floor dimension must show storey height, repeat count, and total height.");
            TestAssert.True(section.Texts.All(text =>
                    !text.Content.EndsWith("F)", StringComparison.Ordinal)
                    || text.Position.X > rightDimensionX),
                "Floor and elevation labels must sit to the right of the vertical dimension line.");
        }

        private static void UsesUpdatedHatchScaleDefaults()
        {
            var project = StairProjectDefinition.CreateDefault();
            TestAssert.Equal(50, project.DrawingScale,
                "A new stair detail must default to drawing scale 1:50.");
            TestAssert.NearlyEqual(200.0, project.Construction.SectionHatch.PatternScale,
                0.001, "Structure hatch default scale must be 200.");
            TestAssert.NearlyEqual(20.0, project.Construction.WallHatch.PatternScale,
                0.001, "Wall hatch default scale must be 20.");
        }

        private static void UsesUpdatedPlatformDoorWindowDefaults()
        {
            var project = StairProjectDefinition.CreateDefault();
            TestAssert.NearlyEqual(2200.0, project.Construction.Door.Height, 0.001,
                "Wall doors must default to 2200 mm high.");

            var wallOpening = StairWallOpeningDefinition.CreateDefault("WALL-L-LB-01");
            TestAssert.NearlyEqual(2200.0, wallOpening.Height, 0.001,
                "A newly registered wall opening must carry the 2200 mm door default.");

            var door = StairPlatformOpeningDefinition.CreateDoorDefault();
            TestAssert.NearlyEqual(150.0, door.DistanceFromWall, 0.001,
                "Door offset must default to 150 mm from the axis.");
            TestAssert.NearlyEqual(2200.0, door.Height, 0.001,
                "Platform doors must default to 2200 mm high.");
            TestAssert.NearlyEqual(0.0, door.DoorFrameWidth, 0.001,
                "Platform doors must default to no door frame.");
            TestAssert.Equal("左平开", door.CellOpeningModes,
                "The default platform door must be left side-hung.");
            TestAssert.True(door.CustomCellLayout.Contains(",左平开,1,0,"),
                "The default door panel must be marked as a door.");
            TestAssert.Equal("无", door.Material,
                "The default platform door material must be none.");
            TestAssert.True(door.CustomCellLayout.EndsWith(",无", StringComparison.Ordinal),
                "The default door cell material must be none.");

            var window = StairPlatformOpeningDefinition.CreateWindowDefault();
            TestAssert.NearlyEqual(150.0, window.DistanceFromWall, 0.001,
                "Window offset must default to 150 mm from the axis.");
            TestAssert.NearlyEqual(1500.0, window.Height, 0.001,
                "Platform windows must default to 1500 mm high.");
            TestAssert.NearlyEqual(900.0, window.SillHeight, 0.001,
                "Platform windows must default to a 900 mm sill.");
            TestAssert.True(window.HasMullion,
                "The default platform window must enable its central mullion.");
            TestAssert.Equal("右平开|左平开", window.CellOpeningModes,
                "The two default window leaves must open right and left respectively.");
            TestAssert.Equal(2, window.CustomCellLayout.Split('|').Length,
                "The default platform window must contain two leaves.");
            TestAssert.True(window.CustomCellLayout.Split('|').All(cell =>
                    cell.EndsWith(",玻璃", StringComparison.Ordinal)
                    && cell.Contains(",0,0,玻璃")),
                "Both default window leaves must be glass and must not be marked as doors.");

            project.WallOpenings.Add(new StairWallOpeningDefinition
            {
                SegmentId = "WALL-R-LB-01",
                Type = WallOpeningType.Door,
                Height = 0.0
            });
            project.Floors[0].DoorWindowElevation = new StairPlatformOpeningDefinition
            {
                Type = WallOpeningType.Door,
                Width = 900.0,
                Height = 0.0
            };
            new StairProjectConstraintService().Normalize(project);
            TestAssert.NearlyEqual(2200.0, project.WallOpenings[0].Height, 0.001,
                "Normalization must not restore the obsolete 2100 mm wall-door default.");
            TestAssert.NearlyEqual(2200.0, project.Floors[0].DoorWindowElevation.Height, 0.001,
                "Normalization must not restore the obsolete 2100 mm platform-door default.");
            TestAssert.Equal("无", project.Floors[0].DoorWindowElevation.Material,
                "Normalization must use no material for a door with missing material data.");
        }

        private static void UsesOppositeSupportAndSlabOverhangDefaults()
        {
            var project = StairProjectDefinition.CreateDefault();
            TestAssert.Equal(22, project.SchemaVersion,
                "New stair projects must use the opposite-support schema.");
            TestAssert.True(project.Construction.OppositeSupportsEnabled,
                "Opposite-wall supports must be enabled by default.");
            TestAssert.NearlyEqual(300.0, project.Construction.SlabOverhang, 0.001,
                "The unified slab overhang must default to 300 mm.");
            TestAssert.True(!project.Construction.CloseSlabOverhangEdge,
                "The slab overhang end must be visibly open by default.");
            TestAssert.True(project.Floors.All(item =>
                    item.OppositeSupportType == OppositeSupportType.Beam),
                "Every new floor must inherit a beam at the opposite wall.");
            TestAssert.True(project.Storeys.SelectMany(item => item.Landings).All(item =>
                    item.OppositeSupportType == OppositeSupportType.None),
                "Rest landings must not create opposite-wall supports.");

            project.SchemaVersion = 20;
            project.Construction.OppositeSupportsEnabled = false;
            project.Construction.SlabOverhang = 0.0;
            project.Construction.CloseSlabOverhangEdge = true;
            foreach (var item in project.Floors)
                item.OppositeSupportType = OppositeSupportType.None;
            project.Storeys.SelectMany(item => item.Landings).ToList()
                .ForEach(item => item.OppositeSupportType = OppositeSupportType.None);
            new StairProjectConstraintService().Normalize(project);
            TestAssert.Equal(22, project.SchemaVersion,
                "Schema 20 projects must migrate to the opposite-support schema.");
            TestAssert.True(project.Construction.OppositeSupportsEnabled,
                "Migrated projects must receive the enabled unified switch.");
            TestAssert.NearlyEqual(300.0, project.Construction.SlabOverhang, 0.001,
                "Migrated projects must receive the 300 mm overhang.");
            TestAssert.True(!project.Construction.CloseSlabOverhangEdge,
                "Migrated projects must retain the open-end drawing convention.");
            TestAssert.True(project.Storeys.SelectMany(item => item.Landings).All(item =>
                    item.OppositeSupportType == OppositeSupportType.None),
                "Migration must remove opposite supports from rest landings.");
        }

        private static void MigratesLegacyProjectsWithoutChangingStairwellAxes()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.SchemaVersion = 21;
            project.Storeys[0].IndependentStairwellEnabled = true;
            project.Storeys[0].StairwellDepthOverride = 5200.0;
            project.Storeys[0].StairwellAlignment = StairwellAlignment.Right;
            project.Storeys[0].StairwellAxisOffset = 350.0;

            new StairProjectConstraintService().Normalize(project);

            TestAssert.Equal(22, project.SchemaVersion,
                "Schema 21 projects must migrate to the independent-stairwell schema.");
            TestAssert.True(project.Storeys.All(item => !item.IndependentStairwellEnabled),
                "Migration must keep every legacy storey on unified stairwell axes.");
            var resolver = new StairwellAxisResolver();
            foreach (var storey in project.Storeys)
            {
                var range = resolver.Resolve(project, storey);
                TestAssert.NearlyEqual(0.0, range.LeftAxisX, 0.001,
                    "A migrated legacy storey must retain the original left axis.");
                TestAssert.NearlyEqual(project.Construction.StairwellDepth, range.RightAxisX, 0.001,
                    "A migrated legacy storey must retain the original right axis.");
            }
        }

        private static void ResolvesIndependentStairwellAlignmentAndOffset()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Construction.StairwellDepth = 5000.0;
            var storey = project.Storeys[0];
            storey.IndependentStairwellEnabled = true;
            storey.StairwellDepthOverride = 4000.0;
            storey.StairwellAxisOffset = 125.0;
            var resolver = new StairwellAxisResolver();

            storey.StairwellAlignment = StairwellAlignment.Left;
            var left = resolver.Resolve(project, storey);
            TestAssert.NearlyEqual(125.0, left.LeftAxisX, 0.001,
                "Left alignment must anchor to the unified left axis before offset.");
            TestAssert.NearlyEqual(4125.0, left.RightAxisX, 0.001,
                "Left alignment must preserve the independent depth.");

            storey.StairwellAlignment = StairwellAlignment.Center;
            var center = resolver.Resolve(project, storey);
            TestAssert.NearlyEqual(625.0, center.LeftAxisX, 0.001,
                "Center alignment must distribute the depth difference evenly.");
            TestAssert.NearlyEqual(4625.0, center.RightAxisX, 0.001,
                "Center alignment must preserve the independent depth.");

            storey.StairwellAlignment = StairwellAlignment.Right;
            var right = resolver.Resolve(project, storey);
            TestAssert.NearlyEqual(1125.0, right.LeftAxisX, 0.001,
                "Right alignment must anchor to the unified right axis before offset.");
            TestAssert.NearlyEqual(5125.0, right.RightAxisX, 0.001,
                "Right alignment must preserve the independent depth.");
        }

        private static void ResolvesOverallStairwellEnvelope()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Construction.StairwellDepth = 4600.0;
            project.Storeys[0].IndependentStairwellEnabled = true;
            project.Storeys[0].StairwellDepthOverride = 4300.0;
            project.Storeys[0].StairwellAlignment = StairwellAlignment.Left;
            project.Storeys[0].StairwellAxisOffset = -300.0;
            project.Storeys[1].IndependentStairwellEnabled = true;
            project.Storeys[1].StairwellDepthOverride = 5000.0;
            project.Storeys[1].StairwellAlignment = StairwellAlignment.Right;
            project.Storeys[1].StairwellAxisOffset = 200.0;

            var envelope = new StairwellAxisResolver().ResolveEnvelope(project);

            TestAssert.NearlyEqual(-300.0, envelope.LeftAxisX, 0.001,
                "The envelope must include the furthest independent left axis.");
            TestAssert.NearlyEqual(4800.0, envelope.RightAxisX, 0.001,
                "The envelope must include the furthest independent right axis.");
        }

        private static void BuildsOppositeBeamSlabWithConfigurableOpenEnd()
        {
            var project = StairProjectDefinition.CreateDefault();
            foreach (var item in project.Floors)
                item.OppositeSupportType = OppositeSupportType.None;
            foreach (var landing in project.Storeys.SelectMany(item => item.Landings))
                landing.OppositeSupportType = OppositeSupportType.None;

            var floor = project.Floors[1];
            floor.PlatformType = PlatformLayoutType.Platform2;
            floor.OppositeSupportType = OppositeSupportType.BeamWithSlab;
            floor.OppositeBeamWidthOverride = 240.0;
            floor.OppositeBeamDepthOverride = 500.0;
            floor.OppositeSlabThicknessOverride = 130.0;
            floor.SlabOverhangOverride = 360.0;
            floor.CloseSlabOverhangEdgeOverride = false;

            new StairProjectConstraintService().Apply(project);
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess,
                "An opposite beam-and-slab override must calculate successfully.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var direction = floor.ProjectionDirection;
            var oppositeAxis = direction > 0 ? project.Construction.StairwellDepth : 0.0;
            var outsideDirection = oppositeAxis < project.Construction.StairwellDepth / 2.0 ? -1.0 : 1.0;
            var slabEndX = oppositeAxis + outsideDirection * (120.0 + 360.0);
            var openEdges = section.Lines.Where(line =>
                    line.ComponentId == floor.Id
                    && line.Role == StairLineRole.HatchBoundary)
                .ToArray();
            TestAssert.True(openEdges.Any(line =>
                    Math.Abs(line.Start.X - slabEndX) < 0.001
                    && Math.Abs(line.End.X - slabEndX) < 0.001),
                "An unclosed overhang must omit its visible end while retaining a hatch boundary.");
            TestAssert.True(section.HatchRegions.Any(region => region.OpenEdges.Any(line =>
                    Math.Abs(line.Start.X - slabEndX) < 0.001
                    && Math.Abs(line.End.X - slabEndX) < 0.001)),
                "The hatch region must preserve which terminal edge is forbidden from bolding.");

            var floorElevation = outcome.Result.Storeys[0].UpperElevation;
            var oppositeWallFaces = new[]
            {
                oppositeAxis - project.Construction.Wall.Thickness / 2.0,
                oppositeAxis + project.Construction.Wall.Thickness / 2.0
            };
            TestAssert.True(!section.Lines.Any(line =>
                    line.Role == StairLineRole.WallBoundary
                    && Math.Abs(line.Start.X - line.End.X) < 0.001
                    && oppositeWallFaces.Any(x => Math.Abs(line.Start.X - x) < 0.001)
                    && Math.Min(line.Start.Y, line.End.Y) < floorElevation - 250.0
                    && Math.Max(line.Start.Y, line.End.Y) > floorElevation - 250.0),
                "The opposite wall must break around the added beam instead of crossing it.");

            floor.CloseSlabOverhangEdgeOverride = true;
            section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            TestAssert.True(!section.Lines.Any(line =>
                    line.ComponentId == floor.Id
                    && line.Role == StairLineRole.HatchBoundary),
                "A closed overhang must not need a hidden hatch-only terminal edge.");
            TestAssert.True(!section.HatchRegions.Any(region => region.OpenEdges.Any(line =>
                    Math.Abs(line.Start.X - slabEndX) < 0.001
                    && Math.Abs(line.End.X - slabEndX) < 0.001)),
                "A closed overhang must not carry no-bold metadata for its terminal edge.");
            TestAssert.True(section.Lines.Any(line =>
                    line.ComponentId == floor.Id
                    && line.Role == StairLineRole.CutBoundary
                    && Math.Abs(line.Start.X - slabEndX) < 0.001
                    && Math.Abs(line.End.X - slabEndX) < 0.001),
                "Selecting a closed overhang must draw its terminal edge.");
        }

        private static void IgnoresLegacyLandingOppositeSupports()
        {
            var project = StairProjectDefinition.CreateDefault();
            var landing = project.Storeys[0].Landings[0];
            landing.PlatformType = PlatformLayoutType.Platform2;
            landing.OppositeSupportType = OppositeSupportType.BeamWithSlab;
            landing.OppositeBeamWidthOverride = 260.0;
            landing.OppositeBeamDepthOverride = 520.0;
            landing.OppositeSlabThicknessOverride = 140.0;
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess,
                "Legacy landing support data must not prevent calculation.");
            var builder = new StairProjectGeometryBuilder();
            var staleCount = builder.BuildSection(project, outcome.Result).Lines
                .Count(line => line.ComponentId == landing.Id);
            landing.OppositeSupportType = OppositeSupportType.None;
            var cleanCount = builder.BuildSection(project, outcome.Result).Lines
                .Count(line => line.ComponentId == landing.Id);
            TestAssert.Equal(cleanCount, staleCount,
                "Rest landings must never draw opposite-wall support geometry.");
        }

        private static void ExtendsTopWallsAboveTopFloorOpening()
        {
            var project = StairProjectDefinition.CreateDefault();
            var topFloor = project.Floors.Last();
            topFloor.DoorWindowElevation = StairPlatformOpeningDefinition.CreateWindowDefault();
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The top-window project must calculate.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var highestFloorElevation = outcome.Result.Storeys.Max(item => item.UpperElevation);
            var openingTop = highestFloorElevation
                + topFloor.DoorWindowElevation.SillHeight
                + topFloor.DoorWindowElevation.Height;
            var wallTop = section.Lines
                .Where(line => line.Role == StairLineRole.WallBoundary)
                .Max(line => Math.Max(line.Start.Y, line.End.Y));
            TestAssert.True(wallTop >= openingTop + 300.0 - 0.001,
                "Top walls must extend at least 300 mm above the top-floor opening.");
        }

        private static void DrawsTwoStandardFloorBreakLinesAtTwelveHundred()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys[0].PlanFloorLabel = "2~13层";
            project.Storeys[0].PlanRepeatCount = 12;
            var lowerFloor = project.Floors.First(floor => floor.Id ==
                project.Storeys[0].LowerFloorId);
            lowerFloor.PlanFloorLabel = "2~13层";
            lowerFloor.PlanRepeatCount = 12;
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var first = section.Lines.Where(line => line.ComponentId ==
                "STANDARD-BREAK-" + project.Storeys[0].Id + "-1").ToArray();
            var second = section.Lines.Where(line => line.ComponentId ==
                "STANDARD-BREAK-" + project.Storeys[0].Id + "-2").ToArray();

            TestAssert.Equal(5, first.Length,
                "The first standard-floor break symbol must contain five segments.");
            TestAssert.Equal(5, second.Length,
                "The second standard-floor break symbol must contain five segments.");
            TestAssert.NearlyEqual(outcome.Result.Storeys[0].LowerElevation + 1200.0,
                first.First().Start.Y, 0.001,
                "The first break line must start 1200 mm above the current floor.");
            TestAssert.NearlyEqual(100.0,
                second.First().Start.Y - first.First().Start.Y, 0.001,
                "The two standard-floor break lines must be 100 mm apart.");
        }

        private static void RecommendsBalancedEvenRiserCount()
        {
            var definition = StandardDefinition();
            definition.TotalRiserCount = null;

            var outcome = new StairCalculator().Calculate(definition);

            TestAssert.True(outcome.IsSuccess, "Automatic riser selection should succeed.");
            TestAssert.Equal(18, outcome.Result.TotalRiserCount, "Recommended riser count is incorrect.");
            TestAssert.Equal(9, outcome.Result.FirstFlight.RiserCount, "The first flight should be balanced.");
            TestAssert.Equal(9, outcome.Result.SecondFlight.RiserCount, "The second flight should be balanced.");
        }

        private static void SplitsOddRiserCountAccordingToPreference()
        {
            var definition = StandardDefinition();
            definition.TotalRiserCount = 17;
            definition.SplitPreference = FlightSplitPreference.FirstFlightGetsExtraRiser;

            var outcome = new StairCalculator().Calculate(definition);

            TestAssert.True(outcome.IsSuccess, "An odd riser count should be supported.");
            TestAssert.Equal(9, outcome.Result.FirstFlight.RiserCount, "The first flight should receive the extra riser.");
            TestAssert.Equal(8, outcome.Result.SecondFlight.RiserCount, "The second-flight count is incorrect.");
        }

        private static void SupportsManualFlightSplit()
        {
            var definition = StandardDefinition();
            definition.TotalRiserCount = 18;
            definition.FirstFlightRiserCount = 10;

            var outcome = new StairCalculator().Calculate(definition);

            TestAssert.True(outcome.IsSuccess, "A valid manual split should succeed.");
            TestAssert.Equal(10, outcome.Result.FirstFlight.RiserCount, "Manual first-flight count was ignored.");
            TestAssert.Equal(8, outcome.Result.SecondFlight.RiserCount, "Derived second-flight count is incorrect.");
        }

        private static void RejectsInvalidGeometry()
        {
            var definition = StandardDefinition();
            definition.FloorHeight = 0.0;

            var outcome = new StairCalculator().Calculate(definition);

            TestAssert.True(!outcome.IsSuccess, "A zero floor height must fail.");
            TestAssert.True(
                outcome.Issues.Any(issue => issue.Code == "WL-ST-001" && issue.Severity == ValidationSeverity.Error),
                "The expected floor-height error was not reported.");
        }

        private static void ReportsLandingAndWidthWarnings()
        {
            var definition = StandardDefinition();
            definition.FlightWidth = 850.0;
            definition.FloorLandingDepth = 800.0;
            definition.IntermediateLandingDepth = 800.0;

            var outcome = new StairCalculator().Calculate(definition);

            TestAssert.True(outcome.IsSuccess, "Warnings should not prevent geometry generation.");
            TestAssert.True(outcome.Issues.Any(issue => issue.Code == "WL-ST-104"), "Flight-width warning is missing.");
            TestAssert.True(outcome.Issues.Any(issue => issue.Code == "WL-ST-105"), "Floor-landing warning is missing.");
            TestAssert.True(outcome.Issues.Any(issue => issue.Code == "WL-ST-106"), "Intermediate-landing warning is missing.");
        }

        private static void UsesGb50352StairStepChecks()
        {
            var project = StairProjectDefinition.CreateDefault();
            var storey = project.Storeys[0];
            storey.Height = 4500.0;
            storey.Flights[0].RiserCount = 15;
            storey.Flights[1].RiserCount = 15;
            storey.Flights[0].TreadDepth = 280.0;
            storey.Flights[1].TreadDepth = 280.0;

            var compliant = new StairProjectCalculator().Calculate(project);
            TestAssert.True(compliant.IsSuccess,
                "A GB 50352-2019-compliant other-building stair must calculate.");
            TestAssert.True(!compliant.Issues.Any(issue => issue.Code == "WL-PR-102"),
                "2h+b alone must not mark a stair noncompliant when table 6.8.10 is satisfied.");

            storey.Flights[0].TreadDepth = 250.0;
            var narrowTread = new StairProjectCalculator().Calculate(project);
            TestAssert.True(narrowTread.Issues.Any(issue => issue.Code == "WL-PR-102"
                    && issue.Message.Contains("GB 50352-2019")
                    && issue.Message.Contains("260mm")),
                "A tread below 260 mm must report the GB 50352-2019 table 6.8.10 warning.");
        }

        private static void RejectsInvalidStructuralThickness()
        {
            var definition = StandardDefinition();
            definition.FlightSlabThickness = 0.0;

            var outcome = new StairCalculator().Calculate(definition);

            TestAssert.True(!outcome.IsSuccess, "A zero flight-slab thickness must fail.");
            TestAssert.True(
                outcome.Issues.Any(issue => issue.Code == "WL-ST-013"),
                "The expected flight-slab thickness error was not reported.");
        }

        private static void BuildsPlanGeometry()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);
            var plan = new StairGeometryBuilder().BuildPlan(definition, outcome.Result);

            TestAssert.Equal("Plan", plan.Name, "Plan view name is incorrect.");
            TestAssert.Equal(4, plan.Lines.Count(line => line.Role == StairLineRole.Outline), "Plan outline is incomplete.");
            TestAssert.Equal(16, plan.Lines.Count(line => line.Role == StairLineRole.Tread), "Plan must show eight treads per flight.");
            TestAssert.Equal(2, plan.Lines.Count(line => line.Role == StairLineRole.StairwellEdge), "Stairwell edges are missing.");
        }

        private static void MarksUpperPlanFlightAsHidden()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);
            var plan = new StairGeometryBuilder().BuildPlan(definition, outcome.Result);
            var treadLines = plan.Lines.Where(line => line.Role == StairLineRole.Tread).ToArray();

            TestAssert.Equal(
                outcome.Result.FirstFlight.TreadCount,
                treadLines.Count(line => !line.IsHidden),
                "The lower flight should remain visible in plan.");
            TestAssert.Equal(
                outcome.Result.SecondFlight.TreadCount,
                treadLines.Count(line => line.IsHidden),
                "The upper flight should use hidden plan lines.");
            TestAssert.Equal(
                3,
                plan.Lines.Count(line => line.Role == StairLineRole.WalkingLine && line.IsHidden),
                "The upper walking arrow should use the hidden-line role.");
        }

        private static void BuildsDistinctFloorPlans()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);
            var builder = new StairGeometryBuilder();
            var firstFloor = builder.BuildPlan(definition, outcome.Result, StairPlanLevel.FirstFloor);
            var intermediateFloor = builder.BuildPlan(
                definition,
                outcome.Result,
                StairPlanLevel.IntermediateFloor);
            var topFloor = builder.BuildPlan(definition, outcome.Result, StairPlanLevel.TopFloor);

            TestAssert.Equal("FirstFloorPlan", firstFloor.Name, "First-floor plan name is incorrect.");
            TestAssert.Equal(
                outcome.Result.FirstFlight.TreadCount,
                firstFloor.Lines.Count(line => line.Role == StairLineRole.Tread),
                "The first-floor plan should show only the rising flight.");
            TestAssert.Equal(
                outcome.Result.FirstFlight.TreadCount + outcome.Result.SecondFlight.TreadCount,
                intermediateFloor.Lines.Count(line => line.Role == StairLineRole.Tread),
                "The intermediate-floor plan should show both flights.");
            TestAssert.Equal(
                outcome.Result.SecondFlight.TreadCount,
                topFloor.Lines.Count(line => line.Role == StairLineRole.Tread),
                "The top-floor plan should show only the arriving flight.");
            TestAssert.True(
                topFloor.Lines.Where(line => line.Role == StairLineRole.Tread).All(line => !line.IsHidden),
                "The arriving top-floor flight should be visible.");
        }

        private static void BuildsPlanDirectionArrowsAndTitles()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);
            var builder = new StairGeometryBuilder();
            var firstFloor = builder.BuildPlan(definition, outcome.Result, StairPlanLevel.FirstFloor);
            var intermediateFloor = builder.BuildPlan(
                definition,
                outcome.Result,
                StairPlanLevel.IntermediateFloor);
            var topFloor = builder.BuildPlan(definition, outcome.Result, StairPlanLevel.TopFloor);

            TestAssert.Equal(
                3,
                firstFloor.Lines.Count(line => line.Role == StairLineRole.WalkingLine),
                "A walking arrow should contain a shaft and two arrowhead lines.");
            TestAssert.Equal(
                6,
                intermediateFloor.Lines.Count(line => line.Role == StairLineRole.WalkingLine),
                "The intermediate-floor plan should contain up and down arrows.");
            TestAssert.Equal(
                3,
                intermediateFloor.Lines.Count(line => line.Role == StairLineRole.WalkingLine && line.IsHidden),
                "The upper walking arrow should be hidden on the intermediate floor.");
            TestAssert.True(
                firstFloor.Texts.Any(text => text.Content == "一层平面"),
                "The first-floor title is missing.");
            TestAssert.True(
                intermediateFloor.Texts.Any(text => text.Content == "中间层平面"),
                "The intermediate-floor title is missing.");
            TestAssert.True(
                topFloor.Texts.Any(text => text.Content == "顶层平面"),
                "The top-floor title is missing.");
            TestAssert.True(
                intermediateFloor.Texts.Any(text => text.Content == "上")
                    && intermediateFloor.Texts.Any(text => text.Content == "下"),
                "The intermediate-floor up/down labels are missing.");
        }

        private static void BuildsSectionToFullFloorHeight()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);
            var section = new StairGeometryBuilder().BuildSection(definition, outcome.Result);
            var highestPoint = section.Lines.SelectMany(line => new[] { line.Start.Y, line.End.Y }).Max();

            TestAssert.Equal("Section", section.Name, "Section view name is incorrect.");
            TestAssert.NearlyEqual(3000.0, highestPoint, 0.001, "Section does not reach the next floor.");
            TestAssert.Equal(
                outcome.Result.SecondFlight.TreadCount - 1,
                section.Lines.Count(line =>
                    line.Role == StairLineRole.CutFlightProfile
                    && Math.Abs(line.Start.X - line.End.X) < 0.001
                    && Math.Abs(line.End.Y - line.Start.Y - outcome.Result.SecondFlight.RiserHeight) < 0.001),
                "The cut upward flight riser count is incorrect.");
            TestAssert.Equal(
                outcome.Result.FirstFlight.TreadCount,
                section.Lines.Count(line =>
                    line.IsHidden
                    && line.Role == StairLineRole.SectionProfile
                    && Math.Abs(line.Start.X - line.End.X) < 0.001
                    && Math.Abs(line.End.Y - line.Start.Y - outcome.Result.FirstFlight.RiserHeight) < 0.001),
                "The rear downward flight riser count is incorrect.");
        }

        private static void BuildsContinuousMultiFloorSection()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);
            var section = new StairGeometryBuilder().BuildMultiFloorSection(
                definition,
                outcome.Result,
                3);
            var highestPoint = section.Lines.SelectMany(line => new[] { line.Start.Y, line.End.Y }).Max();

            TestAssert.Equal("MultiFloorSection", section.Name, "Multi-floor section name is incorrect.");
            TestAssert.NearlyEqual(
                6000.0,
                highestPoint,
                0.001,
                "Three floor levels should reach the second upper floor elevation.");
            TestAssert.Equal(
                ((outcome.Result.FirstFlight.TreadCount * 2)
                    + (outcome.Result.SecondFlight.TreadCount * 2)) - 3,
                section.Lines.Count(line =>
                    (line.Role == StairLineRole.SectionProfile || line.Role == StairLineRole.CutFlightProfile)
                    && line.Role != StairLineRole.BeamBoundary
                    && Math.Abs(line.Start.X - line.End.X) < 0.001
                    && line.End.Y > line.Start.Y
                    && Math.Abs(Math.Abs(line.Start.Y - line.End.Y) - outcome.Result.RiserHeight) < 0.001),
                "Every storey interval must contain a complete pair of stair flights.");
            TestAssert.True(
                section.Texts.Any(text => text.Content == "±0.000")
                    && section.Texts.Any(text => text.Content == "+3.000")
                    && section.Texts.Any(text => text.Content == "+6.000"),
                "Multi-floor section elevations are missing.");
        }

        private static void BuildsSecondSectionFlightInReturnDirection()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);
            var section = new StairGeometryBuilder().BuildSection(definition, outcome.Result);
            var intermediateElevation = outcome.Result.IntermediateLandingElevation;
            var rearFlightTreads = section.Lines
                .Where(line => line.Role == StairLineRole.SectionProfile)
                .Where(line => line.IsHidden)
                .Where(line => Math.Abs(line.Start.Y - line.End.Y) < 0.001)
                .Where(line => line.Start.Y > 0.0 && line.Start.Y <= intermediateElevation)
                .ToArray();

            TestAssert.Equal(
                outcome.Result.SecondFlight.TreadCount,
                rearFlightTreads.Length,
                "Rear-flight tread count is incorrect.");
            TestAssert.True(
                rearFlightTreads.All(line => line.End.X > line.Start.X),
                "The rear flight must rise from the lower floor toward the landing.");
        }

        private static void BuildsSectionLandingsAroundSharedTurnaround()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);
            var section = new StairGeometryBuilder().BuildSection(definition, outcome.Result);
            definition.IntermediateLandingDepthUp = 1250.0;
            definition.IntermediateLandingDepthDown = 950.0;
            definition.FloorLandingDepthUp = 1300.0;
            definition.FloorLandingDepthDown = 1000.0;
            outcome = new StairCalculator().Calculate(definition);
            section = new StairGeometryBuilder().BuildSection(definition, outcome.Result);

            TestAssert.True(
                section.Lines.Any(line => line.Role == StairLineRole.CutBoundary
                    && Math.Abs(line.Start.Y - outcome.Result.IntermediateLandingElevation) < 0.001
                    && Math.Abs(line.End.X - definition.IntermediateLandingDepthUp) < 0.001),
                "The cut landing must use its upward-flight connection depth.");
            TestAssert.True(
                section.Lines.Any(line => !line.IsHidden
                    && line.Role == StairLineRole.CutBoundary
                    && Math.Abs(line.Start.Y - outcome.Result.IntermediateLandingElevation) < 0.001
                    && Math.Abs(line.End.X - definition.IntermediateLandingDepthDown) < 0.001),
                "The rear landing must use its downward-flight connection depth.");
        }

        private static void BuildsSectionStructuralThickness()
        {
            var definition = StandardDefinition();
            definition.FlightSlabThickness = 130.0;
            definition.LandingSlabThickness = 140.0;
            definition.FloorSlabThickness = 150.0;
            var outcome = new StairCalculator().Calculate(definition);
            var section = new StairGeometryBuilder().BuildSection(definition, outcome.Result);
            TestAssert.Equal(2, section.Lines.Count(line =>
                (line.Role == StairLineRole.CutFlightProfile || line.IsHidden)
                && Math.Abs(line.Start.X - line.End.X) > definition.TreadDepth + 0.001
                && Math.Abs(line.Start.Y - line.End.Y) > outcome.Result.FirstFlight.RiserHeight + 0.001),
                "Each flight must have one closing underside edge.");
            TestAssert.True(
                section.Lines.Any(line => !line.IsHidden
                    && Math.Abs(line.Start.Y
                    - (outcome.Result.IntermediateLandingElevation - definition.LandingSlabThickness)) < 0.001
                    && Math.Abs(line.End.Y
                    - (outcome.Result.IntermediateLandingElevation - definition.LandingSlabThickness)) < 0.001),
                "The intermediate landing underside is missing.");
            TestAssert.True(
                section.Lines.Any(line => !line.IsHidden
                    && Math.Abs(line.Start.Y
                    - (outcome.Result.FloorElevation - definition.FloorSlabThickness)) < 0.001
                    && Math.Abs(line.End.Y
                    - (outcome.Result.FloorElevation - definition.FloorSlabThickness)) < 0.001),
                "The upper floor slab underside is missing.");
        }

        private static void BuildsFloorBeams()
        {
            var definition = StandardDefinition();
            definition.FloorBeamWidth = 300.0;
            definition.FloorBeamDepth = 400.0;
            var outcome = new StairCalculator().Calculate(definition);
            var section = new StairGeometryBuilder().BuildSection(definition, outcome.Result);

            TestAssert.Equal(
                4,
                section.Lines.Count(line => line.Role == StairLineRole.BeamBoundary),
                "The first level must omit its beam while the upper level keeps one beam.");
        }

        private static void OmitsFlightSoffitLines()
        {
            var definition = StandardDefinition();
            var outcome = new StairCalculator().Calculate(definition);
            var section = new StairGeometryBuilder().BuildSection(definition, outcome.Result);

            TestAssert.Equal(2, section.Lines.Count(line =>
                Math.Abs(line.Start.X - line.End.X) > definition.TreadDepth + 0.001
                && Math.Abs(line.Start.Y - line.End.Y) > outcome.Result.FirstFlight.RiserHeight + 0.001),
                "The section must draw one closing underside edge per flight.");
        }

        private static void ConnectsFlightsToPlatformsWithFinalTread()
        {
            var project = StairProjectDefinition.CreateDefault();
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var firstStorey = outcome.Result.Storeys[0];
            var firstFlight = firstStorey.Flights[0];

            var flightLines = section.Lines.Where(line => line.ComponentId == "TD-1-1").ToArray();
            var direction = (int)project.Storeys[0].Flights[0].Direction;
            var startAxisX = direction > 0 ? 0.0 : project.Construction.StairwellDepth;
            var flightStartX = startAxisX + direction * project.Floors[0].PlatformWidth;
            var finalTreadStartX = flightStartX
                + direction * (firstFlight.HorizontalRun - firstFlight.TreadDepth);
            var finalTreadEndX = flightStartX + direction * firstFlight.HorizontalRun;

            TestAssert.True(flightLines.Any(line =>
                Math.Abs(line.Start.X - finalTreadStartX) < 0.001
                && Math.Abs(line.End.X - finalTreadEndX) < 0.001
                && Math.Abs(line.Start.Y - (firstFlight.VerticalRise - firstFlight.RiserHeight)) < 0.001
                && Math.Abs(line.End.Y - (firstFlight.VerticalRise - firstFlight.RiserHeight)) < 0.001),
                "The final visible tread must remain one riser below the landing.");
            TestAssert.True(section.Lines.Any(line => line.ComponentId == "PT-1-1"
                && Math.Abs(line.Start.Y - firstFlight.VerticalRise) < 0.001
                && Math.Abs(line.End.Y - firstFlight.VerticalRise) < 0.001),
                "The landing must occupy the ninth riser elevation.");
            TestAssert.Equal(1, flightLines.Count(line =>
                Math.Abs(line.Start.X - line.End.X) > firstFlight.TreadDepth + 0.001
                && Math.Abs(line.Start.Y - line.End.Y) > firstFlight.RiserHeight + 0.001),
                "The project flight must include one closing underside edge.");
        }

        private static void BuildsEachRiserBeforeItsTread()
        {
            var project = StairProjectDefinition.CreateDefault();
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var flight = outcome.Result.Storeys[0].Flights[0];
            var flightLines = section.Lines.Where(line => line.ComponentId == flight.Id).ToArray();
            var direction = (int)project.Storeys[0].Flights[0].Direction;
            var startAxisX = direction > 0 ? 0.0 : project.Construction.StairwellDepth;
            var flightStartX = startAxisX + direction * project.Floors[0].PlatformWidth;

            for (var index = 0; index < flight.TreadCount; index++)
            {
                var treadStartX = flightStartX + direction * index * flight.TreadDepth;
                var treadEndX = flightStartX + direction * (index + 1) * flight.TreadDepth;
                var riserBottom = index * flight.RiserHeight;
                var riserTop = (index + 1) * flight.RiserHeight;

                TestAssert.True(
                    flightLines.Any(line => Math.Abs(line.Start.X - treadStartX) < 0.001
                        && Math.Abs(line.End.X - treadStartX) < 0.001
                        && Math.Abs(line.Start.Y - riserBottom) < 0.001
                        && Math.Abs(line.End.Y - riserTop) < 0.001),
                    "Each stair unit must start with its vertical riser.");
                TestAssert.True(
                    flightLines.Any(line => Math.Abs(line.Start.X - treadStartX) < 0.001
                        && Math.Abs(line.End.X - treadEndX) < 0.001
                        && Math.Abs(line.Start.Y - riserTop) < 0.001
                        && Math.Abs(line.End.Y - riserTop) < 0.001),
                    "Each riser must be followed by its horizontal tread.");
            }
        }

        private static void CalculatesDifferentStoreyHeightsAndFlights()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys[0].Flights[0].RiserCount = 8;
            project.Storeys[0].Flights[1].RiserCount = 9;
            project.Storeys[1].Flights[0].RiserCount = 9;
            project.Storeys[1].Flights[1].RiserCount = 10;

            var outcome = new StairProjectCalculator().Calculate(project);

            TestAssert.True(outcome.IsSuccess, "Different storey heights should calculate independently.");
            TestAssert.NearlyEqual(3000.0 / 17.0, outcome.Result.Storeys[0].RiserHeight, 0.001, "First storey riser height is incorrect.");
            TestAssert.NearlyEqual(3200.0 / 19.0, outcome.Result.Storeys[1].RiserHeight, 0.001, "Second storey riser height is incorrect.");
            TestAssert.NearlyEqual(6200.0, outcome.Result.TotalHeight, 0.001, "Total project height is incorrect.");
        }

        private static void SupportsThreeFlightsPerStorey()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys.RemoveAt(1);
            project.Floors.RemoveAt(2);
            var storey = project.Storeys[0];
            storey.Height = 3300.0;
            storey.Flights.Clear();
            storey.Landings.Clear();
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-1", "第一跑", 6, StairFlightDirection.Right, StairSectionRepresentation.Rear));
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-2", "第二跑", 6, StairFlightDirection.Left, StairSectionRepresentation.Cut));
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-3", "第三跑", 7, StairFlightDirection.Right, StairSectionRepresentation.Cut));
            storey.Landings.Add(StairLandingDefinition.CreateDefault("PT-1-1", "第一平台", "TD-1-1", "TD-1-2"));
            storey.Landings.Add(StairLandingDefinition.CreateDefault("PT-1-2", "第二平台", "TD-1-2", "TD-1-3"));

            var outcome = new StairProjectCalculator().Calculate(project);

            TestAssert.True(outcome.IsSuccess, "A storey should support an arbitrary number of flights.");
            TestAssert.Equal(3, outcome.Result.Storeys[0].Flights.Count, "The third flight was lost.");
            TestAssert.Equal(19, outcome.Result.Storeys[0].TotalRiserCount, "Per-flight riser counts were not summed.");
            TestAssert.Equal(6, outcome.Result.Storeys[0].Flights[2].TreadCount, "The destination platform must replace the final tread.");
        }

        private static void KeepsLinkedTreadDepthWhenFinalFlightDoesNotReachFloor()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys.RemoveAt(1);
            project.Floors.RemoveAt(2);
            var storey = project.Storeys[0];
            storey.Height = 3300.0;
            storey.Flights.Clear();
            storey.Landings.Clear();
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-1", "第一跑", 6, StairFlightDirection.Right, StairSectionRepresentation.Rear));
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-2", "第二跑", 6, StairFlightDirection.Left, StairSectionRepresentation.Cut));
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-3", "第三跑", 7, StairFlightDirection.Right, StairSectionRepresentation.Cut));
            storey.Landings.Add(StairLandingDefinition.CreateDefault("PT-1-1", "第一平台", "TD-1-1", "TD-1-2"));
            storey.Landings.Add(StairLandingDefinition.CreateDefault("PT-1-2", "第二平台", "TD-1-2", "TD-1-3"));
            foreach (var flight in storey.Flights) flight.TreadDepth = 280.0;
            project.Floors[1].AllowLowerFlightClosure = true;

            new StairProjectConstraintService().Apply(project);
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The three-flight storey must calculate before gap validation.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var upperElevation = outcome.Result.Storeys[0].UpperElevation;
            var finalTreadElevation = upperElevation - outcome.Result.Storeys[0].RiserHeight;
            var finalTreads = section.Lines.Where(line => line.ComponentId == "TD-1-3"
                    && Math.Abs(line.Start.Y - line.End.Y) < 0.001
                    && Math.Abs(line.Start.Y - finalTreadElevation) < 0.001)
                .ToArray();
            TestAssert.True(finalTreads.Length > 0, "The final flight top tread is missing.");
            TestAssert.True(finalTreads.All(line => Math.Abs(Math.Abs(line.End.X - line.Start.X) - 280.0) < 0.001),
                "Section geometry must retain the linked storey tread depth instead of stretching the final flight.");

            var finalFlightEnd = finalTreads.Max(line => Math.Max(line.Start.X, line.End.X));
            var bridgeTop = section.Lines.Where(line => line.ComponentId == "TD-1-3"
                    && Math.Abs(line.Start.Y - upperElevation) < 0.001
                    && Math.Abs(line.End.Y - upperElevation) < 0.001)
                .ToArray();
            TestAssert.True(bridgeTop.Length > 0, "The horizontal bridge to the upper floor is missing.");
            var upperFloorConnection = bridgeTop.Max(line => Math.Max(line.Start.X, line.End.X));
            TestAssert.True(upperFloorConnection - finalFlightEnd > 0.001,
                "The final tread must remain short of the fixed-axis floor before the bridge is added.");

            var closingRiser = section.Lines.Any(line => line.ComponentId == "TD-1-3"
                && Math.Abs(line.Start.X - finalFlightEnd) < 0.001
                && Math.Abs(line.End.X - finalFlightEnd) < 0.001
                && Math.Abs(Math.Abs(line.End.Y - line.Start.Y) - outcome.Result.Storeys[0].RiserHeight) < 0.001);
            TestAssert.True(closingRiser,
                "The final tread must rise by one riser height before bridging to the floor.");

            var bridgeUndersideElevation = upperElevation - project.Construction.FloorSlabThickness;
            var bridgeUnderside = section.Lines.Where(line => line.ComponentId == "TD-1-3"
                    && Math.Abs(line.Start.Y - bridgeUndersideElevation) < 0.001
                    && Math.Abs(line.End.Y - bridgeUndersideElevation) < 0.001)
                .ToArray();
            TestAssert.True(bridgeUnderside.Length > 0,
                "The bridge underside must be offset by the destination floor slab thickness.");
            var sharedEdgeMiddle = (upperElevation + bridgeUndersideElevation) / 2.0;
            TestAssert.True(!section.Lines.Any(line => !line.IsHidden
                    && (line.Role == StairLineRole.CutBoundary
                        || line.Role == StairLineRole.CutFlightProfile)
                    && Math.Abs(line.Start.X - upperFloorConnection) < 0.001
                    && Math.Abs(line.End.X - upperFloorConnection) < 0.001
                    && sharedEdgeMiddle > Math.Min(line.Start.Y, line.End.Y) + 0.001
                    && sharedEdgeMiddle < Math.Max(line.Start.Y, line.End.Y) - 0.001),
                "The shared flight-to-floor edge must be removed so both form one hatchable outline.");
            var filletPoint = bridgeUnderside
                .SelectMany(line => new[] { line.Start, line.End })
                .OrderBy(point => point.X)
                .First();
            TestAssert.True(section.Lines.Any(line => line.ComponentId == "TD-1-3"
                    && Math.Abs(line.Start.X - line.End.X) > 0.001
                    && Math.Abs(line.Start.Y - line.End.Y) > 0.001
                    && ((Math.Abs(line.Start.X - filletPoint.X) < 0.001
                            && Math.Abs(line.Start.Y - filletPoint.Y) < 0.001)
                        || (Math.Abs(line.End.X - filletPoint.X) < 0.001
                            && Math.Abs(line.End.Y - filletPoint.Y) < 0.001))),
                "The offset underside and flight soffit must meet at a zero-radius fillet point.");
        }

        private static void LaysOutPlansBeforeSectionAndRetainsEveryItem()
        {
            var items = new[]
            {
                new StairLayoutItem { Key = "LB-01", Name = "1层楼梯平面", Width = 5200, Height = 4600 },
                new StairLayoutItem { Key = "LB-02", Name = "2层楼梯平面", Width = 5200, Height = 4600 },
                new StairLayoutItem { Key = "SECTION", Name = "LT-01 楼梯剖面", Width = 6000, Height = 9800, IsSection = true }
            };
            var plan = StairCombinedLayout.Compute(items, new StairLayoutOptions
            {
                PageWidth = 841 * 30,
                PageHeight = 594 * 30,
                LeftMargin = 30 * 30,
                RightMargin = 60 * 30,
                TopMargin = 20 * 30,
                BottomMargin = 20 * 30,
                ItemGap = 10 * 30
            });

            TestAssert.Equal(3, plan.Slots.Count, "Combined layout lost a plan or section view.");
            TestAssert.Equal("LB-01", plan.Slots[0].Item.Key, "First plan order changed.");
            TestAssert.Equal("LB-02", plan.Slots[1].Item.Key, "Second plan order changed.");
            TestAssert.Equal("SECTION", plan.Slots[2].Item.Key, "The section must remain after all plans.");
            TestAssert.True(plan.PageCount >= 1, "Combined layout must create at least one page.");
        }

        private static void PacksFivePlansAndTallSectionIntoMergedGridCells()
        {
            var items = Enumerable.Range(1, 5).Select(index => new StairLayoutItem
            {
                Key = "PLAN-" + index,
                Name = index + "层楼梯平面图",
                Width = 6900,
                Height = 4800
            }).ToList();
            items.Add(new StairLayoutItem
            {
                Key = "SECTION",
                Name = "LT-01 楼梯剖面图",
                Width = 7000,
                Height = 15000,
                IsSection = true
            });
            var plan = StairCombinedLayout.Compute(items, new StairLayoutOptions
            {
                PageWidth = 841 * 30,
                PageHeight = 594 * 30,
                LeftMargin = 30 * 30,
                RightMargin = 60 * 30,
                TopMargin = 20 * 30,
                BottomMargin = 20 * 30,
                ItemGap = 10 * 30
            });
            TestAssert.Equal(1, plan.PageCount,
                "Five regular plan cells and one tall merged section cell should fit one A1 page.");
            foreach (var a in plan.Slots)
                foreach (var b in plan.Slots.Where(value => !ReferenceEquals(value, a)
                    && value.Page == a.Page))
                    TestAssert.True(a.X + a.Width <= b.X + 0.001
                        || b.X + b.Width <= a.X + 0.001
                        || a.Y + a.Height <= b.Y + 0.001
                        || b.Y + b.Height <= a.Y + 0.001,
                        "Merged-grid layout items must not overlap.");
        }

        private static void PersistsDraggedCombinedLayoutGridRatios()
        {
            var items = Enumerable.Range(1, 5).Select(index => new StairLayoutItem
            {
                Key = "PLAN-" + index,
                Name = index + "层楼梯平面图",
                Width = 6200,
                Height = 4300
            }).ToList();
            items.Add(new StairLayoutItem
            {
                Key = "SECTION",
                Name = "LT-01 楼梯剖面图",
                Width = 6500,
                Height = 13800,
                IsSection = true
            });
            var options = new StairLayoutOptions
            {
                PageWidth = 841 * 30,
                PageHeight = 594 * 30,
                LeftMargin = 30 * 30,
                RightMargin = 60 * 30,
                TopMargin = 20 * 30,
                BottomMargin = 20 * 30,
                ItemGap = 10 * 30
            };
            var automatic = StairCombinedLayout.Compute(items, options);
            TestAssert.True(automatic.Columns > 1 && automatic.Rows > 1,
                "The table layout must expose its real rows and columns.");
            var section = automatic.Slots.Single(value => value.Item.IsSection);
            TestAssert.True(section.ColumnSpan > 1 || section.RowSpan > 1,
                "A large section must be represented as a merged grid cell.");
            var ratios = automatic.ColumnWidths
                .Select(value => value / automatic.ColumnWidths.Sum()).ToList();
            var delta = Math.Min(0.02, ratios[1] * 0.2);
            ratios[0] += delta;
            ratios[1] -= delta;
            options.GridColumns = automatic.Columns;
            options.GridRows = automatic.Rows;
            options.ColumnRatios = ratios;
            options.RowRatios = automatic.RowHeights
                .Select(value => value / automatic.RowHeights.Sum()).ToList();
            var adjusted = StairCombinedLayout.Compute(items, options);
            TestAssert.NearlyEqual(ratios[0],
                adjusted.ColumnWidths[0] / adjusted.ColumnWidths.Sum(), 0.000001,
                "A dragged divider ratio must survive the layout recomputation used by final insertion.");
            TestAssert.Equal(automatic.Columns, adjusted.Columns,
                "Dragging a divider must not change the automatic grid topology.");
        }

        private static void MovesCombinedLayoutItemIntoAnEmptyCell()
        {
            var items = Enumerable.Range(1, 3).Select(index => new StairLayoutItem
            {
                Key = "PLAN-" + index,
                Name = index + "层楼梯平面图",
                Width = 6900,
                Height = 4800
            }).ToList();
            items.Add(new StairLayoutItem
            {
                Key = "SECTION",
                Name = "楼梯剖面图",
                Width = 7000,
                Height = 15000,
                IsSection = true
            });
            var plan = StairCombinedLayout.Compute(items, new StairLayoutOptions
            {
                PageWidth = 841 * 30,
                PageHeight = 594 * 30,
                LeftMargin = 30 * 30,
                RightMargin = 60 * 30,
                TopMargin = 20 * 30,
                BottomMargin = 20 * 30,
                ItemGap = 10 * 30
            });
            var item = plan.Slots.First(value => value.ColumnSpan == 1
                && value.RowSpan == 1);
            var empty = (from row in Enumerable.Range(0, plan.Rows)
                         from column in Enumerable.Range(0, plan.Columns)
                         where !plan.Slots.Any(slot => slot.Page == item.Page
                             && slot.Column <= column
                             && slot.Column + slot.ColumnSpan > column
                             && slot.Row <= row
                             && slot.Row + slot.RowSpan > row)
                         select new { Row = row, Column = column }).First();

            StairCombinedLayout.ApplyPlacements(plan,
                new[] { new StairLayoutPlacementDefinition
                {
                    Key = item.Item.Key,
                    Page = item.Page,
                    Row = empty.Row,
                    Column = empty.Column
                }});

            TestAssert.Equal(empty.Row, item.Row,
                "A dragged layout item must move into an empty row.");
            TestAssert.Equal(empty.Column, item.Column,
                "A dragged layout item must move into an empty column.");
        }

        private static void ClipsOrdinaryPlanSegmentsToCropBoundary()
        {
            var polygon = Rectangle(0.0, 0.0, 10.0, 8.0);
            var clipped = PlanPolygonClipper.ClipSegment(
                new Point2D(-5.0, 4.0),
                new Point2D(15.0, 4.0),
                polygon);

            TestAssert.Equal(1, clipped.Count, "A line crossing a rectangle must produce one inside segment.");
            TestAssert.NearlyEqual(0.0, clipped[0].Start.X, 0.0001, "The clipped start is incorrect.");
            TestAssert.NearlyEqual(10.0, clipped[0].End.X, 0.0001, "The clipped end is incorrect.");
        }

        private static void SplitsSegmentsAcrossConcaveCropBoundary()
        {
            var polygon = new List<Point2D>
            {
                new Point2D(0.0, 0.0),
                new Point2D(10.0, 0.0),
                new Point2D(10.0, 10.0),
                new Point2D(6.0, 10.0),
                new Point2D(6.0, 4.0),
                new Point2D(4.0, 4.0),
                new Point2D(4.0, 10.0),
                new Point2D(0.0, 10.0)
            };
            var clipped = PlanPolygonClipper.ClipSegment(
                new Point2D(-1.0, 6.0),
                new Point2D(11.0, 6.0),
                polygon);

            TestAssert.Equal(2, clipped.Count, "A concave crop boundary must be able to split one line twice.");
            TestAssert.NearlyEqual(0.0, clipped[0].Start.X, 0.0001, "The first concave segment start is incorrect.");
            TestAssert.NearlyEqual(4.0, clipped[0].End.X, 0.0001, "The first concave segment end is incorrect.");
            TestAssert.NearlyEqual(6.0, clipped[1].Start.X, 0.0001, "The second concave segment start is incorrect.");
            TestAssert.NearlyEqual(10.0, clipped[1].End.X, 0.0001, "The second concave segment end is incorrect.");
        }

        private static void KeepsBoundaryAlignedSegmentsAndRejectsOutsideSegments()
        {
            var polygon = Rectangle(0.0, 0.0, 10.0, 8.0);
            var boundary = PlanPolygonClipper.ClipSegment(
                new Point2D(2.0, 0.0),
                new Point2D(8.0, 0.0),
                polygon);
            var outside = PlanPolygonClipper.ClipSegment(
                new Point2D(2.0, 9.0),
                new Point2D(8.0, 9.0),
                polygon);

            TestAssert.Equal(1, boundary.Count, "A segment on the crop boundary must be preserved.");
            TestAssert.Equal(0, outside.Count, "A segment outside the crop boundary must be rejected.");
        }

        private static IList<Point2D> Rectangle(double minX, double minY, double maxX, double maxY)
        {
            return new List<Point2D>
            {
                new Point2D(minX, minY),
                new Point2D(maxX, minY),
                new Point2D(maxX, maxY),
                new Point2D(minX, maxY)
            };
        }

        private static void BuildsWallSegmentDoorAndWindowOpenings()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Construction.OppositeSupportsEnabled = false;
            var calculator = new StairProjectCalculator();
            var outcome = calculator.Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The opening test project must calculate.");
            var builder = new StairProjectGeometryBuilder();
            var original = builder.BuildSection(project, outcome.Result);
            var segment = original.Lines
                .Where(line => line.Role == StairLineRole.WallBoundary
                    && line.ComponentId.StartsWith("WALL-", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(line.Start.X - line.End.X) < 0.001)
                .GroupBy(line => line.ComponentId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Id = group.Key,
                    Bottom = group.SelectMany(line => new[] { line.Start.Y, line.End.Y }).Min(),
                    Top = group.SelectMany(line => new[] { line.Start.Y, line.End.Y }).Max(),
                    LeftFace = group.Min(line => line.Start.X),
                    RightFace = group.Max(line => line.Start.X)
                })
                .Where(item => item.Top - item.Bottom > 2300.0)
                .OrderByDescending(item => item.Top - item.Bottom)
                .First();

            project.WallOpenings.Add(new StairWallOpeningDefinition
            {
                SegmentId = segment.Id,
                Type = WallOpeningType.Window,
                Height = 1200.0,
                SillHeight = 800.0
            });
            var withWindow = builder.BuildSection(project, outcome.Result);
            var symbols = withWindow.Lines.Where(line => line.ComponentId == segment.Id
                && line.Role == StairLineRole.OpeningBoundary).ToArray();
            TestAssert.Equal(4, symbols.Length,
                "A window must create four cyan vertical lines in its selected wall segment.");
            TestAssert.True(symbols.All(line =>
                    Math.Abs(Math.Min(line.Start.Y, line.End.Y) - (segment.Bottom + 800.0)) < 0.001
                    && Math.Abs(Math.Max(line.Start.Y, line.End.Y) - (segment.Bottom + 2000.0)) < 0.001),
                "Window height and sill must be measured from the supporting floor or landing.");
            var symbolXs = symbols.Select(line => line.Start.X).OrderBy(value => value).ToArray();
            TestAssert.NearlyEqual(segment.LeftFace, symbolXs[0], 0.001,
                "The first door/window elevation line must coincide with the first wall face.");
            TestAssert.NearlyEqual(segment.RightFace, symbolXs[3], 0.001,
                "The fourth door/window elevation line must coincide with the second wall face.");
            TestAssert.NearlyEqual(50.0, symbolXs[2] - symbolXs[1], 0.001,
                "The two inner door/window lines must be centred and 50 mm apart.");
            TestAssert.True(withWindow.Lines.Any(line => line.ComponentId == segment.Id
                    && line.Role == StairLineRole.WallOpeningLowerEdge)
                && withWindow.Lines.Any(line => line.ComponentId == segment.Id
                    && line.Role == StairLineRole.WallOpeningUpperEdge),
                "Window sill and header must be explicit wall cut edges so CAD can bold them inward.");
            TestAssert.True(withWindow.HatchRegions.Where(region => region.IsWall
                    && region.ComponentId == segment.Id).All(region =>
                        region.Boundary.Max(point => point.Y) <= segment.Bottom + 800.001
                        || region.Boundary.Min(point => point.Y) >= segment.Bottom + 1999.999),
                "Wall hatch must leave the configured window opening empty.");

            project.WallOpenings[0].Type = WallOpeningType.Door;
            project.WallOpenings[0].Height = 2100.0;
            project.WallOpenings[0].SillHeight = 900.0;
            var withDoor = builder.BuildSection(project, outcome.Result);
            var doorSymbols = withDoor.Lines.Where(line => line.ComponentId == segment.Id
                && line.Role == StairLineRole.OpeningBoundary).ToArray();
            TestAssert.Equal(4, doorSymbols.Length,
                "Doors and windows must use the same four cyan vertical-line convention.");
            var doorSymbol = doorSymbols[0];
            TestAssert.NearlyEqual(segment.Bottom,
                Math.Min(doorSymbol.Start.Y, doorSymbol.End.Y), 0.001,
                "A door must start at the supporting floor or landing regardless of a stale sill value.");
            TestAssert.NearlyEqual(segment.Bottom + 2100.0,
                Math.Max(doorSymbol.Start.Y, doorSymbol.End.Y), 0.001,
                "The configured door height must be retained.");
        }

        private static void BuildsPlatformDoorWindowElevationWithoutChangingPlatform()
        {
            var project = StairProjectDefinition.CreateDefault();
            var floor = project.Floors.Single(item => item.Id == "LB-02");
            var originalWidth = floor.PlatformWidth;
            var originalBeam = floor.BeamDepthOverride;
            floor.DoorWindowElevation = StairPlatformOpeningDefinition.CreateDefault();
            floor.DoorWindowElevation.Type = WallOpeningType.Window;
            floor.DoorWindowElevation.Width = 900.0;
            floor.DoorWindowElevation.Height = 1200.0;
            floor.DoorWindowElevation.SillHeight = 800.0;
            floor.DoorWindowElevation.DistanceFromWall = 100.0;
            floor.DoorWindowElevation.GeometryLines =
                "0,0,900,0,1|900,0,900,1200,3|900,1200,0,1200,4|0,1200,0,0,5";

            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The platform elevation test project must calculate.");
            var view = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var elevationLines = view.Lines.Where(line => line.ComponentId == floor.Id
                && (line.Role == StairLineRole.DoorWindowWindowMain
                    || line.Role == StairLineRole.DoorWindowWindowSash
                    || line.Role == StairLineRole.DoorWindowOpeningHole)).ToArray();
            TestAssert.Equal(4, elevationLines.Length,
                "The shared division geometry must be placed above its floor as cyan linework.");
            TestAssert.True(elevationLines.Any(line => line.Role == StairLineRole.DoorWindowWindowMain)
                    && elevationLines.Any(line => line.Role == StairLineRole.DoorWindowWindowSash)
                    && elevationLines.Any(line => line.Role == StairLineRole.DoorWindowOpeningHole),
                "Shared MCLM geometry must preserve main, sash and opening line categories for CAD styling.");
            TestAssert.NearlyEqual(900.0,
                elevationLines.SelectMany(line => new[] { line.Start.X, line.End.X }).Max()
                - elevationLines.SelectMany(line => new[] { line.Start.X, line.End.X }).Min(), 0.001,
                "The configured door/window elevation width must be retained.");
            TestAssert.NearlyEqual(1200.0,
                elevationLines.SelectMany(line => new[] { line.Start.Y, line.End.Y }).Max()
                - elevationLines.SelectMany(line => new[] { line.Start.Y, line.End.Y }).Min(), 0.001,
                "The configured door/window elevation height must be retained.");
            TestAssert.NearlyEqual(originalWidth, floor.PlatformWidth, 0.001,
                "Adding an elevation must not change the existing platform width.");
            TestAssert.True(floor.BeamDepthOverride == originalBeam,
                "Adding an elevation must not change the existing beam override.");
        }

        private static void ConnectsMixedTwoThreeTwoFlightStoreys()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.BaseElevation = -3000.0;
            project.BasementStoreyCount = 1;
            var basementFloor = StairFloorDefinition.CreateDefault("LB-B1", "负一层楼板");
            project.Floors.Insert(0, basementFloor);
            var basement = StairStoreyDefinition.CreateDoubleFlight(
                "LC-B1", "负一层", "LB-B1", "LB-01", 3000.0, 0);
            project.Storeys.Insert(0, basement);

            var threeFlight = project.Storeys[1];
            threeFlight.Flights.Clear();
            threeFlight.Landings.Clear();
            threeFlight.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-01-1", "第一跑", 6, StairFlightDirection.Right, StairSectionRepresentation.Rear));
            threeFlight.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-01-2", "第二跑", 6, StairFlightDirection.Left, StairSectionRepresentation.Cut));
            threeFlight.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-01-3", "第三跑", 6, StairFlightDirection.Right, StairSectionRepresentation.Cut));
            threeFlight.Landings.Add(StairLandingDefinition.CreateDefault(
                "PT-01-1", "第一平台", "TD-01-1", "TD-01-2"));
            threeFlight.Landings.Add(StairLandingDefinition.CreateDefault(
                "PT-01-2", "第二平台", "TD-01-2", "TD-01-3"));
            threeFlight.TotalRiserCount = 18;
            project.Floors[2].AllowLowerFlightClosure = true;

            foreach (var storey in project.Storeys)
            {
                storey.StairwellConstraintLocked = false;
                storey.TreadDepthLinked = false;
                foreach (var flight in storey.Flights) flight.TreadDepth = 260.0;
            }
            project.Floors[0].PlanFloorLabel = "-1层";
            project.Floors[1].PlanFloorLabel = "1~3层";
            project.Floors[2].PlanFloorLabel = "4层";
            project.Floors[3].PlanFloorLabel = "5层";

            new StairProjectConstraintService().Apply(project);

            var directBoundaries = new[]
            {
                project.Floors[1].PlatformWidth,
                threeFlight.Landings[0].PlatformWidth,
                threeFlight.Landings[1].PlatformWidth
            };
            for (var index = 0; index < 2; index++)
            {
                var run = threeFlight.Flights[index].TreadDepth
                    * Math.Max(0, threeFlight.Flights[index].RiserCount - 1);
                TestAssert.NearlyEqual(
                    project.Construction.StairwellDepth,
                    directBoundaries[index] + run + directBoundaries[index + 1],
                    0.001,
                    "The first two runs of a three-flight storey must meet their landings directly.");
            }

            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess,
                "A basement two-flight, middle three-flight and upper two-flight stair must calculate.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var upperElevation = outcome.Result.Storeys[1].UpperElevation
                - outcome.Result.Storeys.Min(item => item.LowerElevation);
            TestAssert.True(section.Lines.Any(line => line.ComponentId == "TD-01-3"
                    && Math.Abs(line.Start.Y - upperElevation) < 0.001
                    && Math.Abs(line.End.Y - upperElevation) < 0.001
                    && Math.Abs(line.End.X - line.Start.X) > 0.001),
                "The final run of the three-flight storey must close continuously to the upper floor.");

            var lowest = outcome.Result.Storeys.Min(item => item.LowerElevation);
            var expectedElevations = new Dictionary<string, double>
            {
                { "-3.000 (-1F)", outcome.Result.Storeys[0].LowerElevation - lowest },
                { "±0.000 (1F)", outcome.Result.Storeys[1].LowerElevation - lowest },
                { "9.000 (4F)", outcome.Result.Storeys[1].UpperElevation - lowest },
                { "12.200 (5F)", outcome.Result.Storeys[2].UpperElevation - lowest }
            };
            var levelTexts = expectedElevations.Select(pair => section.Texts.Single(text =>
                text.Content == pair.Key)).ToArray();
            foreach (var pair in expectedElevations)
            {
                var text = section.Texts.Single(item => item.Content == pair.Key);
                TestAssert.NearlyEqual(pair.Value, text.Position.Y, 0.001,
                    "Every floor label must sit on its physical slab elevation, including multi-flight storeys.");
            }
            TestAssert.Equal(1, levelTexts.Select(text => Math.Round(text.Position.X, 3)).Distinct().Count(),
                "All floor labels must share one fixed column outside the right axis.");
            TestAssert.True(levelTexts[0].Position.X > project.Construction.StairwellDepth,
                "Floor labels must be placed outside the right stairwell axis.");
        }

        private static void AllowsTwoFlightFinalClosureWithoutChangingUpperStorey()
        {
            var project = StairProjectDefinition.CreateDefault();
            var lower = project.Storeys[0];
            TestAssert.True(!project.Floors[1].AllowLowerFlightClosure
                    && !project.Floors[1].AllowUpperFlightClosure,
                "Both per-boundary closure switches must be disabled by default.");
            project.Floors[1].AllowLowerFlightClosure = true;
            lower.StairwellConstraintLocked = false;
            lower.TreadDepthLinked = false;
            lower.Flights[0].TreadDepth = 280.0;
            lower.Flights[1].TreadDepth = 280.0;
            lower.Flights[1].RiserCount = 7;
            lower.TotalRiserCount = 16;
            project.Floors[1].PlatformWidth = 1350.0;

            var constraints = new StairProjectConstraintService();
            constraints.Apply(project);
            var sharedFloorWidth = project.Floors[1].PlatformWidth;
            var upperLandingWidth = project.Storeys[1].Landings[0].PlatformWidth;
            var upperFloorWidth = project.Floors[2].PlatformWidth;
            var treadDepths = lower.Flights.Select(item => item.TreadDepth).ToArray();

            constraints.SetPlatformWidth(project, lower.Landings[0].Id, 1300.0);
            constraints.Apply(project);

            TestAssert.NearlyEqual(1300.0, lower.Landings[0].PlatformWidth, 0.001,
                "The edited lower-storey landing width must be retained.");
            TestAssert.NearlyEqual(sharedFloorWidth, project.Floors[1].PlatformWidth, 0.001,
                "A lower-storey closure gap must not change the shared upper floor width.");
            TestAssert.NearlyEqual(upperLandingWidth, project.Storeys[1].Landings[0].PlatformWidth, 0.001,
                "A lower-storey edit must not propagate into the upper-storey landing.");
            TestAssert.NearlyEqual(upperFloorWidth, project.Floors[2].PlatformWidth, 0.001,
                "A lower-storey edit must not propagate into the next upper floor.");
            TestAssert.True(lower.Flights.Select((item, index) =>
                    Math.Abs(item.TreadDepth - treadDepths[index]) < 0.001).All(value => value),
                "Allowing a final closure must not change user-selected tread depths.");

            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "A two-flight storey with final closure must calculate.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var upperElevation = outcome.Result.Storeys[0].UpperElevation;
            TestAssert.True(section.Lines.Any(line => line.ComponentId == lower.Flights[1].Id
                    && Math.Abs(line.Start.Y - upperElevation) < 0.001
                    && Math.Abs(line.End.Y - upperElevation) < 0.001
                    && Math.Abs(line.End.X - line.Start.X) > 0.001),
                "The two-flight final run must bridge continuously to its unchanged upper floor.");
            var undersideDiagonals = section.Lines.Where(line =>
                    line.ComponentId == lower.Flights[1].Id
                    && Math.Abs(line.Start.X - line.End.X) > 0.001
                    && Math.Abs(line.Start.Y - line.End.Y) > 0.001)
                .ToArray();
            TestAssert.Equal(1, undersideDiagonals.Length,
                "A nearby upper floor must be reached by one continuous stair soffit without a kink.");
            var structuralClosure = section.Lines.First(line =>
                line.ComponentId == lower.Flights[1].Id
                && Math.Abs(line.Start.Y - upperElevation) < 0.001
                && Math.Abs(line.End.Y - upperElevation) < 0.001
                && Math.Abs(line.End.X - line.Start.X) > 0.001);
            TestAssert.True(section.Lines.Any(line =>
                    line.Role == StairLineRole.Handrail
                    && Math.Abs(line.Start.Y - (upperElevation + project.Construction.Railing.Height)) < 0.001
                    && Math.Abs(line.End.Y - line.Start.Y) < 0.001
                    && SameUndirectedInterval(
                        line.Start.X,
                        line.End.X,
                        structuralClosure.Start.X,
                        structuralClosure.End.X)),
                "A closing flight must continue its handrail horizontally over the closure segment.");
        }

        private static void ConnectsEveryNonThreeFlightStoreyDirectly()
        {
            foreach (var flightCount in new[] { 1, 2, 4 })
            {
                var project = StairProjectDefinition.CreateDefault();
                project.Storeys.RemoveAt(1);
                project.Floors.RemoveAt(2);
                var storey = project.Storeys[0];
                storey.Flights.Clear();
                storey.Landings.Clear();
                for (var index = 0; index < flightCount; index++)
                {
                    var flightId = "TD-X-" + (index + 1);
                    storey.Flights.Add(StairFlightDefinition.CreateDefault(
                        flightId,
                        "测试梯段",
                        9,
                        index % 2 == 0 ? StairFlightDirection.Right : StairFlightDirection.Left,
                        index % 2 == 0 ? StairSectionRepresentation.Rear : StairSectionRepresentation.Cut));
                    if (index > 0)
                    {
                        storey.Landings.Add(StairLandingDefinition.CreateDefault(
                            "PT-X-" + index,
                            "测试平台",
                            storey.Flights[index - 1].Id,
                            flightId));
                    }
                }
                storey.Height = storey.Flights.Sum(item => item.RiserCount) * 166.6666667;
                storey.TotalRiserCount = storey.Flights.Sum(item => item.RiserCount);

                var constraints = new StairProjectConstraintService();
                constraints.Apply(project);
                var outcome = new StairProjectCalculator().Calculate(project);
                TestAssert.True(outcome.IsSuccess, "The non-three-flight storey must calculate.");

                var boundaries = new List<double> { project.Floors[0].PlatformWidth };
                boundaries.AddRange(storey.Landings.Select(item => item.PlatformWidth));
                boundaries.Add(project.Floors[1].PlatformWidth);
                for (var index = 0; index < flightCount; index++)
                {
                    var flight = storey.Flights[index];
                    var total = boundaries[index]
                        + flight.TreadDepth * Math.Max(0, flight.RiserCount - 1)
                        + boundaries[index + 1];
                    TestAssert.NearlyEqual(project.Construction.StairwellDepth, total, 0.001,
                        "Only a three-flight storey may have a flight-to-platform closure gap.");
                }

                new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            }
        }

        private static void SynchronizesDisplayedTotalRiserCount()
        {
            var project = StairProjectDefinition.CreateDefault();
            var storey = project.Storeys[0];
            storey.TotalRiserCount = 999;

            new StairProjectConstraintService().Apply(project);

            TestAssert.Equal(
                storey.Flights.Sum(item => item.RiserCount),
                storey.TotalRiserCount,
                "The displayed storey total must equal the sum of its flights.");
        }

        private static void KeepsTotalRiserCountWhileRespectingFlightLocks()
        {
            var project = StairProjectDefinition.CreateDefault();
            var storey = project.Storeys[0];
            storey.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-01-3", "第三跑", 4, StairFlightDirection.Right, StairSectionRepresentation.Cut));
            storey.Landings.Add(StairLandingDefinition.CreateDefault(
                "PT-01-2", "第二平台", storey.Flights[1].Id, storey.Flights[2].Id));
            storey.TotalRiserCount = 18;
            storey.Flights[0].RiserCount = 7;
            storey.Flights[0].RiserCountLocked = true;
            storey.Flights[1].RiserCount = 4;
            storey.Flights[2].RiserCount = 4;

            new StairProjectConstraintService().Apply(project);

            TestAssert.Equal(18, storey.TotalRiserCount,
                "Changing per-flight risers must not change the storey total.");
            TestAssert.Equal(7, storey.Flights[0].RiserCount,
                "A locked flight riser count must be retained.");
            TestAssert.Equal(11, storey.Flights[1].RiserCount + storey.Flights[2].RiserCount,
                "Unlocked flights must absorb the remaining storey risers.");

            storey.Flights[1].RiserCountLocked = true;
            new StairProjectConstraintService().Apply(project);
            TestAssert.True(storey.Flights.Count(item => item.RiserCountLocked) == 1,
                "A three-flight storey may persistently lock at most one flight.");

            storey.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-01-4", "第四跑", 3, StairFlightDirection.Left, StairSectionRepresentation.Cut));
            storey.Landings.Add(StairLandingDefinition.CreateDefault(
                "PT-01-3", "第三平台", storey.Flights[2].Id, storey.Flights[3].Id));
            storey.TotalRiserCount = 24;
            storey.Flights[0].RiserCountLocked = true;
            storey.Flights[1].RiserCountLocked = true;
            new StairProjectConstraintService().Apply(project);
            TestAssert.True(storey.Flights.Count(item => item.RiserCountLocked) == 2,
                "A four-flight storey may lock two flights at the same time.");
            TestAssert.Equal(24, storey.Flights.Sum(item => item.RiserCount),
                "The four-flight distribution must retain the fixed storey total.");
        }

        private static void MigratesLegacyClosureSettingsToBoundaries()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.SchemaVersion = 7;
            project.Storeys[0].AllowUpperClosureGap = true;
            project.Floors[1].AllowLowerFlightClosure = false;
            project.Storeys[0].Height = 3150.0;
            project.Floors[0].PlatformWidth = 1375.0;
            project.WallOpenings = null;

            new StairProjectConstraintService().Normalize(project);

            TestAssert.Equal(22, project.SchemaVersion,
                "Legacy projects must migrate through the current drawing-output schema.");
            TestAssert.True(project.Floors[1].AllowLowerFlightClosure,
                "The old final-flight switch must migrate to the destination floor.");
            TestAssert.True(!project.Storeys[0].AllowUpperClosureGap,
                "The deprecated storey-level switch must be cleared after migration.");
            TestAssert.NearlyEqual(3150.0, project.Storeys[0].Height, 0.001,
                "Adding wall-opening storage must not alter an existing storey height.");
            TestAssert.NearlyEqual(1375.0, project.Floors[0].PlatformWidth, 0.001,
                "Adding wall-opening storage must not alter an existing platform width.");
            TestAssert.Equal(0, project.WallOpenings.Count,
                "A legacy project must migrate to an empty optional wall-opening list.");
            TestAssert.Equal(0, project.PlanSources.Count,
                "A legacy project must migrate to an empty optional plan-source list without changing old geometry.");
        }

        private static void MigratesPlanSourceTargetScaleWithoutChangingSourceScale()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.SchemaVersion = 16;
            project.DrawingScale = 50;
            project.PlanSources.Add(new StairPlanSourceDefinition
            {
                StoreyId = project.Storeys[0].Id,
                SourceScale = 100,
                TargetScale = 0
            });

            new StairProjectConstraintService().Normalize(project);

            TestAssert.Equal(22, project.SchemaVersion,
                "Plan-source scale metadata must migrate to the current schema.");
            TestAssert.Equal(100, project.PlanSources[0].SourceScale,
                "Migration must not alter the scale read from the source Tianzheng plan.");
            TestAssert.Equal(50, project.PlanSources[0].TargetScale,
                "A copied plan must inherit the stair-detail drawing scale as its target scale.");
        }

        private static void MigratesStandardFloorMetadataWithoutChangingCapturedGeometry()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.SchemaVersion = 17;
            project.Storeys[0].PlanFloorLabel = null;
            project.Storeys[0].PlanRepeatCount = 0;
            project.PlanSources.Add(new StairPlanSourceDefinition
            {
                StoreyId = project.Storeys[0].Id,
                DisplayName = "二层",
                FloorLabel = null,
                RepeatCount = 0,
                CropOffset = 360.0,
                SourceHandle = "2A7"
            });

            new StairProjectConstraintService().Normalize(project);

            var source = project.PlanSources[0];
            TestAssert.Equal(22, project.SchemaVersion,
                "Standard-floor metadata must migrate additively.");
            TestAssert.Equal("二层", source.FloorLabel,
                "A legacy source must inherit its existing display name.");
            TestAssert.Equal(1, source.RepeatCount,
                "A legacy source represents one floor by default.");
            TestAssert.NearlyEqual(360.0, source.CropOffset, 0.001,
                "Adding standard-floor metadata must not alter capture geometry settings.");
            TestAssert.Equal("2A7", source.SourceHandle,
                "Adding standard-floor metadata must preserve source identity.");
            TestAssert.Equal("二层", project.Storeys[0].PlanFloorLabel,
                "Logical-floor metadata must be available before or independently of plan capture.");
        }

        private static void MigratesLogicalFloorRangeToStorey()
        {
            var project = StairProjectDefinition.CreateDefault();
            var removedUpperFloorId = project.Storeys[1].UpperFloorId;
            project.Storeys.RemoveAt(1);
            project.Floors = project.Floors
                .Where(item => item.Id != removedUpperFloorId)
                .ToList();
            project.SchemaVersion = 18;
            project.Storeys[0].PlanFloorLabel = null;
            project.Storeys[0].PlanRepeatCount = 0;
            project.PlanSources.Add(new StairPlanSourceDefinition
            {
                StoreyId = project.Storeys[0].Id,
                FloorLabel = "4~18层",
                RepeatCount = 15
            });

            new StairProjectConstraintService().Normalize(project);

            TestAssert.Equal(22, project.SchemaVersion,
                "Logical-floor metadata must migrate to the current schema.");
            TestAssert.Equal("4~18层", project.Storeys[0].PlanFloorLabel,
                "A captured standard-floor range must migrate to its storey record.");
            TestAssert.Equal(15, project.Storeys[0].PlanRepeatCount,
                "The inclusive physical-floor count must be derived from the range.");
            TestAssert.Equal(15, project.PlanSources[0].RepeatCount,
                "The plan-source compatibility mirror must stay synchronized.");
            var lowerFloor = project.Floors.First(item => item.Id == project.Storeys[0].LowerFloorId);
            var terminalFloor = project.Floors.First(item => item.Id == project.Storeys[project.Storeys.Count - 1].UpperFloorId);
            TestAssert.Equal("4~18层", lowerFloor.PlanFloorLabel,
                "The lower physical floor must own the migrated plan-level label.");
            TestAssert.Equal(15, lowerFloor.PlanRepeatCount,
                "The lower physical floor must own the migrated repeat count.");
            TestAssert.Equal("19层", terminalFloor.PlanFloorLabel,
                "The terminal upper plan must be derived after a standard-floor range.");
            TestAssert.Equal(lowerFloor.Id, project.PlanSources[0].FloorId,
                "A legacy captured plan must migrate to the interval's lower physical floor.");
        }

        private static void AllowsUpperFlightClosureFromAPlatform()
        {
            var project = StairProjectDefinition.CreateDefault();
            var storey = project.Storeys[0];
            project.Floors[0].AllowUpperFlightClosure = true;
            storey.StairwellConstraintLocked = false;
            storey.TreadDepthLinked = false;
            storey.Flights[0].TreadDepth = 260.0;

            new StairProjectConstraintService().Apply(project);
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess,
                "An outgoing flight closure at a floor must remain calculable.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var sourceConnection = project.Floors[0].PlatformWidth;
            var topClosure = section.Lines.FirstOrDefault(line => line.ComponentId == storey.Flights[0].Id
                    && Math.Abs(line.Start.Y) < 0.001
                    && Math.Abs(line.End.Y) < 0.001
                    && (Math.Abs(line.Start.X - sourceConnection) < 0.001
                        || Math.Abs(line.End.X - sourceConnection) < 0.001)
                    && Math.Abs(line.End.X - line.Start.X) > 0.001);
            TestAssert.True(topClosure != null,
                "The outgoing flight must connect back to the source floor with a horizontal closure.");
            var closureStartX = Math.Abs(topClosure.Start.X - sourceConnection) < 0.001
                ? topClosure.End.X
                : topClosure.Start.X;
            var closureUnderside = -project.Construction.FlightSlabThickness;
            var undersideClosure = section.Lines.FirstOrDefault(line => line.ComponentId == storey.Flights[0].Id
                    && Math.Abs(line.Start.Y - closureUnderside) < 0.001
                    && Math.Abs(line.End.Y - closureUnderside) < 0.001
                    && (Math.Abs(line.Start.X - sourceConnection) < 0.001
                        || Math.Abs(line.End.X - sourceConnection) < 0.001));
            TestAssert.True(undersideClosure != null
                    && Math.Abs(undersideClosure.End.X - undersideClosure.Start.X) > 0.001,
                "The outgoing closure underside must stay horizontal and use the flight slab thickness.");
            TestAssert.True(!section.Lines.Any(line => line.ComponentId == storey.Flights[0].Id
                    && Math.Abs(line.Start.X - closureStartX) < 0.001
                    && Math.Abs(line.End.X - closureStartX) < 0.001
                    && Math.Min(line.Start.Y, line.End.Y) < -0.001),
                "The closure-to-flight joint must not leave a visible internal divider or pointed underside.");
            var expectedRailElevation = project.Construction.Railing.Height;
            var horizontalRail = section.Lines.FirstOrDefault(line => line.ComponentId == "RAILING"
                    && line.Role == StairLineRole.Handrail
                    && Math.Abs(line.Start.Y - expectedRailElevation) < 0.001
                    && Math.Abs(line.End.Y - expectedRailElevation) < 0.001
                    && (Math.Abs(line.Start.X - sourceConnection) < 0.001
                        || Math.Abs(line.End.X - sourceConnection) < 0.001));
            TestAssert.True(horizontalRail != null,
                "The outgoing closure handrail must remain horizontal at the platform handrail height.");
            var horizontalRailEnd = Math.Abs(horizontalRail.Start.X - sourceConnection) < 0.001
                ? horizontalRail.End
                : horizontalRail.Start;
            var firstRail = new Point2D(
                closureStartX,
                outcome.Result.Storeys[0].RiserHeight + project.Construction.Railing.Height);
            TestAssert.NearlyEqual(closureStartX, horizontalRailEnd.X, 0.001,
                "The platform handrail must remain horizontal all the way to the flight start.");
            TestAssert.NearlyEqual(outcome.Result.Storeys[0].RiserHeight,
                firstRail.Y - horizontalRailEnd.Y,
                0.001,
                "The sloping handrail must start exactly one riser above the horizontal platform handrail.");
        }

        private static void AlternatesUnifiedFloorAndLandingDirections()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys.RemoveAt(1);
            project.Floors.RemoveAt(2);
            var storey = project.Storeys[0];
            storey.Flights.Clear();
            storey.Landings.Clear();
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-1", "第一跑", 6, StairFlightDirection.Right, StairSectionRepresentation.Rear));
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-2", "第二跑", 6, StairFlightDirection.Right, StairSectionRepresentation.Cut));
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-3", "第三跑", 7, StairFlightDirection.Right, StairSectionRepresentation.Cut));
            storey.Landings.Add(StairLandingDefinition.CreateDefault("PT-1-1", "第一平台", "TD-1-1", "TD-1-2"));
            storey.Landings.Add(StairLandingDefinition.CreateDefault("PT-1-2", "第二平台", "TD-1-2", "TD-1-3"));

            new StairProjectConstraintService().Apply(project);

            TestAssert.Equal(1, project.Floors[0].ProjectionDirection, "The lower floor must point from its axis to the shared flight connection edge.");
            TestAssert.Equal(-1, storey.Landings[0].ProjectionDirection, "Boundary 1 and boundary 2 must face opposite directions.");
            TestAssert.Equal(1, storey.Landings[1].ProjectionDirection, "Adjacent landing directions must alternate.");
            TestAssert.Equal(-1, project.Floors[1].ProjectionDirection, "The upper floor must continue the alternating boundary sequence.");
            TestAssert.Equal(StairFlightDirection.Right, storey.Flights[0].Direction, "The first flight direction is incorrect.");
            TestAssert.Equal(StairFlightDirection.Left, storey.Flights[1].Direction, "The second flight direction must reverse.");
            TestAssert.Equal(StairFlightDirection.Right, storey.Flights[2].Direction, "The third flight direction must reverse again.");
        }

        private static void DrawsUnifiedBoundaryGeometryFromConnectionToAxis()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Construction.OppositeSupportsEnabled = false;
            var constraints = new StairProjectConstraintService();
            constraints.Apply(project);
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The default project must calculate before geometry validation.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var storey = project.Storeys[0];
            var floorLines = section.Lines.Where(line => line.ComponentId == storey.UpperFloorId).ToArray();
            var xs = floorLines.SelectMany(line => new[] { line.Start.X, line.End.X }).ToArray();
            TestAssert.True(xs.Length > 0, "The upper shared floor outline is missing.");
            var floorDirection = project.Floors[1].ProjectionDirection;
            var floorAxisX = floorDirection > 0 ? 0.0 : project.Construction.StairwellDepth;
            var connectionX = floorAxisX + floorDirection * project.Floors[1].PlatformWidth;
            if (floorDirection > 0)
                TestAssert.NearlyEqual(connectionX, xs.Max(), 0.001, "A positive logical direction must draw from the shared connection edge back toward its axis.");
            else
                TestAssert.NearlyEqual(connectionX, xs.Min(), 0.001, "A negative logical direction must draw from the shared connection edge back toward its axis.");
        }

        private static void KeepsPlatformBeamsCenteredOnFixedAxes()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys[0].Flights[0].TreadDepth = 350.0;
            project.Storeys[0].Flights[1].TreadDepth = 350.0;
            new StairProjectConstraintService().Apply(project);
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The constrained project must calculate before fixed-axis validation.");

            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var axes = section.Lines
                .Where(line => line.Role == StairLineRole.AxisLine)
                .Select(line => line.Start.X)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            TestAssert.Equal(2, axes.Length, "The section must retain exactly two fixed stairwell axes.");
            TestAssert.NearlyEqual(project.Construction.StairwellDepth, axes[1] - axes[0], 0.001,
                "The fixed section axes must remain separated by the stairwell depth.");

            var boundaries = project.Floors.Skip(1).Select(floor => new
            {
                floor.Id,
                BeamWidth = floor.BeamWidthOverride ?? project.Construction.FloorBeam.Width
            }).Concat(project.Storeys.SelectMany(storey => storey.Landings).Select(landing => new
            {
                landing.Id,
                BeamWidth = landing.BeamWidthOverride ?? project.Construction.LandingBeam.Width
            }));
            foreach (var boundary in boundaries)
            {
                var verticalXs = section.Lines
                    .Where(line => line.ComponentId == boundary.Id
                        && Math.Abs(line.Start.X - line.End.X) < 0.001)
                    .Select(line => line.Start.X)
                    .Distinct()
                    .ToArray();
                var hasAxisCenteredBeam = axes.Any(axis => verticalXs.Any(first =>
                    verticalXs.Any(second =>
                        Math.Abs(Math.Abs(second - first) - boundary.BeamWidth) < 0.001
                        && Math.Abs((first + second) / 2.0 - axis) < 0.001)));
                TestAssert.True(hasAxisCenteredBeam,
                    boundary.Id + " must keep its axis-end beam centered on a fixed stairwell axis.");
            }
        }

        private static void PropagatesNonThreeFlightConnectionsAcrossSharedFloors()
        {
            var project = StairProjectDefinition.CreateDefault();
            var constraints = new StairProjectConstraintService();
            constraints.SetPlatformWidth(project, "PT-1-1", 1350.0);
            constraints.Apply(project);

            TestAssert.NearlyEqual(1350.0, project.Storeys[0].Landings[0].PlatformWidth, 0.001,
                "The edited landing width must be retained exactly.");
            TestAssert.NearlyEqual(1350.0, project.Storeys[1].Landings[0].PlatformWidth, 0.001,
                "A continuous non-three-flight chain must adapt the next landing so no flight separates from it.");
            TestAssert.NearlyEqual(1050.0, project.Floors[0].PlatformWidth, 0.001,
                "The preceding boundary must move so the incoming flight retains its tread depth.");
            TestAssert.NearlyEqual(1050.0, project.Floors[1].PlatformWidth, 0.001,
                "The following boundary must move so the outgoing flight retains its tread depth.");
            TestAssert.NearlyEqual(1050.0, project.Floors[2].PlatformWidth, 0.001,
                "The continuous non-three-flight chain must remain closed through its final floor.");
            TestAssert.True(project.Storeys[0].Flights.All(flight => Math.Abs(flight.TreadDepth - 280.0) < 0.001),
                "A landing edit must not stretch or recalculate any tread in its own storey.");
            TestAssert.True(project.Storeys[1].Flights.All(flight => Math.Abs(flight.TreadDepth - 280.0) < 0.001),
                "A landing edit must not change tread depths in another storey.");

            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "Storey-local landing widths must remain calculable.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var axes = section.Lines.Where(line => line.Role == StairLineRole.AxisLine)
                .Select(line => line.Start.X).Distinct().ToArray();
            var landingXs = section.Lines.Where(line => line.ComponentId == "PT-1-1"
                    && Math.Abs(line.Start.X - line.End.X) < 0.001)
                .Select(line => line.Start.X).Distinct().ToArray();
            TestAssert.True(axes.Any(axis => landingXs.Any(x => Math.Abs(Math.Abs(x - axis) - 1350.0) < 0.001)),
                "The rendered landing width must equal the edited storey-local value.");
        }

        private static void RebalancesThreeFlightBoundariesAroundEditedLanding()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys.RemoveAt(1);
            project.Floors.RemoveAt(2);
            var storey = project.Storeys[0];
            storey.Flights.Clear();
            storey.Landings.Clear();
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-1", "第一跑", 6, StairFlightDirection.Right, StairSectionRepresentation.Rear));
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-2", "第二跑", 6, StairFlightDirection.Left, StairSectionRepresentation.Cut));
            storey.Flights.Add(StairFlightDefinition.CreateDefault("TD-1-3", "第三跑", 7, StairFlightDirection.Right, StairSectionRepresentation.Cut));
            storey.Landings.Add(StairLandingDefinition.CreateDefault("PT-1-1", "第一平台", "TD-1-1", "TD-1-2"));
            storey.Landings.Add(StairLandingDefinition.CreateDefault("PT-1-2", "第二平台", "TD-1-2", "TD-1-3"));
            foreach (var flight in storey.Flights) flight.TreadDepth = 280.0;

            var constraints = new StairProjectConstraintService();
            constraints.SetPlatformWidth(project, "PT-1-2", 1300.0);
            constraints.Apply(project);

            TestAssert.NearlyEqual(1300.0, storey.Landings[1].PlatformWidth, 0.001,
                "The edited landing must retain the requested width.");
            TestAssert.True(storey.Flights.All(flight => Math.Abs(flight.TreadDepth - 280.0) < 0.001),
                "Rebalancing a three-flight boundary chain must not stretch any tread.");
            var boundaries = new[]
            {
                project.Floors[0].PlatformWidth,
                storey.Landings[0].PlatformWidth,
                storey.Landings[1].PlatformWidth,
                project.Floors[1].PlatformWidth
            };
            for (var index = 0; index < storey.Flights.Count; index++)
            {
                var run = storey.Flights[index].TreadDepth
                    * Math.Max(0, storey.Flights[index].RiserCount - 1);
                TestAssert.NearlyEqual(
                    project.Construction.StairwellDepth,
                    boundaries[index] + run + boundaries[index + 1],
                    0.001,
                    "Every connection before and after the edited landing must remain aligned.");
            }
        }

        private static void RejectsDuplicateComponentIds()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys[1].Flights[0].Id = project.Storeys[0].Flights[0].Id;

            var outcome = new StairProjectCalculator().Calculate(project);

            TestAssert.True(!outcome.IsSuccess, "Duplicate component IDs must fail validation.");
            TestAssert.True(outcome.Issues.Any(issue => issue.Code == "WL-PR-029"), "Duplicate ID error is missing.");
        }

        private static void RejectsBrokenStoreyAndLandingOrder()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Storeys[1].LowerFloorId = project.Floors[0].Id;
            var landing = project.Storeys[0].Landings[0];
            landing.IncomingFlightId = project.Storeys[0].Flights[1].Id;
            landing.OutgoingFlightId = project.Storeys[0].Flights[0].Id;

            var outcome = new StairProjectCalculator().Calculate(project);

            TestAssert.True(!outcome.IsSuccess, "Broken storey and landing order must fail validation.");
            TestAssert.True(outcome.Issues.Any(issue => issue.Code == "WL-PR-033"),
                "The shared-floor continuity error is missing.");
            TestAssert.True(outcome.Issues.Any(issue => issue.Code == "WL-PR-034"),
                "The ordered landing-to-flight relation error is missing.");
        }

        private static void BuildsSharedFloorOnlyOnce()
        {
            var project = StairProjectDefinition.CreateDefault();
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);

            TestAssert.True(section.Lines.Any(line => line.ComponentId == "LB-02"), "The shared floor outline is missing.");
            TestAssert.Equal(0, section.Lines.Count(line => line.ComponentId == "LL-02"), "The beam must be merged into the platform outline.");
            var cutOutline = section.Lines
                .Where(line => !line.IsHidden
                    && (line.Role == StairLineRole.CutBoundary
                        || line.Role == StairLineRole.CutFlightProfile))
                .ToArray();
            TestAssert.True(!HasCollinearOverlap(cutOutline),
                "Connected flights, landings and floors must not retain internal overlapping seams.");
        }

        private static void OmitsFirstFloorSlabAndBeam()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.InsertComponentSchedule = true;
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);

            TestAssert.Equal(0, section.Lines.Count(line => line.ComponentId == "LB-01"),
                "The first level must not generate a floor slab.");
            TestAssert.Equal(0, section.Lines.Count(line => line.ComponentId == "LL-01"),
                "The first level must not generate a floor beam.");
            TestAssert.True(section.Texts.Any(text => text.Content == "±0.000 (1F)"),
                "The first-level elevation label must remain visible at the slab datum.");
        }

        private static void BuildsThreePlatformOutlinesWithoutOverlaps()
        {
            for (var type = PlatformLayoutType.Platform1; type <= PlatformLayoutType.Platform3; type++)
            {
                var project = StairProjectDefinition.CreateDefault();
                project.Construction.OppositeSupportsEnabled = false;
                project.Storeys[0].Landings[0].PlatformType = type;
                var outcome = new StairProjectCalculator().Calculate(project);
                var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
                var outline = section.Lines.Where(line => line.ComponentId == "PT-1-1").ToArray();

                TestAssert.Equal(0, section.Lines.Count(line => line.ComponentId == "PTL-1-1"),
                    "Platform beams must not be emitted as separate overlapping rectangles.");
                TestAssert.Equal(outline.Length, outline.Select(NormalizeLine).Distinct().Count(),
                    "A platform outline must not contain duplicate edges.");
                var expectedEdgeCount = type == PlatformLayoutType.Platform1
                    ? 6
                    : type == PlatformLayoutType.Platform2 ? 9 : 10;
                TestAssert.Equal(expectedEdgeCount, outline.Length,
                    "The selected platform type has an incorrect merged outline.");
            }
        }

        private static string NormalizeLine(DrawingLine line)
        {
            var first = string.Format("{0:0.###},{1:0.###}", line.Start.X, line.Start.Y);
            var second = string.Format("{0:0.###},{1:0.###}", line.End.X, line.End.Y);
            return string.CompareOrdinal(first, second) <= 0
                ? first + "|" + second
                : second + "|" + first;
        }

        private static void AppliesStairwellConstraintsPerLockedStorey()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Construction.StairwellWidth = 2600.0;
            project.Construction.StairwellDepth = 4800.0;
            project.Construction.Wall.Thickness = 200.0;
            project.Floors[0].PlatformWidth = 1200.0;
            project.Storeys[0].Landings[0].PlatformWidth = 1200.0;
            project.Storeys[0].Flights[0].TreadDepth = 300.0;

            new StairProjectConstraintService().Apply(project);

            TestAssert.NearlyEqual(1200.0, project.Storeys[0].Flights[0].Width, 0.001,
                "Locked flight width must be derived from stairwell width and wall thickness.");
            TestAssert.Equal(9, project.Storeys[0].Flights[0].RiserCount,
                "A 2400 mm run at 300 mm must contain eight treads and nine risers.");
            TestAssert.NearlyEqual(300.0, project.Storeys[0].Flights[0].TreadDepth, 0.001,
                "Exact tread depth must be retained when it divides the constrained run.");

            project.Storeys[1].StairwellConstraintLocked = false;
            project.Storeys[1].Flights[0].RiserCount = 7;
            project.Storeys[1].TotalRiserCount = 16;
            new StairProjectConstraintService().Apply(project);
            TestAssert.Equal(7, project.Storeys[1].Flights[0].RiserCount,
                "An unlocked storey must retain its manual riser count.");
        }

        private static void AppliesIndependentStairwellDepthToStoreyConstraints()
        {
            var project = StairProjectDefinition.CreateDefault();
            var storey = project.Storeys[0];
            storey.IndependentStairwellEnabled = true;
            storey.StairwellDepthOverride = 5200.0;
            storey.StairwellAlignment = StairwellAlignment.Center;
            storey.Flights[0].TreadDepth = 300.0;
            storey.Flights[1].TreadDepth = 300.0;
            project.Floors[0].PlatformWidth = 1200.0;
            project.Floors[0].PlatformWidthLocked = true;
            storey.Landings[0].PlatformWidthLocked = false;

            new StairProjectConstraintService().Apply(project);

            TestAssert.NearlyEqual(1600.0, storey.Landings[0].PlatformWidth, 0.001,
                "A constrained storey must solve its platforms from the independent depth.");
            TestAssert.NearlyEqual(300.0, storey.Flights[0].TreadDepth, 0.001,
                "Applying an independent depth must not change the linked tread depth.");
        }

        private static void RejectsInvalidIndependentStairwellParameters()
        {
            var project = StairProjectDefinition.CreateDefault();
            var storey = project.Storeys[0];
            storey.IndependentStairwellEnabled = true;
            storey.StairwellDepthOverride = 0.0;
            storey.StairwellAxisOffset = double.NaN;

            var outcome = new StairProjectCalculator().Calculate(project);

            TestAssert.True(!outcome.IsSuccess,
                "An enabled independent stairwell with invalid parameters must fail validation.");
            TestAssert.True(outcome.Issues.Any(issue => issue.Code == "WL-PR-036"),
                "The independent stairwell depth error is missing.");
            TestAssert.True(outcome.Issues.Any(issue => issue.Code == "WL-PR-038"),
                "The independent stairwell offset error is missing.");
        }

        private static void DrawsWallsFromBeamCenterAxes()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Construction.Wall.Enabled = true;
            project.Construction.Wall.Thickness = 200.0;
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var walls = section.Lines.Where(line => line.Role == StairLineRole.WallBoundary).ToArray();

            TestAssert.True(walls.Length > 4,
                "Walls must be split into independent segments above and below each beam.");
            TestAssert.True(walls.All(line => Math.Abs(line.Start.X - line.End.X) < 0.001),
                "Section walls must be vertical.");
            var wallXs = walls.Select(line => line.Start.X).Distinct().OrderBy(value => value).ToArray();
            TestAssert.True(wallXs.Any(first => wallXs.Any(second => Math.Abs(second - first - 200.0) < 0.001)),
                "Wall faces must offset half the wall thickness from their axis.");
            foreach (var floor in project.Floors.Skip(1))
            {
                var beamDepth = floor.BeamDepthOverride ?? project.Construction.FloorBeam.Depth;
                var elevation = outcome.Result.FloorElevations[floor.Id];
                TestAssert.True(walls.Any(line =>
                    Math.Abs(line.Start.Y - elevation) < 0.001
                    || Math.Abs(line.End.Y - elevation) < 0.001),
                    "An upper wall segment must start at the floor-beam top.");
                TestAssert.True(walls.Any(line =>
                    Math.Abs(line.Start.Y - (elevation - beamDepth)) < 0.001
                    || Math.Abs(line.End.Y - (elevation - beamDepth)) < 0.001),
                    "A lower wall segment must stop at the floor-beam bottom.");
            }
        }

        private static void BuildsShiftedStoreyWallsAxesBeamsAndTransitionSlabs()
        {
            var project = StairProjectDefinition.CreateDefault();
            var shifted = project.Storeys[0];
            shifted.IndependentStairwellEnabled = true;
            shifted.StairwellDepthOverride = 4000.0;
            shifted.StairwellAlignment = StairwellAlignment.Left;
            shifted.StairwellAxisOffset = -300.0;
            new StairProjectConstraintService().Apply(project);
            var outcome = new StairProjectCalculator().Calculate(project);

            TestAssert.True(outcome.IsSuccess,
                "A valid shifted stairwell storey must calculate successfully.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var wallFaceXs = section.Lines
                .Where(line => line.Role == StairLineRole.WallBoundary)
                .Select(line => line.Start.X)
                .Distinct()
                .ToArray();
            // The final section is rebased so the overall left envelope is X=0.
            foreach (var axis in new[] { 0.0, 4000.0, 300.0, 4940.0 })
            {
                TestAssert.True(wallFaceXs.Any(x => Math.Abs(Math.Abs(x - axis) - 100.0) < 0.001),
                    "Each old and new wall axis must produce its own vertical wall faces: "
                    + axis.ToString(CultureInfo.InvariantCulture) + "; actual="
                    + string.Join(",", wallFaceXs.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))));
            }
            TestAssert.True(section.Lines.Where(line => line.Role == StairLineRole.WallBoundary)
                    .All(line => Math.Abs(line.Start.X - line.End.X) < 0.001),
                "A stairwell depth transition must never create sloping walls.");
            var axisXs = section.Lines.Where(line => line.Role == StairLineRole.AxisLine)
                .Select(line => line.Start.X).Distinct().ToArray();
            foreach (var axis in new[] { 0.0, 4000.0, 300.0, 4940.0 })
                TestAssert.True(axisXs.Any(x => Math.Abs(x - axis) < 0.001),
                    "Every storey-local wall axis must be visible in the section.");
            var transitionLines = section.Lines.Where(line =>
                string.Equals(line.ComponentId, "LB-02-SHIFT-L", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line.ComponentId, "LB-02-SHIFT-R", StringComparison.OrdinalIgnoreCase)).ToArray();
            TestAssert.True(!transitionLines.Any(line =>
                    string.Equals(line.ComponentId, "LB-02-SHIFT-L", StringComparison.OrdinalIgnoreCase)),
                "The old floor platform already spanning the left wall offset must be reused without duplicate slab lines.");
            TestAssert.True(transitionLines.Any(line =>
                    string.Equals(line.ComponentId, "LB-02-SHIFT-R", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(line.Start.Y - line.End.Y) < 0.001
                    && SameUndirectedInterval(line.Start.X, line.End.X, 3900.0, 4840.0)),
                "The uncovered side slab must pass across the secondary beam and stop at the primary beam face.");
            TestAssert.True(transitionLines.Any(line =>
                    Math.Abs(line.Start.X - line.End.X) < 0.001
                    && Math.Abs(line.Start.X - 3900.0) < 0.001),
                "The transition slab must retain its external end so the combined beam/slab outline is closed.");
            TestAssert.True(!transitionLines.Any(line =>
                    Math.Abs(line.Start.X - line.End.X) < 0.001
                    && Math.Abs(line.Start.X - 4840.0) < 0.001),
                "The transition slab seam against the primary support must cancel from the visible outline.");
            var transitionTop = outcome.Result.Storeys[0].UpperElevation;
            var transitionBottom = transitionTop - project.Construction.FloorSlabThickness;
            foreach (var gap in new[] { new[] { 0.0, 300.0 }, new[] { 4000.0, 4940.0 } })
            {
                TestAssert.True(!section.Lines.Any(line =>
                        Math.Abs(line.Start.X - line.End.X) < 0.001
                        && line.Start.X > gap[0] + 0.001
                        && line.Start.X < gap[1] - 0.001
                        && Math.Max(line.Start.Y, line.End.Y) > transitionBottom + 0.001
                        && Math.Min(line.Start.Y, line.End.Y) < transitionTop - 0.001),
                    "No beam, floor or infill seam may remain inside the connected slab thickness.");
            }
            foreach (var secondaryBeamAxis in new[] { 0.0, 4000.0 })
            {
                TestAssert.True(!section.Lines.Any(line =>
                        Math.Abs(line.Start.Y - transitionBottom) < 0.001
                        && Math.Abs(line.End.Y - transitionBottom) < 0.001
                        && Math.Min(line.Start.X, line.End.X) < secondaryBeamAxis - 0.001
                        && Math.Max(line.Start.X, line.End.X) > secondaryBeamAxis + 0.001),
                    "The slab underside must disappear through an attached secondary beam width.");
            }
            TestAssert.True(section.Lines.Any(line =>
                    line.ComponentId == "LB-02-RAILING-CONNECTION"
                    && line.Role == StairLineRole.Handrail
                    && Math.Abs(line.Start.Y - line.End.Y) < 0.001),
                "Flights meeting at a shifted shared floor must receive a continuous platform handrail.");
        }

        private static void ConnectsIndependentStairwellTransitionAsOneOutline()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Construction.StairwellDepth = 8900.0;
            project.Construction.OppositeSupportsEnabled = true;
            project.Construction.SlabOverhang = 300.0;
            project.Construction.CloseSlabOverhangEdge = false;

            var lower = project.Storeys[0];
            lower.Height = 4500.0;
            lower.TotalRiserCount = 30;
            lower.IndependentStairwellEnabled = true;
            lower.StairwellDepthOverride = 6000.0;
            lower.StairwellAlignment = StairwellAlignment.Center;
            lower.StairwellAxisOffset = 0.0;
            lower.StairwellConstraintLocked = false;
            lower.TreadDepthLinked = true;
            lower.Flights.Clear();
            lower.Landings.Clear();
            lower.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-B1-1", "第1跑", 10, StairFlightDirection.Left, StairSectionRepresentation.Cut));
            lower.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-B1-2", "第2跑", 10, StairFlightDirection.Right, StairSectionRepresentation.Rear));
            lower.Flights.Add(StairFlightDefinition.CreateDefault(
                "TD-B1-3", "第3跑", 10, StairFlightDirection.Left, StairSectionRepresentation.Cut));
            lower.Landings.Add(StairLandingDefinition.CreateDefault(
                "PT-B1-1", "休息平台1", "TD-B1-1", "TD-B1-2"));
            lower.Landings.Add(StairLandingDefinition.CreateDefault(
                "PT-B1-2", "休息平台2", "TD-B1-2", "TD-B1-3"));
            lower.Landings[0].PlatformWidth = 2140.0;
            lower.Landings[1].PlatformWidth = 1340.0;
            project.Floors[0].PlatformWidth = 1340.0;
            project.Floors[1].PlatformWidth = 2140.0;
            project.Floors[1].PlatformWidthLocked = true;
            project.Floors[1].PlatformType = PlatformLayoutType.Platform3;
            project.Floors[1].OppositeSupportType = OppositeSupportType.BeamWithSlab;

            new StairProjectConstraintService().Apply(project);
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess,
                "The real three-flight independent-depth transition must calculate.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var resolver = new StairwellAxisResolver();
            var lowerRange = resolver.Resolve(project, lower);
            var nextRange = resolver.Resolve(project, project.Storeys[1]);
            var sharedFloor = project.Floors[1];
            var elevation = outcome.Result.Storeys[0].UpperElevation;
            var slabBottom = elevation - project.Construction.FloorSlabThickness;
            var primaryConnectionX = nextRange.LeftAxisX + sharedFloor.PlatformWidth;
            var finalFlightId = lower.Flights.Last().Id;

            TestAssert.True(section.Lines.Any(line =>
                    string.Equals(line.ComponentId, finalFlightId, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(line.Start.Y - elevation) < 0.001
                    && Math.Abs(line.End.Y - elevation) < 0.001
                    && (Math.Abs(line.Start.X - primaryConnectionX) < 0.001
                        || Math.Abs(line.End.X - primaryConnectionX) < 0.001)
                    && Math.Abs(line.End.X - line.Start.X) > 0.001),
                "The lower storey's final flight must automatically bridge to the upper storey's shared floor.");

            var halfBeam = project.Construction.FloorBeam.Width / 2.0;
            var secondaryRightOuter = lowerRange.RightAxisX - halfBeam;
            var primaryRightFace = nextRange.RightAxisX - halfBeam;
            var rightTransition = section.Lines.Where(line =>
                string.Equals(line.ComponentId, sharedFloor.Id + "-SHIFT-R",
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            TestAssert.True(rightTransition.Any(line =>
                    Math.Abs(line.Start.Y - elevation) < 0.001
                    && Math.Abs(line.End.Y - elevation) < 0.001
                    && SameUndirectedInterval(line.Start.X, line.End.X,
                        secondaryRightOuter, primaryRightFace)),
                "The right transition slab must span from the secondary beam outer edge to the primary beam face.");
            TestAssert.True(rightTransition.Any(line =>
                    Math.Abs(line.Start.X - secondaryRightOuter) < 0.001
                    && Math.Abs(line.End.X - secondaryRightOuter) < 0.001
                    && Math.Abs(Math.Max(line.Start.Y, line.End.Y) - elevation) < 0.001
                    && Math.Abs(Math.Min(line.Start.Y, line.End.Y) - slabBottom) < 0.001),
                "The right transition slab must retain its external vertical edge and close the outline.");
            TestAssert.True(!rightTransition.Any(line =>
                    Math.Abs(line.Start.X - primaryRightFace) < 0.001
                    && Math.Abs(line.End.X - primaryRightFace) < 0.001),
                "The internal seam against the primary right support must not remain visible.");

            foreach (var secondaryBeamAxis in new[]
            {
                lowerRange.LeftAxisX,
                lowerRange.RightAxisX
            })
            {
                TestAssert.True(!section.Lines.Any(line =>
                        Math.Abs(line.Start.Y - slabBottom) < 0.001
                        && Math.Abs(line.End.Y - slabBottom) < 0.001
                        && Math.Min(line.Start.X, line.End.X) < secondaryBeamAxis - 0.001
                        && Math.Max(line.Start.X, line.End.X) > secondaryBeamAxis + 0.001),
                    "A connected floor slab underside must not pass visibly through its attached beam.");
            }
        }

        private static void KeepsSectionGeometryWhenIndependentAxesMatchUnifiedAxes()
        {
            var legacy = StairProjectDefinition.CreateDefault();
            var independent = StairProjectDefinition.CreateDefault();
            foreach (var storey in independent.Storeys)
            {
                storey.IndependentStairwellEnabled = true;
                storey.StairwellDepthOverride = independent.Construction.StairwellDepth;
                storey.StairwellAlignment = StairwellAlignment.Center;
                storey.StairwellAxisOffset = 0.0;
            }
            var constraints = new StairProjectConstraintService();
            constraints.Apply(legacy);
            constraints.Apply(independent);
            var calculator = new StairProjectCalculator();
            var legacyOutcome = calculator.Calculate(legacy);
            var independentOutcome = calculator.Calculate(independent);
            var builder = new StairProjectGeometryBuilder();
            var legacyView = builder.BuildSection(legacy, legacyOutcome.Result);
            var independentView = builder.BuildSection(independent, independentOutcome.Result);
            Func<DrawingLine, string> signature = line => string.Join("|", new[]
            {
                NormalizeLine(line),
                line.Role.ToString(),
                line.IsHidden.ToString(),
                line.ComponentId ?? string.Empty
            });
            var legacyLines = legacyView.Lines.Select(signature).OrderBy(item => item).ToArray();
            var independentLines = independentView.Lines.Select(signature).OrderBy(item => item).ToArray();

            TestAssert.Equal(string.Join("\n", legacyLines), string.Join("\n", independentLines),
                "Enabling an equal, centered range must not change any existing section linework.");
        }

        private static void PreservesRiserCountsAndRecalculatesTreadDepth()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Construction.StairwellDepth = 4800.0;
            project.Floors[0].PlatformWidth = 1200.0;
            project.Storeys[0].Landings[0].PlatformWidth = 1200.0;
            project.Storeys[0].Flights[0].RiserCount = 11;
            project.Storeys[0].TotalRiserCount = 20;
            project.Storeys[0].TreadDepthLinked = false;

            new StairProjectConstraintService().Apply(project);

            TestAssert.Equal(11, project.Storeys[0].Flights[0].RiserCount,
                "Changing a locked flight riser count must not be overwritten.");
            TestAssert.NearlyEqual(240.0, project.Storeys[0].Flights[0].TreadDepth, 0.001,
                "Tread depth must be derived from the constrained run and selected riser count.");
        }

        private static void LocksStoreyTreadDepthAndBalancesPlatforms()
        {
            var project = StairProjectDefinition.CreateDefault();
            var storey = project.Storeys[0];
            foreach (var other in project.Storeys.Skip(1))
            {
                other.TreadDepthLinked = false;
                other.StairwellConstraintLocked = false;
            }
            storey.TreadDepthLinked = true;
            storey.Flights[0].TreadDepth = 300.0;
            storey.Flights[1].TreadDepth = 260.0;
            project.Floors[0].PlatformWidth = 900.0;
            storey.Landings[0].PlatformWidth = 1500.0;
            project.Floors[1].PlatformWidth = 900.0;

            new StairProjectConstraintService().Apply(project);

            TestAssert.NearlyEqual(300.0, storey.Flights[0].TreadDepth, 0.001,
                "The locked storey tread depth must retain the selected value.");
            TestAssert.NearlyEqual(300.0, storey.Flights[1].TreadDepth, 0.001,
                "Every flight in a locked storey must use the same tread depth.");
            for (var index = 0; index < storey.Flights.Count; index++)
            {
                var start = index == 0 ? project.Floors[0].PlatformWidth : storey.Landings[index - 1].PlatformWidth;
                var end = index == storey.Flights.Count - 1 ? project.Floors[1].PlatformWidth : storey.Landings[index].PlatformWidth;
                var run = storey.Flights[index].TreadDepth * (storey.Flights[index].RiserCount - 1);
                TestAssert.NearlyEqual(project.Construction.StairwellDepth, start + run + end, 0.001,
                    "Locked tread depth must be absorbed by the adjacent floor and landing widths.");
            }
        }

        private static void PreservesLockedPlatformWidth()
        {
            var project = StairProjectDefinition.CreateDefault();
            foreach (var other in project.Storeys.Skip(1))
            {
                other.TreadDepthLinked = false;
                other.StairwellConstraintLocked = false;
            }
            var storey = project.Storeys[0];
            storey.TreadDepthLinked = true;
            var constraints = new StairProjectConstraintService();
            constraints.SetPlatformWidth(project, storey.Landings[0].Id, 1350.0);
            constraints.Apply(project);

            TestAssert.NearlyEqual(1350.0, storey.Landings[0].PlatformWidth, 0.001,
                "A manually locked platform width must remain unchanged.");
            TestAssert.True(storey.Flights.All(flight => Math.Abs(flight.TreadDepth - 280.0) < 0.001),
                "Editing a platform width must retain every existing tread depth in the storey.");
            for (var index = 0; index < storey.Flights.Count; index++)
            {
                var start = index == 0
                    ? project.Floors[0].PlatformWidth
                    : storey.Landings[index - 1].PlatformWidth;
                var end = index == storey.Flights.Count - 1
                    ? project.Floors[1].PlatformWidth
                    : storey.Landings[index].PlatformWidth;
                var run = storey.Flights[index].TreadDepth
                    * Math.Max(0, storey.Flights[index].RiserCount - 1);
                TestAssert.NearlyEqual(project.Construction.StairwellDepth, start + run + end, 0.001,
                    "Adjacent boundaries must move around the edited platform without crossing a flight.");
            }
        }

        private static bool HasCollinearOverlap(IReadOnlyList<DrawingLine> lines)
        {
            for (var firstIndex = 0; firstIndex < lines.Count; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < lines.Count; secondIndex++)
                {
                    var first = lines[firstIndex];
                    var second = lines[secondIndex];
                    var firstVertical = Math.Abs(first.Start.X - first.End.X) < 0.001;
                    var secondVertical = Math.Abs(second.Start.X - second.End.X) < 0.001;
                    var firstHorizontal = Math.Abs(first.Start.Y - first.End.Y) < 0.001;
                    var secondHorizontal = Math.Abs(second.Start.Y - second.End.Y) < 0.001;

                    if (firstVertical && secondVertical
                        && Math.Abs(first.Start.X - second.Start.X) < 0.001
                        && IntervalsOverlap(first.Start.Y, first.End.Y, second.Start.Y, second.End.Y))
                    {
                        return true;
                    }
                    if (firstHorizontal && secondHorizontal
                        && Math.Abs(first.Start.Y - second.Start.Y) < 0.001
                        && IntervalsOverlap(first.Start.X, first.End.X, second.Start.X, second.End.X))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IntervalsOverlap(double firstStart, double firstEnd, double secondStart, double secondEnd)
        {
            var overlapStart = Math.Max(Math.Min(firstStart, firstEnd), Math.Min(secondStart, secondEnd));
            var overlapEnd = Math.Min(Math.Max(firstStart, firstEnd), Math.Max(secondStart, secondEnd));
            return overlapEnd - overlapStart > 0.001;
        }

        private static void KeepsSectionAxesFixedDuringEditsAndMirroring()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.BaseElevation = -3000.0;
            var constraints = new StairProjectConstraintService();
            constraints.Apply(project);

            Func<DrawingView, double[]> axisCoordinates = view => view.Lines
                .Where(line => line.Role == StairLineRole.AxisLine)
                .OrderBy(line => line.Start.X)
                .SelectMany(line => new[]
                {
                    line.Start.X,
                    Math.Min(line.Start.Y, line.End.Y),
                    Math.Max(line.Start.Y, line.End.Y)
                })
                .ToArray();
            Func<DrawingView> build = () =>
            {
                var outcome = new StairProjectCalculator().Calculate(project);
                TestAssert.True(outcome.IsSuccess, "The fixed-axis project must calculate.");
                return new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            };

            var originalView = build();
            var originalAxes = axisCoordinates(originalView);
            TestAssert.Equal(6, originalAxes.Length, "The section must contain exactly two fixed axes.");
            TestAssert.NearlyEqual(0.0, originalAxes[0], 0.001,
                "The left axis must define insertion X=0.");
            TestAssert.NearlyEqual(-200.0, originalAxes[1], 0.001,
                "The left axis must extend 200 mm below the fixed insertion level.");
            TestAssert.NearlyEqual(project.Construction.StairwellDepth, originalAxes[3], 0.001,
                "The right axis must stay at the fixed stairwell depth.");
            var wallBottom = originalView.Lines
                .Where(line => line.Role == StairLineRole.WallBoundary)
                .Min(line => Math.Min(line.Start.Y, line.End.Y));
            var wallTop = originalView.Lines
                .Where(line => line.Role == StairLineRole.WallBoundary)
                .Max(line => Math.Max(line.Start.Y, line.End.Y));
            TestAssert.NearlyEqual(wallBottom - 200.0, originalAxes[1], 0.001,
                "The axis must extend 200 mm below the wall rather than below a floor or landing.");
            TestAssert.NearlyEqual(wallTop + 200.0, originalAxes[2], 0.001,
                "The axis must extend 200 mm above the wall rather than above the highest floor.");

            constraints.SetPlatformWidth(project, project.Storeys[0].Landings[0].Id, 1350.0);
            constraints.Apply(project);
            var editedAxes = axisCoordinates(build());
            TestAssert.True(originalAxes.SequenceEqual(editedAxes),
                "Editing floor or platform widths must not move either section axis.");

            foreach (var storey in project.Storeys)
                foreach (var flight in storey.Flights)
                    flight.Direction = flight.Direction == StairFlightDirection.Right
                        ? StairFlightDirection.Left
                        : StairFlightDirection.Right;
            constraints.Apply(project);
            var mirroredAxes = axisCoordinates(build());
            TestAssert.True(originalAxes.SequenceEqual(mirroredAxes),
                "Left-right mirroring must not move or exchange the fixed section axes.");
        }

        private static void BuildsHandrailsDimensionsAndOptionalComponentSchedule()
        {
            var projectWithoutSchedule = StairProjectDefinition.CreateDefault();
            projectWithoutSchedule.Construction.OppositeSupportsEnabled = false;
            new StairProjectConstraintService().Apply(projectWithoutSchedule);
            var outcomeWithoutSchedule = new StairProjectCalculator().Calculate(projectWithoutSchedule);
            TestAssert.True(outcomeWithoutSchedule.IsSuccess,
                "The stair project without a component schedule must calculate.");
            var sectionWithoutSchedule = new StairProjectGeometryBuilder().BuildSection(
                projectWithoutSchedule, outcomeWithoutSchedule.Result);
            TestAssert.Equal(0, sectionWithoutSchedule.Tables.Count,
                "Disabling the component schedule must omit the table.");
            TestAssert.True(!sectionWithoutSchedule.Texts.Any(text =>
                    text.Content.StartsWith("TD-", StringComparison.OrdinalIgnoreCase)
                    || text.Content.StartsWith("PT-", StringComparison.OrdinalIgnoreCase)
                    || text.Content.StartsWith("LB-", StringComparison.OrdinalIgnoreCase)),
                "Disabling the component schedule must also omit flight, platform and floor ID labels.");

            var project = StairProjectDefinition.CreateDefault();
            project.Construction.OppositeSupportsEnabled = false;
            project.DrawingScale = 30;
            project.InsertComponentSchedule = true;
            new StairProjectConstraintService().Apply(project);
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The annotated stair project must calculate.");

            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var firstStorey = project.Storeys[0];
            var firstFlight = firstStorey.Flights[0];
            var firstFlightResult = outcome.Result.Storeys[0].Flights[0];
            var handrails = section.Lines
                .Where(line => line.Role == StairLineRole.Handrail)
                .ToArray();
            TestAssert.True(handrails.Length >= firstStorey.Flights.Count * 3,
                "Every enabled stair flight must receive handrail geometry.");
            TestAssert.True(handrails.Any(line => Math.Abs(line.Start.X - line.End.X) < 0.001
                    && Math.Abs(Math.Abs(line.End.Y - line.Start.Y) - 900.0) < 0.001),
                "The default handrail posts must use the 900 mm height.");
            var firstFlightPostBases = handrails
                .Where(line => Math.Abs(line.Start.X - line.End.X) < 0.001
                    && Math.Abs(line.End.Y - line.Start.Y - 900.0) < 0.001
                    && (Math.Abs(line.Start.Y - firstFlightResult.RiserHeight) < 0.001
                        || Math.Abs(line.Start.Y - firstFlightResult.VerticalRise) < 0.001))
                .Select(line => line.Start)
                .GroupBy(point => point.X.ToString("0.000") + ":" + point.Y.ToString("0.000"))
                .Select(group => group.First())
                .ToArray();
            TestAssert.Equal(2, firstFlightPostBases.Length,
                "The first flight handrail must start and finish on its two outer upper vertices.");
            TestAssert.True(firstFlightPostBases.All(post => section.Lines.Any(line =>
                    line.Role != StairLineRole.Handrail
                    && (AreSamePointForTest(line.Start, post)
                        || AreSamePointForTest(line.End, post)))),
                "Each handrail post base must coincide with a flight boundary vertex.");
            var railStart = firstFlightPostBases.OrderBy(point => point.Y).First();
            var railEnd = firstFlightPostBases.OrderBy(point => point.Y).Last();
            var railStartTop = new Point2D(railStart.X, railStart.Y + 900.0);
            var railEndTop = new Point2D(railEnd.X, railEnd.Y + 900.0);
            var slopingRail = handrails.FirstOrDefault(line =>
                (AreSamePointForTest(line.Start, railStartTop)
                    && AreSamePointForTest(line.End, railEndTop))
                || (AreSamePointForTest(line.End, railStartTop)
                    && AreSamePointForTest(line.Start, railEndTop)));
            TestAssert.True(slopingRail != null,
                "The sloping handrail must connect the 900 mm points above the first and arrival vertices.");
            var flightTreadPoints = section.Lines
                .Where(line => line.ComponentId == firstFlight.Id
                    && Math.Abs(line.Start.Y - line.End.Y) < 0.001)
                .SelectMany(line => new[] { line.Start, line.End })
                .Where(point => point.X >= Math.Min(railStart.X, railEnd.X) - 0.001
                    && point.X <= Math.Max(railStart.X, railEnd.X) + 0.001)
                .ToArray();
            TestAssert.True(flightTreadPoints.All(point =>
                    VerticalClearance(slopingRail, point) >= 900.0 - 0.001),
                "Every tread under the sloping handrail must retain at least 900 mm vertical clearance.");

            var flightDimension = section.Dimensions.FirstOrDefault(
                dimension => dimension.ComponentId == firstFlight.Id);
            TestAssert.True(flightDimension != null,
                "Each flight must have an inner vertical-rise dimension.");
            var outlineLines = section.Lines.Where(line =>
                    line.Role != StairLineRole.AxisLine
                    && line.Role != StairLineRole.Handrail
                    && line.Role != StairLineRole.BreakLine
                    && line.Role != StairLineRole.HatchBoundary)
                .ToArray();
            var leftOutline = outlineLines.SelectMany(line => new[] { line.Start.X, line.End.X }).Min();
            var rightOutline = outlineLines.SelectMany(line => new[] { line.Start.X, line.End.X }).Max();
            TestAssert.NearlyEqual(leftOutline - (6.0 * project.DrawingScale),
                flightDimension.DimensionLinePoint.X,
                0.001,
                "The inner dimension line must be 6 drawing millimetres beyond the outer profile.");
            TestAssert.True(Math.Abs(flightDimension.FirstExtensionOrigin.X) > 0.001
                    && Math.Abs(flightDimension.SecondExtensionOrigin.X) > 0.001,
                "Flight dimensions must originate at the outer profile instead of the left axis.");
            TestAssert.Equal(
                FormatExpected(firstFlightResult.RiserHeight) + "×" + firstFlight.RiserCount
                    + "=" + FormatExpected(firstFlightResult.VerticalRise),
                flightDimension.TextOverride,
                "The flight-rise dimension must show riser height multiplied by riser count.");

            var storeyDimensions = section.Dimensions
                .Where(dimension => dimension.ComponentId == outcome.Result.Storeys[0].Id)
                .ToArray();
            TestAssert.Equal(2, storeyDimensions.Length,
                "Each storey must have one outer height dimension on each axis side.");
            var leftStoreyDimension = storeyDimensions.First(dimension => dimension.DimensionLinePoint.X < 0.0);
            var rightStoreyDimension = storeyDimensions.First(dimension => dimension.DimensionLinePoint.X > 0.0);
            TestAssert.NearlyEqual(leftOutline - (11.0 * project.DrawingScale),
                leftStoreyDimension.DimensionLinePoint.X, 0.001,
                "The left storey dimension must be 11 drawing millimetres beyond the outer profile.");
            TestAssert.NearlyEqual(rightOutline + (11.0 * project.DrawingScale),
                rightStoreyDimension.DimensionLinePoint.X, 0.001,
                "The right storey dimension must be 11 drawing millimetres beyond the outer profile.");
            TestAssert.True(Math.Abs(leftStoreyDimension.FirstExtensionOrigin.X) > 0.001
                    && Math.Abs(rightStoreyDimension.FirstExtensionOrigin.X
                        - project.Construction.StairwellDepth) > 0.001,
                "Storey dimensions must originate at the outer profile instead of either axis.");
            var leftVerticalDimensions = section.Dimensions
                .Where(dimension => dimension.Orientation == DrawingDimensionOrientation.Vertical
                    && dimension.ComponentId != "RAILING"
                    && dimension.DimensionLinePoint.X < 0.0)
                .ToArray();
            TestAssert.True(leftVerticalDimensions.All(dimension =>
                    Math.Abs(dimension.FirstExtensionOrigin.X - leftOutline) < 0.001
                    && Math.Abs(dimension.SecondExtensionOrigin.X - leftOutline) < 0.001),
                "All left-side vertical dimensions must share one outer-profile extension baseline.");
            TestAssert.NearlyEqual(5.0 * project.DrawingScale,
                Math.Abs(leftStoreyDimension.DimensionLinePoint.X - flightDimension.DimensionLinePoint.X),
                0.001,
                "The two vertical dimension rows must retain the 5 drawing millimetre spacing.");

            var horizontalDimensions = section.Dimensions
                .Where(dimension => dimension.Orientation == DrawingDimensionOrientation.Horizontal)
                .ToArray();
            TestAssert.True(horizontalDimensions.Length >= 3,
                "At least one chain of left platform, flight run and right platform dimensions is required.");
            TestAssert.True(horizontalDimensions.Any(dimension =>
                    dimension.TextOverride.Contains("×")
                    && dimension.TextOverride.Contains("=")),
                "A horizontal flight dimension must show tread depth multiplied by riser count minus one.");
            TestAssert.True(horizontalDimensions.All(dimension =>
                    Math.Abs(Math.Abs(dimension.FirstExtensionOrigin.Y - dimension.DimensionLinePoint.Y)
                        - (6.0 * project.DrawingScale)) < 0.001),
                "All horizontal dimensions must use the 6 drawing millimetre inner extension length.");
            TestAssert.Equal(1, section.Dimensions.Count(dimension =>
                    dimension.ComponentId == "RAILING"
                    && dimension.Orientation == DrawingDimensionOrientation.Vertical),
                "Only one non-overlapping handrail height dimension may be inserted.");
            var railingDimension = section.Dimensions.Single(dimension =>
                dimension.ComponentId == "RAILING"
                && dimension.Orientation == DrawingDimensionOrientation.Vertical);
            TestAssert.NearlyEqual(6.0 * project.DrawingScale,
                Math.Abs(railingDimension.DimensionLinePoint.X - railingDimension.FirstExtensionOrigin.X),
                0.001,
                "The handrail height dimension must use the 6 drawing millimetre inner extension length.");
            TestAssert.Equal(1, section.Tables.Count,
                "The optional component schedule must create one table.");
            TestAssert.True(section.HatchRegions.Any(region => !region.IsWall
                    && region.Boundary.Count >= 3
                    && string.Equals(region.PatternName, "WL_RC_CONCRETE_V2", StringComparison.OrdinalIgnoreCase)),
                "Visible cut flights and platforms must produce closed concrete hatch regions.");
            var visibleCutComponents = section.Lines
                .Where(line => !line.IsHidden
                    && (line.Role == StairLineRole.CutBoundary
                        || line.Role == StairLineRole.CutFlightProfile))
                .Select(line => line.ComponentId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var structuralRegions = section.HatchRegions.Count(region => !region.IsWall);
            TestAssert.True(structuralRegions < visibleCutComponents,
                "Touching cut flights, platforms and floors must be unioned before hatching and inward bolding.");
            TestAssert.True(section.HatchRegions.Any(region => region.IsWall
                    && region.Boundary.Count == 4
                    && string.Equals(region.PatternName, "ANSI311", StringComparison.OrdinalIgnoreCase)),
                "Wall faces must use the plugin-provided 45-degree single-line hatch.");
            var baseWallLines = section.Lines.Where(line => line.ComponentId == "BASE-WALL").ToArray();
            TestAssert.Equal(4, baseWallLines.Length,
                "The section bottom must retain one closed hatch-only boundary.");
            TestAssert.True(baseWallLines.All(line => line.Role == StairLineRole.HatchBoundary),
                "The base-wall end caps must be hatch boundaries rather than visible geometry.");
            TestAssert.NearlyEqual(100.0,
                baseWallLines.SelectMany(line => new[] { line.Start.Y, line.End.Y }).Max()
                    - baseWallLines.SelectMany(line => new[] { line.Start.Y, line.End.Y }).Min(),
                0.001,
                "The base wall must be 100 millimetres thick.");
            var baseOutsideAxis = project.Construction.FloorBeam.Width / 2.0
                + project.Construction.SlabOverhang;
            var expectedLeftExtension = -baseOutsideAxis;
            var expectedRightExtension = project.Construction.StairwellDepth
                + baseOutsideAxis;
            var visibleBaseWallLines = section.Lines
                .Where(line => line.ComponentId == "BASE-WALL-VISIBLE")
                .ToArray();
            TestAssert.Equal(2, visibleBaseWallLines.Length,
                "The visible base wall must contain only its two open horizontal edges.");
            TestAssert.True(visibleBaseWallLines.All(line =>
                    Math.Abs(line.Start.Y - line.End.Y) < 0.001),
                "The visible base wall must not contain closed vertical end caps.");
            TestAssert.True(visibleBaseWallLines.All(line =>
                    Math.Abs(Math.Min(line.Start.X, line.End.X) - expectedLeftExtension) < 0.001
                    && Math.Abs(Math.Max(line.Start.X, line.End.X) - expectedRightExtension) < 0.001),
                "Both base-wall edges must align with the unified upper-floor overhang.");
            var breakLines = section.Lines.Where(line => line.Role == StairLineRole.BreakLine).ToArray();
            TestAssert.Equal(5, breakLines.Length,
                "The wall top must include the five segments of the QZ-style six-vertex break line.");
            TestAssert.True(breakLines.Any(line => Math.Abs(line.Start.Y - line.End.Y) > 0.001),
                "The top break line must include visible folded segments.");
            var expectedBreakLeft = -(project.Construction.Wall.Thickness / 2.0)
                - (4.0 * project.DrawingScale);
            var expectedBreakRight = project.Construction.StairwellDepth
                + (project.Construction.Wall.Thickness / 2.0)
                + (4.0 * project.DrawingScale);
            TestAssert.NearlyEqual(expectedBreakLeft,
                breakLines.SelectMany(line => new[] { line.Start.X, line.End.X }).Min(), 0.001,
                "The break line must extend four drawing millimetres beyond the left wall face.");
            TestAssert.NearlyEqual(expectedBreakRight,
                breakLines.SelectMany(line => new[] { line.Start.X, line.End.X }).Max(), 0.001,
                "The break line must extend four drawing millimetres beyond the right wall face.");
            TestAssert.True(section.Title != null
                    && section.Title.Text.Contains(project.StairNumber)
                    && section.Title.Text.Contains("楼梯大样")
                    && section.Title.Scale == project.DrawingScale,
                "The section must provide a Tianzheng-compatible stair title and drawing scale.");
            var topGuardrailLines = section.Lines
                .Where(line => line.ComponentId == "TOP-GUARDRAIL")
                .ToArray();
            TestAssert.Equal(3, topGuardrailLines.Length,
                "The uppermost floor must receive one two-post guardrail with a top rail.");
            TestAssert.Equal(2, topGuardrailLines.Count(line =>
                    Math.Abs(line.Start.X - line.End.X) < 0.001
                    && Math.Abs(Math.Abs(line.Start.Y - line.End.Y) - 1100.0) < 0.001),
                "Both top guardrail posts must be 1100 millimetres high.");
            TestAssert.True(section.Leaders.Any(leader => leader.Text.Contains("栏杆")
                    && leader.Text.Contains("1.1m")),
                "The 1.1 metre top guardrail must receive a leader note.");
            var topGuardrailLeader = section.Leaders.Single(leader =>
                leader.Text.Contains("栏杆") && leader.Text.Contains("1.1m"));
            var guardrailCenterX = topGuardrailLines.Average(line =>
                (line.Start.X + line.End.X) / 2.0);
            TestAssert.NearlyEqual(guardrailCenterX, topGuardrailLeader.Vertices[0].X, 0.001,
                "Moving the top guardrail 50 mm toward the floor must move its leader target together.");
            var highestFlightHandrailX = section.Lines
                .Where(line => line.Role == StairLineRole.Handrail
                    && line.ComponentId != "TOP-GUARDRAIL")
                .SelectMany(line => new[] { line.Start, line.End })
                .OrderByDescending(point => point.Y)
                .First().X;
            TestAssert.NearlyEqual(50.0, Math.Abs(guardrailCenterX - highestFlightHandrailX), 0.001,
                "The top guardrail and its annotation must move exactly 50 mm toward the floor.");
            TestAssert.True(section.Tables[0].Rows.Any(row => row.Contains(firstFlight.Id)),
                "The component schedule must list detailed flight parameters.");
        }

        private static string FormatExpected(double value)
        {
            return Math.Abs(value - Math.Round(value)) < 0.05
                ? Math.Round(value).ToString("0")
                : value.ToString("0.0");
        }

        private static bool SameUndirectedInterval(
            double firstStart,
            double firstEnd,
            double secondStart,
            double secondEnd)
        {
            return Math.Abs(Math.Min(firstStart, firstEnd) - Math.Min(secondStart, secondEnd)) < 0.001
                && Math.Abs(Math.Max(firstStart, firstEnd) - Math.Max(secondStart, secondEnd)) < 0.001;
        }

        private static bool AreSamePointForTest(Point2D first, Point2D second)
        {
            return Math.Abs(first.X - second.X) < 0.001
                && Math.Abs(first.Y - second.Y) < 0.001;
        }

        private static double VerticalClearance(DrawingLine rail, Point2D point)
        {
            var deltaX = rail.End.X - rail.Start.X;
            if (Math.Abs(deltaX) < 0.001) return double.PositiveInfinity;
            var factor = (point.X - rail.Start.X) / deltaX;
            var railY = rail.Start.Y + factor * (rail.End.Y - rail.Start.Y);
            return railY - point.Y;
        }

        private static void LabelsFlightsAndLandings()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.InsertComponentSchedule = true;
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);

            TestAssert.True(section.Texts.Any(text => text.Content == "TD-1-1"), "Flight ID label is missing.");
            TestAssert.True(section.Texts.Any(text => text.Content == "PT-1-1"), "Landing ID label is missing.");
            TestAssert.True(section.Texts.Any(text => text.Content == "3.000 (2F)"),
                "The physical-floor level label is missing.");
        }

        private static double DistanceBetweenParallelLines(Point2D lineStart, Point2D lineEnd, Point2D point)
        {
            var deltaX = lineEnd.X - lineStart.X;
            var deltaY = lineEnd.Y - lineStart.Y;
            return Math.Abs((deltaY * point.X) - (deltaX * point.Y)
                + (lineEnd.X * lineStart.Y) - (lineEnd.Y * lineStart.X))
                / Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        }

        private static StairDefinition StandardDefinition()
        {
            return new StairDefinition(
                floorHeight: 3000.0,
                flightWidth: 1100.0,
                stairwellWidth: 200.0,
                landingDepth: 1200.0,
                treadDepth: 280.0)
            {
                TotalRiserCount = 18
            };
        }
    }
}
