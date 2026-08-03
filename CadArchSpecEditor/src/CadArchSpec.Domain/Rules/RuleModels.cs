using System;
using System.Collections.Generic;
using CadArchSpec.Domain.Common;

namespace CadArchSpec.Domain.Rules
{
    public sealed class RulePackage
    {
        public int SchemaVersion { get; set; } = 1;
        public string PackageId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string JurisdictionCode { get; set; } = "CN";
        public string DisplayName { get; set; } = string.Empty;
        public string Status { get; set; } = "Draft";
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public DateTimeOffset? VerifiedAt { get; set; }
        public string Signature { get; set; } = string.Empty;
        public List<ReviewRule> Rules { get; set; } = new List<ReviewRule>();
    }

    public sealed class ReviewRule
    {
        public string RuleId { get; set; } = string.Empty;
        public int Version { get; set; } = 1;
        public string Title { get; set; } = string.Empty;
        public string JurisdictionCode { get; set; } = "CN";
        public List<ArchitectureBuildingType> BuildingTypes { get; set; } = new List<ArchitectureBuildingType>();
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public ReviewSeverity Severity { get; set; }
        public string CheckType { get; set; } = string.Empty;
        public RuleTarget Target { get; set; } = new RuleTarget();
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
        public string Message { get; set; } = string.Empty;
        public bool RequiresProfessionalConfirmation { get; set; }
        public List<RuleReference> References { get; set; } = new List<RuleReference>();
    }

    public sealed class RuleTarget
    {
        public string SectionType { get; set; } = string.Empty;
        public string FieldPath { get; set; } = string.Empty;
        public string TableType { get; set; } = string.Empty;
        public string ColumnKey { get; set; } = string.Empty;
    }

    public sealed class RuleReference
    {
        public string StandardCode { get; set; } = string.Empty;
        public string Clause { get; set; } = string.Empty;
        public string OfficialSourceUrl { get; set; } = string.Empty;
    }
}
