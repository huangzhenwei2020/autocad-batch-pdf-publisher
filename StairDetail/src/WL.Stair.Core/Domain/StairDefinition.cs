using System;

namespace WL.Stair.Core.Domain
{
    /// <summary>
    /// Input dimensions are expressed in millimetres.
    /// </summary>
    public sealed class StairDefinition
    {
        public StairDefinition(
            double floorHeight,
            double flightWidth,
            double stairwellWidth,
            double landingDepth,
            double treadDepth)
        {
            FloorHeight = floorHeight;
            FlightWidth = flightWidth;
            StairwellWidth = stairwellWidth;
            FloorLandingDepthUp = landingDepth;
            FloorLandingDepthDown = landingDepth;
            IntermediateLandingDepthUp = landingDepth;
            IntermediateLandingDepthDown = landingDepth;
            TreadDepth = treadDepth;
            PreferredRiserHeight = 166.7;
            FlightSlabThickness = 120.0;
            LandingSlabThickness = 100.0;
            FloorSlabThickness = 100.0;
            FloorBeamWidth = 200.0;
            FloorBeamDepth = 400.0;
            SplitPreference = FlightSplitPreference.FirstFlightGetsExtraRiser;
        }

        public double FloorHeight { get; set; }

        public double FlightWidth { get; set; }

        public double StairwellWidth { get; set; }

        public double FloorLandingDepth
        {
            get { return Math.Max(FloorLandingDepthUp, FloorLandingDepthDown); }
            set
            {
                FloorLandingDepthUp = value;
                FloorLandingDepthDown = value;
            }
        }

        public double IntermediateLandingDepth
        {
            get { return Math.Max(IntermediateLandingDepthUp, IntermediateLandingDepthDown); }
            set
            {
                IntermediateLandingDepthUp = value;
                IntermediateLandingDepthDown = value;
            }
        }

        public double FloorLandingDepthUp { get; set; }

        public double FloorLandingDepthDown { get; set; }

        public double IntermediateLandingDepthUp { get; set; }

        public double IntermediateLandingDepthDown { get; set; }

        public double TreadDepth { get; set; }

        public int? TotalRiserCount { get; set; }

        public int? FirstFlightRiserCount { get; set; }

        public double PreferredRiserHeight { get; set; }

        public double FlightSlabThickness { get; set; }

        public double LandingSlabThickness { get; set; }

        public double FloorSlabThickness { get; set; }

        public double FloorBeamWidth { get; set; }

        public double FloorBeamDepth { get; set; }

        public FlightSplitPreference SplitPreference { get; set; }

        public StairDefinition Copy()
        {
            return (StairDefinition)MemberwiseClone();
        }
    }
}
