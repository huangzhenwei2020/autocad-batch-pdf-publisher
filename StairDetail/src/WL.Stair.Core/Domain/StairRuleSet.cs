namespace WL.Stair.Core.Domain
{
    /// <summary>
    /// Prototype defaults only. A project-approved rule pack must replace them before release.
    /// </summary>
    public sealed class StairRuleSet
    {
        public StairRuleSet()
        {
            MinimumRiserHeight = 120.0;
            MaximumRiserHeight = 200.0;
            RecommendedMinimumRiserHeight = 150.0;
            RecommendedMaximumRiserHeight = 175.0;
            RecommendedMinimumTreadDepth = 260.0;
            RecommendedMaximumTreadDepth = 320.0;
            MinimumComfortValue = 600.0;
            MaximumComfortValue = 650.0;
            RecommendedMinimumFlightWidth = 900.0;
            MinimumRisersPerFlight = 2;
        }

        public double MinimumRiserHeight { get; set; }

        public double MaximumRiserHeight { get; set; }

        public double RecommendedMinimumRiserHeight { get; set; }

        public double RecommendedMaximumRiserHeight { get; set; }

        public double RecommendedMinimumTreadDepth { get; set; }

        public double RecommendedMaximumTreadDepth { get; set; }

        public double MinimumComfortValue { get; set; }

        public double MaximumComfortValue { get; set; }

        public double RecommendedMinimumFlightWidth { get; set; }

        public int MinimumRisersPerFlight { get; set; }
    }
}

