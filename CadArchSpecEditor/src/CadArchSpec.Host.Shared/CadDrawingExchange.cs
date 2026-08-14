using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.Shared
{
    internal static class CadDrawingExchange
    {
        private sealed class PaperCandidate
        {
            public string Name;
            public double Width;
            public double Height;
        }

        private static readonly PaperCandidate[] Papers =
        {
            Paper("A0", 1189, 841), Paper("A0+1/4", 1486, 841), Paper("A0+1/2", 1784, 841),
            Paper("A1", 841, 594), Paper("A1+1/4", 1051, 594), Paper("A1+1/2", 1261, 594),
            Paper("A2", 594, 420), Paper("A2+1/4", 743, 420), Paper("A2+1/2", 891, 420),
            Paper("A3", 420, 297), Paper("A3+1/4", 525, 297), Paper("A3+1/2", 630, 297),
            Paper("A4", 297, 210), Paper("A4+1/4", 371, 210), Paper("A4+1/2", 446, 210)
        };

        public static async Task<JObject> PickFrameAndTextAreaAsync()
        {
            JObject result = null;
            await Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = PickFrameAndTextAreaCore();
                    await Task.CompletedTask;
                }, null);
            return result;
        }

        public static async Task<JObject> ReadSelectedTextAsync(string sectionId)
        {
            JObject result = null;
            await Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = ReadSelectedTextCore(sectionId);
                    await Task.CompletedTask;
                }, null);
            return result;
        }

        public static async Task<JObject> InsertSectionAsync(JObject payload)
        {
            JObject result = null;
            await Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = InsertSectionCore(payload);
                    await Task.CompletedTask;
                }, null);
            return result;
        }

        private static JObject PickFrameAndTextAreaCore()
        {
            var document = ActiveDocument();
            document.Window.Focus();
            var editor = document.Editor;
            var entityOptions = new PromptEntityOptions("\n请选择建筑设计说明使用的图框块：");
            entityOptions.SetRejectMessage("\n请选择块参照作为图框。");
            entityOptions.AddAllowedClass(typeof(BlockReference), true);
            var entityResult = editor.GetEntity(entityOptions);
            if (entityResult.Status != PromptStatus.OK) throw new InvalidOperationException("已取消拾取图框。");

            Extents3d extents;
            string blockName;
            string handle;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var reference = transaction.GetObject(entityResult.ObjectId, OpenMode.ForRead, false) as BlockReference;
                if (reference == null) throw new InvalidOperationException("所选对象不是有效图框块。");
                extents = reference.GeometricExtents;
                var recordId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                var record = transaction.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
                blockName = record == null ? string.Empty : record.Name;
                handle = reference.Handle.ToString();
                transaction.Commit();
            }

            var first = editor.GetPoint("\n指定说明文字编辑区第一角点：");
            if (first.Status != PromptStatus.OK) throw new InvalidOperationException("已取消指定文字编辑区。");
            var cornerOptions = new PromptCornerOptions("\n指定说明文字编辑区另一角点：", first.Value)
            {
                UseDashedLine = true
            };
            var second = editor.GetCorner(cornerOptions);
            if (second.Status != PromptStatus.OK) throw new InvalidOperationException("已取消指定文字编辑区。");

            var textMin = new Point3d(Math.Min(first.Value.X, second.Value.X), Math.Min(first.Value.Y, second.Value.Y), 0);
            var textMax = new Point3d(Math.Max(first.Value.X, second.Value.X), Math.Max(first.Value.Y, second.Value.Y), 0);
            var frameWidth = extents.MaxPoint.X - extents.MinPoint.X;
            var frameHeight = extents.MaxPoint.Y - extents.MinPoint.Y;
            if (frameWidth <= 0 || frameHeight <= 0) throw new InvalidOperationException("图框尺寸无效。");
            var tolerance = Math.Max(frameWidth, frameHeight) * 0.002;
            if (textMin.X < extents.MinPoint.X - tolerance || textMin.Y < extents.MinPoint.Y - tolerance ||
                textMax.X > extents.MaxPoint.X + tolerance || textMax.Y > extents.MaxPoint.Y + tolerance)
                throw new InvalidOperationException("文字编辑区必须位于所选图框范围内。");

            var paper = DetectPaper(frameWidth, frameHeight);
            var scale = paper.Item4;
            var margins = new JObject
            {
                ["left"] = Round((textMin.X - extents.MinPoint.X) / scale),
                ["top"] = Round((extents.MaxPoint.Y - textMax.Y) / scale),
                ["right"] = Round((extents.MaxPoint.X - textMax.X) / scale),
                ["bottom"] = Round((textMin.Y - extents.MinPoint.Y) / scale)
            };
            editor.WriteMessage("\n已识别 {0}，图框 {1:0.##}×{2:0.##}，文字区 {3:0.##}×{4:0.##}。",
                paper.Item1, frameWidth, frameHeight, textMax.X - textMin.X, textMax.Y - textMin.Y);
            return new JObject
            {
                ["paperName"] = paper.Item1,
                ["landscape"] = paper.Item2 >= paper.Item3,
                ["paperWidthMillimeters"] = paper.Item2,
                ["paperHeightMillimeters"] = paper.Item3,
                ["drawingScale"] = Round(scale),
                ["bodyTextHeightMillimeters"] = 3.5,
                ["columnCount"] = paper.Item2 >= paper.Item3 ? 2 : 1,
                ["columnGapMillimeters"] = 12,
                ["frameBlockName"] = blockName,
                ["frameHandle"] = handle,
                ["drawingPath"] = document.Name ?? string.Empty,
                ["frameArea"] = Rectangle(extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y),
                ["textArea"] = Rectangle(textMin.X, textMin.Y, textMax.X, textMax.Y),
                ["textMarginsMillimeters"] = margins
            };
        }

        private static JObject ReadSelectedTextCore(string sectionId)
        {
            var document = ActiveDocument();
            document.Window.Focus();
            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\n框选或点选需要读取的单行文字/多行文字：",
                MessageForRemoval = "\n移除对象："
            };
            var result = document.Editor.GetSelection(options);
            if (result.Status != PromptStatus.OK) throw new InvalidOperationException("未选择可读取的 CAD 文字。");
            var texts = new List<Tuple<double, double, string>>();
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in result.Value.GetObjectIds())
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    var dbText = entity as DBText;
                    var mText = entity as MText;
                    string text = null;
                    Point3d position;
                    if (dbText != null)
                    {
                        text = dbText.TextString;
                        position = dbText.Position;
                    }
                    else if (mText != null)
                    {
                        text = mText.Text;
                        position = mText.Location;
                    }
                    else continue;
                    text = (text ?? string.Empty).Trim();
                    if (text.Length > 0) texts.Add(Tuple.Create(position.Y, position.X, text));
                }
                transaction.Commit();
            }
            if (texts.Count == 0) throw new InvalidOperationException("所选对象中没有 DBText 或 MText。");
            var ordered = texts.OrderByDescending(x => x.Item1).ThenBy(x => x.Item2).Select(x => x.Item3).ToArray();
            return new JObject
            {
                ["sectionId"] = sectionId ?? string.Empty,
                ["text"] = string.Join(Environment.NewLine, ordered),
                ["count"] = ordered.Length,
                ["drawingPath"] = document.Name ?? string.Empty
            };
        }

        private static JObject InsertSectionCore(JObject payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            var layout = payload["cadLayout"] as JObject;
            var area = layout == null ? null : layout["textArea"] as JObject;
            if (layout == null || area == null || string.IsNullOrWhiteSpace((string)layout["frameHandle"]))
                throw new InvalidOperationException("请先在“CAD 版面”中拾取图框并指定文字编辑区。");
            var text = ((string)payload["plainText"] ?? string.Empty).Trim();
            if (text.Length == 0) throw new InvalidOperationException("当前章节没有可插入的文字。");

            var document = ActiveDocument();
            document.Window.Focus();
            var recordedPath = ((string)layout["drawingPath"] ?? string.Empty).Trim();
            if (recordedPath.Length > 0 && !SameDrawing(recordedPath, document.Name))
                throw new InvalidOperationException("记录的图框不在当前 DWG 中，请重新拾取当前图纸的图框。");

            var minX = RequiredDouble(area, "minX");
            var minY = RequiredDouble(area, "minY");
            var maxX = RequiredDouble(area, "maxX");
            var maxY = RequiredDouble(area, "maxY");
            var scale = Math.Max(0.0001, (double?)layout["drawingScale"] ?? 1d);
            var paperTextHeight = Math.Max(1d, (double?)layout["bodyTextHeightMillimeters"] ?? 3.5d);
            var columnCount = Math.Max(1, Math.Min(3, (int?)layout["columnCount"] ?? 1));
            var columnGap = Math.Max(0d, (double?)layout["columnGapMillimeters"] ?? 12d) * scale;
            string insertedHandle;
            double actualHeight;
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                EnsureFrameExists(document.Database, transaction, (string)layout["frameHandle"]);
                var layerName = EnsureTextLayer(document.Database, transaction);
                var space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) throw new InvalidOperationException("当前空间不可写入。");
                using (var mtext = new MText())
                {
                    mtext.SetDatabaseDefaults(document.Database);
                    mtext.Layer = layerName;
                    mtext.Location = new Point3d(minX, maxY, 0);
                    mtext.Attachment = AttachmentPoint.TopLeft;
                    var textAreaWidth = Math.Max(1d, maxX - minX);
                    mtext.Width = textAreaWidth;
                    mtext.TextHeight = paperTextHeight * scale;
                    mtext.Contents = EscapeMText(text);
                    TryConfigureMTextColumns(mtext, columnCount, textAreaWidth, columnGap);
                    space.AppendEntity(mtext);
                    transaction.AddNewlyCreatedDBObject(mtext, true);
                    insertedHandle = mtext.Handle.ToString();
                    actualHeight = mtext.ActualHeight;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            return new JObject
            {
                ["handle"] = insertedHandle,
                ["sectionId"] = (string)payload["sectionId"] ?? string.Empty,
                ["textAreaHeight"] = maxY - minY,
                ["actualHeight"] = Round(actualHeight),
                ["columnCount"] = columnCount,
                ["overflow"] = actualHeight > (maxY - minY) * 1.01,
                ["drawingPath"] = document.Name ?? string.Empty
            };
        }

        private static void TryConfigureMTextColumns(MText mtext, int columnCount, double totalWidth, double gutter)
        {
            if (columnCount <= 1) return;
            try
            {
                // Column API names are stable in recent AutoCAD releases, but reflection keeps
                // this shared R24 source loadable by hosts whose managed API omits columns.
                var type = mtext.GetType();
                var columnType = type.GetProperty("ColumnType");
                var count = type.GetProperty("ColumnCount");
                var width = type.GetProperty("ColumnWidth");
                var gap = type.GetProperty("ColumnGutter");
                if (columnType == null || count == null || width == null || gap == null) return;
                var staticColumns = Enum.Parse(columnType.PropertyType, "StaticColumns", true);
                columnType.SetValue(mtext, staticColumns, null);
                count.SetValue(mtext, columnCount, null);
                gap.SetValue(mtext, gutter, null);
                width.SetValue(mtext, Math.Max(1d, (totalWidth - gutter * (columnCount - 1)) / columnCount), null);
            }
            catch
            {
                // A single MText remains usable on AutoCAD variants without native columns.
            }
        }

        private static void EnsureFrameExists(Database database, Transaction transaction, string handleText)
        {
            long value;
            if (!long.TryParse((handleText ?? string.Empty).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                throw new InvalidOperationException("记录的图框 Handle 无效，请重新拾取图框。");
            try
            {
                var id = database.GetObjectId(false, new Handle(value), 0);
                if (id.IsNull || transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference == null)
                    throw new InvalidOperationException("记录的图框已被删除，请重新拾取图框。");
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                throw new InvalidOperationException("当前图纸中找不到记录的图框，请重新拾取图框。");
            }
        }

        private static string EnsureTextLayer(Database database, Transaction transaction)
        {
            const string name = "WL-说明-文字";
            var layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name)) return name;
            layers.UpgradeOpen();
            using (var layer = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, 7) })
            {
                layers.Add(layer);
                transaction.AddNewlyCreatedDBObject(layer, true);
            }
            return name;
        }

        private static Tuple<string, double, double, double> DetectPaper(double frameWidth, double frameHeight)
        {
            Tuple<string, double, double, double> best = null;
            var bestError = double.MaxValue;
            foreach (var paper in Papers)
            {
                foreach (var orientation in new[] { Tuple.Create(paper.Width, paper.Height), Tuple.Create(paper.Height, paper.Width) })
                {
                    var scaleX = frameWidth / orientation.Item1;
                    var scaleY = frameHeight / orientation.Item2;
                    var scale = (scaleX + scaleY) / 2d;
                    var error = Math.Abs(scaleX - scaleY) / Math.Max(scale, 0.0001);
                    if (error >= bestError) continue;
                    bestError = error;
                    best = Tuple.Create(paper.Name, orientation.Item1, orientation.Item2, scale);
                }
            }
            if (best == null || bestError > 0.03)
                throw new InvalidOperationException("所选图框长宽比不属于 A0-A4 或 +1/4、+1/2 图框，请检查图框边界。");
            return best;
        }

        private static PaperCandidate Paper(string name, double width, double height)
        {
            return new PaperCandidate { Name = name, Width = width, Height = height };
        }

        private static JObject Rectangle(double minX, double minY, double maxX, double maxY)
        {
            return new JObject { ["minX"] = Round(minX), ["minY"] = Round(minY), ["maxX"] = Round(maxX), ["maxY"] = Round(maxY) };
        }

        private static double RequiredDouble(JObject value, string property)
        {
            var token = value[property];
            if (token == null) throw new InvalidOperationException("CAD 版面缺少 " + property + "。");
            return (double)token;
        }

        private static double Round(double value) { return Math.Round(value, 4, MidpointRounding.AwayFromZero); }

        private static string EscapeMText(string value)
        {
            return value.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}")
                .Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P");
        }

        private static bool SameDrawing(string left, string right)
        {
            try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        }

        private static Document ActiveDocument()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) throw new InvalidOperationException("当前没有活动的 CAD 图纸。");
            return document;
        }
    }
}
