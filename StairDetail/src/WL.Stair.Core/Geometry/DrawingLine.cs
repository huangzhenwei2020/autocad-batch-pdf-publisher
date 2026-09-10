using System;

namespace WL.Stair.Core.Geometry
{
    public sealed class DrawingLine
    {
        public DrawingLine(
            Point2D start,
            Point2D end,
            StairLineRole role,
            bool isHidden = false,
            string componentId = null)
        {
            if (start.Equals(end))
            {
                throw new ArgumentException("A drawing line must have a non-zero length.");
            }

            Start = start;
            End = end;
            Role = role;
            IsHidden = isHidden;
            ComponentId = componentId ?? string.Empty;
        }

        public Point2D Start { get; }

        public Point2D End { get; }

        public StairLineRole Role { get; }

        public bool IsHidden { get; }

        public string ComponentId { get; }
    }
}
