namespace WL.Stair.Core.Calculation
{
    public sealed class StairCalculationResult
    {
        public StairCalculationResult(
            StairFlightResult firstFlight,
            StairFlightResult secondFlight,
            double floorLandingDepth,
            double intermediateLandingDepth,
            double flightWidth,
            double stairwellWidth)
        {
            FirstFlight = firstFlight;
            SecondFlight = secondFlight;
            TotalRiserCount = firstFlight.RiserCount + secondFlight.RiserCount;
            RiserHeight = firstFlight.RiserHeight;
            ComfortValue = (2.0 * RiserHeight) + firstFlight.TreadDepth;
            PlanLength = floorLandingDepth
                + System.Math.Max(firstFlight.HorizontalRun, secondFlight.HorizontalRun)
                + intermediateLandingDepth;
            PlanWidth = (2.0 * flightWidth) + stairwellWidth;
            IntermediateLandingElevation = firstFlight.VerticalRise;
            FloorElevation = firstFlight.VerticalRise + secondFlight.VerticalRise;
        }

        public StairFlightResult FirstFlight { get; }

        public StairFlightResult SecondFlight { get; }

        public int TotalRiserCount { get; }

        public double RiserHeight { get; }

        public double ComfortValue { get; }

        public double PlanLength { get; }

        public double PlanWidth { get; }

        public double IntermediateLandingElevation { get; }

        public double FloorElevation { get; }
    }
}
