using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>Derive all floor elevations from one datum and each floor-to-floor height.</summary>
    public static class StoreyElevationLayout
    {
        public static List<StoreyModel> Resolve(IEnumerable<StoreyModel> ordered,
            string anchorId, double anchorElevation)
        {
            var result = ordered.Select(s => new StoreyModel
            { Id = s.Id, Name = s.Name, TemplateStoreyId = s.TemplateStoreyId,
                Height = s.Height, Elevation = s.Elevation }).ToList();
            var anchor = result.FindIndex(s => string.Equals(s.Id, anchorId,
                StringComparison.OrdinalIgnoreCase));
            if (anchor < 0 || double.IsNaN(anchorElevation) || double.IsInfinity(anchorElevation)
                || result.Any(s => double.IsNaN(s.Height) || double.IsInfinity(s.Height) || s.Height <= 0d))
                throw new ArgumentException("基准楼层、基准标高或层高无效。");
            result[anchor].Elevation = anchorElevation;
            for (var i = anchor + 1; i < result.Count; i++)
                result[i].Elevation = result[i - 1].Elevation + result[i - 1].Height;
            for (var i = anchor - 1; i >= 0; i--)
                result[i].Elevation = result[i + 1].Elevation - result[i].Height;
            return result;
        }
    }
}
