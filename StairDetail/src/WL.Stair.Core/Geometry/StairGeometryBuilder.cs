using System;
using System.Collections.Generic;
using WL.Stair.Core.Calculation;
using WL.Stair.Core.Domain;

namespace WL.Stair.Core.Geometry
{
    /// <summary>
    /// Produces CAD-independent millimetre geometry. Visibility and annotation are renderer concerns.
    /// </summary>
    public sealed class StairGeometryBuilder
    {
        public DrawingView BuildPlan(StairDefinition definition, StairCalculationResult calculation)
        {
            var plan = BuildPlan(definition, calculation, StairPlanLevel.IntermediateFloor);
            return new DrawingView("Plan", plan.Lines, plan.Texts);
        }

        public DrawingView BuildPlan(
            StairDefinition definition,
            StairCalculationResult calculation,
            StairPlanLevel level)
        {
            EnsureArguments(definition, calculation);

            var lines = new List<DrawingLine>();
            var texts = new List<DrawingText>();
            var length = calculation.PlanLength;
            var width = calculation.PlanWidth;

            AddRectangle(lines, 0.0, 0.0, length, width, StairLineRole.Outline);

            var flightStart = definition.FloorLandingDepth;
            var flightEnd = length - definition.IntermediateLandingDepth;
            var firstFlightTop = definition.FlightWidth;
            var secondFlightBottom = definition.FlightWidth + definition.StairwellWidth;

            if (definition.StairwellWidth > 0.0)
            {
                lines.Add(new DrawingLine(
                    new Point2D(flightStart, firstFlightTop),
                    new Point2D(flightEnd, firstFlightTop),
                    StairLineRole.StairwellEdge));
                lines.Add(new DrawingLine(
                    new Point2D(flightStart, secondFlightBottom),
                    new Point2D(flightEnd, secondFlightBottom),
                    StairLineRole.StairwellEdge));
            }

            AddPlanLandingEdges(lines, flightStart, flightEnd, width);

            if (level != StairPlanLevel.TopFloor)
            {
                AddPlanTreads(
                    lines,
                    flightStart,
                    0.0,
                    definition.FlightWidth,
                    calculation.FirstFlight,
                    false,
                    false);
                AddWalkingArrow(
                    lines,
                    new Point2D(flightStart, definition.FlightWidth / 2.0),
                    new Point2D(
                        flightStart + calculation.FirstFlight.HorizontalRun,
                        definition.FlightWidth / 2.0),
                    false,
                    definition.TreadDepth * 0.75);
            }

            if (level != StairPlanLevel.FirstFloor)
            {
                var isHidden = level == StairPlanLevel.IntermediateFloor;
                AddPlanTreads(
                    lines,
                    flightEnd,
                    secondFlightBottom,
                    width,
                    calculation.SecondFlight,
                    true,
                    isHidden);
                AddWalkingArrow(
                    lines,
                    new Point2D(
                        flightEnd,
                        secondFlightBottom + (definition.FlightWidth / 2.0)),
                    new Point2D(
                        flightEnd - calculation.SecondFlight.HorizontalRun,
                        secondFlightBottom + (definition.FlightWidth / 2.0)),
                    isHidden,
                    definition.TreadDepth * 0.75);
            }

            AddPlanTexts(texts, definition, calculation, level, length, width);
            return new DrawingView(GetPlanName(level), lines, texts);
        }

        public DrawingView BuildSection(StairDefinition definition, StairCalculationResult calculation)
        {
            return BuildSectionCore(definition, calculation, 2, "Section");
        }

        public DrawingView BuildMultiFloorSection(
            StairDefinition definition,
            StairCalculationResult calculation,
            int floorCount)
        {
            EnsureArguments(definition, calculation);
            if (floorCount < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(floorCount));
            }

            return BuildSectionCore(definition, calculation, floorCount, "MultiFloorSection");
        }

