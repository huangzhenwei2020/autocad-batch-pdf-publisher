using System.Collections.Generic;
using System.Linq;

namespace WL.Stair.Core.Geometry
{
    public sealed class DrawingView
    {
        public DrawingView(string name, IEnumerable<DrawingLine> lines)
            : this(name, lines, Enumerable.Empty<DrawingText>())
        {
        }

        public DrawingView(
            string name,
            IEnumerable<DrawingLine> lines,
            IEnumerable<DrawingText> texts)
        {
            Name = name;
            Lines = lines.ToArray();
            Texts = texts.ToArray();
        }

        public string Name { get; }

        public IReadOnlyList<DrawingLine> Lines { get; }

        public IReadOnlyList<DrawingText> Texts { get; }
    }
}
