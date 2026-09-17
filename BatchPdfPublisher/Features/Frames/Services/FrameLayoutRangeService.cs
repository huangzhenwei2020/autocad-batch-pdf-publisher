using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Models;
using System;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    internal static class FrameLayoutRangeService
    {
        public static bool HasValidRange(FrameDefinition frame)
        {
            if (frame == null || !frame.HasLayoutRange) return false;
            var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
            return paper != null && paper.Length >= 2
                && frame.LayoutLeftMargin >= 0d && frame.LayoutRightMargin >= 0d
                && frame.LayoutTopMargin >= 0d && frame.LayoutBottomMargin >= 0d
                && frame.LayoutLeftMargin + frame.LayoutRightMargin < paper[0]
                && frame.LayoutTopMargin + frame.LayoutBottomMargin < paper[1];
        }

        public static void SetRange(FrameDefinition frame, double left, double right, double top, double bottom)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            frame.LayoutLeftMargin = Math.Max(0d, left);
            frame.LayoutRightMargin = Math.Max(0d, right);
            frame.LayoutTopMargin = Math.Max(0d, top);
            frame.LayoutBottomMargin = Math.Max(0d, bottom);
            frame.HasLayoutRange = true;
            if (!HasValidRange(frame)) throw new InvalidOperationException("框选的图框排版范围无效。");
        }

        public static void SaveRange(FrameDefinition frame, double left, double right, double top, double bottom)
        {
            SetRange(frame, left, right, top, bottom);
            var store = new PublishPlanStore(); var frames = store.LoadFrames();
            var stored = frames.FirstOrDefault(value => value != null
                && ((!string.IsNullOrWhiteSpace(frame.RegistrationId) && string.Equals(value.RegistrationId, frame.RegistrationId, StringComparison.OrdinalIgnoreCase))
                    || (string.IsNullOrWhiteSpace(frame.RegistrationId) && string.Equals(value.BlockName, frame.BlockName, StringComparison.OrdinalIgnoreCase))));
            if (stored == null) throw new InvalidOperationException("未找到当前图框登记，无法写入排版范围。");
            SetRange(stored, left, right, top, bottom);
            store.SaveFrames(frames);
        }

        public static bool PromptAndSaveRange(Document document, FrameDefinition frame)
        {
            if (document == null || frame == null) return false;
            var anchor = DetailLayoutService.InsertFrameForRange(document, frame, 1);
            if (anchor == null) return false;
            var selected = DetailLayoutService.PromptLayoutRange(document, frame, 1, anchor, new DetailLayoutOptions());
            if (selected == null) return false;
            SaveRange(frame, selected.LeftMargin, selected.RightMargin, selected.TopMargin, selected.BottomMargin);
            document.Editor.WriteMessage("\n图框排版范围已重新登记成功：" + Describe(frame) + "。\n");
            return true;
        }

        public static string Describe(FrameDefinition frame)
        {
            return !HasValidRange(frame) ? "未登记"
                : "左 " + frame.LayoutLeftMargin.ToString("0.##") + "，右 " + frame.LayoutRightMargin.ToString("0.##")
                    + "，上 " + frame.LayoutTopMargin.ToString("0.##") + "，下 " + frame.LayoutBottomMargin.ToString("0.##") + " mm";
        }
    }
}
