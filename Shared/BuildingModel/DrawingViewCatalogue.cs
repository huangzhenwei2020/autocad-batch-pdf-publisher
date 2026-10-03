using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public static class DrawingViewCatalogue
    {
        public static List<ViewDefinitionModel> Resolve(BuildingModelDocument model)
        {
            if (model.DrawingViews != null)
                return BuildingModelJson.FromJson(BuildingModelJson.ToJson(model)).DrawingViews;
            var views = PlanStoreys(model).Select(SampleModelFactory.CreatePlanView).ToList();
            var defaults = SampleModelFactory.CreateDefaultViews(model.Name);
            var section = defaults.First(v => v.Kind == ViewKind.Section);
            var xs = model.Walls.SelectMany(w => new[] { w.X1, w.X2 }).ToArray();
            section.CutPosition = xs.Length == 0 ? 0 : (xs.Min() + xs.Max()) / 2;
            section.ViewDepth = 0;
            views.AddRange(defaults);
            views.Add(SampleModelFactory.CreateScheduleView(model.Name));
            views.Add(new ViewDefinitionModel { Id = "opening-elevations", Title = "门窗立面", Kind = ViewKind.OpeningElevation, Scale = 50 });
            if(model.DrawingScales!=null) foreach(var view in views) view.Scale=model.DrawingScales.For(view.Kind);
            return views;
        }

        public static List<StoreyModel> PlanStoreys(BuildingModelDocument model) => model.Storeys
            .Where(s=>string.IsNullOrWhiteSpace(s.StandardGroupId) || s.StandardGroupId==s.Id)
            .OrderBy(s=>s.Elevation).ToList();

        public static string KindName(ViewKind kind)
        {
            switch (kind) {
                case ViewKind.Plan: return "平面图";
                case ViewKind.Elevation: return "立面图";
                case ViewKind.Section: return "剖面图";
                case ViewKind.Axonometric: return "轴测图";
                case ViewKind.Schedule: return "门窗表";
                case ViewKind.OpeningElevation: return "门窗立面";
                default: return "图纸";
            }
        }
    }
}
