using System;
using System.Collections.Generic;
using System.Linq;
using WL.Stair.Core.Domain;

namespace WL.Stair.Core.Calculation
{
    public sealed class StairProjectConstraintService
    {
        public void Normalize(StairProjectDefinition project)
        {
            if (project == null) return;
            if (project.Floors == null) project.Floors = new List<StairFloorDefinition>();
            if (project.Storeys == null) project.Storeys = new List<StairStoreyDefinition>();
            if (project.Construction == null) project.Construction = StairConstructionDefaults.CreateDefault();
            var defaults = StairConstructionDefaults.CreateDefault();
            if (project.Construction.StairwellWidth <= 0.0)
                project.Construction.StairwellWidth = defaults.StairwellWidth;
            if (project.Construction.StairwellDepth <= 0.0)
                project.Construction.StairwellDepth = defaults.StairwellDepth;
            if (project.Construction.Wall == null) project.Construction.Wall = defaults.Wall;

            if (project.SchemaVersion < 2)
            {
                foreach (var storey in project.Storeys ?? new List<StairStoreyDefinition>())
                {
                    if (storey != null) storey.StairwellConstraintLocked = true;
                }
                project.Construction.Wall.Enabled = true;
            }
            if (project.SchemaVersion < 3)
            {
                foreach (var storey in project.Storeys ?? new List<StairStoreyDefinition>())
                {
                    if (storey != null && storey.TotalRiserCount <= 0)
                        storey.TotalRiserCount = (storey.Flights ?? new List<StairFlightDefinition>())
                            .Where(flight => flight != null)
                            .Sum(flight => flight.RiserCount);
                }
            }
            if (project.SchemaVersion < 4)
            {
                foreach (var floor in project.Floors ?? new List<StairFloorDefinition>())
                    if (floor != null) floor.DirectionLinked = true;
                foreach (var storey in project.Storeys ?? new List<StairStoreyDefinition>())
                {
                    if (storey == null) continue;
                    foreach (var flight in storey.Flights ?? new List<StairFlightDefinition>())
                    {
                        if (flight == null) continue;
                        flight.DirectionLinked = true;
                        flight.SectionRepresentationLinked = true;
                    }
                    foreach (var landing in storey.Landings ?? new List<StairLandingDefinition>())
                        if (landing != null) landing.DirectionLinked = true;
                }
            }
            if (project.SchemaVersion < 5)
            {
                foreach (var storey in project.Storeys ?? new List<StairStoreyDefinition>())
                    if (storey != null) storey.TreadDepthLinked = true;
                project.SchemaVersion = 5;
            }
            if (project.SchemaVersion < 6)
            {
                project.SchemaVersion = 6;
            }
            if (project.SchemaVersion < 7)
            {
                // Existing projects keep direct upper-floor connections unless
                // the user explicitly enables a per-storey closure gap.
                foreach (var storey in project.Storeys ?? new List<StairStoreyDefinition>())
                    if (storey != null) storey.AllowUpperClosureGap = false;
                project.SchemaVersion = 7;
            }
            project.Construction.Wall.Enabled = true;
        }

