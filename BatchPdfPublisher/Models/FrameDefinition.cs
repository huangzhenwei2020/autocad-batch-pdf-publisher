namespace BatchPdfPublisher.Models
{
    public sealed class FrameDefinition
    {
        public string BlockName { get; set; }
        public string PaperSize { get; set; }
        public string Extension { get; set; }
        public string BuildingAttributeTag { get; set; }
        public string SheetNumberAttributeTag { get; set; }
        public string SheetNameAttributeTag { get; set; }
        public string PrintScaleAttributeTag { get; set; }
        public string DisplayName => string.IsNullOrWhiteSpace(Extension) ? PaperSize : PaperSize + "+" + Extension;
    }
}
