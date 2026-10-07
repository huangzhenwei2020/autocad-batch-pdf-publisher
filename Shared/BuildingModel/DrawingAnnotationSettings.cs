using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public static class DrawingLineWeights
    {
        // AutoCAD LineWeight values, in hundredths of a paper millimetre.
        public static readonly int[] Values = { 0,5,9,13,15,18,20,25,30,35,40,50,53,60,70,80,90,100,106,120,140,158,200,211 };
        public static bool Valid(Dictionary<string,int> weights) => weights == null
            || weights.All(p=>ViewLayers.Find(p.Key)!=null && Values.Contains(p.Value));
        public static int Resolve(string layer,int? weight=null) => weight.HasValue && Values.Contains(weight.Value)
            ? weight.Value : ViewLayers.Find(layer)?.LineWeight ?? 13;
        public static ViewDocument Apply(ViewDocument document,ViewDefinitionModel definition)
        {
            int? Weight(string layer) => definition.LineWeights!=null && layer!=null
                && definition.LineWeights.TryGetValue(layer,out var value) && Values.Contains(value) ? (int?)value : null;
            foreach(var line in document.Lines)line.LineWeight=Weight(line.Layer);
            foreach(var text in document.Texts)text.LineWeight=Weight(text.Layer);
            foreach(var circle in document.Circles)circle.LineWeight=Weight(circle.Layer);
            foreach(var hatch in document.Hatches)hatch.LineWeight=Weight(hatch.Layer);
            foreach(var dimension in document.Dimensions)dimension.LineWeight=Weight(dimension.Layer);
            return document;
        }
    }

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
