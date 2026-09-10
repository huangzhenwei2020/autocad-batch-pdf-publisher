using System;

namespace WL.Stair.Core.Geometry
{
    public enum DrawingDimensionOrientation
    {
        Vertical = 0,
        Horizontal = 1
    }

    public sealed class DrawingDimension
    {
        public DrawingDimension(
            Point2D firstExtensionOrigin,
            Point2D secondExtensionOrigin,
            Point2D dimensionLinePoint,
            string textOverride,
            string componentId = null,
            DrawingDimensionOrientation orientation = DrawingDimensionOrientation.Vertical)
        {
            if (firstExtensionOrigin.Equals(secondExtensionOrigin))
                throw new ArgumentException("A dimension must measure a non-zero distance.");
            FirstExtensionOrigin = firstExtensionOrigin;
            SecondExtensionOrigin = secondExtensionOrigin;
            DimensionLinePoint = dimensionLinePoint;
            TextOverride = textOverride ?? string.Empty;
            ComponentId = componentId ?? string.Empty;
            Orientation = orientation;
        }

        public Point2D FirstExtensionOrigin { get; }
        public Point2D SecondExtensionOrigin { get; }
        public Point2D DimensionLinePoint { get; }
        public string TextOverride { get; }
        public string ComponentId { get; }

        public DrawingDimensionOrientation Orientation { get; }
    }
}
