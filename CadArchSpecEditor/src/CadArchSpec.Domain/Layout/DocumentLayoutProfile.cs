using System;

namespace CadArchSpec.Domain.Layout
{
    public sealed class DocumentLayoutProfile
    {
        public string LayoutProfileId { get; set; } = "A1-Landscape-2C";
        public string PaperName { get; set; } = "A1";
        public bool Landscape { get; set; } = true;
        public decimal PaperWidthMillimeters { get; set; } = 841m;
        public decimal PaperHeightMillimeters { get; set; } = 594m;
        public decimal MarginLeftMillimeters { get; set; } = 25m;
        public decimal MarginTopMillimeters { get; set; } = 20m;
        public decimal MarginRightMillimeters { get; set; } = 190m;
        public decimal MarginBottomMillimeters { get; set; } = 20m;
        public int ColumnCount { get; set; } = 2;
        public decimal ColumnGapMillimeters { get; set; } = 12m;
        public string TextStyle { get; set; } = "WL-文字-正文";
        public decimal BodyTextHeightMillimeters { get; set; } = 3.5m;
        public decimal HeadingTextHeightMillimeters { get; set; } = 5m;
        public decimal TableTextHeightMillimeters { get; set; } = 3.5m;
        public decimal LineSpacingFactor { get; set; } = 1.35m;
        public string PageTitlePattern { get; set; } = "建筑设计说明（{page}）";
        public string DrawingNumberPattern { get; set; } = "建施-{page:00}";
    }

    public sealed class CadGeometrySnapshot
    {
        public decimal X { get; set; }
        public decimal Y { get; set; }
        public decimal Width { get; set; }
        public decimal Height { get; set; }
        public decimal RotationDegrees { get; set; }
    }

    public sealed class LayoutBlock
    {
        public Guid SourceNodeId { get; set; }
        public int PageIndex { get; set; }
        public int ColumnIndex { get; set; }
        public decimal XMillimeters { get; set; }
        public decimal YMillimeters { get; set; }
        public decimal WidthMillimeters { get; set; }
        public decimal HeightMillimeters { get; set; }
        public bool KeepTogether { get; set; }
    }
}
