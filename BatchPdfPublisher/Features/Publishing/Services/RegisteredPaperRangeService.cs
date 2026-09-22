using System;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    internal static class RegisteredPaperRangeService
    {
        public static bool TryResolve(BlockReference reference, SheetItem sheet, Transaction transaction, out Extents3d extents)
        {
            extents = default(Extents3d);
            if (reference == null || sheet == null || transaction == null) return false;
            var paper = PaperSizeCatalog.GetSize(sheet.Frame, sheet.Extension,
                string.IsNullOrWhiteSpace(sheet.PaperOrientation) ? "横向" : sheet.PaperOrientation);
            return TryResolve(reference, paper, transaction, out extents);
        }

        public static bool TryResolve(BlockReference reference, double[] paper, Transaction transaction, out Extents3d extents)
        {
            extents = default(Extents3d);
            if (reference == null || paper == null || paper.Length < 2 || transaction == null) return false;
            var expectedRatio = Math.Max(paper[0], paper[1]) / Math.Max(1e-9d, Math.Min(paper[0], paper[1]));
            var bestArea = 0d;
            try
            {
                var definitionId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                var definition = transaction.GetObject(definitionId, OpenMode.ForRead, false) as BlockTableRecord;
                if (definition == null) return false;
                foreach (ObjectId id in definition)
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (!IsClosedPolyline(entity)) continue;
                    Extents3d candidate;
                    try
                    {
                        candidate = entity.GeometricExtents;
                        candidate.TransformBy(reference.BlockTransform);
                    }
                    catch { continue; }
                    var width = Math.Abs(candidate.MaxPoint.X - candidate.MinPoint.X);
                    var height = Math.Abs(candidate.MaxPoint.Y - candidate.MinPoint.Y);
                    if (width < 1e-6d || height < 1e-6d) continue;
                    var ratio = Math.Max(width, height) / Math.Min(width, height);
                    if (Math.Abs(ratio - expectedRatio) / expectedRatio > .02d) continue;
                    var area = width * height;
                    if (area <= bestArea) continue;
                    bestArea = area;
                    extents = candidate;
                }
            }
            catch { return false; }
            return bestArea > 0d;
        }

        public static bool TryRefresh(Database database, SheetItem sheet, Transaction transaction)
        {
            if (database == null || sheet == null || transaction == null || string.IsNullOrWhiteSpace(sheet.BlockHandle)) return false;
            try
            {
                long handleValue;
                if (!long.TryParse(sheet.BlockHandle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handleValue)) return false;
                var id = database.GetObjectId(false, new Handle(handleValue), 0);
                var reference = transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                Extents3d extents;
                if (reference == null || !TryResolve(reference, sheet, transaction, out extents)) return false;
                sheet.MinX = extents.MinPoint.X;
                sheet.MinY = extents.MinPoint.Y;
                sheet.MaxX = extents.MaxPoint.X;
                sheet.MaxY = extents.MaxPoint.Y;
                return true;
            }
            catch { return false; }
        }

        private static bool IsClosedPolyline(Entity entity)
        {
            var lightweight = entity as Polyline;
            if (lightweight != null) return lightweight.Closed;
            var polyline2d = entity as Polyline2d;
            if (polyline2d != null) return polyline2d.Closed;
            var polyline3d = entity as Polyline3d;
            return polyline3d != null && polyline3d.Closed;
        }
    }
}
