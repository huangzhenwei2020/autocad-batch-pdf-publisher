using System;
using System.Collections.Generic;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Validation;

namespace WL.Stair.Core.Calculation
{
    public sealed class StairCalculator
    {
        private readonly StairRuleSet _rules;

        public StairCalculator(StairRuleSet rules = null)
        {
            _rules = rules ?? new StairRuleSet();
        }

        public StairCalculationOutcome Calculate(StairDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var issues = ValidateBasicDimensions(definition);
            if (HasErrors(issues))
            {
                return new StairCalculationOutcome(null, issues);
            }

            var totalRisers = definition.TotalRiserCount
                ?? RecommendTotalRiserCount(definition.FloorHeight, definition.PreferredRiserHeight);

            if (totalRisers < _rules.MinimumRisersPerFlight * 2)
            {
                issues.Add(Error(
                    "WL-ST-011",
                    nameof(definition.TotalRiserCount),
                    "The total riser count cannot provide two valid flights."));
                return new StairCalculationOutcome(null, issues);
            }

            int firstFlightRisers;
            int secondFlightRisers;
            SplitFlights(definition, totalRisers, out firstFlightRisers, out secondFlightRisers);

            if (firstFlightRisers < _rules.MinimumRisersPerFlight
                || secondFlightRisers < _rules.MinimumRisersPerFlight)
            {
                issues.Add(Error(
                    "WL-ST-012",
                    nameof(definition.FirstFlightRiserCount),
                    "Each flight must contain the configured minimum number of risers."));
                return new StairCalculationOutcome(null, issues);
            }

            var riserHeight = definition.FloorHeight / totalRisers;
            ValidateCalculatedValues(definition, riserHeight, issues);

            if (HasErrors(issues))
            {
                return new StairCalculationOutcome(null, issues);
            }

            var firstFlight = new StairFlightResult(firstFlightRisers, riserHeight, definition.TreadDepth);
            var secondFlight = new StairFlightResult(secondFlightRisers, riserHeight, definition.TreadDepth);
            var result = new StairCalculationResult(
                firstFlight,
                secondFlight,
                definition.FloorLandingDepth,
                definition.IntermediateLandingDepth,
                definition.FlightWidth,
                definition.StairwellWidth);

            return new StairCalculationOutcome(result, issues);
        }

        public int RecommendTotalRiserCount(double floorHeight, double preferredRiserHeight)
        {
            if (floorHeight <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(floorHeight));
            }

            if (preferredRiserHeight <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(preferredRiserHeight));
            }

            var rawCount = Math.Max(
                _rules.MinimumRisersPerFlight * 2,
                (int)Math.Round(floorHeight / preferredRiserHeight, MidpointRounding.AwayFromZero));

            // An even count keeps the two prototype flights balanced.
            if (rawCount % 2 != 0)
            {
                var lowerEven = Math.Max(_rules.MinimumRisersPerFlight * 2, rawCount - 1);
                var upperEven = rawCount + 1;
                var lowerDifference = Math.Abs((floorHeight / lowerEven) - preferredRiserHeight);
                var upperDifference = Math.Abs((floorHeight / upperEven) - preferredRiserHeight);
                rawCount = lowerDifference <= upperDifference ? lowerEven : upperEven;
            }

