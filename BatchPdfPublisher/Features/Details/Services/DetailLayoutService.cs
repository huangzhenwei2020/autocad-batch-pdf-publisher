using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    internal static class DetailLayoutService
    {
        public static DetailLayoutItem PromptForDetail(Document document, int number)
        {
            if (document == null) return null;
            var editor = document.Editor;
            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\n框选一个大样，可继续增选；切换减选可移除选错对象，回车完成: ",
                MessageForRemoval = "\n请选择要从当前大样中移除的对象: ",
                AllowDuplicates = false,
                RejectObjectsOnLockedLayers = false
            };
            var selection = editor.GetSelection(selectionOptions);
            if (selection.Status != PromptStatus.OK || selection.Value.Count == 0)
                throw new InvalidOperationException("框选范围内没有可排版对象。");

            var item = new DetailLayoutItem { Name = "大样" + Math.Max(1, number).ToString(CultureInfo.InvariantCulture) };
            string detectedTitle = null; string detectedScale = null;
            var hasBounds = false;
            var bounds = new Extents3d();
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in selection.Value.GetObjectIds().Where(x => !x.IsNull && x.IsValid).Distinct())
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { }
                    if (entity == null || entity.IsErased) continue;
                    try
                    {
                        var extents = entity.GeometricExtents;
                        if (!Finite(extents.MinPoint) || !Finite(extents.MaxPoint)) continue;
                        if (!hasBounds) { bounds = extents; hasBounds = true; }
                        else bounds.AddExtents(extents);
                        item.ObjectIds.Add(id);
                        if (TianzhengTitleService.IsDrawingName(entity)) TryReadDrawingName(entity, ref detectedTitle, ref detectedScale);
                        CapturePreview(entity, item.Preview, 0);
                    }
                    catch { }
                }
            }
            if (!hasBounds || item.ObjectIds.Count == 0) throw new InvalidOperationException("所选对象没有可计算的几何边界。");
            item.MinPoint = bounds.MinPoint;
            item.MaxPoint = bounds.MaxPoint;
            if (!string.IsNullOrWhiteSpace(detectedTitle)) item.Name = detectedTitle;
            item.ScaleText = detectedScale;
            if (item.Width <= 1e-6 || item.Height <= 1e-6) throw new InvalidOperationException("大样边界宽度或高度无效。");
            return item;
        }

        public static DetailLayoutPlan ComputeLayout(IList<DetailLayoutItem> source, FrameDefinition frame, int scale, DetailLayoutOptions options)
        {
            if (frame == null || string.IsNullOrWhiteSpace(frame.BlockName)) throw new InvalidOperationException("请选择登记图框。");
            var items = (source ?? new List<DetailLayoutItem>()).Where(value => value != null).ToList();
            if (items.Count == 0) throw new InvalidOperationException("请先框选至少一个大样。");
            scale = Math.Max(1, scale);
            options = options ?? new DetailLayoutOptions();
            if (!FrameLayoutRangeService.HasValidRange(frame)) throw new InvalidOperationException("当前图框尚未登记排版范围，请点击“登记排版范围”。");
            var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
            var pageWidth = paper[0] * scale;
            var pageHeight = paper[1] * scale;
            var plan = new DetailLayoutPlan
            {
                Frame = frame,
                Scale = scale,
                PageWidth = pageWidth,
                PageHeight = pageHeight,
                ContentLeft = frame.LayoutLeftMargin * scale,
                ContentRight = pageWidth - frame.LayoutRightMargin * scale,
                ContentBottom = frame.LayoutBottomMargin * scale,
                ContentTop = pageHeight - frame.LayoutTopMargin * scale
            };
            if (plan.ContentRight <= plan.ContentLeft || plan.ContentTop <= plan.ContentBottom)
                throw new InvalidOperationException("排版范围无效，请检查四周边距。");

            var gap = Math.Max(0d, options.ItemGap) * scale;
            var numberReserve = Math.Max(8d * scale, 80d);
            var contentWidth = plan.ContentRight - plan.ContentLeft;
            var contentHeight = plan.ContentTop - plan.ContentBottom;
            var grid = FindBestGrid(items, contentWidth, contentHeight, gap, numberReserve);
            if (grid == null)
                throw new InvalidOperationException("所选大样无法放进排版范围，请扩大排版范围、缩小大样间距或选择更大图框。");
            plan.Columns = grid.Columns; plan.Rows = grid.Rows;
            var extraWidth = Math.Max(0d, contentWidth - grid.ColumnWidths.Sum()) / grid.Columns;
            var extraHeight = Math.Max(0d, contentHeight - grid.RowHeights.Sum()) / grid.Rows;
            plan.ColumnWidths.AddRange(grid.ColumnWidths.Select(value => value + extraWidth));
            plan.RowHeights.AddRange(grid.RowHeights.Select(value => value + extraHeight));
            var perPage = plan.Columns * plan.Rows;
            for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                var item = items[itemIndex]; var page = itemIndex / perPage; var pageIndex = itemIndex % perPage;
                var row = pageIndex / plan.Columns; var column = pageIndex % plan.Columns;
                var cellWidth = plan.ColumnWidths[column]; var cellHeight = plan.RowHeights[row];
                var cellX = plan.ContentLeft + plan.ColumnWidths.Take(column).Sum();
                var cellY = plan.ContentTop - plan.RowHeights.Take(row + 1).Sum();
                var reserve = item.AddIndexNumber ? numberReserve : 0d;
                var groupWidth = item.Width + reserve;
                var x = cellX + (cellWidth - groupWidth) / 2d + reserve;
                var y = cellY + (cellHeight - item.Height) / 2d;
                plan.Slots.Add(new DetailLayoutSlot { Item = item, Page = page, X = x, Y = y, Width = item.Width, Height = item.Height, CellX = cellX, CellY = cellY, CellWidth = cellWidth, CellHeight = cellHeight });
            }
            plan.PageCount = (int)Math.Ceiling((double)items.Count / perPage);
            return plan;
        }

        private static GridCandidate FindBestGrid(IList<DetailLayoutItem> items, double contentWidth, double contentHeight, double gap, double numberReserve)
        {
            GridCandidate best = null;
            var targetAspect = contentWidth / Math.Max(1d, contentHeight);
            for (var columns = 1; columns <= items.Count; columns++)
            {
                for (var rows = 1; rows <= items.Count; rows++)
                {
                    var capacity = columns * rows;
                    var widths = new double[columns]; var heights = new double[rows];
                    for (var index = 0; index < items.Count; index++)
                    {
                        var position = index % capacity; var column = position % columns; var row = position / columns;
                        var item = items[index];
                        widths[column] = Math.Max(widths[column], item.Width + (item.AddIndexNumber ? numberReserve : 0d) + gap);
                        heights[row] = Math.Max(heights[row], item.Height + gap);
                    }
                    var requiredWidth = widths.Sum(); var requiredHeight = heights.Sum();
                    if (requiredWidth > contentWidth + 0.01d || requiredHeight > contentHeight + 0.01d) continue;
                    var pages = (int)Math.Ceiling((double)items.Count / capacity);
                    var placedPerPage = Math.Min(items.Count, capacity);
                    var emptyCells = pages * capacity - items.Count;
                    var naturalAspect = requiredWidth / Math.Max(1d, requiredHeight);
                    var aspectError = Math.Abs(Math.Log(Math.Max(1e-6d, naturalAspect / targetAspect)));
                    var candidate = new GridCandidate { Columns = columns, Rows = rows, Pages = pages, Capacity = placedPerPage, EmptyCells = emptyCells, AspectError = aspectError, ColumnWidths = widths, RowHeights = heights };
                    if (best == null || candidate.Pages < best.Pages
                        || (candidate.Pages == best.Pages && candidate.Capacity > best.Capacity)
                        || (candidate.Pages == best.Pages && candidate.Capacity == best.Capacity && candidate.EmptyCells < best.EmptyCells)
                        || (candidate.Pages == best.Pages && candidate.Capacity == best.Capacity && candidate.EmptyCells == best.EmptyCells && candidate.AspectError < best.AspectError))
                        best = candidate;
                }
            }
            return best;
        }

        private sealed class GridCandidate
        {
            public int Columns, Rows, Pages, Capacity, EmptyCells;
            public double AspectError;
            public double[] ColumnWidths, RowHeights;
        }

        public static DetailLayoutFrameAnchor InsertFrameForRange(Document document, FrameDefinition frame, int scale)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (frame == null || string.IsNullOrWhiteSpace(frame.BlockName)) throw new InvalidOperationException("请选择登记图框。");
            scale = Math.Max(1, scale);
            FrameTemplateStore.EnsureAvailable(document.Database, frame);
            var point = document.Editor.GetPoint("\n指定大样排版图框的左下角插入点: ");
            if (point.Status != PromptStatus.OK) return null;
            var anchor = new DetailLayoutFrameAnchor { Origin = point.Value, FrameRegistrationId = frame.RegistrationId, FrameBlockName = frame.BlockName, Scale = scale };
            using (var documentLock = AcquireWriteLock(document))
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var database = document.Database;
                var frameLayer = DraftingStandardService.EnsureFrameLayer(database, transaction);
                var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                if (!blocks.Has(frame.BlockName)) throw new InvalidOperationException("当前图纸无法加载登记图框块“" + frame.BlockName + "”。");
                var definitionId = blocks[frame.BlockName];
                var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                anchor.ReferenceId = AddFrame(space, transaction, definition, definitionId, frame, point.Value, scale, frameLayer, 1);
                transaction.Commit();
            }
            return anchor;
        }

        public static DetailLayoutOptions PromptLayoutRange(Document document, FrameDefinition frame, int scale, DetailLayoutFrameAnchor anchor, DetailLayoutOptions current)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (!AnchorMatches(anchor, frame, scale)) throw new InvalidOperationException("请先按当前图框和比例插入图框。");
            try
            {
                var first = document.Editor.GetPoint("\n指定图框内排版范围的第一角点: ");
                if (first.Status != PromptStatus.OK) return null;
                var second = document.Editor.GetCorner(new PromptCornerOptions("\n指定排版范围的对角点: ", first.Value));
                if (second.Status != PromptStatus.OK) return null;
                var minX = Math.Min(first.Value.X, second.Value.X); var maxX = Math.Max(first.Value.X, second.Value.X);
                var minY = Math.Min(first.Value.Y, second.Value.Y); var maxY = Math.Max(first.Value.Y, second.Value.Y);
                var paper = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, string.IsNullOrWhiteSpace(frame.PaperOrientation) ? "横向" : frame.PaperOrientation);
                var pageWidth = paper[0] * scale; var pageHeight = paper[1] * scale;
                var left = minX - anchor.Origin.X; var right = anchor.Origin.X + pageWidth - maxX;
                var bottom = minY - anchor.Origin.Y; var top = anchor.Origin.Y + pageHeight - maxY;
                if (left < -0.01d || right < -0.01d || bottom < -0.01d || top < -0.01d || maxX - minX <= 1e-6 || maxY - minY <= 1e-6)
                    throw new InvalidOperationException("框选范围必须位于刚插入的图框范围内。");
                current = current ?? new DetailLayoutOptions();
                return new DetailLayoutOptions
                {
                    HasExplicitRange = true,
                    LeftMargin = Math.Max(0d, left / scale), RightMargin = Math.Max(0d, right / scale),
                    TopMargin = Math.Max(0d, top / scale), BottomMargin = Math.Max(0d, bottom / scale),
                    ItemGap = current.ItemGap, PageGap = current.PageGap
                };
            }
            finally { DeleteTemporaryFrame(document, anchor); }
        }

        public static int Insert(Document document, IList<DetailLayoutItem> items, FrameDefinition frame, int scale, DetailLayoutOptions options, DetailLayoutFrameAnchor anchor = null)
        {
            if (document == null) throw new ArgumentNullException("document");
            var plan = ComputeLayout(items, frame, scale, options);
            FrameTemplateStore.EnsureAvailable(document.Database, frame);
            var point = document.Editor.GetPoint("\n指定正式大样排版第一张图框的左下角插入点: ");
            if (point.Status != PromptStatus.OK) return 0;
            var insertionOrigin = point.Value;

            var copied = 0;
            using (var documentLock = AcquireWriteLock(document))
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var database = document.Database;
                var frameLayer = DraftingStandardService.EnsureFrameLayer(database, transaction);
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                if (!blockTable.Has(frame.BlockName)) throw new InvalidOperationException("当前图纸无法加载登记图框块“" + frame.BlockName + "”。");
                var definitionId = blockTable[frame.BlockName];
                var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                var pageGap = Math.Max(0d, options.PageGap) * scale;
                var separatorLayer = EnsureSeparatorLayer(database, transaction);
                var indexLayer = EnsureIndexLayer(database, transaction);

                for (var page = 0; page < plan.PageCount; page++)
                {
                    var pageOrigin = new Point3d(insertionOrigin.X + page * (plan.PageWidth + pageGap), insertionOrigin.Y, insertionOrigin.Z);
                    AddFrame(space, transaction, definition, definitionId, frame, pageOrigin, scale, frameLayer, page + 1);
                    AddGrid(space, transaction, pageOrigin, plan, separatorLayer);
                }
                var detailNumber = 0;
                foreach (var slot in plan.Slots)
                {
                    var pageOrigin = new Point3d(insertionOrigin.X + slot.Page * (plan.PageWidth + pageGap), insertionOrigin.Y, insertionOrigin.Z);
                    var targetMin = new Point3d(pageOrigin.X + slot.X, pageOrigin.Y + slot.Y, pageOrigin.Z);
                    var displacement = targetMin - slot.Item.MinPoint;
                    foreach (var id in slot.Item.ObjectIds)
                    {
                        Entity source = null;
                        try { source = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch { }
                        if (source == null || source.IsErased) continue;
                        Entity clone = null;
                        try
                        {
                            clone = source.Clone() as Entity;
                            if (clone == null) continue;
                            clone.TransformBy(Matrix3d.Displacement(displacement));
                            space.AppendEntity(clone);
                            transaction.AddNewlyCreatedDBObject(clone, true);
                            copied++;
                        }
                        catch { if (clone != null) clone.Dispose(); }
                    }
                    if (slot.Item.AddIndexNumber)
                    {
                        detailNumber++;
                        var indexCenter = new Point3d(targetMin.X - Math.Max(4d * scale, 40d), targetMin.Y + Math.Max(3.5d * scale, 35d), targetMin.Z);
                        AddIndexMarker(space, transaction, indexCenter, detailNumber.ToString(CultureInfo.InvariantCulture), scale, indexLayer);
                    }
                }
                if (options.DeleteSources)
                {
                    foreach (var id in plan.Slots.SelectMany(x => x.Item.ObjectIds).Where(x => !x.IsNull && x.IsValid).Distinct())
                    {
                        try
                        {
                            var source = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (source != null && !source.IsErased) source.Erase();
                        }
                        catch { }
                    }
                }
                transaction.Commit();
            }
            document.Editor.WriteMessage("\n已排版 " + plan.Slots.Count + " 个大样，共 " + plan.PageCount + " 页，复制 " + copied + " 个 CAD 对象。\n");
            return plan.Slots.Count;
        }

        private static ObjectId AddFrame(BlockTableRecord space, Transaction transaction, BlockTableRecord definition, ObjectId definitionId, FrameDefinition frame, Point3d origin, int scale, ObjectId layer, int page)
        {
            var reference = new BlockReference(origin, definitionId) { ScaleFactors = new Scale3d(scale), LayerId = layer };
            space.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
            foreach (ObjectId id in definition)
            {
                var attributeDefinition = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                if (attributeDefinition == null || attributeDefinition.Constant) continue;
                var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                attribute.LayerId = layer;
                var tag = (attributeDefinition.Tag ?? string.Empty).Trim();
                var value = attributeDefinition.TextString;
                if (TagMatches(tag, frame.PrintScaleAttributeTag, "比例")) value = "1:" + scale;
                else if (TagMatches(tag, frame.SheetNameAttributeTag, "图纸名称", "图名")) value = "大样图（" + page + "）";
                else if (TagMatches(tag, frame.BuildingAttributeTag, "子项目名称") && !string.IsNullOrWhiteSpace(frame.DefaultBuilding)) value = frame.DefaultBuilding;
                attribute.TextString = string.IsNullOrWhiteSpace(value) || value.StartsWith("<", StringComparison.Ordinal) ? tag : value;
                reference.AttributeCollection.AppendAttribute(attribute);
                transaction.AddNewlyCreatedDBObject(attribute, true);
            }
            return reference.ObjectId;
        }

        private static bool AnchorMatches(DetailLayoutFrameAnchor anchor, FrameDefinition frame, int scale)
        {
            if (anchor == null || frame == null || anchor.Scale != Math.Max(1, scale)) return false;
            return (!string.IsNullOrWhiteSpace(anchor.FrameRegistrationId) && string.Equals(anchor.FrameRegistrationId, frame.RegistrationId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(anchor.FrameBlockName, frame.BlockName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TagMatches(string tag, string configured, params string[] aliases)
        {
            if (!string.IsNullOrWhiteSpace(configured) && string.Equals(tag, configured.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return aliases.Any(x => string.Equals(tag, x, StringComparison.OrdinalIgnoreCase) || tag.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static ObjectId EnsureSeparatorLayer(Database database, Transaction transaction)
        {
            var lineType = ObjectId.Null;
            var lineTypes = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
            foreach (var name in new[] { "DASHED", "DASH" })
            {
                if (!lineTypes.Has(name)) { try { database.LoadLineTypeFile(name, "acadiso.lin"); } catch { } lineTypes = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead); }
                if (lineTypes.Has(name)) { lineType = lineTypes[name]; break; }
            }
            var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (layers.Has("WL-大样-分隔")) return layers["WL-大样-分隔"];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = "WL-大样-分隔", Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 8), LineWeight = LineWeight.LineWeight013 };
            if (!lineType.IsNull) layer.LinetypeObjectId = lineType;
            var id = layers.Add(layer); transaction.AddNewlyCreatedDBObject(layer, true); return id;
        }

        private static ObjectId EnsureIndexLayer(Database database, Transaction transaction)
        {
            var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (layers.Has("WL-大样-索引")) return layers["WL-大样-索引"];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = "WL-大样-索引", Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7), LineWeight = LineWeight.LineWeight018 };
            var id = layers.Add(layer); transaction.AddNewlyCreatedDBObject(layer, true); return id;
        }

        private static void AddGrid(BlockTableRecord space, Transaction transaction, Point3d pageOrigin, DetailLayoutPlan plan, ObjectId layer)
        {
            var x = pageOrigin.X + plan.ContentLeft;
            AppendGridLine(space, transaction, new Point3d(x, pageOrigin.Y + plan.ContentBottom, pageOrigin.Z), new Point3d(x, pageOrigin.Y + plan.ContentTop, pageOrigin.Z), layer);
            foreach (var width in plan.ColumnWidths)
            {
                x += width;
                AppendGridLine(space, transaction, new Point3d(x, pageOrigin.Y + plan.ContentBottom, pageOrigin.Z), new Point3d(x, pageOrigin.Y + plan.ContentTop, pageOrigin.Z), layer);
            }
            var y = pageOrigin.Y + plan.ContentTop;
            AppendGridLine(space, transaction, new Point3d(pageOrigin.X + plan.ContentLeft, y, pageOrigin.Z), new Point3d(pageOrigin.X + plan.ContentRight, y, pageOrigin.Z), layer);
            foreach (var height in plan.RowHeights)
            {
                y -= height;
                AppendGridLine(space, transaction, new Point3d(pageOrigin.X + plan.ContentLeft, y, pageOrigin.Z), new Point3d(pageOrigin.X + plan.ContentRight, y, pageOrigin.Z), layer);
            }
        }

        private static void AppendGridLine(BlockTableRecord space, Transaction transaction, Point3d start, Point3d end, ObjectId layer)
        { var line = new Line(start, end) { LayerId = layer, ColorIndex = 8 }; space.AppendEntity(line); transaction.AddNewlyCreatedDBObject(line, true); }

        private static void AddIndexMarker(BlockTableRecord space, Transaction transaction, Point3d center, string number, int scale, ObjectId layer)
        {
            var radius = Math.Max(3.5d * scale, 35d);
            var circle = new Circle(center, Vector3d.ZAxis, radius) { LayerId = layer };
            space.AppendEntity(circle); transaction.AddNewlyCreatedDBObject(circle, true);
            var text = new MText { Contents = number, Location = center, Attachment = AttachmentPoint.MiddleCenter, TextHeight = Math.Max(3.5d * scale, 35d), LayerId = layer };
            space.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true);
        }

        private static void DeleteTemporaryFrame(Document document, DetailLayoutFrameAnchor anchor)
        {
            if (document == null || anchor == null || anchor.ReferenceId.IsNull || !anchor.ReferenceId.IsValid) return;
            try
            {
                using (var documentLock = AcquireWriteLock(document)) using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var reference = transaction.GetObject(anchor.ReferenceId, OpenMode.ForWrite, false) as BlockReference;
                    if (reference != null && !reference.IsErased) reference.Erase();
                    transaction.Commit();
                }
                anchor.ReferenceId = ObjectId.Null;
            }
            catch { }
        }

        private static bool Finite(Point3d point)
        { return !double.IsNaN(point.X) && !double.IsInfinity(point.X) && !double.IsNaN(point.Y) && !double.IsInfinity(point.Y) && !double.IsNaN(point.Z) && !double.IsInfinity(point.Z); }

        private static DocumentLock AcquireWriteLock(Document document)
        {
            var mode = document.LockMode();
            return mode == DocumentLockMode.Write || mode == DocumentLockMode.ExclusiveWrite
                || mode == DocumentLockMode.ProtectedAutoWrite || mode == DocumentLockMode.AutoWrite
                ? null
                : document.LockDocument();
        }

        private static void TryReadDrawingName(Entity entity, ref string title, ref string scale)
        {
            var texts = new List<string>();
            var exploded = new DBObjectCollection();
            try { entity.Explode(exploded); CollectExplodedText(exploded, texts, 0); }
            catch { }
            finally { foreach (DBObject value in exploded) value.Dispose(); }
            foreach (var raw in texts)
            {
                var value = CleanText(raw);
                if (string.IsNullOrWhiteSpace(value)) continue;
                var ratio = Regex.Match(value, @"1\s*[:：]\s*\d+");
                if (ratio.Success) { if (string.IsNullOrWhiteSpace(scale)) scale = ratio.Value.Replace(" ", string.Empty).Replace('：', ':'); continue; }
                if (string.IsNullOrWhiteSpace(title) || value.Length > title.Length) title = value;
            }
            if (string.IsNullOrWhiteSpace(scale))
            {
                try
                {
                    var com = entity.AcadObject;
                    var value = com.GetType().InvokeMember("Scale", System.Reflection.BindingFlags.GetProperty, null, com, null, CultureInfo.CurrentCulture);
                    var number = Convert.ToDouble(value, CultureInfo.CurrentCulture);
                    if (number > 0d) scale = "1:" + number.ToString("0", CultureInfo.InvariantCulture);
                }
                catch { }
            }
        }

        private static void CollectExplodedText(DBObjectCollection values, IList<string> output, int depth)
        {
            foreach (DBObject value in values)
            {
                var text = value as DBText; if (text != null) { output.Add(text.TextString); continue; }
                var mtext = value as MText; if (mtext != null) { output.Add(mtext.Contents); continue; }
                var entity = value as Entity;
                if (entity == null || depth >= 2) continue;
                var nested = new DBObjectCollection();
                try { entity.Explode(nested); CollectExplodedText(nested, output, depth + 1); }
                catch { }
                finally { foreach (DBObject child in nested) child.Dispose(); }
            }
        }

        private static string CleanText(string value)
        {
            var text = (value ?? string.Empty).Replace("\\P", " ").Replace("{", string.Empty).Replace("}", string.Empty);
            text = Regex.Replace(text, @"\\[A-Za-z][^;]*;", string.Empty);
            return text.Trim();
        }

        private static void CapturePreview(Entity entity, IList<DetailPreviewPrimitive> output, int depth)
        {
            if (entity == null || output == null || output.Count >= 2500) return;
            var line = entity as Line;
            if (line != null) { output.Add(LinePrimitive(line.StartPoint, line.EndPoint)); return; }
            var polyline = entity as Polyline;
            if (polyline != null)
            {
                for (var i = 1; i < polyline.NumberOfVertices && output.Count < 2500; i++) output.Add(LinePrimitive(polyline.GetPoint3dAt(i - 1), polyline.GetPoint3dAt(i)));
                if (polyline.Closed && polyline.NumberOfVertices > 2) output.Add(LinePrimitive(polyline.GetPoint3dAt(polyline.NumberOfVertices - 1), polyline.GetPoint3dAt(0)));
                return;
            }
            var text = entity as DBText;
            if (text != null) { AddTextPreview(text.GeometricExtents, text.TextString, output); return; }
            var mtext = entity as MText;
            if (mtext != null) { AddTextPreview(mtext.GeometricExtents, CleanText(mtext.Contents), output); return; }
            var circle = entity as Circle;
            if (circle != null) { output.Add(new DetailPreviewPrimitive { Kind = DetailPreviewPrimitiveKind.Ellipse, X1 = circle.Center.X - circle.Radius, Y1 = circle.Center.Y - circle.Radius, X2 = circle.Center.X + circle.Radius, Y2 = circle.Center.Y + circle.Radius }); return; }
            if (depth < 2)
            {
                var exploded = new DBObjectCollection();
                try
                {
                    entity.Explode(exploded);
                    if (exploded.Count > 0) { foreach (DBObject value in exploded) CapturePreview(value as Entity, output, depth + 1); return; }
                }
                catch { }
                finally { foreach (DBObject value in exploded) value.Dispose(); }
            }
            try
            {
                var extents = entity.GeometricExtents;
                output.Add(new DetailPreviewPrimitive { Kind = DetailPreviewPrimitiveKind.Box, X1 = extents.MinPoint.X, Y1 = extents.MinPoint.Y, X2 = extents.MaxPoint.X, Y2 = extents.MaxPoint.Y });
            }
            catch { }
        }

        private static DetailPreviewPrimitive LinePrimitive(Point3d first, Point3d second)
        { return new DetailPreviewPrimitive { Kind = DetailPreviewPrimitiveKind.Line, X1 = first.X, Y1 = first.Y, X2 = second.X, Y2 = second.Y }; }
        private static void AddTextPreview(Extents3d extents, string text, IList<DetailPreviewPrimitive> output)
        { output.Add(new DetailPreviewPrimitive { Kind = DetailPreviewPrimitiveKind.Text, X1 = extents.MinPoint.X, Y1 = extents.MinPoint.Y, X2 = extents.MaxPoint.X, Y2 = extents.MaxPoint.Y, Text = text }); }
    }
}
