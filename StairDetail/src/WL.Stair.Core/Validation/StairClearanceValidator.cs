using System;
using System.Collections.Generic;
using System.Linq;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Geometry;

namespace WL.Stair.Core.Validation
{
    /// <summary>
    /// Checks the vertical clear height in the generated stair section.
    /// The measurement is made from walking surfaces to the nearest overhead
    /// structural projection, matching GB 55031-2022 5.3.7 and
    /// GB 50352-2019 6.8.6.
    /// </summary>
    public sealed class StairClearanceValidator
    {
        private const double FlightMinimum = 2200.0;
        private const double PlatformMinimum = 2000.0;
        private const double Tolerance = 0.01;
        private const double MinimumObstructionGap = 500.0;

        public IReadOnlyList<ValidationIssue> Validate(
            StairProjectDefinition project,
            DrawingView section)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (section == null) throw new ArgumentNullException(nameof(section));

            var lines = section.Lines ?? new DrawingLine[0];
            var results = new List<ClearanceResult>();
            foreach (var storey in project.Storeys ?? new List<StairStoreyDefinition>())
            {
                if (storey == null) continue;
                foreach (var flight in storey.Flights ?? new List<StairFlightDefinition>())
                {
                    if (flight == null || string.IsNullOrWhiteSpace(flight.Id)) continue;
                    MeasureFlight(lines, flight, results);
                }
                foreach (var landing in storey.Landings ?? new List<StairLandingDefinition>())
                {
                    if (landing == null || string.IsNullOrWhiteSpace(landing.Id)) continue;
                    MeasurePlatform(lines, landing.Id, "休息平台及其上下过道", results);
                }
            }
            foreach (var floor in project.Floors ?? new List<StairFloorDefinition>())
            {
                if (floor == null || string.IsNullOrWhiteSpace(floor.Id)) continue;
                MeasurePlatform(lines, floor.Id, "楼层平台及其上下过道", results);
            }

