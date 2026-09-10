using System;

namespace WL.Stair.Core.Geometry
{
    public sealed class DrawingText
    {
        public DrawingText(Point2D position, string content, double height)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new ArgumentException("Drawing text must have content.", nameof(content));
            }

            if (height <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            Position = position;
            Content = content;
            Height = height;
        }

        public Point2D Position { get; }

        public string Content { get; }

        public double Height { get; }
    }
}