        private static DrawingView BuildSectionCore(
            StairDefinition definition,
            StairCalculationResult calculation,
            int floorCount,
            string name)
        {
            EnsureArguments(definition, calculation);
            var lines = new List<DrawingLine>();
            var texts = new List<DrawingText>();
            var floorHeight = calculation.FloorElevation;

            // The section cuts the upward flight. The return flight is behind the cut plane.
            for (var storyIndex = 0; storyIndex < floorCount - 1; storyIndex++)
            {
                var lowerFloorElevation = storyIndex * floorHeight;
                var intermediateElevation = lowerFloorElevation
                    + calculation.FirstFlight.VerticalRise;
                var upperFloorElevation = lowerFloorElevation + floorHeight;

                var hiddenFloorConnectionX = -calculation.FirstFlight.HorizontalRun;
                AddFlightBoundary(
                    lines,
                    hiddenFloorConnectionX,
                    lowerFloorElevation,
                    calculation.FirstFlight,
                    1.0,
                    definition.FlightSlabThickness,
                    true,
                    Math.Abs(lowerFloorElevation) < 0.001);
                AddFlightBoundary(
                    lines,
                    0.0,
                    intermediateElevation,
                    calculation.SecondFlight,
                    -1.0,
                    definition.FlightSlabThickness,
                    false,
                    false);

                AddPlatformBoundary(
                    lines,
                    0.0,
                    definition.IntermediateLandingDepthDown,
                    intermediateElevation,
                    definition.LandingSlabThickness,
                    false);
                AddPlatformBoundary(
                    lines,
                    0.0,
                    definition.IntermediateLandingDepthUp,
                    intermediateElevation,
                    definition.LandingSlabThickness,
                    false);

                var cutFloorConnectionX = -calculation.SecondFlight.HorizontalRun;
                AddPlatformBoundary(
                    lines,
                    cutFloorConnectionX - definition.FloorLandingDepthUp,
                    cutFloorConnectionX,
                    upperFloorElevation,
                    definition.FloorSlabThickness,
                    false);
                AddFloorBeam(
                    lines,
                    cutFloorConnectionX,
                    upperFloorElevation,
                    definition.FloorBeamWidth,
                    definition.FloorBeamDepth,
                    false);
            }

            for (var floorIndex = 0; floorIndex < floorCount; floorIndex++)
            {
                var elevationOffset = floorIndex * floorHeight;
                texts.Add(new DrawingText(
                    new Point2D(-450.0, elevationOffset),
                    FormatElevation(elevationOffset),
                    105.0));
            }

            texts.Add(new DrawingText(
                new Point2D(calculation.PlanLength / 2.0, -650.0),
                "楼梯剖面",
                105.0));
            texts.Add(new DrawingText(
                new Point2D(calculation.PlanLength / 2.0, -820.0),
                "1:30",
                84.0));

            return new DrawingView(name, lines, texts);
        }

        private static string FormatElevation(double elevation)
        {
            if (Math.Abs(elevation) < 0.001)
            {
                return "±0.000";
            }

            return "+" + (elevation / 1000.0).ToString("0.000");
        }

        private static void AddFlightBoundary(
            ICollection<DrawingLine> lines,
            double startX,
            double startElevation,
            StairFlightResult flight,
            double direction,
            double slabThickness,
            bool isHidden,
            bool startsAtFirstFloor)
        {
            var x = startX;
            var elevation = startElevation;
            var role = isHidden ? StairLineRole.SectionProfile : StairLineRole.CutFlightProfile;

            for (var treadIndex = 0; treadIndex < flight.TreadCount; treadIndex++)
            {
                var nextElevation = elevation + flight.RiserHeight;
                var riserBottom = treadIndex == 0 && !startsAtFirstFloor
                    ? elevation - flight.RiserHeight
                    : elevation;
                lines.Add(new DrawingLine(
                    new Point2D(x, riserBottom),
                    new Point2D(x, nextElevation),
                    role,
                    isHidden));
                elevation = nextElevation;

                var nextX = x + (direction * flight.TreadDepth);
                lines.Add(new DrawingLine(
                    new Point2D(x, elevation),
                    new Point2D(nextX, elevation),
                    role,
                    isHidden));
                x = nextX;
            }

            Point2D outlineStart;
            if (startsAtFirstFloor)
            {
                outlineStart = new Point2D(
                    startX + (direction * flight.TreadDepth),
                    startElevation);
                lines.Add(new DrawingLine(
                    new Point2D(startX, startElevation),
                    outlineStart,
                    role,
                    isHidden));
            }
            else
            {
                outlineStart = new Point2D(startX, startElevation - flight.RiserHeight);
            }

            var outlineEnd = new Point2D(x, elevation - flight.RiserHeight);
            lines.Add(new DrawingLine(
                new Point2D(x, elevation),
                outlineEnd,
                role,
                isHidden));
            lines.Add(new DrawingLine(
                outlineEnd,
                outlineStart,
                role,
                isHidden));
        }