            return results
                .GroupBy(item => item.ComponentId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(item => item.Clearance).First())
                .Where(item => item.Clearance + Tolerance < item.Required)
                .Select(item => new ValidationIssue(
                    item.IsFlight ? "WL-GB-CLR-2200" : "WL-GB-CLR-2000",
                    ValidationSeverity.Warning,
                    item.ComponentId,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0}实测净高约{1:0}mm，小于{2:0}mm；应符合《民用建筑通用规范》GB 55031-2022第5.3.7条及《民用建筑设计统一标准》GB 50352-2019第6.8.6条。",
                        item.Description,
                        item.Clearance,
                        item.Required)))
                .ToArray();
        }

        private static void MeasureFlight(
            IReadOnlyList<DrawingLine> lines,
            StairFlightDefinition flight,
            ICollection<ClearanceResult> results)
        {
            var maximumTreadLength = Math.Max(1.0, flight.TreadDepth) * 1.15;
            var treads = lines.Where(line =>
                string.Equals(line.ComponentId, flight.Id, StringComparison.OrdinalIgnoreCase)
                && IsFlightProfile(line.Role)
                && IsHorizontal(line)
                && Math.Abs(line.End.X - line.Start.X) <= maximumTreadLength + Tolerance
                && HasRiserBelowAtStart(lines, line))
                .ToArray();
            foreach (var tread in treads)
            {
                // Sampling just behind the nosing avoids a coincident riser,
                // while remaining on the actual walking surface.
                var x = (tread.Start.X + tread.End.X) / 2.0;
                AddMeasurement(lines, flight.Id, "梯段", true,
                    x, tread.Start.Y, FlightMinimum, results, tread.IsHidden, null);
            }
            if (treads.Length == 0) return;
            var lowest = treads.OrderBy(line => line.Start.Y).First();
            var highest = treads.OrderByDescending(line => line.Start.Y).First();
            var direction = Math.Sign(lowest.End.X - lowest.Start.X);
            if (direction == 0) return;
            // Both cited clauses include 0.30m beyond the lowest and highest
            // tread nosings in the flight-clearance measurement zone.
            AddMeasurement(lines, flight.Id, "梯段最低踏步前缘外300mm范围", true,
                lowest.Start.X - (direction * 300.0), lowest.Start.Y,
                FlightMinimum, results, lowest.IsHidden, null);
            AddMeasurement(lines, flight.Id, "梯段最高踏步前缘外300mm范围", true,
                highest.End.X + (direction * 300.0), highest.End.Y,
                FlightMinimum, results, highest.IsHidden, null);
        }

        private static bool HasRiserBelowAtStart(
            IReadOnlyList<DrawingLine> lines,
            DrawingLine tread)
        {
            return lines.Any(line =>
                string.Equals(line.ComponentId, tread.ComponentId,
                    StringComparison.OrdinalIgnoreCase)
                && IsFlightProfile(line.Role)
                && IsVertical(line)
                && (PointsEqual(line.Start, tread.Start) && line.End.Y < tread.Start.Y - Tolerance
                    || PointsEqual(line.End, tread.Start) && line.Start.Y < tread.Start.Y - Tolerance));
        }

        private static bool PointsEqual(Point2D first, Point2D second)
        {
            return Math.Abs(first.X - second.X) <= Tolerance
                && Math.Abs(first.Y - second.Y) <= Tolerance;
        }

        private static void MeasurePlatform(
            IReadOnlyList<DrawingLine> lines,
            string componentId,
            string description,
            ICollection<ClearanceResult> results)
        {
            var candidates = lines.Where(line =>
                string.Equals(line.ComponentId, componentId, StringComparison.OrdinalIgnoreCase)
                && line.Role == StairLineRole.CutBoundary
                && IsHorizontal(line))
                .ToArray();
            if (candidates.Length == 0) return;
            var top = candidates.Max(line => line.Start.Y);
            foreach (var surface in candidates.Where(line => Math.Abs(line.Start.Y - top) <= Tolerance))
            {
                var x = (surface.Start.X + surface.End.X) / 2.0;
                AddMeasurement(lines, componentId, description, false,
                    x, top, PlatformMinimum, results, null,
                    ComponentFamily(componentId));
            }
        }

        private static void AddMeasurement(
            IReadOnlyList<DrawingLine> lines,
            string componentId,
            string description,
            bool isFlight,
            double x,
            double walkingElevation,
            double required,
            ICollection<ClearanceResult> results,
            bool? flightHidden,
            string platformFamily)
        {
            var overhead = lines
                .Where(line => !string.Equals(line.ComponentId, componentId,
                    StringComparison.OrdinalIgnoreCase)
                    && IsOverheadStructure(line.Role)
                    && !IsVertical(line)
                    && ContainsX(line, x)
                    && IsRelevantOverhead(line, isFlight, flightHidden, platformFamily))
                .Select(line => ElevationAt(line, x))
                // Connected risers and arrival-platform edges can occupy the
                // same projected X within one tread height. They are walking
                // surface continuations, not overhead projections.
                .Where(y => y > walkingElevation + MinimumObstructionGap)
                .DefaultIfEmpty(double.PositiveInfinity)
                .Min();
            if (double.IsInfinity(overhead)) return;
            results.Add(new ClearanceResult(componentId, description, isFlight,
                overhead - walkingElevation, required));
        }

        private static bool IsRelevantOverhead(DrawingLine line, bool isFlight,
            bool? flightHidden, string platformFamily)
        {
            if (isFlight)
            {
                // Front and rear runs overlap only in the 2D projection but
                // occupy different plan lanes. Comparing the two would create
                // a false near-zero clearance at every projected crossing.
                if (IsFlightProfile(line.Role)) return line.IsHidden == flightHidden.GetValueOrDefault();
                return line.Role == StairLineRole.CutBoundary
                    || line.Role == StairLineRole.BeamBoundary
                    || line.Role == StairLineRole.StructuralEdge;
            }

            // Floors are checked against floors above, and intermediate
            // landings against intermediate landings above. A floor and a
            // landing can overlap in elevation projection while being on
            // opposite sides of the stairwell, so they must not be paired.
            return line.Role == StairLineRole.CutBoundary
                && string.Equals(ComponentFamily(line.ComponentId), platformFamily,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string ComponentFamily(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId)) return string.Empty;
            if (componentId.StartsWith("LB-", StringComparison.OrdinalIgnoreCase)) return "LB";
            if (componentId.StartsWith("PT-", StringComparison.OrdinalIgnoreCase)) return "PT";
            return string.Empty;
        }

        private static bool IsFlightProfile(StairLineRole role)
        {
            return role == StairLineRole.CutFlightProfile
                || role == StairLineRole.SectionProfile;
        }

        private static bool IsOverheadStructure(StairLineRole role)
        {
            return role == StairLineRole.CutFlightProfile
                || role == StairLineRole.SectionProfile
                || role == StairLineRole.CutBoundary
                || role == StairLineRole.BeamBoundary
                || role == StairLineRole.StructuralEdge;
        }

        private static bool IsHorizontal(DrawingLine line)
        {
            return Math.Abs(line.Start.Y - line.End.Y) <= Tolerance;
        }

        private static bool IsVertical(DrawingLine line)
        {
            return Math.Abs(line.Start.X - line.End.X) <= Tolerance;
        }

        private static bool ContainsX(DrawingLine line, double x)
        {
            return x >= Math.Min(line.Start.X, line.End.X) - Tolerance
                && x <= Math.Max(line.Start.X, line.End.X) + Tolerance;
        }

        private static double ElevationAt(DrawingLine line, double x)
        {
            var width = line.End.X - line.Start.X;
            if (Math.Abs(width) <= Tolerance) return double.PositiveInfinity;
            var factor = (x - line.Start.X) / width;
            return line.Start.Y + factor * (line.End.Y - line.Start.Y);
        }

        private sealed class ClearanceResult
        {
            public ClearanceResult(string componentId, string description,
                bool isFlight, double clearance, double required)
            {
                ComponentId = componentId;
                Description = description;
                IsFlight = isFlight;
                Clearance = clearance;
                Required = required;
            }

            public string ComponentId { get; private set; }
            public string Description { get; private set; }
            public bool IsFlight { get; private set; }
            public double Clearance { get; private set; }
            public double Required { get; private set; }
        }
    }
}