        public void Apply(StairProjectDefinition project)
        {
            if (project == null || project.Construction == null) return;

            var wallThickness = project.Construction.Wall != null
                ? project.Construction.Wall.Thickness
                : 0.0;
            var constrainedFlightWidth = Math.Max(
                1.0,
                (project.Construction.StairwellWidth - wallThickness) / 2.0);

            var floors = (project.Floors ?? new List<StairFloorDefinition>())
                .Where(floor => floor != null && !string.IsNullOrWhiteSpace(floor.Id))
                .ToDictionary(floor => floor.Id, StringComparer.OrdinalIgnoreCase);

            ApplyAlternatingBoundaryDirections(project, floors);

            foreach (var storey in project.Storeys ?? new List<StairStoreyDefinition>())
            {
                if (storey == null || storey.Flights == null) continue;

                // TotalRiserCount is a UI constraint/input; the individual
                // flights are the geometric source of truth after distribution.
                // Never leave both representations with different totals.
                storey.TotalRiserCount = storey.Flights
                    .Where(flight => flight != null)
                    .Sum(flight => flight.RiserCount);

                StairFloorDefinition lowerFloor;
                StairFloorDefinition upperFloor;
                floors.TryGetValue(storey.LowerFloorId ?? string.Empty, out lowerFloor);
                floors.TryGetValue(storey.UpperFloorId ?? string.Empty, out upperFloor);

                if (storey.StairwellConstraintLocked)
                {
                    foreach (var flight in storey.Flights.Where(item => item != null))
                        flight.Width = constrainedFlightWidth;
                }

                if (storey.PlatformWidthsEqual && storey.Flights.Count > 0)
                {
                    var boundaries = new List<object> { lowerFloor };
                    boundaries.AddRange(storey.Landings.Cast<object>());
                    boundaries.Add(upperFloor);
                    var equalWidth = boundaries.Select(PlatformWidth).FirstOrDefault();
                    foreach (var boundary in boundaries)
                    {
                        var floor = boundary as StairFloorDefinition;
                        if (floor != null) floor.PlatformWidth = equalWidth;
                        var landing = boundary as StairLandingDefinition;
                        if (landing != null) landing.PlatformWidth = equalWidth;
                    }
                    if (storey.StairwellConstraintLocked)
                    {
                        foreach (var flight in storey.Flights.Where(f => f != null && f.RiserCount > 1))
                            flight.TreadDepth = Math.Max(1.0, (project.Construction.StairwellDepth - 2.0 * equalWidth) / (flight.RiserCount - 1));
                    }
                    continue;
                }

                var storeyBoundaries = new List<object> { lowerFloor };
                storeyBoundaries.AddRange(storey.Landings.Cast<object>());
                storeyBoundaries.Add(upperFloor);
                if (storey.StairwellConstraintLocked
                    && storey.TreadDepthLinked
                    && storey.Flights.Count > 0)
                {
                    var linkedTreadDepth = storey.Flights[0].TreadDepth;
                    var precedingWidth = PlatformWidth(lowerFloor);
                    for (var index = 0; index < storey.Flights.Count; index++)
                    {
                        var flight = storey.Flights[index];
                        if (flight == null || flight.RiserCount < 2) continue;
                        flight.TreadDepth = linkedTreadDepth;
                        if (index < storey.Landings.Count)
                        {
                            var landing = storey.Landings[index];
                            if (!landing.PlatformWidthLocked)
                            {
                                var requiredPairWidth = project.Construction.StairwellDepth
                                    - linkedTreadDepth * (flight.RiserCount - 1);
                                landing.PlatformWidth = Math.Max(
                                    0.0,
                                    requiredPairWidth - precedingWidth);
                            }
                            precedingWidth = landing.PlatformWidth;
                        }
                    }
                    continue;
                }

                if (storey.TreadDepthLinked && storey.Flights.Count > 0)
                {
                    var linkedTreadDepth = storey.Flights[0].TreadDepth;
                    var boundaries = new List<object> { lowerFloor };
                    boundaries.AddRange(storey.Landings.Cast<object>());
                    boundaries.Add(upperFloor);
                    var connectedFlightCount = AllowsFinalClosure(storey)
                        ? Math.Max(0, storey.Flights.Count - 1)
                        : storey.Flights.Count;
                    boundaries = boundaries.Take(connectedFlightCount + 1).ToList();
                    var currentWidths = boundaries.Select(PlatformWidth).ToArray();
                    var locked = boundaries.Select(IsPlatformWidthLocked).ToArray();
                    var signs = new double[boundaries.Count];
                    var constants = new double[boundaries.Count];
                    signs[0] = 1.0;
                    for (var index = 0; index < connectedFlightCount; index++)
                    {
                        var requiredPairWidth = project.Construction.StairwellDepth
                            - linkedTreadDepth * Math.Max(0, storey.Flights[index].RiserCount - 1);
                        signs[index + 1] = -signs[index];
                        constants[index + 1] = requiredPairWidth - constants[index];
                    }
                    var anchor = Array.FindIndex(locked, value => value);
                    var firstWidth = anchor >= 0
                        ? signs[anchor] * (currentWidths[anchor] - constants[anchor])
                        : signs.Select((sign, index) => sign * (currentWidths[index] - constants[index])).Average();
                    for (var index = 0; index < boundaries.Count; index++)
                    {
                        var width = locked[index] ? currentWidths[index] : Math.Max(0.0, signs[index] * firstWidth + constants[index]);
                        var floor = boundaries[index] as StairFloorDefinition;
                        if (floor != null) floor.PlatformWidth = width;
                        var landing = boundaries[index] as StairLandingDefinition;
                        if (landing != null) landing.PlatformWidth = width;
                    }
                    foreach (var flight in storey.Flights)
                        flight.TreadDepth = linkedTreadDepth;
                }
                else if (storey.StairwellConstraintLocked)
                {
                    for (var index = 0; index < storey.Flights.Count; index++)
                    {
                        var flight = storey.Flights[index];
                        if (flight == null || flight.RiserCount < 2) continue;
                        var startWidth = index == 0 ? PlatformWidth(lowerFloor) : PlatformWidth(storey.Landings[index - 1]);
                        var endWidth = index == storey.Flights.Count - 1 ? PlatformWidth(upperFloor) : PlatformWidth(storey.Landings[index]);
                        var horizontalRun = project.Construction.StairwellDepth - startWidth - endWidth;
                        if (horizontalRun > 0.0) flight.TreadDepth = horizontalRun / (flight.RiserCount - 1);
                    }
                }
            }

            EnsureDirectConnections(project, floors);
        }

