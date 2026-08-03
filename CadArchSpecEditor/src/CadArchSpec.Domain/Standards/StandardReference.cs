using System;
using System.Collections.Generic;
using CadArchSpec.Domain.Common;

namespace CadArchSpec.Domain.Standards
{
    public sealed class StandardReference
    {
        public string StandardId { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime? PublishedDate { get; set; }
        public DateTime? EffectiveDate { get; set; }
        public DateTime? RepealedDate { get; set; }
        public StandardStatus Status { get; set; } = StandardStatus.Unknown;
        public string JurisdictionCode { get; set; } = "CN";
        public string IssuingAuthority { get; set; } = string.Empty;
        public List<string> ApplicableBuildingTypes { get; set; } = new List<string>();
        public List<string> Supersedes { get; set; } = new List<string>();
        public List<string> SupersededBy { get; set; } = new List<string>();
        public string OfficialSourceUrl { get; set; } = string.Empty;
        public DateTimeOffset? LastVerifiedAt { get; set; }
    }
}
