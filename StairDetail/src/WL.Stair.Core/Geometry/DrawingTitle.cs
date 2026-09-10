namespace WL.Stair.Core.Geometry
{
    public sealed class DrawingTitle
    {
        public DrawingTitle(Point2D position, string text, int scale, double targetWidth)
        {
            Position = position;
            Text = string.IsNullOrWhiteSpace(text) ? "楼梯大样" : text;
            Scale = System.Math.Max(1, scale);
            TargetWidth = System.Math.Max(1.0, targetWidth);
        }

        public Point2D Position { get; }
        public string Text { get; }
        public int Scale { get; }
        public double TargetWidth { get; }
    }
}
