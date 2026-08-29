using System;
using System.Collections.Generic;
using System.Linq;
using WL.Stair.Core.Domain;

namespace WL.Stair.Core.Calculation
{
    public sealed class StairwellAxisRange
    {
        public StairwellAxisRange(double leftAxisX, double rightAxisX)
        {
            LeftAxisX = Math.Min(leftAxisX, rightAxisX);
            RightAxisX = Math.Max(leftAxisX, rightAxisX);
        }

        public double LeftAxisX { get; }

        public double RightAxisX { get; }

        public double Depth => RightAxisX - LeftAxisX;

        public double CenterX => (LeftAxisX + RightAxisX) / 2.0;
    }

    /// <summary>
    /// Single source of truth for resolving unified and storey-local
    /// stairwell axes. Geometry, constraints and the editor must use this
    /// service instead of independently recreating the alignment formula.
    /// </summary>
    public sealed class StairwellAxisResolver
    {
        public StairwellAxisRange Resolve(StairProjectDefinition project, StairStoreyDefinition storey)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));

            var unifiedDepth = project.Construction != null
                && IsPositiveFinite(project.Construction.StairwellDepth)
                    ? project.Construction.StairwellDepth
                    : StairConstructionDefaults.CreateDefault().StairwellDepth;

            if (storey == null || !storey.IndependentStairwellEnabled)
                return new StairwellAxisRange(0.0, unifiedDepth);

            var depth = IsPositiveFinite(storey.StairwellDepthOverride)
                ? storey.StairwellDepthOverride
                : unifiedDepth;
            var offset = IsFinite(storey.StairwellAxisOffset)
                ? storey.StairwellAxisOffset
                : 0.0;
            double left;
            switch (storey.StairwellAlignment)
            {
                case StairwellAlignment.Left:
                    left = 0.0;
                    break;
                case StairwellAlignment.Right:
                    left = unifiedDepth - depth;
                    break;
                default:
                    left = (unifiedDepth - depth) / 2.0;
                    break;
            }

            return new StairwellAxisRange(left + offset, left + offset + depth);
        }

        public StairwellAxisRange ResolveById(StairProjectDefinition project, string storeyId)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var storey = (project.Storeys ?? new List<StairStoreyDefinition>())
                .FirstOrDefault(item => item != null
                    && string.Equals(item.Id, storeyId, StringComparison.OrdinalIgnoreCase));
            return Resolve(project, storey);
        }

        public StairwellAxisRange ResolveEnvelope(StairProjectDefinition project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var ranges = (project.Storeys ?? new List<StairStoreyDefinition>())
                .Where(item => item != null)
                .Select(item => Resolve(project, item))
                .ToList();
            if (ranges.Count == 0) ranges.Add(Resolve(project, null));
            return new StairwellAxisRange(
                ranges.Min(item => item.LeftAxisX),
                ranges.Max(item => item.RightAxisX));
        }

        private static bool IsPositiveFinite(double value)
        {
            return value > 0.0 && IsFinite(value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
