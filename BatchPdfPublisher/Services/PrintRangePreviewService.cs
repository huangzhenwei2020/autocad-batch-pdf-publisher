using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public sealed class PrintRangePreviewService : IDisposable
    {
        private readonly List<Entity> _drawables = new List<Entity>();
        private readonly IntegerCollection _viewports = new IntegerCollection();

        public void Show(Document document, IEnumerable<SheetItem> sheets, SheetItem selectedSheet)
        {
            Clear();
            if (document == null || selectedSheet == null) return;

            using (document.LockDocument())
            {
                foreach (var sheet in sheets.OrderBy(x => x.Order))
                {
                    var isSelected = ReferenceEquals(sheet, selectedSheet);
                    var width = Math.Abs(sheet.MaxX - sheet.MinX);
                    var height = Math.Abs(sheet.MaxY - sheet.MinY);
                    if (!IsFinite(sheet.MinX) || !IsFinite(sheet.MinY) || !IsFinite(sheet.MaxX) || !IsFinite(sheet.MaxY) ||
                        width <= 0 || height <= 0) continue;

                    var rectangle = new Autodesk.AutoCAD.DatabaseServices.Polyline(4)
                    {
                        Closed = true,
                        ColorIndex = isSelected ? 1 : 2,
                        LineWeight = LineWeight.LineWeight050
                    };
                    rectangle.AddVertexAt(0, new Point2d(sheet.MinX, sheet.MinY), 0, 0, 0);
                    rectangle.AddVertexAt(1, new Point2d(sheet.MaxX, sheet.MinY), 0, 0, 0);
                    rectangle.AddVertexAt(2, new Point2d(sheet.MaxX, sheet.MaxY), 0, 0, 0);
                    rectangle.AddVertexAt(3, new Point2d(sheet.MinX, sheet.MaxY), 0, 0, 0);
                    Add(rectangle);

                    var firstDiagonal = new Line(new Point3d(sheet.MinX, sheet.MinY, 0), new Point3d(sheet.MaxX, sheet.MaxY, 0))
                    {
                        ColorIndex = isSelected ? 1 : 2,
                        LineWeight = LineWeight.LineWeight050
                    };
                    var secondDiagonal = new Line(new Point3d(sheet.MinX, sheet.MaxY, 0), new Point3d(sheet.MaxX, sheet.MinY, 0))
                    {
                        ColorIndex = isSelected ? 1 : 2,
                        LineWeight = LineWeight.LineWeight050
                    };
                    Add(firstDiagonal);
                    Add(secondDiagonal);

                    var label = new MText
                    {
                        Contents = sheet.Order.ToString(),
                        Location = new Point3d((sheet.MinX + sheet.MaxX) / 2d, (sheet.MinY + sheet.MaxY) / 2d, 0),
                        Attachment = AttachmentPoint.MiddleCenter,
                        TextHeight = Math.Max(1d, Math.Min(width, height) * 0.62d),
                        ColorIndex = isSelected ? 1 : 2,
                        BackgroundFill = false,
                        UseBackgroundColor = false
                    };
                    Add(label);
                }
                document.Editor.UpdateScreen();
            }
        }

        public void Clear()
        {
            foreach (var drawable in _drawables)
            {
                try { TransientManager.CurrentTransientManager.EraseTransient(drawable, _viewports); }
                catch { }
                drawable.Dispose();
            }
            _drawables.Clear();
        }

        private void Add(Entity drawable)
        {
            try
            {
                TransientManager.CurrentTransientManager.AddTransient(drawable, TransientDrawingMode.DirectShortTerm, 128, _viewports);
                _drawables.Add(drawable);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                drawable.Dispose();
            }
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        public void Dispose()
        {
            Clear();
        }
    }
}
