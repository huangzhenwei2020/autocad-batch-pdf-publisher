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
            int scale)
        {
            Name = name;
            Lines = lines.ToArray();
            Texts = texts.ToArray();
            Dimensions = dimensions.ToArray();
            Tables = tables.ToArray();
            Scale = System.Math.Max(1, scale);
        }

        public string Name { get; }

        public IReadOnlyList<DrawingLine> Lines { get; }

        public IReadOnlyList<DrawingText> Texts { get; }

        public IReadOnlyList<DrawingDimension> Dimensions { get; }

        public IReadOnlyList<DrawingTable> Tables { get; }

        public int Scale { get; }
    }
}
