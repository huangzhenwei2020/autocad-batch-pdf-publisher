using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace BatchPdfPublisher.Services
{
    public static class FrameCreationService
    {
        public static ObjectId EnsureTextStyle(Database database, Transaction transaction, string name)
        { return FindTextStyle(database, transaction, name); }
        public static string[] GetTextStyleNames(Document document)
        {
            var names = new System.Collections.Generic.List<string> { "黑体", "宋体", "微软雅黑", "Arial" };
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
                var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
                var poly = Rectangle(point.Value, width, height); space.AppendEntity(poly); tr.AddNewlyCreatedDBObject(poly, true); tr.Commit();
            }
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
                var text = new AttributeDefinition { Position = center, Height = height, WidthFactor = widthFactor, TextString = property, Tag = property, Prompt = property, Layer = "0", TextStyleId = style, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = center, Constant = false, Verifiable = false };
                if (color == null) text.ColorIndex = 7;
                else if (color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci) text.ColorIndex = color.ColorIndex;
                else text.Color = color;
                var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite); space.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true); text.AdjustAlignment(document.Database); tr.Commit();
            }
            return true;
        }

        public static string CreateFrameBlock(Document document, string requestedName, double width, double height, string font, double textHeight)
        {
            if (document == null) return null;
            var editor = document.Editor; var point = editor.GetPoint("\n指定图框块左下角插入点: ");
            if (point.Status != PromptStatus.OK) return null;
            using (document.LockDocument()) using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                var blocks = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                var name = requestedName; var index = 2; while (blocks.Has(name)) name = requestedName + "_" + index++;
                var record = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
                blocks.UpgradeOpen(); var recordId = blocks.Add(record); tr.AddNewlyCreatedDBObject(record, true);
                var poly = Rectangle(Point3d.Origin, width, height); record.AppendEntity(poly); tr.AddNewlyCreatedDBObject(poly, true);
                var style = FindTextStyle(document.Database, tr, font);
                foreach (var tag in new[] { "工程名称", "子项目名称", "图纸名称", "设计编号", "设计阶段", "图号", "序号", "纸张", "比例", "日期", "版本" })
                {
                    var att = new AttributeDefinition { Position = new Point3d(width / 2d, height / 2d, 0), Height = textHeight, TextString = "<" + tag + ">", Tag = tag, Prompt = tag, Verifiable = false, Constant = false, Invisible = false, TextStyleId = style, HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid, AlignmentPoint = new Point3d(width / 2d, height / 2d, 0) };
                    record.AppendEntity(att); tr.AddNewlyCreatedDBObject(att, true); att.AdjustAlignment(document.Database);
                }
                var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite); var reference = new BlockReference(point.Value, recordId); space.AppendEntity(reference); tr.AddNewlyCreatedDBObject(reference, true);
                foreach (ObjectId id in record)
                {
                    var definition = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition; if (definition == null || definition.Constant) continue;
                    var attribute = new AttributeReference(); attribute.SetAttributeFromBlock(definition, reference.BlockTransform); attribute.TextString = string.IsNullOrWhiteSpace(definition.TextString) || definition.TextString.StartsWith("<", StringComparison.Ordinal) ? definition.Tag : definition.TextString; reference.AttributeCollection.AppendAttribute(attribute); tr.AddNewlyCreatedDBObject(attribute, true);
                }
                tr.Commit(); return name;
            }
        }

        public static string CreateFrameBlockFromSelection(Document document, string requestedName, double expectedWidth, double expectedHeight, string font, double textHeight, string remark, out string error, out string detectedPaper, out string detectedExtension, out string detectedOrientation)
        {
            error = null; detectedPaper = string.Empty; detectedExtension = string.Empty; detectedOrientation = string.Empty;
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
                var baseName = paperDisplay + "_BPP_" + SafeName(remark);
                var name = baseName; var index = 2; while (blocks.Has(name)) name = baseName + "_" + index++;
                var record = new BlockTableRecord { Name = name, Origin = Point3d.Origin }; var recordId = blocks.Add(record); tr.AddNewlyCreatedDBObject(record, true);
                var ids = new ObjectIdCollection(); foreach (SelectedObject selected in selection.Value) if (selected != null) ids.Add(selected.ObjectId);
                var mapping = new IdMapping(); document.Database.DeepCloneObjects(ids, recordId, mapping, false);
                var move = Matrix3d.Displacement(new Vector3d(-extents.MinPoint.X, -extents.MinPoint.Y, -extents.MinPoint.Z));
                foreach (ObjectId id in record) { var entity = tr.GetObject(id, OpenMode.ForWrite) as Entity; if (entity != null) entity.TransformBy(move); }
                var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite); var reference = new BlockReference(extents.MinPoint, recordId); space.AppendEntity(reference); tr.AddNewlyCreatedDBObject(reference, true);
                foreach (ObjectId id in record)
                {
                    var definition = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition; if (definition == null || definition.Constant) continue;
                    var attribute = new AttributeReference(); attribute.SetAttributeFromBlock(definition, reference.BlockTransform); attribute.TextString = string.IsNullOrWhiteSpace(definition.TextString) || definition.TextString.StartsWith("<", StringComparison.Ordinal) ? definition.Tag : definition.TextString; reference.AttributeCollection.AppendAttribute(attribute); tr.AddNewlyCreatedDBObject(attribute, true);
                }
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null) continue;
                    var original = tr.GetObject(selected.ObjectId, OpenMode.ForWrite, false) as Entity;
                    if (original != null && !original.IsErased) original.Erase();
                }
                tr.Commit(); return name;
            }
        }

        private static Polyline Rectangle(Point3d origin, double width, double height)
        { var p = new Polyline(4); p.AddVertexAt(0, new Point2d(origin.X, origin.Y), 0, 0, 0); p.AddVertexAt(1, new Point2d(origin.X + width, origin.Y), 0, 0, 0); p.AddVertexAt(2, new Point2d(origin.X + width, origin.Y + height), 0, 0, 0); p.AddVertexAt(3, new Point2d(origin.X, origin.Y + height), 0, 0, 0); p.Closed = true; return p; }
        private static ObjectId FindTextStyle(Database database, Transaction tr, string name)
        {
            var table = (TextStyleTable)tr.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in table) { var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead); if (string.Equals(style.Name, "BPP_" + name, StringComparison.OrdinalIgnoreCase)) return id; }
            table.UpgradeOpen();
            var created = new TextStyleTableRecord { Name = "BPP_" + name };
            var fonts = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "黑体", "simhei.ttf" }, { "宋体", "simsun.ttc" }, { "微软雅黑", "msyh.ttc" }, { "Arial", "arial.ttf" } };
            created.FileName = fonts.ContainsKey(name ?? string.Empty) ? fonts[name] : "simhei.ttf";
            var idCreated = table.Add(created); tr.AddNewlyCreatedDBObject(created, true); return idCreated;
        }

        private static string SafeName(string value)
        { var clean = string.IsNullOrWhiteSpace(value) ? "自建图框" : value.Trim(); foreach (var character in System.IO.Path.GetInvalidFileNameChars()) clean = clean.Replace(character, '_'); return clean; }
    }
}
