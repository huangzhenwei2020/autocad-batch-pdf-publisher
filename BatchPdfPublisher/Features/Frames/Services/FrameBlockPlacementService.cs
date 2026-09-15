using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using System;

namespace BatchPdfPublisher.Services
{
    internal sealed class FrameBlockPlacement
    {
        public Point3d DefinitionMinimum { get; set; }
        public double DefinitionWidth { get; set; }
        public double DefinitionHeight { get; set; }
        public double Factor { get; set; }

        public Point3d PositionForLowerLeft(Point3d lowerLeft)
        {
            return new Point3d(
                lowerLeft.X - DefinitionMinimum.X * Factor,
                lowerLeft.Y - DefinitionMinimum.Y * Factor,
                lowerLeft.Z - DefinitionMinimum.Z * Factor);
        }
    }

    /// <summary>
    /// Places registered frames by their measured geometry instead of assuming that
    /// every source block was authored at paper 1:1 with its origin at the lower-left.
    /// </summary>
    internal static class FrameBlockPlacementService
    {
        public static FrameBlockPlacement Measure(
            BlockTableRecord definition,
            Transaction transaction,
            FrameDefinition frame,
            int drawingScale)
        {
            if (definition == null) throw new ArgumentNullException("definition");
            if (transaction == null) throw new ArgumentNullException("transaction");
            if (frame == null) throw new ArgumentNullException("frame");

            var first = true;
            var extents = new Extents3d();
            foreach (ObjectId id in definition)
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (entity == null) continue;
                try
                {
                    if (first) { extents = entity.GeometricExtents; first = false; }
                    else extents.AddExtents(entity.GeometricExtents);
                }
                catch { }
            }

            if (first)
                throw new InvalidOperationException("登记图框块“" + definition.Name + "”没有可计算范围的图形。");
            var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
            var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            if (width < 1e-6 || height < 1e-6)
                throw new InvalidOperationException("登记图框块“" + definition.Name + "”的尺寸无效。");

            var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension,
                string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
            if (paper == null || paper.Length < 2)
                throw new InvalidOperationException("登记图框“" + frame.DisplayName + "”的纸张尺寸无效。");
            var targetWidth = paper[0] * Math.Max(1, drawingScale);
            var targetHeight = paper[1] * Math.Max(1, drawingScale);
            var factor = Math.Min(targetWidth / width, targetHeight / height);
            if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0d)
                throw new InvalidOperationException("登记图框“" + frame.DisplayName + "”无法计算插入比例。");

            return new FrameBlockPlacement
            {
                DefinitionMinimum = extents.MinPoint,
                DefinitionWidth = width,
                DefinitionHeight = height,
                Factor = factor
            };
        }
    }
}
