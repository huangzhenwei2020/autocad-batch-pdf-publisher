using System;
using System.Collections.Generic;
using System.Linq;
using WL.Stair.Core.Validation;

namespace WL.Stair.Core.Calculation
{
    public sealed class StairCalculationOutcome
    {
        public StairCalculationOutcome(StairCalculationResult result, IEnumerable<ValidationIssue> issues)
        {
            Result = result;
            Issues = (issues ?? Enumerable.Empty<ValidationIssue>()).ToArray();
        }

        public StairCalculationResult Result { get; }

        public IReadOnlyList<ValidationIssue> Issues { get; }

        public bool IsSuccess => Result != null && !Issues.Any(issue => issue.Severity == ValidationSeverity.Error);
    }
}

