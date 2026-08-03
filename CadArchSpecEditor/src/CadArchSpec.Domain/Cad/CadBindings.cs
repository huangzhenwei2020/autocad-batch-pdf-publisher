using System;
using System.Collections.Generic;
using CadArchSpec.Domain.Layout;

namespace CadArchSpec.Domain.Cad
{
    public sealed class CadDocumentBinding
    {
        public string DocumentFingerprint { get; set; } = string.Empty;
        public string DrawingPath { get; set; } = string.Empty;
        public string SpaceName { get; set; } = "Model";
        public List<CadEntityBinding> Entities { get; set; } = new List<CadEntityBinding>();
        public DateTimeOffset? LastSynchronizedAt { get; set; }
    }

    public sealed class CadEntityBinding
    {
        public string DocumentFingerprint { get; set; } = string.Empty;
        public string Handle { get; set; } = string.Empty;
        public Guid NodeId { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public string OriginalContentHash { get; set; } = string.Empty;
        public string LayoutProfileId { get; set; } = string.Empty;
        public int PageIndex { get; set; }
        public int ColumnIndex { get; set; }
        public CadGeometrySnapshot Geometry { get; set; } = new CadGeometrySnapshot();
    }
}
