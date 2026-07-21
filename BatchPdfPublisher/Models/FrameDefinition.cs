namespace BatchPdfPublisher.Models
{
    public sealed class FrameDefinition
    {
        public string BlockName { get; set; }
        public string PaperSize { get; set; }
        public string Extension { get; set; }
        public string PaperOrientation { get; set; }
        public string Note { get; set; }
        public string BuildingAttributeTag { get; set; }
        public string SheetNumberAttributeTag { get; set; }
        public string SheetNameAttributeTag { get; set; }
        public string PrintScaleAttributeTag { get; set; }
        public string DefaultBuilding { get; set; }
        public string DefaultSheetNumber { get; set; }
        public string DefaultSheetName { get; set; }
        public string DefaultPrintScale { get; set; }

        public string PaperDisplay => string.IsNullOrWhiteSpace(Extension) ? PaperSize : PaperSize + "+" + Extension;
        public string DisplayName
        {
            get
            {
                var parts = new[] { PaperDisplay, Note, BlockName };
                return string.Join("_", System.Linq.Enumerable.Where(parts, x => !string.IsNullOrWhiteSpace(x)));
            }
        }
    }
}
