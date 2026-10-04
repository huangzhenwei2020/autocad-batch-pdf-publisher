using System;
using System.Globalization;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class DrawingAnnotationSettings
    {
        public double TextHeight { get; set; } = 2.5;
        public double WidthFactor { get; set; } = 1;
        public double AxisDiameter { get; set; } = 8;
        public static DrawingAnnotationSettings Resolve(BuildingModelDocument model, ViewDefinitionModel view = null)
        {
            var value = view?.Annotations ?? model?.Annotations;
            return Valid(value) ? new DrawingAnnotationSettings { TextHeight=value.TextHeight,
                WidthFactor=value.WidthFactor, AxisDiameter=value.AxisDiameter } : new DrawingAnnotationSettings();
        }
        public static bool Valid(DrawingAnnotationSettings value) => value != null
            && Finite(value.TextHeight) && value.TextHeight >= .5 && value.TextHeight <= 20
            && Finite(value.WidthFactor) && value.WidthFactor >= .1 && value.WidthFactor <= 5
            && Finite(value.AxisDiameter) && value.AxisDiameter >= value.TextHeight*2 && value.AxisDiameter <= 50;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static string DimensionText(ViewDimension dimension) => string.IsNullOrWhiteSpace(dimension.Text)
            ? Math.Round(Math.Abs(dimension.To-dimension.From),MidpointRounding.AwayFromZero).ToString("0",CultureInfo.InvariantCulture)
            : dimension.Text;
    }
}