        private static void EnsureDirectConnections(
            StairProjectDefinition project,
            IDictionary<string, StairFloorDefinition> floors)
        {
            var boundaries = new List<object>();
            var flights = new List<StairFlightDefinition>();
            Action solveSegment = () =>
            {
                SolveDirectConnectionSegment(project, boundaries, flights);
            };

            foreach (var storey in project.Storeys.Where(item => item != null && item.Flights != null))
            {
                StairFloorDefinition lowerFloor;
                StairFloorDefinition upperFloor;
                floors.TryGetValue(storey.LowerFloorId ?? string.Empty, out lowerFloor);
                floors.TryGetValue(storey.UpperFloorId ?? string.Empty, out upperFloor);
                var localBoundaries = new List<object> { lowerFloor };
                localBoundaries.AddRange(storey.Landings.Cast<object>());
                localBoundaries.Add(upperFloor);

                // Three-flight storeys always allow the designed final closure.
                // Other storeys do so only when explicitly enabled. The chain
                // then stops before the last run, so edits below cannot rewrite
                // the upper floor or any platform above it.
                if (AllowsFinalClosure(storey))
                {
                    solveSegment();
                    boundaries.Clear();
                    flights.Clear();
                    var directFlightCount = Math.Max(0, storey.Flights.Count - 1);
                    SolveDirectConnectionSegment(
                        project,
                        localBoundaries.Take(directFlightCount + 1).ToList(),
                        storey.Flights.Take(directFlightCount).ToList());
                    continue;
                }

                if (boundaries.Count == 0) boundaries.Add(localBoundaries[0]);
                for (var index = 0; index < storey.Flights.Count; index++)
                {
                    flights.Add(storey.Flights[index]);
                    boundaries.Add(localBoundaries[index + 1]);
                }
            }
            solveSegment();
        }

        private static bool AllowsFinalClosure(StairStoreyDefinition storey)
        {
            return storey != null
                && storey.Flights != null
                && (storey.Flights.Count == 3 || storey.AllowUpperClosureGap);
        }

