using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using BatchPdfPublisher.Models;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Services
{
    internal static class ExternalCadPreview
    {
        internal static IEnumerable<DetailPreviewPrimitive> Asset(ExternalCadAsset asset, Point3d anchor, double x, double y, double factor,
            int turns = 0, int limit = int.MaxValue)
        {
            foreach (var original in Sample(asset.Preview, limit))
            {
                var a = Rotate((original.X1 - anchor.X) * factor, (original.Y1 - anchor.Y) * factor, turns);
                var b = Rotate((original.X2 - anchor.X) * factor, (original.Y2 - anchor.Y) * factor, turns);
                yield return ExternalCadSheet.Shape(original.Kind, x + a.X, y + a.Y, x + b.X, y + b.Y, original.Text);
            }
        }

        internal static IEnumerable<DetailPreviewPrimitive> Sample(IReadOnlyList<DetailPreviewPrimitive> parts, int limit)
        {
            var count = Math.Min(parts.Count, limit);
            for (var index = 0; index < count; index++)
                yield return parts[count <= 1 ? 0 : (int)((long)index * (parts.Count - 1) / (count - 1))];
        }

        internal static Point2d Rotate(double x, double y, int turns)
        {
            switch (turns % 4)
            {
                case 1: return new Point2d(-y, x);
                case 2: return new Point2d(-x, -y);
                case 3: return new Point2d(y, -x);
                default: return new Point2d(x, y);
            }
        }

        internal static int NextClockwiseTurn(int turns) => (turns + 3) % 4;

        internal static List<DetailPreviewPrimitive> Frame(Document doc, FrameDefinition frame, int scale)
        {
            if (frame == null) return new List<DetailPreviewPrimitive>();
            var path = FrameTemplatePaths.ExistingSnapshot(frame);
            using (var snapshot = path == null ? null : ExternalCadLibrary.OpenDrawing(path))
            {
                var database = snapshot ?? doc.Database;
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var name = snapshot == null ? frame.BlockName : BlockTableRecord.ModelSpace;
                    if (!table.Has(name)) throw new InvalidOperationException("此图框缺少可用模板，请先补齐登记图框。");
                    var record = (BlockTableRecord)transaction.GetObject(table[name], OpenMode.ForRead);
                    var placement = FrameBlockPlacementService.Measure(record, transaction, frame, scale);
                    var preview = new List<DetailPreviewPrimitive>();
                    foreach (ObjectId id in record)
                        DetailLayoutService.CapturePreview(transaction.GetObject(id, OpenMode.ForRead) as Entity, preview, 0, true);
                    return preview.Where(part => part.Kind != DetailPreviewPrimitiveKind.Box).Select(part =>
                        ExternalCadSheet.Shape(part.Kind,
                            (part.X1 - placement.DefinitionMinimum.X) * placement.Factor, (part.Y1 - placement.DefinitionMinimum.Y) * placement.Factor,
                            (part.X2 - placement.DefinitionMinimum.X) * placement.Factor, (part.Y2 - placement.DefinitionMinimum.Y) * placement.Factor, part.Text)).ToList();
                }
            }
        }

        internal static IEnumerable<DetailPreviewPrimitive> Page(ExternalCadSheet sheet, int page, IReadOnlyList<DetailPreviewPrimitive> frame,
            int limit = 4500)
        {
            var slots = sheet.Slots.Where(slot => slot.Page == page).ToList();
            var frameBudget = Math.Min(frame.Count, limit / 4);
            var slotBudget = Math.Max(1, (limit - frameBudget) / Math.Max(1, slots.Count));
            foreach (var part in Sample(frame, frameBudget)) yield return part;
            foreach (var slot in slots)
            {
                foreach (var part in Asset(slot.Asset, slot.Asset.Bounds.MinPoint, slot.X, slot.Y,
                    ExternalCadLibrary.ToUnits(slot.Asset.Units, UnitsValue.Millimeters), limit: slotBudget)) yield return part;
                foreach (var part in ExternalCadSheet.Label(slot.Title, slot.Number, sheet.Scale, slot.X + slot.Width / 2, slot.Y))
                    yield return part;
            }
        }

        internal static IEnumerable<DetailPreviewPrimitive> Single(ExternalCadSheet sheet, ExternalCadAsset asset, int turns)
        {
            var factor = ExternalCadLibrary.ToUnits(asset.Units, UnitsValue.Millimeters);
            foreach (var part in Asset(asset, asset.Base, 0, 0, factor, turns, 500)) yield return part;
            var a = Rotate((asset.Bounds.MinPoint.X - asset.Base.X) * factor, (asset.Bounds.MinPoint.Y - asset.Base.Y) * factor, turns);
            var b = Rotate((asset.Bounds.MaxPoint.X - asset.Base.X) * factor, (asset.Bounds.MaxPoint.Y - asset.Base.Y) * factor, turns);
            foreach (var part in ExternalCadSheet.Label(sheet.Title ? sheet.SingleTitle : null, sheet.Number ? sheet.SingleNumber : null,
                sheet.Scale, (a.X + b.X) / 2, Math.Min(a.Y, b.Y))) yield return part;
        }
    }

    internal sealed class ExternalCadInsertionJig : DrawJig
    {
        private readonly Document _document;
        private readonly bool _single;
        private readonly bool _hasFrame;
        private readonly Func<int, IEnumerable<DetailPreviewPrimitive>> _geometry;
        private readonly double _unit;
        private List<DetailPreviewPrimitive> _preview;
        private bool _changed;
        private bool _hasPoint;
        private Point3d _point;
        private int _turns;

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        internal ExternalCadInsertionJig(Document document, bool single, bool hasFrame, Func<int, IEnumerable<DetailPreviewPrimitive>> geometry)
        {
            _document = document;
            _single = single;
            _hasFrame = hasFrame;
            _geometry = geometry;
            _unit = ExternalCadLibrary.ToUnits(UnitsValue.Millimeters, document.Database.Insunits);
            _preview = _geometry(0).ToList();
        }

        internal bool Pick(out Point3d point, out int turns)
        {
            if (_single) AcApp.PreTranslateMessage += ApplyRotation;
            try
            {
                var result = _document.Editor.Drag(this);
                point = _point;
                turns = _turns;
                return result.Status == PromptStatus.OK && _hasPoint;
            }
            finally { if (_single) AcApp.PreTranslateMessage -= ApplyRotation; }
        }

        private void ApplyRotation(object sender, PreTranslateMessageEventArgs args)
        {
            if (_document != AcApp.DocumentManager.MdiActiveDocument) return;
            const int down = 0x204, up = 0x205, twice = 0x206;
            var message = args.Message;
            if (message.message != down && message.message != up && message.message != twice) return;
            args.Handled = true;
            if (message.message != up) return;
            _turns = ExternalCadPreview.NextClockwiseTurn(_turns);
            _changed = true;
            PostMessage(message.hwnd, 0x200, message.wParam, message.lParam);
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var hint = _single
                ? "\n右键顺时针旋转 90°（已顺时针转 " + (((4 - _turns) % 4) * 90) + "°），左键插入，Esc 取消："
                : (_hasFrame ? "\n指定第一张图框左下角" : "\n指定整批插入基点") + "，左键放置整批，Esc 取消：";
            var result = prompts.AcquirePoint(new JigPromptPointOptions(hint)
            {
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.GovernedByOrthoMode
            });
            if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;
            var point = result.Value.TransformBy(_document.Editor.CurrentUserCoordinateSystem);
            if (_hasPoint && _point.IsEqualTo(point) && !_changed) return SamplerStatus.NoChange;
            _point = point;
            _hasPoint = true;
            if (_changed) { _preview = _geometry(_turns).ToList(); _changed = false; }
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            if (draw.Geometry == null) return true;
            draw.SubEntityTraits.Color = 4;
            foreach (var part in _preview)
            {
                var a = _point + new Vector3d(part.X1 * _unit, part.Y1 * _unit, 0);
                var b = _point + new Vector3d(part.X2 * _unit, part.Y2 * _unit, 0);
                if (part.Kind == DetailPreviewPrimitiveKind.Ellipse)
                {
                    var radius = Math.Abs(b.X - a.X) / 2;
                    if (radius > 1e-7) draw.Geometry.Circle(a + (b - a) / 2, radius, Vector3d.ZAxis);
                }
                else if (part.Kind == DetailPreviewPrimitiveKind.Text)
                {
                    var height = Math.Abs(b.Y - a.Y);
                    if (height > 1e-7 && !string.IsNullOrWhiteSpace(part.Text))
                    {
                        using (var text = new DBText { Position = a, TextString = part.Text, Height = height, ColorIndex = 4 })
                            draw.Geometry.Draw(text);
                    }
                }
                else if (!a.IsEqualTo(b)) draw.Geometry.WorldLine(a, b);
            }
            return true;
        }
    }
}
