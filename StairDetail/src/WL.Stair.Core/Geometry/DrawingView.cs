using System.Collections.Generic;
using System.Linq;

namespace WL.Stair.Core.Geometry
{
    public sealed class DrawingView
    {
        public DrawingView(string name, IEnumerable<DrawingLine> lines)
            : this(name, lines, Enumerable.Empty<DrawingText>(),
                Enumerable.Empty<DrawingDimension>(), Enumerable.Empty<DrawingTable>(), 1)
        {
        }

        public DrawingView(
            string name,
            IEnumerable<DrawingLine> lines,
            IEnumerable<DrawingText> texts)
            : this(name, lines, texts, Enumerable.Empty<DrawingDimension>(),
                Enumerable.Empty<DrawingTable>(), 1)
        {
        }

        public DrawingView(
            string name,
            IEnumerable<DrawingLine> lines,
            IEnumerable<DrawingText> texts,
            IEnumerable<DrawingDimension> dimensions,
            IEnumerable<DrawingTable> tables,
            int scale,
            IEnumerable<DrawingHatchRegion> hatchRegions = null,
            DrawingTitle title = null,
            IEnumerable<DrawingLeader> leaders = null,
            bool showBold = true,
            bool showFill = true)
        {
            Name = name;
            Lines = lines.ToArray();
            Texts = texts.ToArray();
            Dimensions = dimensions.ToArray();
            Tables = tables.ToArray();
            HatchRegions = (hatchRegions ?? Enumerable.Empty<DrawingHatchRegion>()).ToArray();
            Title = title;
            Leaders = (leaders ?? Enumerable.Empty<DrawingLeader>()).ToArray();
            Scale = System.Math.Max(1, scale);
            ShowBold = showBold;
            ShowFill = showFill;
        }

        public string Name { get; }

        public IReadOnlyList<DrawingLine> Lines { get; }

        public IReadOnlyList<DrawingText> Texts { get; }

        public IReadOnlyList<DrawingDimension> Dimensions { get; }

        public IReadOnlyList<DrawingTable> Tables { get; }

        public IReadOnlyList<DrawingHatchRegion> HatchRegions { get; }

        public DrawingTitle Title { get; }

        public IReadOnlyList<DrawingLeader> Leaders { get; }

        public int Scale { get; }

        public bool ShowBold { get; }

        public bool ShowFill { get; }
    }
}
