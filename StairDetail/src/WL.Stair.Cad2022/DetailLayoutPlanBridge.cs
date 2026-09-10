using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using WL.Stair.CadShared.PlanCapture;
using WL.Stair.Core.Domain;

namespace WL.Stair.Cad2022
{
    public sealed class DetailLayoutPlanResult
    {
        public string Name { get; set; }
        public string ScaleText { get; set; }
        public string CacheRelativePath { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double CacheLayoutOffsetX { get; set; }
        public double CacheLayoutOffsetY { get; set; }
        // The unexpanded user frame.  The detail-layout host uses this range
        // to obtain a Tianzheng room name without changing the cached plan.
        public double SelectionMinX { get; set; }
        public double SelectionMinY { get; set; }
        public double SelectionMaxX { get; set; }
        public double SelectionMaxY { get; set; }
        public IList<DetailLayoutPlanPreviewLine> PreviewLines { get; set; }
    }

    public sealed class DetailLayoutPlanPreviewLine
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }

    public static class DetailLayoutPlanBridge
    {
        public static DetailLayoutPlanResult Capture(Document document,
            string projectName, string name, int targetScale, int captureMode,
            Func<double, double, double, double, string> resolveRoomName)
        {
            if (document == null) throw new ArgumentNullException("document");
            name = string.IsNullOrWhiteSpace(name) ? "小平面" : name.Trim();
            targetScale = Math.Max(1, targetScale);
            var key = "DETAIL-" + Guid.NewGuid().ToString("N");
            var capture = new StairPlanCaptureService();
            var source = captureMode == 2
                ? capture.CaptureFrame(document, key, name, 300.0)
                : capture.CaptureTianzhengStair(document, key, name, 300.0,
                    () => System.Windows.MessageBox.Show(
                        "CAD 中已显示识别边界，是否接受当前小平面？",
                        "确认小平面", System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question)
                        == System.Windows.MessageBoxResult.Yes);
            if (source == null) return null;

            var boundary = source.BoundaryPoints ?? new List<StairPlanPointDefinition>();
            var hasBoundary = boundary.Count >= 3;
            if (hasBoundary && resolveRoomName != null)
            {
                var roomName = resolveRoomName(
                    boundary.Min(point => point.X),
                    boundary.Min(point => point.Y),
                    boundary.Max(point => point.X),
                    boundary.Max(point => point.Y));
                if (!string.IsNullOrWhiteSpace(roomName))
                {
                    roomName = roomName.Trim();
                    name = roomName.EndsWith("平面图", StringComparison.Ordinal)
                        ? roomName : roomName + "平面图";
                }
            }

            source.FloorId = key;
            source.StoreyId = key;
            source.FloorLabel = name;
            source.DisplayName = name;
            source.RepeatCount = 1;
            source.TargetScale = targetScale;
            var project = StairProjectDefinition.CreateDefault();
            project.ProjectName = string.IsNullOrWhiteSpace(projectName)
                ? "默认项目" : projectName.Trim();
            project.StairNumber = "大样排版小平面";
            project.DrawingScale = targetScale;
            var cache = new StairPlanCacheService();
            cache.Build(document, project, source, name);

            double offsetX, offsetY, width, height;
            StairPlanCacheService.GetLayoutRange(source, out offsetX, out offsetY,
                out width, out height);
            return new DetailLayoutPlanResult
            {
                Name = name,
                ScaleText = "1:" + targetScale,
                CacheRelativePath = source.CacheRelativePath,
                Width = width,
                Height = height,
                CacheLayoutOffsetX = offsetX,
                CacheLayoutOffsetY = offsetY,
                SelectionMinX = hasBoundary ? boundary.Min(point => point.X) : 0.0,
                SelectionMinY = hasBoundary ? boundary.Min(point => point.Y) : 0.0,
                SelectionMaxX = hasBoundary ? boundary.Max(point => point.X) : 0.0,
                SelectionMaxY = hasBoundary ? boundary.Max(point => point.Y) : 0.0,
                PreviewLines = cache.ReadPreviewLines(source, 2500)
                    .Select(line => new DetailLayoutPlanPreviewLine
                    {
                        X1 = line.X1,
                        Y1 = line.Y1,
                        X2 = line.X2,
                        Y2 = line.Y2
                    }).ToList()
            };
        }

        public static int Insert(Document document, string cacheRelativePath,
            double x, double y, double z, double offsetX, double offsetY)
        {
            var source = new StairPlanSourceDefinition
            {
                CacheRelativePath = cacheRelativePath
            };
            return new StairPlanCacheService().Insert(document, source,
                new Point3d(x - offsetX, y - offsetY, z));
        }
    }
}