        private static void AddPlatformBoundary(
            ICollection<DrawingLine> lines,
            double startX,
            double endX,
            double topElevation,
            double thickness,
            bool isHidden)
        {
            var role = isHidden ? StairLineRole.Landing : StairLineRole.CutBoundary;
            var bottomElevation = topElevation - thickness;
            lines.Add(new DrawingLine(
                new Point2D(startX, topElevation),
                new Point2D(endX, topElevation),
                role,
                isHidden));
            lines.Add(new DrawingLine(
                new Point2D(endX, topElevation),
                new Point2D(endX, bottomElevation),
                role,
                isHidden));
            lines.Add(new DrawingLine(
                new Point2D(endX, bottomElevation),
                new Point2D(startX, bottomElevation),
                role,
                isHidden));
            lines.Add(new DrawingLine(
                new Point2D(startX, bottomElevation),
                new Point2D(startX, topElevation),
                role,
                isHidden));
        }

        private static void AddFloorBeam(
            ICollection<DrawingLine> lines,
            double connectionX,
            double topElevation,
            double width,
            double depth,
            bool isHidden)
        {
            var role = StairLineRole.BeamBoundary;
            var left = connectionX - width;
            var bottom = topElevation - depth;
            lines.Add(new DrawingLine(new Point2D(left, bottom), new Point2D(connectionX, bottom), role, isHidden));
            lines.Add(new DrawingLine(new Point2D(connectionX, bottom), new Point2D(connectionX, topElevation), role, isHidden));
            lines.Add(new DrawingLine(new Point2D(connectionX, topElevation), new Point2D(left, topElevation), role, isHidden));
            lines.Add(new DrawingLine(new Point2D(left, topElevation), new Point2D(left, bottom), role, isHidden));
        }

        private static void AddPlanTreads(
            ICollection<DrawingLine> lines,
            double originX,
            double bottomY,
            double topY,
            StairFlightResult flight,
            bool reverse,
            bool isHidden)
        {
            for (var index = 1; index <= flight.TreadCount; index++)
            {
                var offset = index * flight.TreadDepth;
                var x = reverse ? originX - offset : originX + offset;
                lines.Add(new DrawingLine(
                    new Point2D(x, bottomY),
                    new Point2D(x, topY),
                    StairLineRole.Tread,
                    isHidden));
            }
        }

        private static void AddPlanLandingEdges(
            ICollection<DrawingLine> lines,
            double flightStart,
            double flightEnd,
            double width)
        {
            lines.Add(new DrawingLine(
                new Point2D(flightStart, 0.0),
                new Point2D(flightStart, width),
                StairLineRole.Landing));
            lines.Add(new DrawingLine(
                new Point2D(flightEnd, 0.0),
                new Point2D(flightEnd, width),
                StairLineRole.Landing));
        }

