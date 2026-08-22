using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WL.Stair.Core.Calculation;
using WL.Stair.Core.Domain;

namespace WL.Stair.Core.Geometry
{
    public sealed class StairProjectGeometryBuilder
    {
        public DrawingView BuildPlan(StairProjectDefinition project, StairProjectCalculationResult calculation, int storeyIndex)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (calculation == null) throw new ArgumentNullException(nameof(calculation));
            if (storeyIndex < 0 || storeyIndex >= project.Storeys.Count) return new DrawingView("Plan", new DrawingLine[0]);
            var storey = project.Storeys[storeyIndex];
            var lines = new List<DrawingLine>();
            var x = 0.0;
            foreach (var flight in storey.Flights)
            {
                var run = Math.Max(1.0, flight.TreadDepth * Math.Max(1, flight.RiserCount - 1));
                AddPlanRect(lines, x, 0, run, flight.Width, flight.Id);
                x += run;
                var landing = storey.Landings.FirstOrDefault(item => item.IncomingFlightId == flight.Id);
                if (landing != null)
                {
                    AddPlanRect(lines, x, 0, landing.PlatformWidth, flight.Width, landing.Id);
                    x += landing.PlatformWidth;
                }
            }
            return new DrawingView("Plan-" + storey.Id, lines);
        }

        private static void AddPlanRect(List<DrawingLine> lines, double x, double y, double width, double height, string id)
        {
            lines.Add(new DrawingLine(new Point2D(x, y), new Point2D(x + width, y), StairLineRole.CutFlightProfile, false, id));
            lines.Add(new DrawingLine(new Point2D(x + width, y), new Point2D(x + width, y + height), StairLineRole.CutFlightProfile, false, id));
            lines.Add(new DrawingLine(new Point2D(x + width, y + height), new Point2D(x, y + height), StairLineRole.CutFlightProfile, false, id));
            lines.Add(new DrawingLine(new Point2D(x, y + height), new Point2D(x, y), StairLineRole.CutFlightProfile, false, id));
        }

        public DrawingView BuildSection(
            StairProjectDefinition project,
            StairProjectCalculationResult calculation)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (calculation == null) throw new ArgumentNullException(nameof(calculation));

            var lines = new List<DrawingLine>();
            var texts = new List<DrawingText>();
            var floors = project.Floors.ToDictionary(floor => floor.Id, StringComparer.OrdinalIgnoreCase);
            var storeyResults = calculation.Storeys.ToDictionary(result => result.Id, StringComparer.OrdinalIgnoreCase);
            var floorPositions = new Dictionary<string, ComponentPosition>(StringComparer.OrdinalIgnoreCase);
            var wallAnchors = new List<WallAnchor>();
            var lowestElevation = calculation.Storeys.Min(result => result.LowerElevation);
            var firstStorey = project.Storeys.First(storey => storey.Flights.Count > 0);
            var firstFloor = floors[firstStorey.LowerFloorId];
            var firstDirection = (int)firstStorey.Flights[0].Direction;
            var firstAxisX = -(firstDirection * firstFloor.PlatformWidth);
            var secondAxisX = firstAxisX
                + (firstDirection * project.Construction.StairwellDepth);