        private static void SolveDirectConnectionSegment(
            StairProjectDefinition project,
            IList<object> boundaries,
            IList<StairFlightDefinition> flights)
        {
            if (project == null
                || flights == null
                || boundaries == null
                || flights.Count == 0
                || boundaries.Count != flights.Count + 1)
            {
                return;
            }

            var anchorIndex = boundaries.ToList().FindIndex(IsPlatformWidthLocked);
            if (anchorIndex < 0) anchorIndex = 0;
            for (var index = anchorIndex - 1; index >= 0; index--)
            {
                var width = project.Construction.StairwellDepth
                    - PlatformWidth(boundaries[index + 1])
                    - flights[index].TreadDepth * Math.Max(0, flights[index].RiserCount - 1);
                SetPlatformWidth(boundaries[index], Math.Max(1.0, width));
            }
            for (var index = anchorIndex; index < flights.Count; index++)
            {
                var width = project.Construction.StairwellDepth
                    - PlatformWidth(boundaries[index])
                    - flights[index].TreadDepth * Math.Max(0, flights[index].RiserCount - 1);
                SetPlatformWidth(boundaries[index + 1], Math.Max(1.0, width));
            }
        }

        public void SetPlatformWidth(
            StairProjectDefinition project,
            string componentId,
            double width)
        {
            if (project == null || width <= 0.0 || string.IsNullOrWhiteSpace(componentId)) return;
            var floors = (project.Floors ?? new List<StairFloorDefinition>())
                .Where(floor => floor != null && !string.IsNullOrWhiteSpace(floor.Id))
                .ToDictionary(floor => floor.Id, StringComparer.OrdinalIgnoreCase);
            var sequence = BoundarySequence(project, floors);
            var sourceIndex = sequence.FindIndex(item => string.Equals(
                ComponentId(item), componentId, StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0) return;
            SetPlatformWidth(sequence[sourceIndex], width);
            SetPlatformWidthLocked(sequence[sourceIndex], true);

            var landing = sequence[sourceIndex] as StairLandingDefinition;
            if (landing == null) return;
            var storey = (project.Storeys ?? new List<StairStoreyDefinition>())
                .FirstOrDefault(item => item != null && item.Landings.Contains(landing));
            if (storey == null || storey.Flights == null) return;

            var boundaries = new List<object>();
            StairFloorDefinition lowerFloor;
            StairFloorDefinition upperFloor;
            floors.TryGetValue(storey.LowerFloorId ?? string.Empty, out lowerFloor);
            floors.TryGetValue(storey.UpperFloorId ?? string.Empty, out upperFloor);
            boundaries.Add(lowerFloor);
            boundaries.AddRange(storey.Landings.Cast<object>());
            boundaries.Add(upperFloor);
            var anchorIndex = storey.Landings.IndexOf(landing) + 1;
            if (anchorIndex <= 0 || anchorIndex >= boundaries.Count) return;

            // Keep every tread unchanged. Solve the boundary chain outward from
            // the edited landing so each adjacent flight still terminates at a
            // platform/floor instead of crossing through it.
            for (var index = anchorIndex - 1; index >= 0; index--)
            {
                var flight = storey.Flights[index];
                var pairWidth = project.Construction.StairwellDepth
                    - flight.TreadDepth * Math.Max(0, flight.RiserCount - 1);
                SetPlatformWidth(boundaries[index], Math.Max(
                    0.0,
                    pairWidth - PlatformWidth(boundaries[index + 1])));
            }
            var directFlightCount = storey.AllowUpperClosureGap
                && storey.Flights.Count != 3
                ? Math.Max(0, storey.Flights.Count - 1)
                : storey.Flights.Count;
            for (var index = anchorIndex; index < directFlightCount; index++)
            {
                var flight = storey.Flights[index];
                var pairWidth = project.Construction.StairwellDepth
                    - flight.TreadDepth * Math.Max(0, flight.RiserCount - 1);
                SetPlatformWidth(boundaries[index + 1], Math.Max(
                    0.0,
                    pairWidth - PlatformWidth(boundaries[index])));
            }

            var changedFloorIds = new HashSet<string>(
                boundaries.OfType<StairFloorDefinition>()
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                    .Select(item => item.Id),
                StringComparer.OrdinalIgnoreCase);
            foreach (var otherStorey in (project.Storeys ?? new List<StairStoreyDefinition>())
                .Where(item => item != null
                    && !ReferenceEquals(item, storey)
                    && (changedFloorIds.Contains(item.LowerFloorId ?? string.Empty)
                        || changedFloorIds.Contains(item.UpperFloorId ?? string.Empty))))
            {
                // A shared floor may move as part of this storey's boundary
                // chain, but landings in the storey on its other side must not
                // be recalculated as a side effect.
                foreach (var otherLanding in otherStorey.Landings ?? new List<StairLandingDefinition>())
                    if (otherLanding != null) otherLanding.PlatformWidthLocked = true;
            }
        }

        private static List<object> BoundarySequence(
            StairProjectDefinition project,
            IDictionary<string, StairFloorDefinition> floors)
        {
            var result = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var storey in project.Storeys ?? new List<StairStoreyDefinition>())
            {
                StairFloorDefinition floor;
                if (floors.TryGetValue(storey.LowerFloorId ?? string.Empty, out floor)
                    && seen.Add(floor.Id)) result.Add(floor);
                foreach (var landing in storey.Landings ?? new List<StairLandingDefinition>())
                    if (landing != null && seen.Add(landing.Id)) result.Add(landing);
                if (floors.TryGetValue(storey.UpperFloorId ?? string.Empty, out floor)
                    && seen.Add(floor.Id)) result.Add(floor);
            }
            return result;
        }

