using System;
using System.Collections.Generic;
using CadArchSpec.Domain.Cad;
using CadArchSpec.Domain.Common;
using CadArchSpec.Domain.Layout;
using CadArchSpec.Domain.Tables;

namespace CadArchSpec.Domain.Documents
{
    public sealed class ArchitectureDesignSpecDocument
    {
        public Guid DocumentId { get; set; }
        public int SchemaVersion { get; set; } = 1;
        public Guid ProjectId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<ArchitectureSection> Sections { get; set; } = new List<ArchitectureSection>();
        public List<ArchitectureTable> Tables { get; set; } = new List<ArchitectureTable>();
        public DocumentLayoutProfile Layout { get; set; } = new DocumentLayoutProfile();
        public CadDocumentBinding CadBinding { get; set; }
        public DocumentRevision Revision { get; set; } = new DocumentRevision();
    }

    public sealed class ArchitectureSection
    {
        public Guid SectionId { get; set; }
        public string SectionType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public RequirementState RequirementState { get; set; }
        public List<DocumentNode> Content { get; set; } = new List<DocumentNode>();
        public List<string> ApplicableRuleIds { get; set; } = new List<string>();
        public List<string> ReferencedProjectFields { get; set; } = new List<string>();
        public ReviewState ReviewState { get; set; }
    }

    public sealed class DocumentNode
    {
        public Guid NodeId { get; set; }
        public DocumentNodeType NodeType { get; set; }
        public string Text { get; set; } = string.Empty;
        public string FieldPath { get; set; } = string.Empty;
        public string StandardId { get; set; } = string.Empty;
        public string TableId { get; set; } = string.Empty;
        public string DrawingReference { get; set; } = string.Empty;
        public int Level { get; set; }
        public bool IsLockedField { get; set; }
        public List<DocumentNode> Children { get; set; } = new List<DocumentNode>();
    }

    public sealed class DocumentRevision
    {
        public int RevisionNumber { get; set; }
        public string RevisionCode { get; set; } = "A";
        public string Description { get; set; } = string.Empty;
        public string ChangedBy { get; set; } = string.Empty;
        public DateTimeOffset? ChangedAt { get; set; }
        public string ParentContentHash { get; set; } = string.Empty;
    }
}
