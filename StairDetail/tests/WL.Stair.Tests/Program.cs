using System;
using System.Collections.Generic;
using System.Linq;
using WL.Stair.Core.Calculation;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Geometry;
using WL.Stair.Core.Validation;

namespace WL.Stair.Tests
{
    internal static class Program
    {
        private static readonly IList<Action> Tests = new List<Action>
        {
            CalculatesStandardDoubleFlightStair,
            RecommendsBalancedEvenRiserCount,
            SplitsOddRiserCountAccordingToPreference,
            SupportsManualFlightSplit,
            RejectsInvalidGeometry,
            ReportsLandingAndWidthWarnings,
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
            ConnectsEveryNonThreeFlightStoreyDirectly,
            SynchronizesDisplayedTotalRiserCount,
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
            PreservesRiserCountsAndRecalculatesTreadDepth,
            LocksStoreyTreadDepthAndBalancesPlatforms,
            PreservesLockedPlatformWidth,
            DrawsWallsFromBeamCenterAxes,
            LabelsFlightsAndLandings,
            SupportsBasementBaseElevation
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
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);

            TestAssert.True(outcome.IsSuccess, "A negative basement base elevation must be supported.");
            TestAssert.NearlyEqual(-3000.0, outcome.Result.Storeys[0].LowerElevation, 0.001,
                "The basement base elevation was lost.");
            TestAssert.NearlyEqual(6200.0, outcome.Result.TotalHeight, 0.001,
                "Total height must measure from the lowest floor to the highest floor.");
            TestAssert.True(section.Texts.Any(text => text.Content.Contains("一层楼板  -3.000")),
                "The lowest basement floor label must use its configured name and elevation.");
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
            var finalTreadStartX = firstFlight.HorizontalRun - firstFlight.TreadDepth;

            TestAssert.True(flightLines.Any(line =>
                Math.Abs(line.Start.X - finalTreadStartX) < 0.001
                && Math.Abs(line.End.X - firstFlight.HorizontalRun) < 0.001
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

            for (var index = 0; index < flight.TreadCount; index++)
            {
                var treadStartX = index * flight.TreadDepth;
                var treadEndX = (index + 1) * flight.TreadDepth;
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
            var constraints = new StairProjectConstraintService();
            constraints.Apply(project);
            var outcome = new StairProjectCalculator().Calculate(project);
            TestAssert.True(outcome.IsSuccess, "The default project must calculate before geometry validation.");
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);
            var storey = project.Storeys[0];
            var result = outcome.Result.Storeys[0];
            var connectionX = 0.0;
            for (var index = 0; index < storey.Flights.Count; index++)
                connectionX += (int)storey.Flights[index].Direction * result.Flights[index].HorizontalRun;
            var floorLines = section.Lines.Where(line => line.ComponentId == storey.UpperFloorId).ToArray();
            var xs = floorLines.SelectMany(line => new[] { line.Start.X, line.End.X }).ToArray();
            TestAssert.True(xs.Length > 0, "The upper shared floor outline is missing.");
            if (project.Floors[1].ProjectionDirection > 0)
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
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);

            TestAssert.Equal(0, section.Lines.Count(line => line.ComponentId == "LB-01"),
                "The first level must not generate a floor slab.");
            TestAssert.Equal(0, section.Lines.Count(line => line.ComponentId == "LL-01"),
                "The first level must not generate a floor beam.");
            TestAssert.True(section.Texts.Any(text => text.Content.StartsWith("首层")),
                "The first-level elevation label must remain visible.");
        }

        private static void BuildsThreePlatformOutlinesWithoutOverlaps()
        {
            for (var type = PlatformLayoutType.Platform1; type <= PlatformLayoutType.Platform3; type++)
            {
                var project = StairProjectDefinition.CreateDefault();
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
            new StairProjectConstraintService().Apply(project);
            TestAssert.Equal(7, project.Storeys[1].Flights[0].RiserCount,
                "An unlocked storey must retain its manual riser count.");
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

        private static void PreservesRiserCountsAndRecalculatesTreadDepth()
        {
            var project = StairProjectDefinition.CreateDefault();
            project.Construction.StairwellDepth = 4800.0;
            project.Floors[0].PlatformWidth = 1200.0;
            project.Storeys[0].Landings[0].PlatformWidth = 1200.0;
            project.Storeys[0].Flights[0].RiserCount = 11;
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

        private static void LabelsFlightsAndLandings()
        {
            var project = StairProjectDefinition.CreateDefault();
            var outcome = new StairProjectCalculator().Calculate(project);
            var section = new StairProjectGeometryBuilder().BuildSection(project, outcome.Result);

            TestAssert.True(section.Texts.Any(text => text.Content == "TD-1-1"), "Flight ID label is missing.");
            TestAssert.True(section.Texts.Any(text => text.Content == "PT-1-1"), "Landing ID label is missing.");
            TestAssert.True(section.Texts.Any(text => text.Content.StartsWith("LB-02")), "Floor ID label is missing.");
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