        private static string ComponentId(object platform)
        {
            var floor = platform as StairFloorDefinition;
            if (floor != null) return floor.Id;
            var landing = platform as StairLandingDefinition;
            return landing == null ? string.Empty : landing.Id;
        }

        private static void SetPlatformWidth(object platform, double width)
        {
            var floor = platform as StairFloorDefinition;
            if (floor != null) floor.PlatformWidth = width;
            var landing = platform as StairLandingDefinition;
            if (landing != null) landing.PlatformWidth = width;
        }

        private static void SetPlatformWidthLocked(object platform, bool value)
        {
            var floor = platform as StairFloorDefinition;
            if (floor != null) floor.PlatformWidthLocked = value;
            var landing = platform as StairLandingDefinition;
            if (landing != null) landing.PlatformWidthLocked = value;
        }

        private static void ApplyAlternatingBoundaryDirections(
            StairProjectDefinition project,
            IDictionary<string, StairFloorDefinition> floors)
        {
            var storeys = (project.Storeys ?? new List<StairStoreyDefinition>())
                .Where(storey => storey != null && storey.Flights != null && storey.Flights.Count > 0)
                .ToArray();
            if (storeys.Length == 0) return;

            var direction = (int)storeys[0].Flights[0].Direction;
            if (direction != -1 && direction != 1) direction = 1;
            var assignedFloors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var storey in storeys)
            {
                StairFloorDefinition lowerFloor;
                if (floors.TryGetValue(storey.LowerFloorId ?? string.Empty, out lowerFloor)
                    && assignedFloors.Add(lowerFloor.Id))
                    lowerFloor.ProjectionDirection = direction;

                for (var index = 0; index < storey.Flights.Count; index++)
                {
                    storey.Flights[index].Direction = (StairFlightDirection)direction;
                    direction = -direction;
                    if (index < storey.Landings.Count)
                        storey.Landings[index].ProjectionDirection = direction;
                }

                StairFloorDefinition upperFloor;
                if (floors.TryGetValue(storey.UpperFloorId ?? string.Empty, out upperFloor)
                    && assignedFloors.Add(upperFloor.Id))
                    upperFloor.ProjectionDirection = direction;
            }
        }

        private static double PlatformWidth(StairFloorDefinition floor)
        {
            return floor == null ? 0.0 : floor.PlatformWidth;
        }

        private static double PlatformWidth(StairLandingDefinition landing)
        {
            return landing == null ? 0.0 : landing.PlatformWidth;
        }

        private static double PlatformWidth(object platform)
        {
            var floor = platform as StairFloorDefinition;
            if (floor != null) return floor.PlatformWidth;
            var landing = platform as StairLandingDefinition;
            return landing == null ? 0.0 : landing.PlatformWidth;
        }

        private static bool IsPlatformWidthLocked(object platform)
        {
            var floor = platform as StairFloorDefinition;
            if (floor != null) return floor.PlatformWidthLocked;
            var landing = platform as StairLandingDefinition;
            return landing != null && landing.PlatformWidthLocked;
        }

    }
}
