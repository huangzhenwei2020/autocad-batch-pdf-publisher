using System;
using System.Collections.Generic;
using CadArchSpec.Domain.Layout;

namespace CadArchSpec.LayoutEngine
{
    public sealed class PaperLayoutService
    {
        private static readonly IDictionary<string, Tuple<decimal, decimal>> PaperSizes =
            new Dictionary<string, Tuple<decimal, decimal>>(StringComparer.OrdinalIgnoreCase)
            {
                { "A0", Tuple.Create(1189m, 841m) },
                { "A0+1/4", Tuple.Create(1486m, 841m) },
                { "A0+1/2", Tuple.Create(1784m, 841m) },
                { "A1", Tuple.Create(841m, 594m) },
                { "A1+1/4", Tuple.Create(1051m, 594m) },
                { "A1+1/2", Tuple.Create(1261m, 594m) },
                { "A2", Tuple.Create(594m, 420m) },
                { "A2+1/4", Tuple.Create(743m, 420m) },
                { "A2+1/2", Tuple.Create(891m, 420m) },
                { "A3", Tuple.Create(420m, 297m) },
                { "A3+1/4", Tuple.Create(525m, 297m) },
                { "A3+1/2", Tuple.Create(630m, 297m) },
                { "A4", Tuple.Create(297m, 210m) },
                { "A4+1/4", Tuple.Create(371m, 210m) },
                { "A4+1/2", Tuple.Create(446m, 210m) }
            };

        public DocumentLayoutProfile CreateDefault(string paperName, bool landscape)
        {
            Tuple<decimal, decimal> size;
            if (!PaperSizes.TryGetValue(paperName ?? string.Empty, out size))
                throw new ArgumentException("仅支持 A0-A4 及 +1/4、+1/2 版面。", nameof(paperName));

            var width = landscape ? Math.Max(size.Item1, size.Item2) : Math.Min(size.Item1, size.Item2);
            var height = landscape ? Math.Min(size.Item1, size.Item2) : Math.Max(size.Item1, size.Item2);
            return new DocumentLayoutProfile
            {
                LayoutProfileId = paperName.ToUpperInvariant() + (landscape ? "-Landscape-2C" : "-Portrait-1C"),
                PaperName = paperName.ToUpperInvariant(),
                Landscape = landscape,
                PaperWidthMillimeters = width,
                PaperHeightMillimeters = height,
                ColumnCount = landscape ? 2 : 1
            };
        }

        public decimal PaperMillimetersToCadUnits(decimal millimeters, decimal plotScale, decimal drawingUnitsPerMillimeter)
        {
            if (plotScale <= 0) throw new ArgumentOutOfRangeException(nameof(plotScale));
            if (drawingUnitsPerMillimeter <= 0) throw new ArgumentOutOfRangeException(nameof(drawingUnitsPerMillimeter));
            return millimeters * plotScale * drawingUnitsPerMillimeter;
        }
    }
}
