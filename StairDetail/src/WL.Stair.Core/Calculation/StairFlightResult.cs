namespace WL.Stair.Core.Calculation
{
    public sealed class StairFlightResult
    {
        public StairFlightResult(int riserCount, double riserHeight, double treadDepth)
        {
            RiserCount = riserCount;
            RiserHeight = riserHeight;
            TreadCount = System.Math.Max(0, riserCount - 1);
            TreadDepth = treadDepth;
            HorizontalRun = TreadCount * treadDepth;
            VerticalRise = riserCount * riserHeight;
        }

        public int RiserCount { get; }

        public int TreadCount { get; }

        public double RiserHeight { get; }

        public double TreadDepth { get; }

        public double HorizontalRun { get; }

        public double VerticalRise { get; }
    }
}
