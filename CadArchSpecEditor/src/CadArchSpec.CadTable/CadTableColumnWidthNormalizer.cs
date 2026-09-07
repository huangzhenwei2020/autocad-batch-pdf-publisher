using System;
using System.Collections.Generic;
using System.Linq;

namespace CadArchSpec.CadTable
{
    public static class CadTableColumnWidthNormalizer
    {
        public static IReadOnlyList<double> Normalize(IEnumerable<double> sourceWidths,
            double targetTotalMillimeters = 240d, double minimumMillimeters = 18d,
            double maximumMillimeters = 180d)
        {
            var widths = (sourceWidths ?? Enumerable.Empty<double>())
                .Select(value => double.IsNaN(value) || double.IsInfinity(value) || value <= 0d ? 1d : value)
                .ToList();
            if (widths.Count == 0) return new double[0];
            var total = widths.Sum();
            return widths.Select(value => Math.Round(Math.Max(minimumMillimeters,
                Math.Min(maximumMillimeters, value / total * targetTotalMillimeters)), 1)).ToList();
        }
    }
}
