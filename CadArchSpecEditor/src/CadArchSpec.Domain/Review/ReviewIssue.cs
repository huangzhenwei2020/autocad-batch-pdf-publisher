using System;
using CadArchSpec.Domain.Common;

namespace CadArchSpec.Domain.Review
{
    public sealed class ReviewIssue
    {
        public Guid IssueId { get; set; }
        public string RuleId { get; set; } = string.Empty;
        public ReviewSeverity Severity { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string StandardCode { get; set; } = string.Empty;
        public string ClauseReference { get; set; } = string.Empty;
        public string TargetNodeId { get; set; } = string.Empty;
        public string TargetFieldPath { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
        public string SuggestedAction { get; set; } = string.Empty;
        public bool RequiresProfessionalConfirmation { get; set; }
    }
}