        private static void AddWalkingArrow(
            ICollection<DrawingLine> lines,
            Point2D start,
            Point2D end,
            bool isHidden,
            double arrowSize)
        {
            lines.Add(new DrawingLine(start, end, StairLineRole.WalkingLine, isHidden));

            var direction = Math.Sign(end.X - start.X);
            var backX = end.X - (direction * arrowSize);
            var halfWidth = arrowSize * 0.45;
            lines.Add(new DrawingLine(
                end,
                new Point2D(backX, end.Y + halfWidth),
                StairLineRole.WalkingLine,
                isHidden));
            lines.Add(new DrawingLine(
                end,
                new Point2D(backX, end.Y - halfWidth),
                StairLineRole.WalkingLine,
                isHidden));
        }

        private static void AddPlanTexts(
            ICollection<DrawingText> texts,
            StairDefinition definition,
            StairCalculationResult calculation,
            StairPlanLevel level,
            double length,
            double width)
        {
            var textHeight = 105.0;
            texts.Add(new DrawingText(
                new Point2D(length / 2.0, -350.0),
                GetPlanTitle(level),
                textHeight));
            texts.Add(new DrawingText(
                new Point2D(length / 2.0, -520.0),
                "1:30",
                textHeight * 0.8));

            if (level != StairPlanLevel.TopFloor)
            {
                texts.Add(new DrawingText(
                    new Point2D(
                        definition.FloorLandingDepth + (calculation.FirstFlight.HorizontalRun * 0.55),
                        definition.FlightWidth / 2.0 + 120.0),
                    "上",
                    textHeight));
            }

            if (level != StairPlanLevel.FirstFloor)
            {
                texts.Add(new DrawingText(
                    new Point2D(
                        length - definition.IntermediateLandingDepth
                            - (calculation.SecondFlight.HorizontalRun * 0.55),
                        width - (definition.FlightWidth / 2.0) + 120.0),
                    "下",
                    textHeight));
            }
        }

        private static string GetPlanName(StairPlanLevel level)
        {
            switch (level)
            {
                case StairPlanLevel.FirstFloor:
                    return "FirstFloorPlan";
                case StairPlanLevel.TopFloor:
                    return "TopFloorPlan";
                default:
                    return "IntermediateFloorPlan";
            }
        }

        private static string GetPlanTitle(StairPlanLevel level)
        {
            switch (level)
            {
                case StairPlanLevel.FirstFloor:
                    return "一层平面";
                case StairPlanLevel.TopFloor:
                    return "顶层平面";
                default:
                    return "中间层平面";
            }
        }

        private static void AddSectionStructure(
            ICollection<DrawingLine> lines,
            StairDefinition definition,
            StairCalculationResult calculation,
            double turnaroundX,
            double upperFlightEndX)
        {
            var firstFlightStartX = definition.FloorLandingDepth;
            var intermediateElevation = calculation.IntermediateLandingElevation;
            var floorElevation = calculation.FloorElevation;
            var firstFlightVerticalOffset = CalculateSoffitVerticalOffset(
                definition.FlightSlabThickness,
                calculation.FirstFlight);
            var secondFlightVerticalOffset = CalculateSoffitVerticalOffset(
                definition.FlightSlabThickness,
                calculation.SecondFlight);

            AddHorizontalStructureLine(
                lines,
                0.0,
                firstFlightStartX,
                -definition.FloorSlabThickness);
            AddVerticalStructureLine(
                lines,
                0.0,
                -definition.FloorSlabThickness,
                0.0);

            lines.Add(new DrawingLine(
                new Point2D(firstFlightStartX, -firstFlightVerticalOffset),
                new Point2D(turnaroundX, intermediateElevation - firstFlightVerticalOffset),
                StairLineRole.StructuralEdge));

            AddHorizontalStructureLine(
                lines,
                turnaroundX,
                turnaroundX + definition.IntermediateLandingDepth,
                intermediateElevation - definition.LandingSlabThickness);
            AddVerticalStructureLine(
                lines,
                turnaroundX + definition.IntermediateLandingDepth,
                intermediateElevation - definition.LandingSlabThickness,
                intermediateElevation);

            lines.Add(new DrawingLine(
                new Point2D(turnaroundX, intermediateElevation - secondFlightVerticalOffset),
                new Point2D(upperFlightEndX, floorElevation - secondFlightVerticalOffset),
                StairLineRole.StructuralEdge));

            AddHorizontalStructureLine(
                lines,
                upperFlightEndX - definition.FloorLandingDepth,
                upperFlightEndX,
                floorElevation - definition.FloorSlabThickness);
            AddVerticalStructureLine(
                lines,
                upperFlightEndX - definition.FloorLandingDepth,
                floorElevation - definition.FloorSlabThickness,
                floorElevation);

            AddThicknessJoint(
                lines,
                firstFlightStartX,
                -definition.FloorSlabThickness,
                -firstFlightVerticalOffset);
            AddThicknessJoint(
                lines,
                turnaroundX,
                intermediateElevation - firstFlightVerticalOffset,
                intermediateElevation - definition.LandingSlabThickness);
            AddThicknessJoint(
                lines,
                turnaroundX,
                intermediateElevation - secondFlightVerticalOffset,
                intermediateElevation - definition.LandingSlabThickness);
            AddThicknessJoint(
                lines,
                upperFlightEndX,
                floorElevation - secondFlightVerticalOffset,
                floorElevation - definition.FloorSlabThickness);
        }

