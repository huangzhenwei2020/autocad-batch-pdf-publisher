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
        private const double WallHeightAboveHighestFloor = 1800.0;
        private const double AxisExtensionBeyondWall = 200.0;
        private const double BaseWallThickness = 100.0;

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
            var dimensions = new List<DrawingDimension>();
            var tables = new List<DrawingTable>();
            var hatchRegions = new List<DrawingHatchRegion>();
            var leaders = new List<DrawingLeader>();
            var horizontalDimensionSpecs = new List<HorizontalDimensionSpec>();
            var floors = project.Floors.ToDictionary(floor => floor.Id, StringComparer.OrdinalIgnoreCase);
            var storeyResults = calculation.Storeys.ToDictionary(result => result.Id, StringComparer.OrdinalIgnoreCase);
            var floorPositions = new Dictionary<string, ComponentPosition>(StringComparer.OrdinalIgnoreCase);
            var wallAnchors = new List<WallAnchor>();
            var lowestElevation = calculation.Storeys.Min(result => result.LowerElevation);
            // The section uses an immutable axis coordinate system. The left
            // axis is always X=0 and the right axis is always the stairwell
            // depth, regardless of the first flight direction or platform
            // widths. Mirroring therefore moves only stair components.
            var firstAxisX = 0.0;
            var secondAxisX = project.Construction.StairwellDepth;

            Func<int, double> axisForDirection = direction =>
                direction > 0 ? firstAxisX : secondAxisX;
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
                    object sourceBoundary = index == 0
                        ? (object)lowerFloor
                        : storey.Landings[index - 1];
                    object destinationBoundary = index < storey.Landings.Count
                        ? (object)storey.Landings[index]
                        : upperFloor;
                    var destinationDirection = -(int)flight.Direction;
                    var destinationWidth = index < storey.Landings.Count
                        ? storey.Landings[index].PlatformWidth
                        : upperFloor == null ? 0.0 : upperFloor.PlatformWidth;
                    var boundaryConnectionX = connectionForBoundary(destinationDirection, destinationWidth);
                    // If both sides of one flight permit closure, the lower
                    // boundary wins: keep its position and close at the upper
                    // destination. This makes bottom-up editing deterministic.
                    var allowEndClosure = AllowsLowerFlightClosure(destinationBoundary);
                    var allowStartClosure = !allowEndClosure
                        && AllowsUpperFlightClosure(sourceBoundary);
                    var flightStartX = allowStartClosure
                        ? boundaryConnectionX - (direction * flightResult.HorizontalRun)
                        : currentX;
                    var flightEndX = flightStartX + (direction * flightResult.HorizontalRun);
                    var startClosureGap = direction * (flightStartX - currentX);
                    var endClosureGap = direction * (boundaryConnectionX - flightEndX);
                    if ((allowStartClosure && startClosureGap < -0.01)
                        || (allowEndClosure && endClosureGap < -0.01))
                    {
                        throw new InvalidOperationException(
                            storey.Id + " 的梯段补齐距离不能为负，请调整踏步或平台宽度。");
                    }
                    if (!allowEndClosure
                        && !allowStartClosure
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
                    var sourceSlabThickness = index > 0
                        ? storey.Landings[index - 1].SlabThicknessOverride
                            ?? project.Construction.LandingSlabThickness
                        : lowerFloor == null
                            ? project.Construction.FloorSlabThickness
                            : lowerFloor.SlabThicknessOverride
                                ?? project.Construction.FloorSlabThickness;
                    AddFlightBoundary(
                        lines,
                        flightStartX,
                        currentElevation,
                        flightResult,
                        direction,
                        currentX,
                        sourceSlabThickness,
                        allowStartClosure,
                        boundaryConnectionX,
                        destinationSlabThickness,
                        allowEndClosure,
                        isHidden,
                        Math.Abs(currentElevation - lowestElevation) < 0.001,
                        flight.Id);

                    var endElevation = currentElevation + flightResult.VerticalRise;
                    AddHandrail(
                        lines,
                        flightStartX,
                        flightEndX,
                        currentElevation,
                        endElevation,
                        flightResult,
                        direction,
                        currentX,
                        allowStartClosure,
                        boundaryConnectionX,
                        allowEndClosure,
                        project.Construction.Railing);
                    horizontalDimensionSpecs.Add(new HorizontalDimensionSpec(
                        flight.Id,
                        Math.Min(flightStartX, flightEndX),
                        Math.Max(flightStartX, flightEndX),
                        currentElevation,
                        endElevation,
                        flightResult.TreadDepth,
                        flight.RiserCount));
                    var scale = Math.Max(1, project.DrawingScale);
                    dimensions.Add(new DrawingDimension(
                        new Point2D(firstAxisX, currentElevation),
                        new Point2D(firstAxisX, endElevation),
                        new Point2D(firstAxisX - (6.0 * scale), (currentElevation + endElevation) / 2.0),
                        FormatMillimeter(flightResult.RiserHeight) + "×" + flight.RiserCount
                            + "=" + FormatMillimeter(flightResult.VerticalRise),
                        flight.Id));
                    if (project.InsertComponentSchedule)
                        texts.Add(new DrawingText(
                            new Point2D((flightStartX + flightEndX) / 2.0, (currentElevation + endElevation) / 2.0 + 180.0),
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
                            -destinationDirection,
                            project.InsertComponentSchedule);
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
                    if (project.InsertComponentSchedule)
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
                AddFloor(lines, texts, floor, position, project.Construction, floorDirection,
                    project.InsertComponentSchedule);
            }

            var drawingScale = Math.Max(1, project.DrawingScale);
            foreach (var storeyResult in calculation.Storeys)
            {
                var middle = (storeyResult.LowerElevation + storeyResult.UpperElevation) / 2.0;
                dimensions.Add(new DrawingDimension(
                    new Point2D(firstAxisX, storeyResult.LowerElevation),
                    new Point2D(firstAxisX, storeyResult.UpperElevation),
                    new Point2D(firstAxisX - (11.0 * drawingScale), middle),
                    FormatMillimeter(storeyResult.UpperElevation - storeyResult.LowerElevation),
                    storeyResult.Id));
                dimensions.Add(new DrawingDimension(
                    new Point2D(secondAxisX, storeyResult.LowerElevation),
                    new Point2D(secondAxisX, storeyResult.UpperElevation),
                    new Point2D(secondAxisX + (11.0 * drawingScale), middle),
                    FormatMillimeter(storeyResult.UpperElevation - storeyResult.LowerElevation),
                    storeyResult.Id));
            }

            var highestElevation = calculation.Storeys.Max(result => result.UpperElevation);
            AddStairwellWalls(
                lines,
                wallAnchors,
                lowestElevation,
                highestElevation,
                project.Construction);
            AddBaseWall(lines, firstAxisX, secondAxisX, lowestElevation, drawingScale,
                project.Construction);
            AddTopBreakLine(lines, firstAxisX, secondAxisX,
                highestElevation + WallHeightAboveHighestFloor,
                drawingScale,
                project.Construction);
            AddStairwellAxisLines(
                lines,
                firstAxisX,
                secondAxisX,
                lowestElevation - AxisExtensionBeyondWall,
                highestElevation + WallHeightAboveHighestFloor + AxisExtensionBeyondWall);
            AddHorizontalDimensions(
                dimensions,
                horizontalDimensionSpecs,
                firstAxisX,
                secondAxisX,
                drawingScale);
            AddOneHandrailHeightDimension(dimensions, lines, project.Construction.Railing, drawingScale,
                firstAxisX, secondAxisX);
            AddTopGuardrail(lines, leaders, highestElevation, drawingScale,
                project.Construction.Railing);

            var titleX = floorPositions.Count == 0 ? 0.0 : floorPositions.Values.Average(position => position.X);
            var titleY = lowestElevation - 650.0;
            var drawingTitle = new DrawingTitle(
                new Point2D(titleX, titleY),
                (string.IsNullOrWhiteSpace(project.StairNumber) ? string.Empty : project.StairNumber + " ")
                    + "楼梯大样",
                drawingScale,
                Math.Abs(secondAxisX - firstAxisX));
            if (project.InsertComponentSchedule)
            {
                tables.Add(BuildComponentSchedule(
                    project,
                    calculation,
                    new Point2D(secondAxisX + (24.0 * drawingScale), highestElevation)));
            }
            // Regions are derived before coincident cut edges are merged.  This
            // preserves each component's complete closed boundary; the visible
            // linework below is still merged to one line per shared edge.
            hatchRegions.AddRange(BuildStructuralHatchRegions(lines, project.Construction.SectionHatch));
            hatchRegions.AddRange(BuildWallHatchRegions(lines, project.Construction.WallHatch));
            var mergedLines = MergeConnectedCutOutlines(lines).ToArray();
            var alignedDimensions = AlignDimensionsToOuterOutline(
                mergedLines,
                dimensions,
                drawingScale);
            return RebaseSectionToLeftAxisLowerPoint(
                mergedLines,
                texts,
                alignedDimensions,
                tables,
                hatchRegions,
                drawingScale,
                firstAxisX,
                lowestElevation,
                drawingTitle,
                leaders);
        }

        private static IEnumerable<DrawingDimension> AlignDimensionsToOuterOutline(
            IEnumerable<DrawingLine> sourceLines,
            IEnumerable<DrawingDimension> sourceDimensions,
            int scale)
        {
            var geometry = sourceLines
                .Where(line => line.Role != StairLineRole.AxisLine
                    && line.Role != StairLineRole.Handrail
                    && line.Role != StairLineRole.BreakLine
                    && line.Role != StairLineRole.HatchBoundary)
                .ToArray();
            if (geometry.Length == 0) return sourceDimensions.ToArray();
            var leftOutline = geometry.SelectMany(line => new[] { line.Start.X, line.End.X }).Min();
            var rightOutline = geometry.SelectMany(line => new[] { line.Start.X, line.End.X }).Max();
            return sourceDimensions.Select(dimension =>
            {
                if (dimension.Orientation == DrawingDimensionOrientation.Horizontal
                    || string.Equals(dimension.ComponentId, "RAILING", StringComparison.OrdinalIgnoreCase))
                    return dimension;
                var isLeft = dimension.DimensionLinePoint.X
                    < (dimension.FirstExtensionOrigin.X + dimension.SecondExtensionOrigin.X) / 2.0;
                var paperOffset = Math.Abs(
                    dimension.DimensionLinePoint.X - dimension.FirstExtensionOrigin.X)
                    / Math.Max(1, scale);
                // One side uses one fixed outermost profile baseline. This
                // keeps every extension origin co-linear and gives the inner
                // and outer dimension rows equal extension lengths.
                var firstX = isLeft ? leftOutline : rightOutline;
                var secondX = firstX;
                var dimensionX = isLeft
                    ? leftOutline - (paperOffset * Math.Max(1, scale))
                    : rightOutline + (paperOffset * Math.Max(1, scale));
                return new DrawingDimension(
                    new Point2D(firstX, dimension.FirstExtensionOrigin.Y),
                    new Point2D(secondX, dimension.SecondExtensionOrigin.Y),
                    new Point2D(dimensionX, dimension.DimensionLinePoint.Y),
                    dimension.TextOverride,
                    dimension.ComponentId,
                    dimension.Orientation);
            }).ToArray();
        }

        private static void AddHorizontalDimensions(
            ICollection<DrawingDimension> dimensions,
            IEnumerable<HorizontalDimensionSpec> source,
            double firstAxisX,
            double secondAxisX,
            int scale)
        {
            var placedAtElevation = new Dictionary<double, int>();
            foreach (var spec in source
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => Math.Min(item.StartElevation, item.EndElevation)))
            {
                var key = Math.Round(Math.Min(spec.StartElevation, spec.EndElevation), 3);
                int row;
                placedAtElevation.TryGetValue(key, out row);
                placedAtElevation[key] = row + 1;
                var anchorElevation = Math.Min(spec.StartElevation, spec.EndElevation);
                // 除层高外，所有标注均以 1:1 下 6mm 的尺寸界线长度布置。
                // 同一标高只保留一组横向尺寸链，因此不再额外外移分层。
                var dimensionElevation = anchorElevation - (6.0 * Math.Max(1, scale));
                var leftLength = spec.LeftEdge - firstAxisX;
                var rightLength = secondAxisX - spec.RightEdge;
                if (leftLength > 0.001)
                {
                    dimensions.Add(new DrawingDimension(
                        new Point2D(firstAxisX, anchorElevation),
                        new Point2D(spec.LeftEdge, anchorElevation),
                        new Point2D((firstAxisX + spec.LeftEdge) / 2.0, dimensionElevation),
                        FormatMillimeter(leftLength),
                        spec.ComponentId,
                        DrawingDimensionOrientation.Horizontal));
                }
                dimensions.Add(new DrawingDimension(
                    new Point2D(spec.LeftEdge, anchorElevation),
                    new Point2D(spec.RightEdge, anchorElevation),
                    new Point2D((spec.LeftEdge + spec.RightEdge) / 2.0, dimensionElevation),
                    FormatMillimeter(spec.TreadDepth) + "×" + Math.Max(0, spec.RiserCount - 1)
                        + "=" + FormatMillimeter(spec.RightEdge - spec.LeftEdge),
                    spec.ComponentId,
                    DrawingDimensionOrientation.Horizontal));
                if (rightLength > 0.001)
                {
                    dimensions.Add(new DrawingDimension(
                        new Point2D(spec.RightEdge, anchorElevation),
                        new Point2D(secondAxisX, anchorElevation),
                        new Point2D((spec.RightEdge + secondAxisX) / 2.0, dimensionElevation),
                        FormatMillimeter(rightLength),
                        spec.ComponentId,
                        DrawingDimensionOrientation.Horizontal));
                }
            }
        }

        private static void AddOneHandrailHeightDimension(
            ICollection<DrawingDimension> dimensions,
            IEnumerable<DrawingLine> lines,
            RailingDefaults railing,
            int scale,
            double firstAxisX,
            double secondAxisX)
        {
            if (railing == null || !railing.Enabled || railing.Height <= 0.0) return;
            var center = (firstAxisX + secondAxisX) / 2.0;
            var post = lines
                .Where(line => line.Role == StairLineRole.Handrail
                    && Math.Abs(line.Start.X - line.End.X) < 0.001
                    && Math.Abs(Math.Abs(line.End.Y - line.Start.Y) - railing.Height) < 0.001)
                .OrderBy(line => Math.Abs(line.Start.X - center))
                .FirstOrDefault();
            if (post == null) return;
            var bottom = post.Start.Y < post.End.Y ? post.Start : post.End;
            var top = post.Start.Y < post.End.Y ? post.End : post.Start;
            // 扶手高度属于内层标注，尺寸界线长度与梯段、横向尺寸统一为 6mm。
            var lineX = bottom.X < center
                ? bottom.X + (6.0 * Math.Max(1, scale))
                : bottom.X - (6.0 * Math.Max(1, scale));
            dimensions.Add(new DrawingDimension(
                bottom,
                top,
                new Point2D(lineX, (bottom.Y + top.Y) / 2.0),
                FormatMillimeter(railing.Height),
                "RAILING"));
        }

        private static double FindOuterOutlineAtElevation(
            IEnumerable<DrawingLine> lines,
            double elevation,
            bool findLeft,
            double fallback)
        {
            const double tolerance = 0.001;
            var intersections = new List<double>();
            foreach (var line in lines)
            {
                var bottom = Math.Min(line.Start.Y, line.End.Y);
                var top = Math.Max(line.Start.Y, line.End.Y);
                if (elevation < bottom - tolerance || elevation > top + tolerance) continue;
                var deltaY = line.End.Y - line.Start.Y;
                if (Math.Abs(deltaY) < tolerance)
                {
                    if (Math.Abs(elevation - line.Start.Y) > tolerance) continue;
                    intersections.Add(line.Start.X);
                    intersections.Add(line.End.X);
                    continue;
                }
                var factor = (elevation - line.Start.Y) / deltaY;
                if (factor < -tolerance || factor > 1.0 + tolerance) continue;
                intersections.Add(line.Start.X + factor * (line.End.X - line.Start.X));
            }
            if (intersections.Count == 0) return fallback;
            return findLeft ? intersections.Min() : intersections.Max();
        }

        private static DrawingView RebaseSectionToLeftAxisLowerPoint(
            IEnumerable<DrawingLine> lines,
            IEnumerable<DrawingText> texts,
            IEnumerable<DrawingDimension> dimensions,
            IEnumerable<DrawingTable> tables,
            IEnumerable<DrawingHatchRegion> hatchRegions,
            int scale,
            double leftAxisX,
            double lowestElevation,
            DrawingTitle title,
            IEnumerable<DrawingLeader> leaders)
        {
            Func<Point2D, Point2D> translate = point => new Point2D(
                point.X - leftAxisX,
                point.Y - lowestElevation);
            var rebasedLines = lines.Select(line => new DrawingLine(
                translate(line.Start),
                translate(line.End),
                line.Role,
                line.IsHidden,
                line.ComponentId));
            var rebasedTexts = texts.Select(text => new DrawingText(
                translate(text.Position),
                text.Content,
                text.Height));
            var rebasedDimensions = dimensions.Select(dimension => new DrawingDimension(
                translate(dimension.FirstExtensionOrigin),
                translate(dimension.SecondExtensionOrigin),
                translate(dimension.DimensionLinePoint),
                dimension.TextOverride,
                dimension.ComponentId,
                dimension.Orientation));
            var rebasedTables = tables.Select(table => new DrawingTable(
                translate(table.Position),
                table.RowHeight,
                table.ColumnWidths,
                table.Rows));
            var rebasedHatches = hatchRegions.Select(region => new DrawingHatchRegion(
                region.Boundary.Select(translate),
                region.ComponentId,
                region.IsWall,
                region.PatternName,
                region.PatternScale));
            var rebasedTitle = title == null ? null : new DrawingTitle(
                translate(title.Position), title.Text, title.Scale, title.TargetWidth);
            var rebasedLeaders = leaders.Select(leader => new DrawingLeader(
                leader.Vertices.Select(translate), leader.Text, leader.TextHeight));
            return new DrawingView(
                "ProjectSection",
                rebasedLines,
                rebasedTexts,
                rebasedDimensions,
                rebasedTables,
                scale,
                rebasedHatches,
                rebasedTitle,
                rebasedLeaders);
        }

        private static IEnumerable<DrawingHatchRegion> BuildStructuralHatchRegions(
            IEnumerable<DrawingLine> source,
            SectionHatchDefaults hatch)
        {
            if (hatch == null || !hatch.Enabled) return Enumerable.Empty<DrawingHatchRegion>();
            var cutLines = source
                .Where(line => !line.IsHidden
                    && (line.Role == StairLineRole.CutBoundary
                        || line.Role == StairLineRole.CutFlightProfile
                        || line.Role == StairLineRole.HatchBoundary)
                    && !string.IsNullOrWhiteSpace(line.ComponentId))
                .ToArray();
            // Treat every touching foreground flight, platform and floor as
            // one graph. Shared edges cancel before loop tracing, so both the
            // hatch and the inward bold offset use only the combined perimeter.
            var unionBoundary = MergeConnectedCutOutlines(cutLines);
            return TraceClosedLoops(unionBoundary)
                .Where(loop => Math.Abs(PolygonArea(loop.Points)) > 1.0)
                .Select(loop => new DrawingHatchRegion(loop.Points, loop.ComponentId, false,
                    hatch.PatternName, hatch.PatternScale));
        }

        private static IEnumerable<DrawingHatchRegion> BuildWallHatchRegions(
            IEnumerable<DrawingLine> source,
            SectionHatchDefaults hatch)
        {
            if (hatch == null || !hatch.Enabled) return Enumerable.Empty<DrawingHatchRegion>();
            var walls = source.Where(line => line.Role == StairLineRole.WallBoundary && !line.IsHidden).ToArray();
            var regions = new List<DrawingHatchRegion>();
            foreach (var interval in walls.GroupBy(line => Math.Round(Math.Min(line.Start.Y, line.End.Y), 3)
                    + ":" + Math.Round(Math.Max(line.Start.Y, line.End.Y), 3)))
            {
                var sides = interval.OrderBy(line => line.Start.X).ToArray();
                for (var index = 0; index + 1 < sides.Length; index += 2)
                {
                    var left = sides[index];
                    var right = sides[index + 1];
                    var bottom = Math.Min(left.Start.Y, left.End.Y);
                    var top = Math.Max(left.Start.Y, left.End.Y);
                    if (right.Start.X - left.Start.X < 0.001 || top - bottom < 0.001) continue;
                    regions.Add(new DrawingHatchRegion(new[]
                    {
                        new Point2D(left.Start.X, bottom), new Point2D(right.Start.X, bottom),
                        new Point2D(right.Start.X, top), new Point2D(left.Start.X, top)
                    }, "WALL", true, hatch.PatternName, hatch.PatternScale));
                }
            }
            return regions;
        }

        private static IEnumerable<TracedLoop> TraceClosedLoops(IEnumerable<DrawingLine> source)
        {
            var remaining = source.ToList();
            while (remaining.Count > 0)
            {
                var first = remaining[0];
                remaining.RemoveAt(0);
                var points = new List<Point2D> { first.Start, first.End };
                var componentId = first.ComponentId;
                var current = first.End;
                while (!SamePoint(current, points[0]))
                {
                    var nextIndex = remaining.FindIndex(line => SamePoint(line.Start, current) || SamePoint(line.End, current));
                    if (nextIndex < 0) break;
                    var next = remaining[nextIndex];
                    remaining.RemoveAt(nextIndex);
                    current = SamePoint(next.Start, current) ? next.End : next.Start;
                    points.Add(current);
                    if (points.Count > 2048) break;
                }
                if (points.Count > 3 && SamePoint(points[0], points[points.Count - 1]))
                {
                    points.RemoveAt(points.Count - 1);
                    yield return new TracedLoop(points, componentId);
                }
            }
        }

        private static double PolygonArea(IReadOnlyList<Point2D> points)
        {
            var area = 0.0;
            for (var index = 0; index < points.Count; index++)
            {
                var next = points[(index + 1) % points.Count];
                area += points[index].X * next.Y - next.X * points[index].Y;
            }
            return area / 2.0;
        }

        private static bool SamePoint(Point2D first, Point2D second)
        {
            return Math.Abs(first.X - second.X) < 0.001 && Math.Abs(first.Y - second.Y) < 0.001;
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
                    || line.Role == StairLineRole.CutFlightProfile
                    || line.Role == StairLineRole.HatchBoundary);
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

        private static bool AllowsLowerFlightClosure(object boundary)
        {
            var floor = boundary as StairFloorDefinition;
            if (floor != null) return floor.AllowLowerFlightClosure;
            var landing = boundary as StairLandingDefinition;
            return landing != null && landing.AllowLowerFlightClosure;
        }

        private static bool AllowsUpperFlightClosure(object boundary)
        {
            var floor = boundary as StairFloorDefinition;
            if (floor != null) return floor.AllowUpperFlightClosure;
            var landing = boundary as StairLandingDefinition;
            return landing != null && landing.AllowUpperFlightClosure;
        }

        private static void AddHandrail(
            ICollection<DrawingLine> lines,
            double flightStartX,
            double flightEndX,
            double startElevation,
            double endElevation,
            StairProjectFlightResult flight,
            double direction,
            double sourceConnectionX,
            bool allowStartClosure,
            double destinationConnectionX,
            bool allowEndClosure,
            RailingDefaults railing)
        {
            if (railing == null || !railing.Enabled || railing.Height <= 0.0 || flight.TreadCount <= 0)
                return;
            // The sloping rail starts above the front upper corner of the first
            // tread, not above the source floor. Together with the arrival
            // vertex this produces the same rise/run pitch as the stair and
            // therefore keeps at least the configured vertical clearance over
            // every tread, even when adjacent flights are horizontally offset.
            var firstNosing = new Point2D(
                flightStartX,
                startElevation + flight.RiserHeight);
            var lastNosing = new Point2D(flightEndX, endElevation);
            var firstRail = new Point2D(firstNosing.X, firstNosing.Y + railing.Height);
            var lastRail = new Point2D(lastNosing.X, lastNosing.Y + railing.Height);
            if (allowStartClosure
                && direction * (flightStartX - sourceConnectionX) > 0.001)
            {
                lines.Add(new DrawingLine(
                    new Point2D(sourceConnectionX, startElevation + railing.Height),
                    firstRail,
                    StairLineRole.Handrail,
                    false,
                    "RAILING"));
            }
            lines.Add(new DrawingLine(firstNosing, firstRail, StairLineRole.Handrail, false, "RAILING"));
            if (!firstRail.Equals(lastRail))
                lines.Add(new DrawingLine(firstRail, lastRail, StairLineRole.Handrail, false, "RAILING"));
            lines.Add(new DrawingLine(lastNosing, lastRail, StairLineRole.Handrail, false, "RAILING"));
            if (allowEndClosure
                && direction * (destinationConnectionX - flightEndX) > 0.001)
            {
                lines.Add(new DrawingLine(
                    lastRail,
                    new Point2D(destinationConnectionX, endElevation + railing.Height),
                    StairLineRole.Handrail,
                    false,
                    "RAILING"));
            }
        }

        private static DrawingTable BuildComponentSchedule(
            StairProjectDefinition project,
            StairProjectCalculationResult calculation,
            Point2D position)
        {
            var scale = Math.Max(1, project.DrawingScale);
            var rows = new List<IEnumerable<string>>
            {
                new[] { "编号", "构件", "主要尺寸", "数量", "板厚", "梁/备注" }
            };
            var resultLookup = calculation.Storeys.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var storey in project.Storeys)
            {
                StairStoreyResult storeyResult;
                if (!resultLookup.TryGetValue(storey.Id, out storeyResult)) continue;
                for (var index = 0; index < storey.Flights.Count; index++)
                {
                    var flight = storey.Flights[index];
                    var result = storeyResult.Flights[index];
                    rows.Add(new[]
                    {
                        flight.Id,
                        "梯段",
                        FormatMillimeter(result.TreadDepth) + "×" + FormatMillimeter(result.RiserHeight),
                        result.TreadCount + "踏步/" + result.RiserCount + "级",
                        FormatMillimeter(flight.SlabThicknessOverride ?? project.Construction.FlightSlabThickness),
                        "净宽 " + FormatMillimeter(result.Width)
                    });
                }
                foreach (var landing in storey.Landings)
                {
                    rows.Add(new[]
                    {
                        landing.Id,
                        "休息平台",
                        "宽 " + FormatMillimeter(landing.PlatformWidth),
                        "1",
                        FormatMillimeter(landing.SlabThicknessOverride ?? project.Construction.LandingSlabThickness),
                        FormatMillimeter(landing.BeamWidthOverride ?? project.Construction.LandingBeam.Width)
                            + "×" + FormatMillimeter(landing.BeamDepthOverride ?? project.Construction.LandingBeam.Depth)
                    });
                }
            }
            foreach (var floor in project.Floors)
            {
                rows.Add(new[]
                {
                    floor.Id,
                    "楼板",
                    "宽 " + FormatMillimeter(floor.PlatformWidth),
                    "1",
                    FormatMillimeter(floor.SlabThicknessOverride ?? project.Construction.FloorSlabThickness),
                    FormatMillimeter(floor.BeamWidthOverride ?? project.Construction.FloorBeam.Width)
                        + "×" + FormatMillimeter(floor.BeamDepthOverride ?? project.Construction.FloorBeam.Depth)
                });
            }
            return new DrawingTable(
                position,
                7.0 * scale,
                new[] { 24.0, 24.0, 38.0, 30.0, 20.0, 34.0 }.Select(value => value * scale),
                rows);
        }

        private static void AddFlightBoundary(
            ICollection<DrawingLine> lines,
            double startX,
            double startElevation,
            StairProjectFlightResult flight,
            double direction,
            double sourceConnectionX,
            double sourceSlabThickness,
            bool allowStartClosure,
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
            var sourceGap = direction * (startX - sourceConnectionX);
            if (allowStartClosure && sourceGap > 0.001)
            {
                var sourceUndersideElevation = startElevation - sourceSlabThickness;
                lines.Add(new DrawingLine(
                    new Point2D(sourceConnectionX, startElevation),
                    new Point2D(startX, startElevation),
                    role,
                    isHidden,
                    componentId));
                lines.Add(new DrawingLine(
                    new Point2D(sourceConnectionX, startElevation),
                    new Point2D(sourceConnectionX, sourceUndersideElevation),
                    role,
                    isHidden,
                    componentId));
                lines.Add(new DrawingLine(
                    new Point2D(sourceConnectionX, sourceUndersideElevation),
                    outlineStart,
                    role,
                    isHidden,
                    componentId));
            }
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
            int drawingDirection,
            bool showText)
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
            if (showText)
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
            int logicalDirection,
            bool showText)
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
            if (showText)
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
            var top = highestElevation + WallHeightAboveHighestFloor;
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

        private static void AddBaseWall(
            ICollection<DrawingLine> lines,
            double firstAxisX,
            double secondAxisX,
            double lowestElevation,
            int drawingScale,
            StairConstructionDefaults defaults)
        {
            var wallThickness = defaults.Wall == null ? 0.0 : Math.Max(0.0, defaults.Wall.Thickness);
            var halfWall = wallThickness / 2.0;
            var extension = 4.0 * Math.Max(1, drawingScale);
            var left = Math.Min(firstAxisX, secondAxisX) - halfWall - extension;
            var right = Math.Max(firstAxisX, secondAxisX) + halfWall + extension;
            AddRectangleBoundary(lines, left, right, lowestElevation, BaseWallThickness,
                StairLineRole.HatchBoundary, false, "BASE-WALL");
            lines.Add(new DrawingLine(new Point2D(left, lowestElevation),
                new Point2D(right, lowestElevation), StairLineRole.StructuralEdge, false, "BASE-WALL-VISIBLE"));
            lines.Add(new DrawingLine(new Point2D(left, lowestElevation - BaseWallThickness),
                new Point2D(right, lowestElevation - BaseWallThickness), StairLineRole.StructuralEdge, false,
                "BASE-WALL-VISIBLE"));
        }

        private static void AddTopGuardrail(
            ICollection<DrawingLine> lines,
            ICollection<DrawingLeader> leaders,
            double highestElevation,
            int drawingScale,
            RailingDefaults railing)
        {
            if (railing == null || !railing.Enabled) return;
            var handrailPoints = lines
                .Where(line => line.Role == StairLineRole.Handrail)
                .SelectMany(line => new[] { line.Start, line.End })
                .ToArray();
            if (handrailPoints.Length == 0) return;
            var highestHandrailPoint = handrailPoints.OrderByDescending(point => point.Y).First();

            const double guardrailHeight = 1100.0;
            const double postWidth = 40.0;
            var x = highestHandrailPoint.X;
            var bottom = highestElevation;
            var top = highestElevation + guardrailHeight;
            var left = x - postWidth / 2.0;
            var right = x + postWidth / 2.0;
            lines.Add(new DrawingLine(new Point2D(left, bottom), new Point2D(left, top),
                StairLineRole.Handrail, false, "TOP-GUARDRAIL"));
            lines.Add(new DrawingLine(new Point2D(right, bottom), new Point2D(right, top),
                StairLineRole.Handrail, false, "TOP-GUARDRAIL"));
            lines.Add(new DrawingLine(new Point2D(left, top), new Point2D(right, top),
                StairLineRole.Handrail, false, "TOP-GUARDRAIL"));

            var scale = Math.Max(1, drawingScale);
            var target = new Point2D(x, top);
            var elbow = new Point2D(x - (6.0 * scale), top + (4.0 * scale));
            var textPoint = new Point2D(x - (13.0 * scale), elbow.Y);
            leaders.Add(new DrawingLeader(new[] { target, elbow, textPoint },
                "栏杆 H=1.1m", 3.5 * scale));
        }

        private static void AddTopBreakLine(
            ICollection<DrawingLine> lines,
            double firstAxisX,
            double secondAxisX,
            double elevation,
            int drawingScale,
            StairConstructionDefaults defaults)
        {
            var wallThickness = defaults.Wall == null ? 0.0 : Math.Max(0.0, defaults.Wall.Thickness);
            var halfWall = wallThickness / 2.0;
            var extension = 4.0 * Math.Max(1, drawingScale);
            var start = new Point2D(Math.Min(firstAxisX, secondAxisX) - halfWall - extension, elevation);
            var end = new Point2D(Math.Max(firstAxisX, secondAxisX) + halfWall + extension, elevation);
            var middle = new Point2D((start.X + end.X) / 2.0, elevation);

            // QZ折断线 uses a six-vertex polyline. Its four middle points are
            // obtained from the midpoint with the paired -104/+104 and
            // +76/-76 degree vectors. A fixed plotted seed length keeps the
            // break symbol legible without making its height depend on the
            // full stairwell span.
            var seedLength = 30.0 * Math.Max(1, drawingScale);
            var offset = seedLength * 0.115;
            var p3 = Polar(middle, -104.0, offset);
            var p2 = Polar(p3, 104.0, offset);
            var p4 = Polar(middle, 76.0, offset);
            var p5 = Polar(p4, -76.0, offset);
            var points = new[] { start, p2, p3, p4, p5, end };
            for (var index = 0; index + 1 < points.Length; index++)
                lines.Add(new DrawingLine(points[index], points[index + 1],
                    StairLineRole.BreakLine, false, "TOP-BREAK"));
        }

        private static Point2D Polar(Point2D origin, double angleDegrees, double distance)
        {
            var radians = angleDegrees * Math.PI / 180.0;
            return new Point2D(
                origin.X + Math.Cos(radians) * distance,
                origin.Y + Math.Sin(radians) * distance);
        }

        private static string FormatMillimeter(double value)
        {
            return Math.Abs(value - Math.Round(value)) < 0.05
                ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.0", CultureInfo.InvariantCulture);
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

        private sealed class TracedLoop
        {
            public TracedLoop(IEnumerable<Point2D> points, string componentId)
            {
                Points = points.ToArray();
                ComponentId = componentId ?? string.Empty;
            }

            public IReadOnlyList<Point2D> Points { get; }
            public string ComponentId { get; }
        }

        private sealed class HorizontalDimensionSpec
        {
            public HorizontalDimensionSpec(
                string componentId,
                double leftEdge,
                double rightEdge,
                double startElevation,
                double endElevation,
                double treadDepth,
                int riserCount)
            {
                ComponentId = componentId;
                LeftEdge = leftEdge;
                RightEdge = rightEdge;
                StartElevation = startElevation;
                EndElevation = endElevation;
                TreadDepth = treadDepth;
                RiserCount = riserCount;
                Key = string.Join("|", new[]
                {
                    Math.Round(leftEdge, 3).ToString(CultureInfo.InvariantCulture),
                    Math.Round(rightEdge - leftEdge, 3).ToString(CultureInfo.InvariantCulture),
                    Math.Round(treadDepth, 3).ToString(CultureInfo.InvariantCulture),
                    riserCount.ToString(CultureInfo.InvariantCulture)
                });
            }

            public string ComponentId { get; }

            public double LeftEdge { get; }

            public double RightEdge { get; }

            public double StartElevation { get; }

            public double EndElevation { get; }

            public double TreadDepth { get; }

            public int RiserCount { get; }

            public string Key { get; }
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
