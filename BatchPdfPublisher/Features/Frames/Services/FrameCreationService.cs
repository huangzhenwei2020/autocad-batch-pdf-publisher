using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public static class FrameCreationService
    {
        public static ObjectId EnsureTextStyle(Database database, Transaction transaction, string name)
        { return FindTextStyle(database, transaction, name); }
        public static string[] GetTextStyleNames(Document document)
        {
            var names = new System.Collections.Generic.List<string>
            {
                DraftingStandardService.GetTextStyleName(DraftingStandardProfile.BodyTextKey),
                DraftingStandardService.GetTextStyleName(DraftingStandardProfile.TitleTextKey),
                DraftingStandardService.GetTextStyleName(DraftingStandardProfile.AnnotationTextKey),
                "黑体", "宋体", "微软雅黑", "Arial"
            };
            if (document == null) return names.ToArray();
            using (var tr = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (TextStyleTable)tr.GetObject(document.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var record = tr.GetObject(id, OpenMode.ForRead) as TextStyleTableRecord;
                    if (record != null && !names.Contains(record.Name, StringComparer.OrdinalIgnoreCase)) names.Add(record.Name);
                }
            }
            return names.ToArray();
        }

        public static bool InsertBorder(Document document, double width, double height)
        {
            if (document == null) return false;
            var editor = document.Editor; var point = editor.GetPoint("\n指定图框左下角插入点: ");
            if (point.Status != PromptStatus.OK) return false;
            using (document.LockDocument()) using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                DraftingStandardService.EnsureAll(document.Database, tr);
                var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
                var poly = Rectangle(point.Value, width, height); poly.Layer = DraftingStandardService.GetLayerName(DraftingStandardProfile.FrameKey); space.AppendEntity(poly); tr.AddNewlyCreatedDBObject(poly, true); tr.Commit();
            }
            return true;
        }

        public static bool InsertRegisteredFrame(Document document, FrameDefinition frame, int drawingScale)
        {
            if (document == null) return false;
            if (frame == null || string.IsNullOrWhiteSpace(frame.BlockName))
                throw new InvalidOperationException("请选择已登记的图框。");
            if (drawingScale <= 0) throw new InvalidOperationException("图框比例必须是大于 0 的整数。");

            var templateTiming = System.Diagnostics.Stopwatch.StartNew();
            FrameTemplateStore.EnsureAvailable(document.Database, frame);
            var templateMilliseconds = templateTiming.ElapsedMilliseconds;
            var point = document.Editor.GetPoint("\n指定登记图框左下角插入点: ");
            if (point.Status != PromptStatus.OK) return false;

            var insertionTiming = System.Diagnostics.Stopwatch.StartNew();
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var database = document.Database;
                var frameLayerId = DraftingStandardService.EnsureFrameLayer(database, transaction);
                var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                if (!blocks.Has(frame.BlockName))
                    throw new InvalidOperationException("无法加载登记图框块“" + frame.BlockName + "”。");

                var definitionId = blocks[frame.BlockName];
                var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
                // 与 CAD 的 INSERT 一致：插入点就是块基点，所选出图比例就是统一块比例。
                var reference = new BlockReference(point.Value, definitionId)
                {
                    ScaleFactors = new Scale3d(drawingScale),
                    LayerId = frameLayerId
                };
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                space.AppendEntity(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);

                foreach (ObjectId id in definition)
                {
                    var attributeDefinition = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                    if (attributeDefinition == null || attributeDefinition.Constant) continue;
                    var attribute = new AttributeReference();
                    attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                    attribute.LayerId = frameLayerId;
                    var tag = (attributeDefinition.Tag ?? string.Empty).Trim();
                    var value = attributeDefinition.TextString;
                    if (TagMatches(tag, frame.PrintScaleAttributeTag, "比例", "SCALE", "PRINTSCALE", "PRINT_SCALE")) value = "1:" + drawingScale;
                    else if (TagMatches(tag, frame.BuildingAttributeTag, "子项目名称")) value = PreferDefault(frame.DefaultBuilding, value);
                    else if (TagMatches(tag, frame.SheetNumberAttributeTag, "图号")) value = PreferDefault(frame.DefaultSheetNumber, value);
                    else if (TagMatches(tag, frame.SheetNameAttributeTag, "图纸名称", "图名")) value = PreferDefault(frame.DefaultSheetName, value);
                    attribute.TextString = string.IsNullOrWhiteSpace(value) || value.StartsWith("<", StringComparison.Ordinal) ? tag : value;
                    reference.AttributeCollection.AppendAttribute(attribute);
                    transaction.AddNewlyCreatedDBObject(attribute, true);
                }

                transaction.Commit();
            }
            LogInsertion("frame=" + frame.BlockName + " scale=1:" + drawingScale + " templateMs=" + templateMilliseconds + " insertMs=" + insertionTiming.ElapsedMilliseconds + " drawing=" + (document.Database.Filename ?? string.Empty));
            document.Editor.WriteMessage("\n已插入登记图框“" + frame.DisplayName + "”，比例 1:" + drawingScale + "。\n");
            return true;
        }

        public static bool InsertCenteredProperty(Document document, string property, string font, double height, double widthFactor, Autodesk.AutoCAD.Colors.Color color)
        {
            if (document == null) return false;
            var editor = document.Editor; var first = editor.GetPoint("\n指定属性文字框第一角点: ");
            if (first.Status != PromptStatus.OK) return false;
            var second = editor.GetCorner(new PromptCornerOptions("\n指定属性文字框对角点: ", first.Value));
            if (second.Status != PromptStatus.OK) return false;
            var center = new Point3d((first.Value.X + second.Value.X) / 2d, (first.Value.Y + second.Value.Y) / 2d, first.Value.Z);
            using (document.LockDocument()) using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                var style = FindTextStyle(document.Database, tr, font);
                var text = new AttributeDefinition { Position = center, Height = height, WidthFactor = widthFactor, TextString = property, Tag = property, Prompt = property, Layer = DraftingStandardService.GetLayerName(DraftingStandardProfile.AnnotationTextLayerKey), TextStyleId = style, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = center, Constant = false, Verifiable = false };
                if (color == null) text.ColorIndex = 7;
                else if (color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci) text.ColorIndex = color.ColorIndex;
                else text.Color = color;
                var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite); space.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true); text.AdjustAlignment(document.Database); tr.Commit();
            }
            return true;
        }

        public static string CreateFrameBlockFromSelection(Document document, string requestedName, double expectedWidth, double expectedHeight, string font, double textHeight, string remark, out string error, out string detectedPaper, out string detectedExtension, out string detectedOrientation, out ObjectId createdReferenceId)
        {
            error = null; detectedPaper = string.Empty; detectedExtension = string.Empty; detectedOrientation = string.Empty; createdReferenceId = ObjectId.Null;
            if (document == null) { error = "没有当前 CAD 文档。"; return null; }
            var editor = document.Editor;
            var selection = editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n请选择用于生成图框的对象: " });
            if (selection.Status != PromptStatus.OK || selection.Value.Count == 0) { error = "没有选择对象，已取消创建图框。"; return null; }
            Extents3d extents;
            try
            {
                extents = new Extents3d();
                var first = true;
                using (var tr = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject selected in selection.Value)
                    {
                        if (selected == null) continue;
                        var entity = tr.GetObject(selected.ObjectId, OpenMode.ForRead) as Entity;
                        if (entity == null) continue;
                        if (first) { extents = entity.GeometricExtents; first = false; } else extents.AddExtents(entity.GeometricExtents);
                    }
                    tr.Commit();
                }
            }
            catch (Exception exception) { error = "无法读取所选对象范围：" + exception.Message; return null; }
            var actualWidth = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X); var actualHeight = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            if (!PaperSizeCatalog.TryIdentify(actualWidth, actualHeight, out detectedPaper, out detectedExtension, out detectedOrientation)) { error = "所选对象的实际尺寸为 " + Math.Round(actualWidth) + " × " + Math.Round(actualHeight) + " mm，不属于图框数据库中的 A0～A4 或常用加长尺寸，不能创建图框块。"; return null; }
            using (document.LockDocument()) using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                var blocks = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForWrite);
                var paperDisplay = detectedPaper + (string.IsNullOrWhiteSpace(detectedExtension) ? string.Empty : "+" + detectedExtension);
                // AutoCAD symbol names cannot contain '/', although the user-facing
                // extension notation intentionally uses values such as "1/4".
                // Keep the readable notation in FrameDefinition, but encode it as
                // "1_4" in the block table record name.
                var baseName = SafeBlockName(paperDisplay + "_BPP_" + SafeName(remark));
                var name = baseName; var index = 2; while (blocks.Has(name)) name = baseName + "_" + index++;
                try
                {
                    SymbolUtilityServices.ValidateSymbolName(name, false);
                }
                catch (Exception exception)
                {
                    error = "生成的图块名称无效：" + name + "。\r\n请修改用户备注后重试。\r\n" + exception.Message;
                    return null;
                }
                var record = new BlockTableRecord { Name = name, Origin = Point3d.Origin }; var recordId = blocks.Add(record); tr.AddNewlyCreatedDBObject(record, true);
                var ids = new ObjectIdCollection(); foreach (SelectedObject selected in selection.Value) if (selected != null) ids.Add(selected.ObjectId);
                var mapping = new IdMapping(); document.Database.DeepCloneObjects(ids, recordId, mapping, false);
                var move = Matrix3d.Displacement(new Vector3d(-extents.MinPoint.X, -extents.MinPoint.Y, -extents.MinPoint.Z));
                foreach (ObjectId id in record) { var entity = tr.GetObject(id, OpenMode.ForWrite) as Entity; if (entity != null) entity.TransformBy(move); }
                DraftingStandardService.EnsureAll(document.Database, tr);
                var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite); var reference = new BlockReference(extents.MinPoint, recordId) { Layer = DraftingStandardService.GetLayerName(DraftingStandardProfile.FrameKey) }; space.AppendEntity(reference); tr.AddNewlyCreatedDBObject(reference, true);
                foreach (ObjectId id in record)
                {
                    var definition = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition; if (definition == null || definition.Constant) continue;
                    var attribute = new AttributeReference(); attribute.SetAttributeFromBlock(definition, reference.BlockTransform); attribute.Layer = DraftingStandardService.GetLayerName(DraftingStandardProfile.FrameKey); attribute.TextString = string.IsNullOrWhiteSpace(definition.TextString) || definition.TextString.StartsWith("<", StringComparison.Ordinal) ? definition.Tag : definition.TextString; reference.AttributeCollection.AppendAttribute(attribute); tr.AddNewlyCreatedDBObject(attribute, true);
                }
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null) continue;
                    var original = tr.GetObject(selected.ObjectId, OpenMode.ForWrite, false) as Entity;
                    if (original != null && !original.IsErased) original.Erase();
                }
                createdReferenceId = reference.ObjectId;
                tr.Commit(); return name;
            }
        }

        private static Polyline Rectangle(Point3d origin, double width, double height)
        { var p = new Polyline(4); p.AddVertexAt(0, new Point2d(origin.X, origin.Y), 0, 0, 0); p.AddVertexAt(1, new Point2d(origin.X + width, origin.Y), 0, 0, 0); p.AddVertexAt(2, new Point2d(origin.X + width, origin.Y + height), 0, 0, 0); p.AddVertexAt(3, new Point2d(origin.X, origin.Y + height), 0, 0, 0); p.Closed = true; return p; }
        private static ObjectId FindTextStyle(Database database, Transaction tr, string name)
        {
            return DraftingStandardService.ResolveTextStyle(database, tr, name, false);
        }

        private static string PreferDefault(string configured, string fallback)
        { return string.IsNullOrWhiteSpace(configured) ? fallback : configured; }

        private static bool TagMatches(string tag, string configured, params string[] aliases)
        {
            if (!string.IsNullOrWhiteSpace(configured) && string.Equals(tag, configured.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return aliases.Any(alias => string.Equals(tag, alias, StringComparison.OrdinalIgnoreCase) || tag.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void LogInsertion(string message)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(UserDataPaths.LogsDirectory, "frame-insertion.log"),
                    DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static string SafeName(string value)
        { var clean = string.IsNullOrWhiteSpace(value) ? "自建图框" : value.Trim(); foreach (var character in System.IO.Path.GetInvalidFileNameChars()) clean = clean.Replace(character, '_'); return clean; }
        private static string SafeBlockName(string value)
        {
            var clean = SafeName(value);
            foreach (var character in new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' })
                clean = clean.Replace(character, '_');
            return clean.Trim();
        }
    }
}
