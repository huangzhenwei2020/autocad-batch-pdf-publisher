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
        private readonly List<PreviewEntry> _entries = new List<PreviewEntry>();
        private readonly IntegerCollection _viewports = new IntegerCollection();
        private Document _document;

        public void Show(Document document, IEnumerable<SheetItem> sheets, SheetItem selectedSheet, SheetItem errorSheet = null)
        {
            if (document == null || selectedSheet == null) { Clear(); return; }

            var visible = sheets.OrderBy(x => x.Order).Where(HasValidBounds).ToList();
            if (CanReuse(document, visible))
            {
                UpdateStyles(selectedSheet, errorSheet);
                return;
            }

            Clear();
            _document = document;

            using (document.LockDocument())
            {
                foreach (var sheet in visible)
                {
                    var isSelected = ReferenceEquals(sheet, selectedSheet);
                    var isError = ReferenceEquals(sheet, errorSheet);
                    var width = Math.Abs(sheet.MaxX - sheet.MinX);
                    var height = Math.Abs(sheet.MaxY - sheet.MinY);
                    var entry = new PreviewEntry(sheet);

                    var rectangle = new Autodesk.AutoCAD.DatabaseServices.Polyline(4)
                    {
                        Closed = true,
                        ColorIndex = isError ? 1 : isSelected ? 2 : 3,
                        LineWeight = isError ? LineWeight.LineWeight100 : LineWeight.LineWeight050
                    };
                    rectangle.AddVertexAt(0, new Point2d(sheet.MinX, sheet.MinY), 0, 0, 0);
                    rectangle.AddVertexAt(1, new Point2d(sheet.MaxX, sheet.MinY), 0, 0, 0);
                    rectangle.AddVertexAt(2, new Point2d(sheet.MaxX, sheet.MaxY), 0, 0, 0);
                    rectangle.AddVertexAt(3, new Point2d(sheet.MinX, sheet.MaxY), 0, 0, 0);
                    Add(rectangle, entry);

                    var firstDiagonal = new Line(new Point3d(sheet.MinX, sheet.MinY, 0), new Point3d(sheet.MaxX, sheet.MaxY, 0))
                    {
                        ColorIndex = isError ? 1 : isSelected ? 2 : 3,
                        LineWeight = isError ? LineWeight.LineWeight100 : LineWeight.LineWeight050
                    };
                    var secondDiagonal = new Line(new Point3d(sheet.MinX, sheet.MaxY, 0), new Point3d(sheet.MaxX, sheet.MinY, 0))
                    {
                        ColorIndex = isError ? 1 : isSelected ? 2 : 3,
                        LineWeight = isError ? LineWeight.LineWeight100 : LineWeight.LineWeight050
                    };
                    Add(firstDiagonal, entry);
                    Add(secondDiagonal, entry);

                    var label = new MText
                    {
                        Contents = isError ? "错误\\P" + sheet.Order : sheet.Order.ToString(),
                        Location = new Point3d((sheet.MinX + sheet.MaxX) / 2d, (sheet.MinY + sheet.MaxY) / 2d, 0),
                        Attachment = AttachmentPoint.MiddleCenter,
                        TextHeight = Math.Max(1d, Math.Min(width, height) * 0.62d),
                        ColorIndex = isError ? 1 : isSelected ? 2 : 3,
                        BackgroundFill = false,
                        UseBackgroundColor = false
                    };
                    Add(label, entry);
                    entry.State = PreviewState(isSelected, isError);
                    _entries.Add(entry);
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
            _entries.Clear();
            _document = null;
        }

        private void Add(Entity drawable, PreviewEntry entry)
        {
            try
            {
                TransientManager.CurrentTransientManager.AddTransient(drawable, TransientDrawingMode.DirectShortTerm, 128, _viewports);
                _drawables.Add(drawable);
                entry.Drawables.Add(drawable);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                drawable.Dispose();
            }
        }

        private bool CanReuse(Document document, IList<SheetItem> sheets)
        {
            if (!ReferenceEquals(_document, document) || _entries.Count != sheets.Count) return false;
            for (var index = 0; index < sheets.Count; index++)
                if (!_entries[index].Matches(sheets[index])) return false;
            return true;
        }

        private void UpdateStyles(SheetItem selectedSheet, SheetItem errorSheet)
        {
            var changed = false;
            foreach (var entry in _entries)
            {
                var state = PreviewState(ReferenceEquals(entry.Sheet, selectedSheet), ReferenceEquals(entry.Sheet, errorSheet));
                if (entry.State == state) continue;
                entry.State = state;
                var colorIndex = state == 2 ? 1 : state == 1 ? 2 : 3;
                var lineWeight = state == 2 ? LineWeight.LineWeight100 : LineWeight.LineWeight050;
                foreach (var drawable in entry.Drawables)
                {
                    drawable.ColorIndex = colorIndex;
                    if (drawable is Autodesk.AutoCAD.DatabaseServices.Polyline polyline) polyline.LineWeight = lineWeight;
                    else if (drawable is Line line) line.LineWeight = lineWeight;
                    try { TransientManager.CurrentTransientManager.UpdateTransient(drawable, _viewports); }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
                changed = true;
            }
            if (changed)
                try { _document?.Editor.UpdateScreen(); } catch { }
        }

        private static int PreviewState(bool selected, bool error) => error ? 2 : selected ? 1 : 0;

        private static bool HasValidBounds(SheetItem sheet)
        {
            return sheet != null && IsFinite(sheet.MinX) && IsFinite(sheet.MinY) && IsFinite(sheet.MaxX) && IsFinite(sheet.MaxY)
                && Math.Abs(sheet.MaxX - sheet.MinX) > 0 && Math.Abs(sheet.MaxY - sheet.MinY) > 0;
        }

        private sealed class PreviewEntry
        {
            public PreviewEntry(SheetItem sheet)
            {
                Sheet = sheet;
                Order = sheet.Order;
                MinX = sheet.MinX;
                MinY = sheet.MinY;
                MaxX = sheet.MaxX;
                MaxY = sheet.MaxY;
            }

            public SheetItem Sheet { get; }
            public int Order { get; }
            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
            public int State { get; set; }
            public List<Entity> Drawables { get; } = new List<Entity>();

            public bool Matches(SheetItem sheet)
            {
                return ReferenceEquals(Sheet, sheet) && Order == sheet.Order && MinX == sheet.MinX && MinY == sheet.MinY
                    && MaxX == sheet.MaxX && MaxY == sheet.MaxY;
            }
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        public void Dispose()
        {
            Clear();
        }
    }
}
