using System;
using System.Collections.Generic;
using CadArchSpec.Domain.Common;

namespace CadArchSpec.Domain.Tables
{
    public sealed class ArchitectureTable
    {
        public Guid TableId { get; set; }
        public int SchemaVersion { get; set; } = 1;
        public ProfessionalTableType TableType { get; set; }
        public string TableNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool RepeatHeader { get; set; } = true;
        public bool AllowSplitAcrossPages { get; set; } = true;
        public List<ArchitectureTableColumn> Columns { get; set; } = new List<ArchitectureTableColumn>();
        public List<ArchitectureTableRow> Rows { get; set; } = new List<ArchitectureTableRow>();
        public List<TableFormulaAudit> FormulaAudits { get; set; } = new List<TableFormulaAudit>();
    }

    public sealed class ArchitectureTableColumn
    {
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal WidthMillimeters { get; set; }
        public int DecimalPlaces { get; set; }
        public bool Required { get; set; }
    }

    public sealed class ArchitectureTableRow
    {
        public Guid RowId { get; set; }
        public string RowType { get; set; } = "Data";
        public bool KeepTogether { get; set; } = true;
        public List<ArchitectureTableCell> Cells { get; set; } = new List<ArchitectureTableCell>();
    }

    public sealed class ArchitectureTableCell
    {
        public Guid CellId { get; set; }
        public string ColumnKey { get; set; } = string.Empty;
        public string DisplayValue { get; set; } = string.Empty;
        public decimal? NumericValue { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string FieldPath { get; set; } = string.Empty;
        public string Formula { get; set; } = string.Empty;
        public ValueState State { get; set; }
        public string Source { get; set; } = string.Empty;
        public int RowSpan { get; set; } = 1;
        public int ColumnSpan { get; set; } = 1;
    }

    public sealed class TableFormulaAudit
    {
        public Guid AuditId { get; set; }
        public Guid CellId { get; set; }
        public string Formula { get; set; } = string.Empty;
        public string FormulaVersion { get; set; } = "1";
        public Dictionary<string, decimal> Inputs { get; set; } = new Dictionary<string, decimal>();
        public decimal Result { get; set; }
        public DateTimeOffset CalculatedAt { get; set; }
        public bool IsManuallyOverridden { get; set; }
        public string OverrideReason { get; set; } = string.Empty;
    }
}
