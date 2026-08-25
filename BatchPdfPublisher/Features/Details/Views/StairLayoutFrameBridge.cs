using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Linq;

namespace BatchPdfPublisher.Views
{
    public sealed class StairLayoutFrameInfo
    {
        public string RegistrationId { get; set; }
        public string DisplayName { get; set; }
        public double PageWidth { get; set; }
        public double PageHeight { get; set; }
        public double LeftMargin { get; set; }
        public double RightMargin { get; set; }
        public double TopMargin { get; set; }
        public double BottomMargin { get; set; }
    }

    public static class StairLayoutFrameBridge
    {
        public static StairLayoutFrameInfo[] GetRegisteredFrames()
        {
            return new PublishPlanStore().LoadFrames()
                .Where(frame => frame != null && !string.IsNullOrWhiteSpace(frame.BlockName)
                    && FrameLayoutRangeService.HasValidRange(frame))
                .Select(frame =>
                {
                    var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension,
                        string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
                    return new StairLayoutFrameInfo
                    {
                        RegistrationId = frame.RegistrationId,
                        DisplayName = frame.DisplayName,
                        PageWidth = paper[0],
                        PageHeight = paper[1],
                        LeftMargin = frame.LayoutLeftMargin,
                        RightMargin = frame.LayoutRightMargin,
                        TopMargin = frame.LayoutTopMargin,
                        BottomMargin = frame.LayoutBottomMargin
                    };
                })
                .ToArray();
        }

        public static void InsertFrames(
            string registrationId,
            int scale,
            double x,
            double y,
            double z,
            int pageCount,
            double pageGap)
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) throw new InvalidOperationException("当前没有活动图纸。");
            var frame = new PublishPlanStore().LoadFrames().FirstOrDefault(value => value != null
                && string.Equals(value.RegistrationId, registrationId, StringComparison.OrdinalIgnoreCase));
            if (frame == null) throw new InvalidOperationException("找不到所选登记图框，请刷新楼梯窗口后重试。");
            DetailLayoutService.InsertRegisteredFramesAt(document, frame, scale,
                new Point3d(x, y, z), pageCount, pageGap);
        }
    }
}
