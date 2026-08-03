using System.Collections.Generic;
using CadArchSpec.RuleEngine;
using Xunit;

namespace CadArchSpec.Stage0.Tests
{
    public sealed class FormulaEvaluatorTests
    {
        private readonly FormulaEvaluator _evaluator = new FormulaEvaluator();

        [Fact]
        public void CalculatesPlotRatioWithFieldReferences()
        {
            var variables = new Dictionary<string, decimal>
            {
                { "building.aboveGroundAreaSquareMeters", 30000m },
                { "planning.siteAreaSquareMeters", 12000m }
            };

            var result = _evaluator.Evaluate(
                "ROUND(building.aboveGroundAreaSquareMeters / planning.siteAreaSquareMeters, 2)",
                variables);

            Assert.Equal(2.50m, result);
        }

        [Theory]
        [InlineData("SUM(1, 2, 3)", 6)]
        [InlineData("MIN(8, 3, 5)", 3)]
        [InlineData("MAX(8, 3, 5)", 8)]
        [InlineData("ABS(-12)", 12)]
        [InlineData("COUNT(8, 3, 5)", 3)]
        [InlineData("IF(10 > 5, 100, 0)", 100)]
        public void SupportsOnlyWhitelistedFunctions(string formula, int expected)
        {
            Assert.Equal(expected, _evaluator.Evaluate(formula, new Dictionary<string, decimal>()));
        }

        [Fact]
        public void RejectsUnknownFunction()
        {
            Assert.Throws<FormulaException>(() => _evaluator.Evaluate("EXEC(1)", new Dictionary<string, decimal>()));
        }

        [Fact]
        public void RejectsUnknownField()
        {
            Assert.Throws<FormulaException>(() => _evaluator.Evaluate("missing.field + 1", new Dictionary<string, decimal>()));
        }
    }
}
