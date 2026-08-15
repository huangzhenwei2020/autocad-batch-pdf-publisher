using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    internal static class DoorWindowElevationInsertionService
    {
        private sealed class ElevationPlacement
        {
            public DoorWindowScheduleItem Item;
            public double DimensionGap, LowerExtent;
        }

        public static int Insert(Document document, IList<DoorWindowScheduleItem> source, int drawingScale, FrameDefinition frame, Action<int, int, string> progress = null)
        {
            if (document == null) throw new ArgumentNullException("document");
            var items = (source ?? new List<DoorWindowScheduleItem>()).Where(x => x.Selected && (x.Status ?? string.Empty).Contains("可生成")).ToList();
            if (items.Count == 0) throw new InvalidOperationException("没有勾选参数完整的门窗。");
            drawingScale = Math.Max(1, drawingScale);
            if (progress != null) progress(0, items.Count, "等待指定插入点…");
            var pointResult = document.Editor.GetPoint(frame == null ? "\n指定批量门窗立面左下角插入点: " : "\n指定第一张门窗立面图框左下角插入点: ");
            if (pointResult.Status != PromptStatus.OK) return 0;

            var pageCount = 0;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var database = document.Database;
                DoorWindowElevationMetadataService.EnsureRegistered(database, transaction);
                var profile = DraftingStandardService.LoadProfile();
                var resources = DraftingStandardService.EnsureAll(database, transaction, profile, profile.UpdateExisting);
                var dimensionStyle = DraftingStandardService.EnsureDimensionStyleForScale(database, transaction, drawingScale, profile, resources, true);
                var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite);
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                var elevations = items.Select(item => CreatePlacement(item, drawingScale)).ToList();
                if (frame == null) InsertContinuous(elevations, pointResult.Value, drawingScale, space, transaction, resources, dimensionStyle, progress);
                else pageCount = InsertPaged(elevations, pointResult.Value, drawingScale, frame, blocks, space, transaction, resources, dimensionStyle, progress);
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\n已插入 " + items.Count + " 个可独立编辑的门窗立面" + (frame == null ? string.Empty : "，自动排入 " + pageCount + " 张 " + frame.PaperDisplay + " 图框") + "。几何按实际毫米 1:1 绘制，标注采用万落建筑工具 1:" + drawingScale + " 标注样式。\n");
            return items.Count;
        }

        public static int Update(Document document, DoorWindowElevationMetadata metadata, IList<DoorWindowScheduleItem> source, int drawingScale)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (metadata == null) throw new ArgumentNullException("metadata");
            var item = (source ?? new List<DoorWindowScheduleItem>()).FirstOrDefault(x =>
                string.Equals(x.Code, metadata.Code, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(x.Width - metadata.Width) < 0.01 && Math.Abs(x.Height - metadata.Height) < 0.01) ?? metadata.ToItem();
            DoorWindowElevationGeometryBuilder.Build(item);
            drawingScale = Math.Max(1, drawingScale);
            var replaced = 0;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var database = document.Database;
                DoorWindowElevationMetadataService.EnsureRegistered(database, transaction);
                var profile = DraftingStandardService.LoadProfile();
                var resources = DraftingStandardService.EnsureAll(database, transaction, profile, profile.UpdateExisting);
                var dimensionStyle = DraftingStandardService.EnsureDimensionStyleForScale(database, transaction, drawingScale, profile, resources, true);
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                var ids = DoorWindowElevationMetadataService.FindGroup(space, transaction, metadata.GroupId);
                foreach (var id in ids)
                {
                    var entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (entity == null) continue;
                    entity.Erase(); replaced++;
                }
                InsertElevation(CreatePlacement(item, drawingScale), metadata.Origin, drawingScale, space, transaction, resources, dimensionStyle, metadata.GroupId);
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\n门窗立面“" + (item.Code ?? "未编号") + "”已原位更新，替换 " + replaced + " 个插件生成对象。\n");
            return replaced;
        }

        private static ElevationPlacement CreatePlacement(DoorWindowScheduleItem item, int drawingScale)
        {
            DoorWindowElevationGeometryBuilder.Build(item);
            var dimensionGap = Math.Max(4d * drawingScale, 120d);
            return new ElevationPlacement { Item = item, DimensionGap = dimensionGap, LowerExtent = dimensionGap + 12d * drawingScale };
        }

        private static void InsertElevation(ElevationPlacement elevation, Point3d origin, int drawingScale, BlockTableRecord space, Transaction transaction, DraftingStandardResources resources, ObjectId dimensionStyle, string existingGroupId = null)
        {
            var item = elevation.Item; var geometry = DoorWindowElevationGeometryBuilder.Build(item);
            var metadata = DoorWindowElevationMetadata.Create(item, origin, drawingScale, existingGroupId);
            AppendGeometry(geometry, origin, space, transaction, resources, metadata);
            var dimensionGap = elevation.DimensionGap;
            AppendTagged(space, transaction, new AlignedDimension(origin, new Point3d(origin.X + item.Width, origin.Y, origin.Z), new Point3d(origin.X + item.Width / 2d, origin.Y - dimensionGap, origin.Z), string.Empty, dimensionStyle) { LayerId = resources.AnnotationDimensionLayerId }, metadata);
            AppendTagged(space, transaction, new AlignedDimension(origin, new Point3d(origin.X, origin.Y + item.Height, origin.Z), new Point3d(origin.X - dimensionGap, origin.Y + item.Height / 2d, origin.Z), string.Empty, dimensionStyle) { LayerId = resources.AnnotationDimensionLayerId }, metadata);
            AddSegmentDimensions(geometry, origin, dimensionGap, space, transaction, resources.AnnotationDimensionLayerId, dimensionStyle, metadata);
            var titleHeight = Math.Max(3.5d * drawingScale, 70d); var noteHeight = Math.Max(2.5d * drawingScale, 50d);
            var titleY = origin.Y - dimensionGap - 5d * drawingScale;
            AddCenteredText(space, transaction, (item.Code ?? "未编号") + " 立面", new Point3d(origin.X + item.Width / 2d, titleY, origin.Z), titleHeight, resources.TitleTextStyleId, resources.AnnotationTextLayerId, metadata);
            AddCenteredText(space, transaction, "1:" + drawingScale.ToString(CultureInfo.InvariantCulture), new Point3d(origin.X + item.Width / 2d, titleY - 3.6d * drawingScale, origin.Z), noteHeight, resources.AnnotationTextStyleId, resources.AnnotationTextLayerId, metadata);
        }

        private static void AppendGeometry(DoorWindowElevationGeometry geometry, Point3d origin, BlockTableRecord space, Transaction transaction, DraftingStandardResources resources, DoorWindowElevationMetadata metadata)
        {
            const double tolerance = .01d;
            foreach (var roleGroup in geometry.Lines.GroupBy(x => x.Role))
            {
                var segments = roleGroup.ToList(); var used = new bool[segments.Count];
                for (var seed = 0; seed < segments.Count; seed++)
                {
                    if (used[seed]) continue; used[seed] = true;
                    var points = new List<Point2d> { new Point2d(segments[seed].X1, segments[seed].Y1), new Point2d(segments[seed].X2, segments[seed].Y2) };
                    bool extended;
                    do
                    {
                        extended = false;
                        for (var index = 0; index < segments.Count; index++)
                        {
                            if (used[index]) continue; var segment = segments[index];
                            var a = new Point2d(segment.X1, segment.Y1); var b = new Point2d(segment.X2, segment.Y2);
                            if (Near(points[points.Count - 1], a)) { points.Add(b); used[index] = true; extended = true; break; }
                            if (Near(points[points.Count - 1], b)) { points.Add(a); used[index] = true; extended = true; break; }
                            if (Near(points[0], b)) { points.Insert(0, a); used[index] = true; extended = true; break; }
                            if (Near(points[0], a)) { points.Insert(0, b); used[index] = true; extended = true; break; }
                        }
                    } while (extended);

                    var layer = roleGroup.Key == DoorWindowLineRole.Hole || roleGroup.Key == DoorWindowLineRole.Opening
                        ? resources.ArchitectureHiddenLayerId
                        : roleGroup.Key == DoorWindowLineRole.Frame ? resources.ArchitectureOutlineLayerId : resources.ArchitectureFineLayerId;
                    var closed = points.Count > 2 && Near(points[0], points[points.Count - 1]);
                    if (closed) points.RemoveAt(points.Count - 1);
                    if (points.Count > 2)
                    {
                        var polyline = new Polyline(points.Count) { LayerId = layer, Closed = closed };
                        for (var index = 0; index < points.Count; index++) polyline.AddVertexAt(index, new Point2d(origin.X + points[index].X, origin.Y + points[index].Y), 0d, 0d, 0d);
                        AppendTagged(space, transaction, polyline, metadata);
                    }
                    else
                    {
                        var line = new Line(new Point3d(origin.X + points[0].X, origin.Y + points[0].Y, origin.Z), new Point3d(origin.X + points[1].X, origin.Y + points[1].Y, origin.Z)) { LayerId = layer };
                        AppendTagged(space, transaction, line, metadata);
                    }
                }
            }
            bool Near(Point2d first, Point2d second) { return first.GetDistanceTo(second) <= tolerance; }
        }

        private static void InsertContinuous(IList<ElevationPlacement> elevations, Point3d origin, int scale, BlockTableRecord space, Transaction transaction, DraftingStandardResources resources, ObjectId dimensionStyle, Action<int, int, string> progress)
        {
            var x = origin.X; var completed = 0;
            foreach (var elevation in elevations)
            {
                InsertElevation(elevation, new Point3d(x, origin.Y, origin.Z), scale, space, transaction, resources, dimensionStyle);
                x += elevation.Item.Width + Math.Max(16d * scale, 800d);
                completed++; if (progress != null) progress(completed, elevations.Count, elevation.Item.Code);
            }
        }

        private static int InsertPaged(IList<ElevationPlacement> elevations, Point3d origin, int scale, FrameDefinition frame, BlockTable blockTable, BlockTableRecord space, Transaction transaction, DraftingStandardResources resources, ObjectId dimensionStyle, Action<int, int, string> progress)
        {
            if (frame == null || string.IsNullOrWhiteSpace(frame.BlockName)) throw new InvalidOperationException("请选择有效的登记图框。");
            if (!blockTable.Has(frame.BlockName)) throw new InvalidOperationException("当前图纸不存在已登记图框块“" + frame.BlockName + "”。请先把该图框插入当前图纸，或重新登记当前图纸中的图框。");
            var frameDefinitionId = blockTable[frame.BlockName];
            var frameRecord = (BlockTableRecord)transaction.GetObject(frameDefinitionId, OpenMode.ForRead);
            Point3d definitionMin; double definitionWidth, definitionHeight;
            GetDefinitionBounds(frameRecord, transaction, out definitionMin, out definitionWidth, out definitionHeight);
            var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
            var pageWidth = paper[0] * scale; var pageHeight = paper[1] * scale;
            var frameFactor = Math.Min(pageWidth / definitionWidth, pageHeight / definitionHeight);
            var leftMargin = 20d * scale; var topMargin = 15d * scale; var bottomMargin = 20d * scale;
            var landscape = pageWidth >= pageHeight; var rightMargin = (landscape ? Math.Min(190d, paper[0] * .24d) : 20d) * scale;
            var contentLeft = leftMargin; var contentRight = pageWidth - rightMargin; var contentBottom = bottomMargin; var contentTop = pageHeight - topMargin;
            if (contentRight <= contentLeft || contentTop <= contentBottom) throw new InvalidOperationException("登记图框的可排版区域无效。");

            var pageGap = 30d * scale; var page = -1; var cursorX = 0d; var cursorY = 0d; var rowHeight = 0d;
            var completed = 0;
            foreach (var elevation in elevations)
            {
                var footprintWidth = elevation.DimensionGap + elevation.Item.Width + 8d * scale;
                var footprintHeight = elevation.LowerExtent + elevation.Item.Height + 8d * scale;
                if (footprintWidth > contentRight - contentLeft || footprintHeight > contentTop - contentBottom)
                    throw new InvalidOperationException("门窗“" + elevation.Item.Code + "”在 1:" + scale + " 时放不进所选 " + frame.PaperDisplay + " 图框，请选择更大或加长图框。");
                if (page < 0 || cursorY + footprintHeight > contentTop)
                {
                    page++;
                    var pageOrigin = new Point3d(origin.X + page * (pageWidth + pageGap), origin.Y, origin.Z);
                    AddFrameReference(space, transaction, frameRecord, frameDefinitionId, frame, pageOrigin, definitionMin, frameFactor, scale, page + 1, resources.FrameLayerId);
                    cursorX = contentLeft; cursorY = contentBottom; rowHeight = 0d;
                }
                if (cursorX + footprintWidth > contentRight)
                {
                    cursorX = contentLeft; cursorY += rowHeight + 8d * scale; rowHeight = 0d;
                    if (cursorY + footprintHeight > contentTop)
                    {
                        page++;
                        var pageOrigin = new Point3d(origin.X + page * (pageWidth + pageGap), origin.Y, origin.Z);
                        AddFrameReference(space, transaction, frameRecord, frameDefinitionId, frame, pageOrigin, definitionMin, frameFactor, scale, page + 1, resources.FrameLayerId);
                        cursorY = contentBottom;
                    }
                }
                var currentPageOrigin = new Point3d(origin.X + page * (pageWidth + pageGap), origin.Y, origin.Z);
                var insertion = new Point3d(currentPageOrigin.X + cursorX + elevation.DimensionGap, currentPageOrigin.Y + cursorY + elevation.LowerExtent, origin.Z);
                InsertElevation(elevation, insertion, scale, space, transaction, resources, dimensionStyle);
                cursorX += footprintWidth + 8d * scale; rowHeight = Math.Max(rowHeight, footprintHeight);
                completed++; if (progress != null) progress(completed, elevations.Count, elevation.Item.Code);
            }
            return page + 1;
        }

        private static void AddSegmentDimensions(DoorWindowElevationGeometry geometry, Point3d origin, double dimensionGap, BlockTableRecord space, Transaction transaction, ObjectId layer, ObjectId style, DoorWindowElevationMetadata metadata)
        {
            const double tolerance = .05d; var horizontalY = origin.Y - dimensionGap * .56d; var verticalX = origin.X - dimensionGap * .56d;
            var xs = geometry.Cells.SelectMany(x => new[] { x.Left, x.Right }).Select(x => Math.Round(x, 3)).Distinct().OrderBy(x => x).ToList();
            for (var index = 0; index + 1 < xs.Count; index++)
            {
                var first = xs[index]; var second = xs[index + 1];
                if (!geometry.Cells.Any(x => x.Left <= first + tolerance && x.Right >= second - tolerance)) continue;
                AppendTagged(space, transaction, new AlignedDimension(new Point3d(origin.X + first, origin.Y + geometry.FrameBottom, origin.Z), new Point3d(origin.X + second, origin.Y + geometry.FrameBottom, origin.Z), new Point3d(origin.X + (first + second) / 2d, horizontalY, origin.Z), string.Empty, style) { LayerId = layer }, metadata);
            }
            var ys = geometry.Cells.SelectMany(x => new[] { x.Bottom, x.Top }).Select(x => Math.Round(x, 3)).Distinct().OrderBy(x => x).ToList();
            for (var index = 0; index + 1 < ys.Count; index++)
            {
                var first = ys[index]; var second = ys[index + 1];
                if (!geometry.Cells.Any(x => x.Bottom <= first + tolerance && x.Top >= second - tolerance)) continue;
                AppendTagged(space, transaction, new AlignedDimension(new Point3d(origin.X + geometry.FrameLeft, origin.Y + first, origin.Z), new Point3d(origin.X + geometry.FrameLeft, origin.Y + second, origin.Z), new Point3d(verticalX, origin.Y + (first + second) / 2d, origin.Z), string.Empty, style) { LayerId = layer }, metadata);
            }
        }

        private static void AddFrameReference(BlockTableRecord space, Transaction transaction, BlockTableRecord definition, ObjectId definitionId, FrameDefinition frame, Point3d pageOrigin, Point3d definitionMin, double factor, int scale, int pageNumber, ObjectId layer)
        {
            var position = new Point3d(pageOrigin.X - definitionMin.X * factor, pageOrigin.Y - definitionMin.Y * factor, pageOrigin.Z - definitionMin.Z * factor);
            var reference = new BlockReference(position, definitionId) { ScaleFactors = new Scale3d(factor), LayerId = layer };
            space.AppendEntity(reference); transaction.AddNewlyCreatedDBObject(reference, true);
            foreach (ObjectId id in definition)
            {
                var attributeDefinition = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                if (attributeDefinition == null || attributeDefinition.Constant) continue;
                var attribute = new AttributeReference(); attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                var tag = (attributeDefinition.Tag ?? string.Empty).Trim(); var value = attributeDefinition.TextString;
                if (TagMatches(tag, frame.PrintScaleAttributeTag, "比例")) value = "1:" + scale;
                else if (TagMatches(tag, frame.SheetNameAttributeTag, "图纸名称", "图名")) value = "门窗立面图（" + pageNumber + "）";
                else if (TagMatches(tag, frame.SheetNumberAttributeTag, "图号")) value = "MCLM-" + pageNumber.ToString("00", CultureInfo.InvariantCulture);
                else if (TagMatches(tag, frame.BuildingAttributeTag, "子项目名称")) value = string.IsNullOrWhiteSpace(frame.DefaultBuilding) ? value : frame.DefaultBuilding;
                attribute.TextString = string.IsNullOrWhiteSpace(value) || value.StartsWith("<", StringComparison.Ordinal) ? tag : value;
                reference.AttributeCollection.AppendAttribute(attribute); transaction.AddNewlyCreatedDBObject(attribute, true);
            }
        }

        private static bool TagMatches(string tag, string configured, params string[] aliases)
        {
            if (!string.IsNullOrWhiteSpace(configured) && string.Equals(tag, configured.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return aliases.Any(x => string.Equals(tag, x, StringComparison.OrdinalIgnoreCase) || tag.Contains(x));
        }

        private static void GetDefinitionBounds(BlockTableRecord definition, Transaction transaction, out Point3d min, out double width, out double height)
        {
            var first = true; var extents = new Extents3d();
            foreach (ObjectId id in definition)
            {
                var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; if (entity == null) continue;
                try { if (first) { extents = entity.GeometricExtents; first = false; } else extents.AddExtents(entity.GeometricExtents); } catch { }
            }
            if (first) throw new InvalidOperationException("登记图框块“" + definition.Name + "”没有可计算范围的图形。");
            min = extents.MinPoint; width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X); height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            if (width < 1e-6 || height < 1e-6) throw new InvalidOperationException("登记图框块尺寸无效。");
        }

        private static void AddCenteredText(BlockTableRecord owner, Transaction transaction, string value, Point3d point, double height, ObjectId style, ObjectId layer, DoorWindowElevationMetadata metadata)
        {
            // DBText recalculates AlignmentPoint when a SHX/TTF text style is
            // assigned, which caused the scale labels to jump far away from the
            // elevation. MText uses Location as its stable anchor.
            var text = new MText
            {
                Contents = value ?? string.Empty,
                Location = point,
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = height,
                TextStyleId = style,
                LayerId = layer
            };
            AppendTagged(owner, transaction, text, metadata);
        }
        private static void AppendTagged(BlockTableRecord owner, Transaction transaction, Entity entity, DoorWindowElevationMetadata metadata)
        { Append(owner, transaction, entity); DoorWindowElevationMetadataService.Attach(entity, metadata); }
        private static void Append(BlockTableRecord owner, Transaction transaction, Entity entity)
        { owner.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true); }
    }
}