            Func<int, double> axisForDirection = direction =>
                direction == firstDirection ? firstAxisX : secondAxisX;
            Func<int, double, double> connectionForBoundary = (direction, width) =>
                axisForDirection(direction) + (direction * width);
            var floorDirections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in project.Storeys.Where(item => item.Flights.Count > 0))
            {
                if (!floorDirections.ContainsKey(item.LowerFloorId))
                    floorDirections.Add(item.LowerFloorId, (int)item.Flights[0].Direction);
                if (!floorDirections.ContainsKey(item.UpperFloorId))
                    floorDirections.Add(item.UpperFloorId, -(int)item.Flights.Last().Direction);
            }

            foreach (var storey in project.Storeys)
            {
                var storeyResult = storeyResults[storey.Id];
                StairFloorDefinition lowerFloor;
                StairFloorDefinition upperFloor;
                floors.TryGetValue(storey.LowerFloorId, out lowerFloor);
                floors.TryGetValue(storey.UpperFloorId, out upperFloor);
                ComponentPosition lowerPosition;
                if (!floorPositions.TryGetValue(storey.LowerFloorId, out lowerPosition))
                {
                    var lowerDirection = lowerFloor == null
                        ? (int)storey.Flights[0].Direction
                        : floorDirections[lowerFloor.Id];
                    var lowerWidth = lowerFloor == null ? 0.0 : lowerFloor.PlatformWidth;
                    lowerPosition = new ComponentPosition(
                        connectionForBoundary(lowerDirection, lowerWidth),
                        storeyResult.LowerElevation);
                    floorPositions.Add(storey.LowerFloorId, lowerPosition);
                }

                var currentX = lowerPosition.X;
                var currentElevation = lowerPosition.Elevation;
                for (var index = 0; index < storey.Flights.Count; index++)
                {
                    var flight = storey.Flights[index];
                    var flightResult = storeyResult.Flights[index];
                    var direction = (double)flight.Direction;
                    var destinationDirection = -(int)flight.Direction;
                    var destinationWidth = index < storey.Landings.Count
                        ? storey.Landings[index].PlatformWidth
                        : upperFloor == null ? 0.0 : upperFloor.PlatformWidth;
                    var boundaryConnectionX = connectionForBoundary(destinationDirection, destinationWidth);
                    // Only the final run of a three-flight storey may
                    // deliberately end short and use the designed bridge.
                    var flightEndX = currentX + (direction * flightResult.HorizontalRun);
                    var allowBridgeClosure = (storey.Flights.Count == 3
                            || storey.AllowUpperClosureGap)
                        && index == storey.Flights.Count - 1;
                    if (!allowBridgeClosure
                        && Math.Abs(flightEndX - boundaryConnectionX) > 0.01)
                    {
                        throw new InvalidOperationException(
                            storey.Id + " 的梯段必须直接连接楼板或休息平台。");
                    }
                    var isHidden = flight.SectionRepresentation == StairSectionRepresentation.Rear;
                    var destinationSlabThickness = index < storey.Landings.Count
                        ? storey.Landings[index].SlabThicknessOverride
                            ?? project.Construction.LandingSlabThickness
                        : upperFloor == null
                            ? project.Construction.FloorSlabThickness
                            : upperFloor.SlabThicknessOverride
                                ?? project.Construction.FloorSlabThickness;
                    AddFlightBoundary(
                        lines,
                        currentX,
                        currentElevation,
                        flightResult,
                        direction,
                        boundaryConnectionX,
                        destinationSlabThickness,
                        allowBridgeClosure,
                        isHidden,
                        Math.Abs(currentElevation - lowestElevation) < 0.001,
                        flight.Id);

                    var endElevation = currentElevation + flightResult.VerticalRise;
                    texts.Add(new DrawingText(
                        new Point2D((currentX + flightEndX) / 2.0, (currentElevation + endElevation) / 2.0 + 180.0),
                        flight.Id,
                        90.0));
                    currentX = boundaryConnectionX;
                    currentElevation = endElevation;

                    if (index < storey.Landings.Count)
                    {
                        var landing = storey.Landings[index];
                        // currentX is the shared connection edge for the incoming
                        // and outgoing flights. The landing's opposite end is its
                        // fixed beam axis; neither flight starts from the axis end.
                        AddLanding(
                            lines,
                            texts,
                            landing,
                            currentX,
                            currentElevation,
                            project.Construction,
                            -destinationDirection);
                        var landingAxisX = axisForDirection(destinationDirection);
                        wallAnchors.Add(new WallAnchor(
                            landingAxisX,
                            currentElevation,
                            landing.BeamDepthOverride ?? project.Construction.LandingBeam.Depth));
                    }
                }

                if (!floorPositions.ContainsKey(storey.UpperFloorId))
                {
                    var upperDirection = upperFloor == null
                        ? -(int)storey.Flights.Last().Direction
                        : floorDirections[upperFloor.Id];
                    var upperWidth = upperFloor == null ? 0.0 : upperFloor.PlatformWidth;
                    floorPositions.Add(
                        storey.UpperFloorId,
                        new ComponentPosition(
                            connectionForBoundary(upperDirection, upperWidth),
                            storeyResult.UpperElevation));
                }
            }

            foreach (var floor in project.Floors)
            {
                ComponentPosition position;
                if (!floorPositions.TryGetValue(floor.Id, out position))
                {
                    continue;
                }

                if (Math.Abs(position.Elevation - lowestElevation) < 0.001)
                {
                    var lowestFloorName = project.BaseElevation < -0.001
                        ? floor.Name
                        : "首层";
                    texts.Add(new DrawingText(
                        new Point2D(position.X, position.Elevation + 150.0),
                        lowestFloorName + "  " + FormatElevation(position.Elevation),
                        90.0));
                    continue;
                }
                var floorDirection = floorDirections.ContainsKey(floor.Id)
                    ? floorDirections[floor.Id]
                    : floor.ProjectionDirection;
                wallAnchors.Add(new WallAnchor(
                    axisForDirection(floorDirection),
                    position.Elevation,
                    floor.BeamDepthOverride ?? project.Construction.FloorBeam.Depth));
                AddFloor(lines, texts, floor, position, project.Construction, floorDirection);
            }

            AddStairwellWalls(
                lines,
                wallAnchors,
                lowestElevation,
                calculation.Storeys.Max(result => result.UpperElevation),
                project.Construction);

            AddStairwellAxisLines(lines, firstAxisX, secondAxisX, lowestElevation,
                calculation.Storeys.Max(result => result.UpperElevation));

            var titleX = floorPositions.Count == 0 ? 0.0 : floorPositions.Values.Average(position => position.X);
            var titleY = lowestElevation - 650.0;
            var title = string.Join(" · ", new[]
            {
                project.ProjectName,
                project.SubprojectName,
                project.BuildingNumber,
                project.StairNumber,
                project.Name
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            texts.Add(new DrawingText(new Point2D(titleX, titleY), title, 105.0));
            texts.Add(new DrawingText(new Point2D(titleX, titleY - 170.0), "1:30", 84.0));
            return new DrawingView("ProjectSection", MergeConnectedCutOutlines(lines), texts);
        }


        private static void AddStairwellAxisLines(
            ICollection<DrawingLine> lines,
            double firstAxisX,
            double secondAxisX,
            double lowestElevation,
            double highestElevation)
        {
            foreach (var x in new[] { firstAxisX, secondAxisX }.Distinct())
            {
                lines.Add(new DrawingLine(
                    new Point2D(x, lowestElevation),
                    new Point2D(x, highestElevation),
                    StairLineRole.AxisLine,
                    false,
                    string.Empty));
            }
        }

        private static IReadOnlyList<DrawingLine> MergeConnectedCutOutlines(
            IReadOnlyList<DrawingLine> sourceLines)
        {
            var result = sourceLines.Where(line => !IsMergeCandidate(line)).ToList();
            var candidates = sourceLines.Where(IsMergeCandidate).ToArray();

            AddMergedAxisLines(result, candidates, true);
            AddMergedAxisLines(result, candidates, false);
            result.AddRange(candidates.Where(line => !IsHorizontal(line) && !IsVertical(line)));
            return result;
        }

        private static void AddMergedAxisLines(
            ICollection<DrawingLine> result,
            IEnumerable<DrawingLine> candidates,
            bool vertical)
        {
            var axisLines = candidates.Where(line => vertical ? IsVertical(line) : IsHorizontal(line));
            foreach (var group in axisLines.GroupBy(line => AxisKey(line, vertical)))
            {
                var lines = group.ToArray();
                var breaks = lines
                    .SelectMany(line => new[] { AxisStart(line, vertical), AxisEnd(line, vertical) })
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray();

                for (var index = 0; index < breaks.Length - 1; index++)
                {
                    var start = breaks[index];
                    var end = breaks[index + 1];
                    if (end - start < 0.001) continue;
                    var middle = (start + end) / 2.0;
                    var owners = lines.Where(line => Covers(line, middle, vertical)).ToArray();
                    if (owners.Length != 1) continue;

                    var owner = owners[0];
                    var fixedAxis = vertical ? owner.Start.X : owner.Start.Y;
                    var first = vertical
                        ? new Point2D(fixedAxis, start)
                        : new Point2D(start, fixedAxis);
                    var second = vertical
                        ? new Point2D(fixedAxis, end)
                        : new Point2D(end, fixedAxis);
                    result.Add(new DrawingLine(
                        first,
                        second,
                        owner.Role,
                        owner.IsHidden,
                        owner.ComponentId));
                }
            }
        }

        private static bool IsMergeCandidate(DrawingLine line)
        {
            return !line.IsHidden
                && (line.Role == StairLineRole.CutBoundary
                    || line.Role == StairLineRole.CutFlightProfile);
        }

        private static bool IsHorizontal(DrawingLine line)
        {
            return Math.Abs(line.Start.Y - line.End.Y) < 0.001;
        }

        private static bool IsVertical(DrawingLine line)
        {
            return Math.Abs(line.Start.X - line.End.X) < 0.001;
        }

        private static double AxisKey(DrawingLine line, bool vertical)
        {
            return Math.Round(vertical ? line.Start.X : line.Start.Y, 3);
        }

        private static double AxisStart(DrawingLine line, bool vertical)
        {
            return Math.Min(
                vertical ? line.Start.Y : line.Start.X,
                vertical ? line.End.Y : line.End.X);
        }

        private static double AxisEnd(DrawingLine line, bool vertical)
        {
            return Math.Max(
                vertical ? line.Start.Y : line.Start.X,
                vertical ? line.End.Y : line.End.X);
        }

        private static bool Covers(DrawingLine line, double value, bool vertical)
        {
            return value > AxisStart(line, vertical) - 0.001
                && value < AxisEnd(line, vertical) + 0.001;
        }

        private static void AddFlightBoundary(
            ICollection<DrawingLine> lines,
            double startX,
            double startElevation,
            StairProjectFlightResult flight,
            double direction,
            double destinationConnectionX,
            double destinationSlabThickness,
            bool allowBridgeClosure,
            bool isHidden,
            bool startsAtFirstFloor,
            string componentId)
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
                    isHidden,
                    componentId));
                elevation = nextElevation;

                var nextX = x + (direction * flight.TreadDepth);
                lines.Add(new DrawingLine(
                    new Point2D(x, elevation),
                    new Point2D(nextX, elevation),
                    role,
                    isHidden,
                    componentId));
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
                    isHidden,
                    componentId));
            }
            else
            {
                outlineStart = new Point2D(startX, startElevation - flight.RiserHeight);
            }

            var outlineEnd = new Point2D(x, elevation - flight.RiserHeight);
            var horizontalGap = direction * (destinationConnectionX - x);
            if (allowBridgeClosure && horizontalGap > 0.001)
            {
                var destinationElevation = elevation + flight.RiserHeight;
                var bridgeUndersideElevation = destinationElevation - destinationSlabThickness;
                Point2D? soffitConnection = null;
                var soffitRise = outlineEnd.Y - outlineStart.Y;
                if (Math.Abs(soffitRise) > 0.001)
                {
                    // FILLET R=0 between the underside of the horizontal bridge
                    // and the existing flight soffit: extend both to their exact
                    // intersection without changing the linked tread depth.
                    var factor = (bridgeUndersideElevation - outlineStart.Y) / soffitRise;
                    var soffitIntersectionX = outlineStart.X
                        + (factor * (outlineEnd.X - outlineStart.X));
                    var intersectionAfterFlightEnd = direction * (soffitIntersectionX - x);
                    var intersectionBeforeBoundary = direction
                        * (destinationConnectionX - soffitIntersectionX);
                    if (intersectionAfterFlightEnd > -0.001
                        && intersectionBeforeBoundary > -0.001)
                    {
                        soffitConnection = new Point2D(
                            soffitIntersectionX,
                            bridgeUndersideElevation);
                    }
                }

                lines.Add(new DrawingLine(
                    new Point2D(x, elevation),
                    new Point2D(x, destinationElevation),
                    role,
                    isHidden,
                    componentId));
                lines.Add(new DrawingLine(
                    new Point2D(x, destinationElevation),
                    new Point2D(destinationConnectionX, destinationElevation),
                    role,
                    isHidden,
                    componentId));
                // Duplicate the shared interface with the destination
                // floor/landing. MergeConnectedCutOutlines removes the
                // coincident copies, leaving one continuous outer boundary.
                lines.Add(new DrawingLine(
                    new Point2D(destinationConnectionX, destinationElevation),
                    new Point2D(destinationConnectionX, bridgeUndersideElevation),
                    role,
                    isHidden,
                    componentId));
                if (soffitConnection.HasValue)
                {
                    lines.Add(new DrawingLine(
                        new Point2D(destinationConnectionX, bridgeUndersideElevation),
                        soffitConnection.Value,
                        role,
                        isHidden,
                        componentId));
                    lines.Add(new DrawingLine(
                        soffitConnection.Value,
                        outlineStart,
                        role,
                        isHidden,
                        componentId));
                }
                else
                {
                    // When the remaining distance is too short for a valid
                    // horizontal soffit intersection, redraw the underside as
                    // one continuous diagonal to the destination slab. A short
                    // extra diagonal at the flight end creates an unrealistic
                    // kink in the structural stair profile.
                    lines.Add(new DrawingLine(
                        new Point2D(destinationConnectionX, bridgeUndersideElevation),
                        outlineStart,
                        role,
                        isHidden,
                        componentId));
                }
                return;
            }

            lines.Add(new DrawingLine(
                new Point2D(x, elevation),
                outlineEnd,
                role,
                isHidden,
                componentId));
            lines.Add(new DrawingLine(
                outlineEnd,
                outlineStart,
                role,
                isHidden,
                componentId));
        }

        private static void AddLanding(
            ICollection<DrawingLine> lines,
            ICollection<DrawingText> texts,
            StairLandingDefinition landing,
            double connectionX,
            double elevation,
            StairConstructionDefaults defaults,
            int drawingDirection)
        {
            var projection = drawingDirection;
            var platformWidth = landing.PlatformWidth;
            var thickness = landing.SlabThicknessOverride ?? defaults.LandingSlabThickness;
            var beamWidth = landing.BeamWidthOverride ?? defaults.LandingBeam.Width;
            var beamDepth = landing.BeamDepthOverride ?? defaults.LandingBeam.Depth;
            AddPlatformOutline(
                lines,
                connectionX,
                elevation,
                projection,
                landing.PlatformType,
                platformWidth,
                thickness,
                beamWidth,
                beamDepth,
                StairLineRole.CutBoundary,
                landing.Id);
            texts.Add(new DrawingText(
                new Point2D(connectionX + (projection * platformWidth / 2.0), elevation + 150.0),
                landing.Id,
                90.0));
        }

        private static void AddFloor(
            ICollection<DrawingLine> lines,
            ICollection<DrawingText> texts,
            StairFloorDefinition floor,
            ComponentPosition position,
            StairConstructionDefaults defaults,
            int logicalDirection)
        {
            var platformWidth = floor.PlatformWidth;
            var slabThickness = floor.SlabThicknessOverride ?? defaults.FloorSlabThickness;
            var beamWidth = floor.BeamWidthOverride ?? defaults.FloorBeam.Width;
            var beamDepth = floor.BeamDepthOverride ?? defaults.FloorBeam.Depth;
            AddPlatformOutline(
                lines,
                position.X,
                position.Elevation,
                -logicalDirection,
                floor.PlatformType,
                platformWidth,
                slabThickness,
                beamWidth,
                beamDepth,
                StairLineRole.CutBoundary,
                floor.Id);
            AddFloorText(
                texts,
                floor,
                position,
                position.X - (logicalDirection * platformWidth / 2.0));
        }

        private static void AddPlatformOutline(
            ICollection<DrawingLine> lines,
            double connectionX,
            double topElevation,
            int direction,
            PlatformLayoutType platformType,
            double platformWidth,
            double slabThickness,
            double beamWidth,
            double beamHeight,
            StairLineRole role,
            string componentId)
        {
            var outsideBeamOffset = platformType == PlatformLayoutType.Platform1
                ? 0.0
                : beamWidth / 2.0;
            var totalWidth = platformType == PlatformLayoutType.Platform3
                ? platformWidth + beamWidth
                : platformWidth + outsideBeamOffset;
            var points = new List<Point2D>
            {
                PlatformPoint(connectionX, topElevation, direction, 0.0, 0.0),
                PlatformPoint(connectionX, topElevation, direction, totalWidth, 0.0),
                PlatformPoint(connectionX, topElevation, direction, totalWidth, -slabThickness)
            };

            if (platformType != PlatformLayoutType.Platform1)
            {
                if (platformType == PlatformLayoutType.Platform3)
                {
                    points.Add(PlatformPoint(connectionX, topElevation, direction, platformWidth + outsideBeamOffset, -slabThickness));
                }
                points.Add(PlatformPoint(connectionX, topElevation, direction, platformWidth + outsideBeamOffset, -beamHeight));
                points.Add(PlatformPoint(connectionX, topElevation, direction, platformWidth - outsideBeamOffset, -beamHeight));
                points.Add(PlatformPoint(connectionX, topElevation, direction, platformWidth - outsideBeamOffset, -slabThickness));
            }

            points.Add(PlatformPoint(connectionX, topElevation, direction, beamWidth, -slabThickness));
            points.Add(PlatformPoint(connectionX, topElevation, direction, beamWidth, -beamHeight));
            points.Add(PlatformPoint(connectionX, topElevation, direction, 0.0, -beamHeight));

            for (var index = 0; index < points.Count; index++)
            {
                var nextIndex = (index + 1) % points.Count;
                if (AreSamePoint(points[index], points[nextIndex])) continue;
                lines.Add(new DrawingLine(points[index], points[nextIndex], role, false, componentId));
            }
        }

        private static Point2D PlatformPoint(
            double connectionX,
            double topElevation,
            int direction,
            double localX,
            double localY)
        {
            return new Point2D(connectionX + (direction * localX), topElevation + localY);
        }

        private static bool AreSamePoint(Point2D first, Point2D second)
        {
            return Math.Abs(first.X - second.X) < 0.001
                && Math.Abs(first.Y - second.Y) < 0.001;
        }

        private static void AddStairwellWalls(
            ICollection<DrawingLine> lines,
            IEnumerable<WallAnchor> anchors,
            double lowestElevation,
            double highestElevation,
            StairConstructionDefaults defaults)
        {
            if (defaults.Wall == null || defaults.Wall.Thickness <= 0.0)
                return;

            var positions = anchors.ToArray();
            if (positions.Length == 0) return;

            var halfThickness = defaults.Wall.Thickness / 2.0;
            var bottom = lowestElevation;
            var top = highestElevation + 1800.0;
            foreach (var axisGroup in positions.GroupBy(anchor => Math.Round(anchor.AxisX, 3)))
            {
                var beamIntervals = MergeIntervals(axisGroup
                    .Select(anchor => new ElevationInterval(
                        anchor.TopElevation - anchor.BeamDepth,
                        anchor.TopElevation))
                    .OrderBy(interval => interval.Bottom));
                var axisX = axisGroup.First().AxisX;
                foreach (var faceX in new[] { axisX - halfThickness, axisX + halfThickness })
                {
                    var cursor = bottom;
                    foreach (var interval in beamIntervals)
                    {
                        if (interval.Bottom > cursor + 0.001)
                        {
                            AddWallSegment(lines, faceX, cursor, interval.Bottom);
                        }
                        cursor = Math.Max(cursor, interval.Top);
                    }
                    if (top > cursor + 0.001)
                    {
                        AddWallSegment(lines, faceX, cursor, top);
                    }
                }
            }
        }

        private static IList<ElevationInterval> MergeIntervals(IEnumerable<ElevationInterval> source)
        {
            var result = new List<ElevationInterval>();
            foreach (var interval in source)
            {
                var last = result.LastOrDefault();
                if (last == null || interval.Bottom > last.Top + 0.001)
                {
                    result.Add(new ElevationInterval(interval.Bottom, interval.Top));
                }
                else
                {
                    last.Top = Math.Max(last.Top, interval.Top);
                }
            }
            return result;
        }

        private static void AddWallSegment(
            ICollection<DrawingLine> lines,
            double x,
            double bottom,
            double top)
        {
            lines.Add(new DrawingLine(
                new Point2D(x, bottom),
                new Point2D(x, top),
                StairLineRole.WallBoundary,
                false,
                "WALL"));
        }

        private static void AddFloorText(
            ICollection<DrawingText> texts,
            StairFloorDefinition floor,
            ComponentPosition position,
            double? textX = null)
        {
            texts.Add(new DrawingText(
                new Point2D(textX ?? position.X, position.Elevation + 150.0),
                floor.Id + "  " + FormatElevation(position.Elevation),
                90.0));
        }

        private static void AddRectangleBoundary(
            ICollection<DrawingLine> lines,
            double startX,
            double endX,
            double topElevation,
            double thickness,
            StairLineRole role,
            bool isHidden,
            string componentId)
        {
            var bottom = topElevation - thickness;
            lines.Add(new DrawingLine(new Point2D(startX, topElevation), new Point2D(endX, topElevation), role, isHidden, componentId));
            lines.Add(new DrawingLine(new Point2D(endX, topElevation), new Point2D(endX, bottom), role, isHidden, componentId));
            lines.Add(new DrawingLine(new Point2D(endX, bottom), new Point2D(startX, bottom), role, isHidden, componentId));
            lines.Add(new DrawingLine(new Point2D(startX, bottom), new Point2D(startX, topElevation), role, isHidden, componentId));
        }

        private static void AddBeam(
            ICollection<DrawingLine> lines,
            double connectionX,
            double topElevation,
            double width,
            double depth,
            int structureDirection,
            string componentId)
        {
            var left = structureDirection < 0 ? connectionX - width : connectionX;
            var right = structureDirection < 0 ? connectionX : connectionX + width;
            AddRectangleBoundary(
                lines,
                left,
                right,
                topElevation,
                depth,
                StairLineRole.BeamBoundary,
                false,
                componentId);
        }

        private static string FormatElevation(double elevation)
        {
            if (Math.Abs(elevation) < 0.001)
            {
                return "±0.000";
            }
            return elevation > 0.0
                ? "+" + (elevation / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)
                : (elevation / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
        }

        private sealed class ComponentPosition
        {
            public ComponentPosition(double x, double elevation)
            {
                X = x;
                Elevation = elevation;
            }

            public double X { get; }

            public double Elevation { get; }
        }

        private sealed class WallAnchor
        {
            public WallAnchor(double axisX, double topElevation, double beamDepth)
            {
                AxisX = axisX;
                TopElevation = topElevation;
                BeamDepth = beamDepth;
            }

            public double AxisX { get; }

            public double TopElevation { get; }

            public double BeamDepth { get; }
        }

        private sealed class ElevationInterval
        {
            public ElevationInterval(double bottom, double top)
            {
                Bottom = bottom;
                Top = top;
            }

            public double Bottom { get; }

            public double Top { get; set; }
        }
    }
}
