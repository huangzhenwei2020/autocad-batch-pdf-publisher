using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    internal sealed class ExternalCadSheet
    {
        internal const double DefaultFrameInset = 5;
        internal FrameDefinition Frame;
        internal int Scale = 25, Start = 1, PageCount;
        internal bool Title, Number;
        internal string SingleTitle, SingleNumber;
        internal double PageWidth, PageHeight, Left, Right, Bottom, Top;
        internal double PageGap => 20 * Scale;
        internal readonly List<Slot> Slots = new List<Slot>();
        internal sealed class Slot
        {
            internal ExternalCadAsset Asset;
            internal int Page;
            internal double X, Y, Width;
            internal string Title, Number;
        }

        internal static ExternalCadSheet Arrange(IList<ExternalCadAsset> assets, FrameDefinition frame, int scale,
            double gap, bool paginate, bool title, bool number, int start, double frameInset = DefaultFrameInset)
        {
            if (assets.Count == 0) throw new InvalidOperationException("请勾选至少一个对象。");
            var sheet = new ExternalCadSheet { Frame = frame, Scale = scale, Title = title, Number = number, Start = start };
            if (frame != null)
            {
                if (!FrameLayoutRangeService.HasValidRange(frame))
                    throw new InvalidOperationException("此图框没有有效排版范围，请先在图框登记中设置。");
                if (!(frameInset >= 0) || double.IsInfinity(frameInset))
                    throw new InvalidOperationException("排版边框留白须为大于或等于 0 的数值。");
                var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, frame.PaperOrientation);
                sheet.PageWidth = paper[0] * scale;
                sheet.PageHeight = paper[1] * scale;
                sheet.Left = (frame.LayoutLeftMargin + frameInset) * scale;
                sheet.Right = sheet.PageWidth - (frame.LayoutRightMargin + frameInset) * scale;
                sheet.Bottom = (frame.LayoutBottomMargin + frameInset) * scale;
                sheet.Top = sheet.PageHeight - (frame.LayoutTopMargin + frameInset) * scale;
                if (sheet.Left >= sheet.Right || sheet.Bottom >= sheet.Top)
                    throw new InvalidOperationException("排版边框留白过大，请减小留白。");
            }
            gap *= scale;
            var x = sheet.Left;
            var top = sheet.Top;
            var rowHeight = 0d;
            var page = 0;
            var extentRight = 0d;
            var extentTop = 0d;
            for (var index = 0; index < assets.Count; index++)
            {
                var asset = assets[index];
                ExternalCadLibrary.LoadPreview(asset);
                var factor = ExternalCadLibrary.ToUnits(asset.Units, UnitsValue.Millimeters);
                var width = Math.Max(1, (asset.Bounds.MaxPoint.X - asset.Bounds.MinPoint.X) * factor);
                var height = Math.Max(1, (asset.Bounds.MaxPoint.Y - asset.Bounds.MinPoint.Y) * factor);
                var label = asset.Name;
                var code = number ? (start + index).ToString(CultureInfo.InvariantCulture) : null;
                var annotation = LabelWidth(title ? label : null, code, scale);
                var cellWidth = Math.Max(width, annotation);
                var band = annotation > 0 ? Math.Max(11, 7 + NumberDiameter(code) / 2) * scale : 0;
                var cellHeight = height + band;
                if (frame != null)
                {
                    if (cellWidth > sheet.Right - sheet.Left + 1e-7 || cellHeight > sheet.Top - sheet.Bottom + 1e-7)
                        throw new InvalidOperationException("“" + asset.Name + "”放不进留白后的排版范围。请减小留白、增大图框或调整打印比例。");
                    if (x + cellWidth > sheet.Right + 1e-7)
                    {
                        x = sheet.Left;
                        top -= rowHeight + gap;
                        rowHeight = 0;
                    }
                    if (top - cellHeight < sheet.Bottom - 1e-7)
                    {
                        if (!paginate) throw new InvalidOperationException("一张图框放不下，请开启自动分页。");
                        page++;
                        x = sheet.Left; top = sheet.Top; rowHeight = 0;
                    }
                }
                var y = frame == null ? band : top - height;
                sheet.Slots.Add(new Slot { Asset = asset, Page = page, X = x + (cellWidth - width) / 2, Y = y, Width = width,
                    Title = title ? label : null, Number = code });
                extentRight = x + cellWidth;
                extentTop = Math.Max(extentTop, y + height);
                x += cellWidth + gap;
                rowHeight = Math.Max(rowHeight, cellHeight);
            }
            sheet.PageCount = page + 1;
            if (frame == null) { sheet.PageWidth = extentRight; sheet.PageHeight = extentTop; }
            return sheet;
        }

        internal static double LabelWidth(string title, string number, int scale)
        {
            var titleWidth = string.IsNullOrWhiteSpace(title) ? 0 : (TextWidth(title, 3.5) + 3 + ScaleWidth(scale)) * scale;
            var diameter = NumberDiameter(number);
            var numberWidth = diameter == 0 ? 0 : (diameter + (titleWidth > 0 ? 3 : 0)) * scale;
            return titleWidth + numberWidth;
        }

        private static double TextWidth(string text, double height) => text.Sum(value => value > 255 ? height : height * .6);
        private static double NumberDiameter(string number) => string.IsNullOrWhiteSpace(number) ? 0 : Math.Max(7, TextWidth(number, 3) + 2);
        private static double ScaleWidth(int scale) => Math.Max(11, TextWidth("1:" + scale, 2.5));

        internal static IEnumerable<DetailPreviewPrimitive> Label(string title, string number, int scale, double center, double bottom)
        {
            var width = LabelWidth(title, number, scale);
            if (width <= 0) yield break;
            var x = center - width / 2;
            var y = bottom - 7 * scale;
            if (!string.IsNullOrWhiteSpace(number))
            {
                var diameter = NumberDiameter(number) * scale;
                yield return Shape(DetailPreviewPrimitiveKind.Ellipse, x, y - diameter / 2, x + diameter, y + diameter / 2);
                yield return Shape(DetailPreviewPrimitiveKind.Text, x + diameter / 2 - TextWidth(number, 3) * scale / 2, y - 1.5 * scale, x + diameter, y + 1.5 * scale, number);
                x += diameter + 3 * scale;
            }
            if (string.IsNullOrWhiteSpace(title)) yield break;
            var end = x + TextWidth(title, 3.5) * scale;
            yield return Shape(DetailPreviewPrimitiveKind.Text, x, y - 1.75 * scale, end, y + 1.75 * scale, title);
            yield return Shape(DetailPreviewPrimitiveKind.Line, x, y - 2.5 * scale, end, y - 2.5 * scale);
            yield return Shape(DetailPreviewPrimitiveKind.Text, end + 3 * scale, y - 1.25 * scale,
                end + (3 + ScaleWidth(scale)) * scale, y + 1.25 * scale, "1:" + scale);
        }

        internal static DetailPreviewPrimitive Shape(DetailPreviewPrimitiveKind kind, double x, double y, double x2, double y2, string text = null) =>
            new DetailPreviewPrimitive { Kind = kind, X1 = x, Y1 = y, X2 = x2, Y2 = y2, Text = text };

        internal static void Insert(Document doc, ExternalCadSheet sheet, ExternalCadAsset single, Point3d origin, int turns,
            Database preparedSingle = null)
        {
            var db = doc.Database;
            var mm = ExternalCadLibrary.ToUnits(UnitsValue.Millimeters, db.Insunits);
            var assetList = single == null ? sheet.Slots.Select(slot => slot.Asset).ToList() : new List<ExternalCadAsset> { single };
            var sources = new List<Database>();
            try
            {
                foreach (var asset in assetList)
                {
                    ExternalCadLibrary.RequireSameDrawing(asset);
                    sources.Add(preparedSingle ?? ExternalCadLibrary.OpenDrawing(asset.Path));
                    ExternalCadLibrary.RequireSameDrawing(asset);
                }
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    if (sheet.Frame != null)
                    {
                        var frameId = FrameTemplateStore.EnsureAvailable(doc, sheet.Frame);
                        var frame = (BlockTableRecord)tr.GetObject(frameId, OpenMode.ForRead);
                        var layer = DraftingStandardService.EnsureFrameLayer(db, tr);
                        for (var i = 0; i < sheet.PageCount; i++)
                            DetailLayoutService.AddFrame(space, tr, frame, frameId, sheet.Frame,
                                origin + new Vector3d(i * (sheet.PageWidth + sheet.PageGap) * mm, 0, 0), sheet.Scale, layer, i + 1, mm, "图库详图");
                    }
                    for (var index = 0; index < sources.Count; index++)
                    {
                        var asset = assetList[index];
                        var slot = single == null ? sheet.Slots[index] : null;
                        var target = slot == null ? origin : origin + new Vector3d((slot.X + slot.Page * (sheet.PageWidth + sheet.PageGap)) * mm, slot.Y * mm, 0);
                        var anchor = slot == null ? asset.Base : asset.Bounds.MinPoint;
                        var factor = ExternalCadLibrary.ToUnits(asset.Units, UnitsValue.Millimeters) * mm;
                        var matrix = Matrix3d.Displacement(target - Point3d.Origin)
                            * Matrix3d.Rotation(turns * Math.PI / 2, Vector3d.ZAxis, Point3d.Origin)
                            * Matrix3d.Scaling(factor, Point3d.Origin)
                            * Matrix3d.Displacement(Point3d.Origin - anchor);
                        var source = sources[index];
                        var import = new IdMapping();
                        using (var read = source.TransactionManager.StartOpenCloseTransaction())
                            source.WblockCloneObjects(ExternalCadLibrary.Entities(source, read), space.ObjectId, import, DuplicateRecordCloning.MangleName, false);
                        var inserted = 0;
                        foreach (IdPair pair in import)
                        {
                            if (!pair.IsPrimary || !pair.IsCloned) continue;
                            var entity = tr.GetObject(pair.Value, OpenMode.ForWrite, false, true) as Entity;
                            if (entity == null) continue;
                            entity.TransformBy(matrix);
                            inserted++;
                        }
                        if (inserted == 0) throw new InvalidOperationException("“" + asset.Name + "”没有可插入的对象。");
                        var title = slot?.Title ?? (single != null && sheet.Title ? sheet.SingleTitle : null);
                        var number = slot?.Number ?? (single != null && sheet.Number ? sheet.SingleNumber : null);
                        var bounds = asset.Bounds;
                        bounds.TransformBy(matrix);
                        AddLabel(db, space, tr, sheet, title, number, origin, bounds, mm);
                    }
                    tr.Commit();
                }
            }
            finally { foreach (var source in sources) if (source != preparedSingle) source.Dispose(); }
        }

        private static void AddLabel(Database db, BlockTableRecord space, Transaction tr, ExternalCadSheet sheet, string title,
            string number, Point3d origin, Extents3d bounds, double mm)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(number)) return;
            var center = ((bounds.MinPoint.X + bounds.MaxPoint.X) / 2 - origin.X) / mm;
            var minY = (bounds.MinPoint.Y - origin.Y) / mm;
            var parts = Label(title, number, sheet.Scale, center, minY).ToList();
            var numberParts = string.IsNullOrWhiteSpace(number) ? 0 : 2;
            if (!string.IsNullOrWhiteSpace(title))
            {
                var firstText = parts[numberParts];
                var maxX = center + LabelWidth(title, number, sheet.Scale) / 2;
                var area = new Extents3d(origin + new Vector3d(firstText.X1 * mm, (minY - 11 * sheet.Scale) * mm, 0),
                    origin + new Vector3d(maxX * mm, (minY - sheet.Scale) * mm, 0));
                if (TianzhengTitleService.TryInsertVerifiedNativeTitle(db, space.ObjectId, area, title, sheet.Scale))
                    parts.RemoveRange(numberParts, parts.Count - numberParts);
            }
            if (parts.Count == 0) return;
            var textStyle = DraftingStandardService.EnsureAnnotationTextStyle(db, tr);
            var layer = DraftingStandardService.EnsureLayerFor(db, tr, DraftingStandardProfile.AnnotationTextLayerKey);
            foreach (var part in parts)
            {
                var a = origin + new Vector3d(part.X1 * mm, part.Y1 * mm, 0);
                var b = origin + new Vector3d(part.X2 * mm, part.Y2 * mm, 0);
                Entity entity;
                if (part.Kind == DetailPreviewPrimitiveKind.Ellipse)
                    entity = new Circle(a + (b - a) / 2, Vector3d.ZAxis, (b.X - a.X) / 2) { ColorIndex = 3 };
                else if (part.Kind == DetailPreviewPrimitiveKind.Line)
                {
                    var line = new Polyline(2) { Elevation = origin.Z, ConstantWidth = .45 * sheet.Scale * mm, ColorIndex = 3 };
                    line.AddVertexAt(0, new Point2d(a.X, a.Y), 0, 0, 0);
                    line.AddVertexAt(1, new Point2d(b.X, b.Y), 0, 0, 0);
                    entity = line;
                }
                else entity = new DBText { Position = a, TextString = part.Text, Height = b.Y - a.Y, TextStyleId = textStyle, ColorIndex = 7 };
                entity.LayerId = layer;
                space.AppendEntity(entity);
                tr.AddNewlyCreatedDBObject(entity, true);
            }
        }
    }
}