        private static double CalculateSoffitVerticalOffset(
            double slabThickness,
            StairFlightResult flight)
        {
            var slopeLength = Math.Sqrt(
                (flight.HorizontalRun * flight.HorizontalRun)
                + (flight.VerticalRise * flight.VerticalRise));
            return slabThickness * slopeLength / flight.HorizontalRun;
        }

        private static void AddHorizontalStructureLine(
            ICollection<DrawingLine> lines,
            double startX,
            double endX,
            double elevation)
        {
            lines.Add(new DrawingLine(
                new Point2D(startX, elevation),
                new Point2D(endX, elevation),
                StairLineRole.StructuralEdge));
        }

        private static void AddVerticalStructureLine(
            ICollection<DrawingLine> lines,
            double x,
            double startElevation,
            double endElevation)
        {
            lines.Add(new DrawingLine(
                new Point2D(x, startElevation),
                new Point2D(x, endElevation),
                StairLineRole.StructuralEdge));
        }

        private static void AddThicknessJoint(
            ICollection<DrawingLine> lines,
            double x,
            double firstElevation,
            double secondElevation)
        {
            if (Math.Abs(firstElevation - secondElevation) < 0.001)
            {
                return;
            }

            AddVerticalStructureLine(lines, x, firstElevation, secondElevation);
        }

        private static void AddSectionFlight(
            ICollection<DrawingLine> lines,
            ref double x,
            ref double z,
            StairFlightResult flight,
            double direction)
        {
            for (var treadIndex = 0; treadIndex < flight.TreadCount; treadIndex++)
            {
                var nextZ = z + flight.RiserHeight;
                lines.Add(new DrawingLine(
                    new Point2D(x, z),
                    new Point2D(x, nextZ),
                    StairLineRole.SectionProfile));
                z = nextZ;

                var nextX = x + (direction * flight.TreadDepth);
                lines.Add(new DrawingLine(
                    new Point2D(x, z),
                    new Point2D(nextX, z),
                    StairLineRole.SectionProfile));
                x = nextX;
            }
        }

        private static void AddRectangle(
            ICollection<DrawingLine> lines,
            double left,
            double bottom,
            double right,
            double top,
            StairLineRole role)
        {
            lines.Add(new DrawingLine(new Point2D(left, bottom), new Point2D(right, bottom), role));
            lines.Add(new DrawingLine(new Point2D(right, bottom), new Point2D(right, top), role));
            lines.Add(new DrawingLine(new Point2D(right, top), new Point2D(left, top), role));
            lines.Add(new DrawingLine(new Point2D(left, top), new Point2D(left, bottom), role));
        }

        private static void EnsureArguments(StairDefinition definition, StairCalculationResult calculation)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (calculation == null)
            {
                throw new ArgumentNullException(nameof(calculation));
            }
        }
    }
}