            return rawCount;
        }

        private List<ValidationIssue> ValidateBasicDimensions(StairDefinition definition)
        {
            var issues = new List<ValidationIssue>();

            RequirePositive(definition.FloorHeight, nameof(definition.FloorHeight), "WL-ST-001", issues);
            RequirePositive(definition.FlightWidth, nameof(definition.FlightWidth), "WL-ST-002", issues);
            RequireNonNegative(definition.StairwellWidth, nameof(definition.StairwellWidth), "WL-ST-003", issues);
            RequirePositive(definition.FloorLandingDepthUp, nameof(definition.FloorLandingDepthUp), "WL-ST-004", issues);
            RequirePositive(definition.FloorLandingDepthDown, nameof(definition.FloorLandingDepthDown), "WL-ST-016", issues);
            RequirePositive(definition.IntermediateLandingDepthUp, nameof(definition.IntermediateLandingDepthUp), "WL-ST-005", issues);
            RequirePositive(definition.IntermediateLandingDepthDown, nameof(definition.IntermediateLandingDepthDown), "WL-ST-017", issues);
            RequirePositive(definition.TreadDepth, nameof(definition.TreadDepth), "WL-ST-006", issues);
            RequirePositive(definition.PreferredRiserHeight, nameof(definition.PreferredRiserHeight), "WL-ST-007", issues);
            RequirePositive(definition.FlightSlabThickness, nameof(definition.FlightSlabThickness), "WL-ST-013", issues);
            RequirePositive(definition.LandingSlabThickness, nameof(definition.LandingSlabThickness), "WL-ST-014", issues);
            RequirePositive(definition.FloorSlabThickness, nameof(definition.FloorSlabThickness), "WL-ST-015", issues);
            RequirePositive(definition.FloorBeamWidth, nameof(definition.FloorBeamWidth), "WL-ST-018", issues);
            RequirePositive(definition.FloorBeamDepth, nameof(definition.FloorBeamDepth), "WL-ST-019", issues);

            if (definition.FloorBeamDepth < definition.FloorSlabThickness)
            {
                issues.Add(Error(
                    "WL-ST-021",
                    nameof(definition.FloorBeamDepth),
                    "Floor beam depth cannot be smaller than the floor slab thickness."));
            }

            if (definition.TotalRiserCount.HasValue && definition.TotalRiserCount.Value <= 0)
            {
                issues.Add(Error("WL-ST-008", nameof(definition.TotalRiserCount), "Total riser count must be positive."));
            }

            if (definition.FirstFlightRiserCount.HasValue && !definition.TotalRiserCount.HasValue)
            {
                issues.Add(Error(
                    "WL-ST-009",
                    nameof(definition.FirstFlightRiserCount),
                    "A manual first-flight count requires a total riser count."));
            }

            if (definition.TotalRiserCount.HasValue
                && definition.FirstFlightRiserCount.HasValue
                && definition.FirstFlightRiserCount.Value >= definition.TotalRiserCount.Value)
            {
                issues.Add(Error(
                    "WL-ST-010",
                    nameof(definition.FirstFlightRiserCount),
                    "First-flight risers must be fewer than the total riser count."));
            }

            return issues;
        }

        private void ValidateCalculatedValues(
            StairDefinition definition,
            double riserHeight,
            ICollection<ValidationIssue> issues)
        {
            if (riserHeight < _rules.MinimumRiserHeight || riserHeight > _rules.MaximumRiserHeight)
            {
                issues.Add(Error(
                    "WL-ST-020",
                    nameof(definition.TotalRiserCount),
                    "The calculated riser height is outside the configured generation limits."));
                return;
            }

            if (riserHeight < _rules.RecommendedMinimumRiserHeight
                || riserHeight > _rules.RecommendedMaximumRiserHeight)
            {
                issues.Add(Warning(
                    "WL-ST-101",
                    nameof(definition.TotalRiserCount),
                    "The calculated riser height is outside the prototype recommended range."));
            }

            if (definition.TreadDepth < _rules.RecommendedMinimumTreadDepth
                || definition.TreadDepth > _rules.RecommendedMaximumTreadDepth)
            {
                issues.Add(Warning(
                    "WL-ST-102",
                    nameof(definition.TreadDepth),
                    "Tread depth is outside the prototype recommended range."));
            }

            var comfortValue = (2.0 * riserHeight) + definition.TreadDepth;
            if (comfortValue < _rules.MinimumComfortValue || comfortValue > _rules.MaximumComfortValue)
            {
                issues.Add(Warning(
                    "WL-ST-103",
                    nameof(definition.TreadDepth),
                    "The configured comfort formula value is outside its prototype range."));
            }

            if (definition.FlightWidth < _rules.RecommendedMinimumFlightWidth)
            {
                issues.Add(Warning(
                    "WL-ST-104",
                    nameof(definition.FlightWidth),
                    "Flight width is below the prototype recommended minimum."));
            }

            if (definition.FloorLandingDepthUp < definition.FlightWidth
                || definition.FloorLandingDepthDown < definition.FlightWidth)
            {
                issues.Add(Warning(
                    "WL-ST-105",
                    nameof(definition.FloorLandingDepth),
                    "Floor landing depth is smaller than the flight width."));
            }

            if (definition.IntermediateLandingDepthUp < definition.FlightWidth
                || definition.IntermediateLandingDepthDown < definition.FlightWidth)
            {
                issues.Add(Warning(
                    "WL-ST-106",
                    nameof(definition.IntermediateLandingDepth),
                    "Intermediate landing depth is smaller than the flight width."));
            }
        }

        private static void SplitFlights(
            StairDefinition definition,
            int totalRisers,
            out int firstFlightRisers,
            out int secondFlightRisers)
        {
            if (definition.FirstFlightRiserCount.HasValue)
            {
                firstFlightRisers = definition.FirstFlightRiserCount.Value;
                secondFlightRisers = totalRisers - firstFlightRisers;
                return;
            }

            firstFlightRisers = totalRisers / 2;
            secondFlightRisers = totalRisers - firstFlightRisers;

            if (totalRisers % 2 != 0
                && definition.SplitPreference == FlightSplitPreference.FirstFlightGetsExtraRiser)
            {
                var temporary = firstFlightRisers;
                firstFlightRisers = secondFlightRisers;
                secondFlightRisers = temporary;
            }
        }

        private static bool HasErrors(IEnumerable<ValidationIssue> issues)
        {
            foreach (var issue in issues)
            {
                if (issue.Severity == ValidationSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RequirePositive(
            double value,
            string parameterName,
            string code,
            ICollection<ValidationIssue> issues)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            {
                issues.Add(Error(code, parameterName, "Value must be a finite positive number."));
            }
        }

        private static void RequireNonNegative(
            double value,
            string parameterName,
            string code,
            ICollection<ValidationIssue> issues)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
            {
                issues.Add(Error(code, parameterName, "Value must be a finite non-negative number."));
            }
        }

        private static ValidationIssue Error(string code, string parameterName, string message)
        {
            return new ValidationIssue(code, ValidationSeverity.Error, parameterName, message);
        }

        private static ValidationIssue Warning(string code, string parameterName, string message)
        {
            return new ValidationIssue(code, ValidationSeverity.Warning, parameterName, message);
        }
    }
}
