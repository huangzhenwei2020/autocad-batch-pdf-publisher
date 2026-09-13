using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CadArchSpec.CadTable;
using CadArchSpec.EditorBridge;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal static class CadTableExchange
    {
        public static JObject ExportSelectedTableToXlsx()
        {
            JObject recognized = null;
            var templates = CadTableTemplateStore.Load();
            if (templates.Count > 0)
            {
                using (var start = new CadTableStartForm(templates))
                {
                    start.ShowDialog();
                    if (start.SelectedAction == CadTableStartAction.Cancel)
                        return new JObject { ["cancelled"] = true };
                    if (start.SelectedAction == CadTableStartAction.OpenTemplate && start.SelectedTemplate != null)
                        recognized = (JObject)start.SelectedTemplate.Payload.DeepClone();
                    else if (start.SelectedAction == CadTableStartAction.PickExistingForUpdate)
                        recognized = ReadSelectedTableCore(false, true);
                }
            }
            while (true)
            {
                if (recognized == null) recognized = ReadSelectedTableCore(false, false);
                if ((bool?)recognized["cancelled"] == true) return recognized;
                string currentTextStyle;
                var textStyles = ReadTextStyles(out currentTextStyle);
                using (var preview = new CadTablePreviewForm(recognized, textStyles, currentTextStyle))
                {
                    preview.ShowDialog();
                    if (preview.SelectedAction == CadTablePreviewAction.Repick)
                    {
                        recognized = ReadSelectedTableCore(false, true);
                        continue;
                    }
                    if (preview.SelectedAction == CadTablePreviewAction.PickCadObjects)
                    {
                        recognized = CaptureCellCadObjects(preview.Payload);
                        continue;
                    }
                    if (preview.SelectedAction == CadTablePreviewAction.Cancel)
                        return new JObject { ["cancelled"] = true };
                    if (preview.SelectedAction == CadTablePreviewAction.InsertCad)
                        return InsertTable(preview.Payload);

                    var exported = CadTableXlsxExchange.Export(preview.Payload, null);
                    if ((bool?)exported["cancelled"] == true) return exported;
                    CopyResultSummary(exported, preview.Payload);
                    exported["action"] = "exported";
                    return exported;
                }
            }
        }

        private static JObject InsertTable(JObject payload)
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) throw new InvalidOperationException("当前没有活动的 CAD 图纸。");
            document.Window.Focus();
            var updateTarget = ResolveTableUpdateTarget(payload, document);
            Point3d insertionPoint;
            if (updateTarget != null) insertionPoint = updateTarget.Position;
            else
            {
                var pointResult = document.Editor.GetPoint("\n指定重新插入 CAD 表格的位置：");
                if (pointResult.Status != PromptStatus.OK) return new JObject { ["cancelled"] = true };
                insertionPoint = pointResult.Value;
            }

            var source = payload["table"] as JObject;
            var columns = (source == null ? null : source["columns"] as JArray) ?? new JArray();
            var rows = (source == null ? null : source["rows"] as JArray) ?? new JArray();
            if (columns.Count == 0 || rows.Count == 0) throw new InvalidDataException("预览中没有可插入的表格数据。");

            var options = payload["cadInsertOptions"] as JObject ?? new JObject();
            SaveEditorDefaults(source, options);
            var useOriginalSize = (bool?)options["useOriginalCadSize"] == true && (bool?)payload["hasOriginalCadSize"] == true;
            var scale = Math.Max(.001d, (double?)options["scale"] ?? 1d);
            var contentScale = useOriginalSize ? ResolveOriginalCadScale(source) : scale;
            var textHeight = Math.Max(.1d, (double?)options["textHeightMillimeters"] ?? 3.5d) * contentScale;
            var textStyleName = ((string)options["textStyle"] ?? string.Empty).Trim();
            var insertAsTianzheng = string.Equals((string)options["insertType"], "tianzheng", StringComparison.OrdinalIgnoreCase);
            var outerBorderColor = ReadCadIndexedColor(source["outerBorderColorIndex"]);
            var innerBorderColor = ReadCadIndexedColor(source["innerBorderColorIndex"]);
            var outerBorderWeight = (double?)source["outerBorderWeightMillimeters"] ?? .25d;
            var innerBorderWeight = (double?)source["innerBorderWeightMillimeters"] ?? .13d;
            var editedColumnTotal = columns.OfType<JObject>().Sum(column => Math.Max(.001d, (double?)column["widthMillimeters"] ?? 36d));
            var sourceColumnTotal = columns.OfType<JObject>().Sum(column => Math.Max(0d, (double?)column["sourceWidthCadUnits"] ?? 0d));
            var editedRowTotal = rows.OfType<JObject>().Sum(row => Math.Max(.001d, (double?)row["heightMillimeters"] ?? 8d));
            var sourceRowTotal = rows.OfType<JObject>().Sum(row => Math.Max(0d, (double?)row["sourceHeightCadUnits"] ?? 0d));

            if (insertAsTianzheng)
                return InsertTianzhengTable(payload, document, insertionPoint, updateTarget);

            var cadObjectBlocks = ImportCellCadObjectBlocks(document.Database, source);

            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
                var textStyleId = ResolveTextStyle(document.Database, transaction, textStyleName);
                var updateNative = updateTarget != null && !updateTarget.NativeTableId.IsNull && updateTarget.NativeTableId.IsValid;
                var table = updateNative
                    ? (Table)transaction.GetObject(updateTarget.NativeTableId, OpenMode.ForWrite, false)
                    : new Table();
                if (updateNative) ClearNativeTableMerges(table);
                else
                {
                    table.SetDatabaseDefaults(document.Database);
                    table.Position = insertionPoint;
                    if (updateTarget != null)
                    {
                        table.Rotation = updateTarget.RotationRadians;
                        if (!updateTarget.LayerId.IsNull && updateTarget.LayerId.IsValid) table.LayerId = updateTarget.LayerId;
                    }
                }
                table.SetSize(rows.Count, columns.Count);
                table.SetRowHeight(Math.Max(8d * scale, textHeight * 1.8d));
                table.SetColumnWidth(36d * scale);

                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    var column = columns[columnIndex] as JObject;
                    var editedWidth = Math.Max(.001d, (double?)column?["widthMillimeters"] ?? 36d);
                    table.Columns[columnIndex].Width = useOriginalSize && sourceColumnTotal > .001d
                        ? sourceColumnTotal * editedWidth / editedColumnTotal
                        : Math.Max(8d * scale, editedWidth * scale);
                }

                var merges = new List<CellRange>();
                var cellMargins = new List<Tuple<int, int, double>>();
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var row = rows[rowIndex] as JObject;
                    var cells = (row == null ? null : row["cells"] as JArray) ?? new JArray();
                    var editedHeight = Math.Max(.001d, (double?)row?["heightMillimeters"] ?? 8d);
                    table.Rows[rowIndex].Height = useOriginalSize && sourceRowTotal > .001d
                        ? sourceRowTotal * editedHeight / editedRowTotal
                        : Math.Max(textHeight * 1.8d, editedHeight * scale);
                    for (var columnIndex = 0; columnIndex < Math.Min(cells.Count, columns.Count); columnIndex++)
                    {
                        var cell = cells[columnIndex] as JObject;
                        if (cell == null) continue;
                        var rowSpan = Math.Max(0, (int?)cell["rowSpan"] ?? 1);
                        var columnSpan = Math.Max(0, (int?)cell["columnSpan"] ?? 1);
                        var borderColor = ReadCadIndexedColor(cell["borderColorIndex"]);
                        var fillColor = ReadCadIndexedColor(cell["fillColorIndex"]);
                        var contentColor = ReadCadIndexedColor(cell["textColorIndex"]);
                        ApplyCellColors(table.Cells[rowIndex, columnIndex], fillColor, contentColor);
                        ApplyCellBorders(table.Cells[rowIndex, columnIndex], borderColor, outerBorderColor,
                            innerBorderColor, outerBorderWeight, innerBorderWeight, rowIndex, columnIndex,
                            rows.Count, columns.Count);
                        if (rowSpan == 0 || columnSpan == 0)
                        {
                            table.Cells[rowIndex, columnIndex].TextString = string.Empty;
                            continue;
                        }
                        var padding = Math.Max(0d, (double?)cell["horizontalPaddingMillimeters"] ?? 1d) * contentScale;
                        cellMargins.Add(Tuple.Create(rowIndex, columnIndex, padding));
                        var actualTextHeight = textHeight;
                        var assetPath = ((string)cell["cadObjectAssetPath"] ?? string.Empty).Trim();
                        ObjectId cellBlockId;
                        if (assetPath.Length > 0 && cadObjectBlocks.TryGetValue(assetPath, out cellBlockId))
                        {
#pragma warning disable CS0618
                            table.SetBlockTableRecordId(rowIndex, columnIndex, cellBlockId, true);
#pragma warning restore CS0618
                        }
                        else
                        {
                            table.Cells[rowIndex, columnIndex].TextString = PadCadTableText(
                                CellTextForCadInsert(cell), (string)cell["alignment"], padding, actualTextHeight);
                        }
                        table.Cells[rowIndex, columnIndex].TextHeight = actualTextHeight;
                        table.Cells[rowIndex, columnIndex].TextStyleId = textStyleId;
                        table.Cells[rowIndex, columnIndex].Alignment = CadAlignment((string)cell["alignment"]);
                        if (rowSpan > 1 || columnSpan > 1)
                        {
                            var bottom = Math.Min(rows.Count - 1, rowIndex + rowSpan - 1);
                            var right = Math.Min(columns.Count - 1, columnIndex + columnSpan - 1);
                            if (bottom > rowIndex || right > columnIndex)
                                merges.Add(CellRange.Create(table, rowIndex, columnIndex, bottom, right));
                        }
                    }
                }
                foreach (var merge in merges) table.MergeCells(merge);
#pragma warning disable CS0618
                foreach (var margin in cellMargins)
                {
                    table.SetMargin(margin.Item1, margin.Item2, CellMargins.Left, margin.Item3);
                    table.SetMargin(margin.Item1, margin.Item2, CellMargins.Right, margin.Item3);
                }
#pragma warning restore CS0618
                table.GenerateLayout();
                if (!updateNative)
                {
                    currentSpace.AppendEntity(table);
                    transaction.AddNewlyCreatedDBObject(table, true);
                }
                if (updateTarget != null)
                    foreach (var id in updateTarget.LooseEntityIds.Where(id => id.IsValid && !id.IsErased))
                    {
                        var oldEntity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (oldEntity != null) oldEntity.Erase();
                    }
                transaction.Commit();
            }

            var result = new JObject { ["action"] = updateTarget == null ? "inserted" : "updated", ["insertType"] = "autocad" };
            CopyResultSummary(result, payload);
            return result;
        }

        public static async Task<JObject> InsertTableAsync(JObject payload)
        {
            JObject result = null;
            await Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = InsertTable(payload ?? new JObject());
                    await Task.CompletedTask;
                }, null);
            return result;
        }

        public static JObject PrepareStandaloneEditorPayload(JObject payload)
        {
            var result = payload == null ? new JObject() : (JObject)payload.DeepClone();
            string currentTextStyle;
            ApplyEditorDefaults(result);
            result["standaloneEditor"] = true;
            result["cadTextStyles"] = new JArray(ReadTextStyles(out currentTextStyle));
            result["currentTextStyle"] = currentTextStyle;
            result["cadTableDefaults"] = CadTableDefaultsStore.Load();
            result["cadTableTemplates"] = SerializeTemplates(CadTableTemplateStore.Load());
            AttachCadObjectPreviewData(result["table"] as JObject);
            return result;
        }

        public static JObject CreateStandaloneEditorPayload()
        {
            var tableId = "cad-table-" + Guid.NewGuid().ToString("N");
            return PrepareStandaloneEditorPayload(new JObject
            {
                ["showStartChooser"] = true,
                ["nativeTable"] = false,
                ["hasOriginalCadSize"] = false,
                ["suggestedInsertType"] = "autocad",
                ["table"] = new JObject
                {
                    ["tableId"] = tableId,
                    ["schemaVersion"] = 1,
                    ["tableType"] = "custom",
                    ["tableNumber"] = string.Empty,
                    ["title"] = "新表格",
                    ["repeatHeader"] = false,
                    ["allowSplitAcrossPages"] = false,
                    ["formulaAudits"] = new JArray(),
                    ["columns"] = new JArray(new JObject
                    {
                        ["key"] = "A", ["title"] = "A", ["unit"] = string.Empty,
                        ["widthMillimeters"] = 36d, ["decimalPlaces"] = 0, ["required"] = false
                    }),
                    ["rows"] = new JArray(new JObject
                    {
                        ["rowId"] = "row-" + Guid.NewGuid().ToString("N"), ["rowType"] = "Data", ["keepTogether"] = true,
                        ["heightMillimeters"] = 8d,
                        ["cells"] = new JArray(new JObject
                        {
                            ["cellId"] = "cell-" + Guid.NewGuid().ToString("N"), ["columnKey"] = "A",
                            ["displayValue"] = string.Empty, ["numericValue"] = JValue.CreateNull(), ["unit"] = string.Empty,
                            ["fieldPath"] = string.Empty, ["formula"] = string.Empty, ["state"] = "unknown", ["source"] = string.Empty,
                            ["sourceHandles"] = new JArray(), ["rowSpan"] = 1, ["columnSpan"] = 1
                        })
                    })
                }
            });
        }

        private static void AttachCadObjectPreviewData(JObject table)
        {
            if (table == null) return;
            foreach (var cell in (table["rows"] as JArray ?? new JArray()).OfType<JObject>()
                .SelectMany(row => (row["cells"] as JArray ?? new JArray()).OfType<JObject>()))
            {
                var path = ((string)cell["cadObjectPreviewPath"] ?? string.Empty).Trim();
                if (path.Length == 0 || !File.Exists(path)) continue;
                try { cell["cadObjectPreviewDataUrl"] = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path)); }
                catch { }
            }
        }

        private static void ApplyEditorDefaults(JObject payload)
        {
            var defaults = CadTableDefaultsStore.Load();
            var table = payload?["table"] as JObject;
            if (table == null) return;
            if (table["outerBorderColorIndex"] == null) table["outerBorderColorIndex"] = defaults["outerBorderColorIndex"]?.DeepClone() ?? 7;
            if (table["innerBorderColorIndex"] == null) table["innerBorderColorIndex"] = defaults["innerBorderColorIndex"]?.DeepClone() ?? 7;
            if (table["outerBorderWeightMillimeters"] == null) table["outerBorderWeightMillimeters"] = (double?)defaults["outerBorderWeightMillimeters"] ?? .25d;
            if (table["innerBorderWeightMillimeters"] == null) table["innerBorderWeightMillimeters"] = (double?)defaults["innerBorderWeightMillimeters"] ?? .13d;
            foreach (var row in (table["rows"] as JArray ?? new JArray()).OfType<JObject>())
                foreach (var cell in (row["cells"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (cell["horizontalPaddingMillimeters"] == null) cell["horizontalPaddingMillimeters"] = (double?)defaults["horizontalPaddingMillimeters"] ?? 1d;
                    if (cell["borderColorIndex"] == null && defaults["cellBorderColorIndex"] != null) cell["borderColorIndex"] = defaults["cellBorderColorIndex"].DeepClone();
                    if (cell["fillColorIndex"] == null && defaults["cellFillColorIndex"] != null) cell["fillColorIndex"] = defaults["cellFillColorIndex"].DeepClone();
                    if (cell["textColorIndex"] == null && defaults["cellTextColorIndex"] != null) cell["textColorIndex"] = defaults["cellTextColorIndex"].DeepClone();
                }
        }

        private static void SaveEditorDefaults(JObject table, JObject options)
        {
            if (table == null) return;
            var firstCell = (table["rows"] as JArray)?.OfType<JObject>()
                .SelectMany(row => (row["cells"] as JArray ?? new JArray()).OfType<JObject>())
                .FirstOrDefault(cell => (int?)cell["rowSpan"] != 0 && (int?)cell["columnSpan"] != 0);
            CadTableDefaultsStore.Save(new JObject
            {
                ["scale"] = (double?)options?["scale"] ?? 1d,
                ["textStyle"] = (string)options?["textStyle"] ?? "Standard",
                ["textHeightMillimeters"] = (double?)options?["textHeightMillimeters"] ?? 3.5d,
                ["useOriginalCadSize"] = (bool?)options?["useOriginalCadSize"] == true,
                ["insertType"] = (string)options?["insertType"] ?? "autocad",
                ["horizontalPaddingMillimeters"] = (double?)firstCell?["horizontalPaddingMillimeters"] ?? 1d,
                ["cellBorderColorIndex"] = firstCell?["borderColorIndex"]?.DeepClone(),
                ["cellFillColorIndex"] = firstCell?["fillColorIndex"]?.DeepClone(),
                ["cellTextColorIndex"] = firstCell?["textColorIndex"]?.DeepClone(),
                ["outerBorderColorIndex"] = table["outerBorderColorIndex"]?.DeepClone() ?? 7,
                ["innerBorderColorIndex"] = table["innerBorderColorIndex"]?.DeepClone() ?? 7,
                ["outerBorderWeightMillimeters"] = (double?)table["outerBorderWeightMillimeters"] ?? .25d,
                ["innerBorderWeightMillimeters"] = (double?)table["innerBorderWeightMillimeters"] ?? .13d
            });
        }

        public static async Task<JObject> CaptureCellCadObjectsAsync(JObject payload)
        {
            JObject result = null;
            await Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = PrepareStandaloneEditorPayload(CaptureCellCadObjects(payload));
                    await Task.CompletedTask;
                }, null);
            return result;
        }

        public static JObject SaveTemplateForEditor(JObject payload)
        {
            var name = ((string)payload?["name"] ?? string.Empty).Trim();
            var editorPayload = payload?["editorPayload"] as JObject ?? new JObject();
            var saved = CadTableTemplateStore.Save(name, editorPayload);
            return new JObject
            {
                ["savedId"] = saved.Id,
                ["cadTableTemplates"] = SerializeTemplates(CadTableTemplateStore.Load())
            };
        }

        public static JObject DeleteTemplateForEditor(JObject payload)
        {
            CadTableTemplateStore.Delete(((string)payload?["id"] ?? string.Empty).Trim());
            return new JObject { ["cadTableTemplates"] = SerializeTemplates(CadTableTemplateStore.Load()) };
        }

        private static JArray SerializeTemplates(IEnumerable<CadTableTemplate> templates)
        {
            return new JArray((templates ?? Enumerable.Empty<CadTableTemplate>()).Select(item =>
            {
                var templatePayload = item.Payload == null ? new JObject() : (JObject)item.Payload.DeepClone();
                AttachCadObjectPreviewData(templatePayload["table"] as JObject);
                return new JObject
                {
                    ["id"] = item.Id,
                    ["name"] = item.Name,
                    ["updatedAt"] = item.UpdatedAt,
                    ["payload"] = templatePayload
                };
            }));
        }

        private static JObject CaptureCellCadObjects(JObject payload)
        {
            var result = payload == null ? new JObject() : (JObject)payload.DeepClone();
            var row = (int?)result["pendingCadObjectRow"] ?? -1;
            var column = (int?)result["pendingCadObjectColumn"] ?? -1;
            result.Remove("pendingCadObjectRow");
            result.Remove("pendingCadObjectColumn");
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null || row < 0 || column < 0) return result;
            document.Window.Focus();
            var selection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\n框选要放入单元格的图块、线条或其他 CAD 对象：",
                MessageForRemoval = "\n移除不需要放入单元格的对象："
            });
            if (selection.Status != PromptStatus.OK) return result;
            var ids = new ObjectIdCollection(selection.Value.GetObjectIds());
            if (ids.Count == 0) return result;

            Extents3d? extents = null;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;
                    try
                    {
                        var current = entity.GeometricExtents;
                        if (extents == null) extents = current;
                        else { var combined = extents.Value; combined.AddExtents(current); extents = combined; }
                    }
                    catch { }
                }
                transaction.Commit();
            }
            var basePoint = extents.HasValue
                ? new Point3d((extents.Value.MinPoint.X + extents.Value.MaxPoint.X) * .5d,
                    (extents.Value.MinPoint.Y + extents.Value.MaxPoint.Y) * .5d,
                    (extents.Value.MinPoint.Z + extents.Value.MaxPoint.Z) * .5d)
                : Point3d.Origin;
            var assetPath = WriteCadObjectSnapshot(document, ids.Cast<ObjectId>(), basePoint);
            var table = result["table"] as JObject;
            var rows = table == null ? null : table["rows"] as JArray;
            var cells = rows != null && row < rows.Count ? (rows[row] as JObject)?["cells"] as JArray : null;
            var cell = cells != null && column < cells.Count ? cells[column] as JObject : null;
            if (cell != null)
            {
                cell["cadObjectAssetPath"] = assetPath;
                cell["cadObjectPreviewPath"] = Path.ChangeExtension(assetPath, ".png");
                cell["cadObjectCount"] = ids.Count;
            }
            return result;
        }

        private static string WriteCadObjectSnapshot(Document document, IEnumerable<ObjectId> objectIds, Point3d basePoint)
        {
            var ids = new ObjectIdCollection(objectIds.Where(id => !id.IsNull && id.IsValid).Distinct().ToArray());
            if (ids.Count == 0) throw new InvalidOperationException("没有可保存的单元格 CAD 对象。");
            var directory = CadArchSpec.EditorBridge.PortableDataPaths.DirectoryFor(Path.Combine("表格模板", "单元格对象"));
            var assetPath = Path.Combine(directory, "cell-object-" + Guid.NewGuid().ToString("N") + ".dwg");
            using (var snapshot = new Database(true, true))
            {
                document.Database.Wblock(snapshot, ids, basePoint, DuplicateRecordCloning.Ignore);
                snapshot.SaveAs(assetPath, DwgVersion.Current);
            }
            WriteCadObjectPreview(document, ids.Cast<ObjectId>(), Path.ChangeExtension(assetPath, ".png"));
            return assetPath;
        }

        private static void WriteCadObjectPreview(Document document, IEnumerable<ObjectId> objectIds, string previewPath)
        {
            var strokes = new List<List<Point2d>>();
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in objectIds)
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null) CollectPreviewStrokes(entity, strokes, 0);
                }
                transaction.Commit();
            }
            var points = strokes.SelectMany(stroke => stroke).ToList();
            if (points.Count == 0) return;
            const int size = 320;
            const float margin = 18f;
            var minX = points.Min(point => point.X); var maxX = points.Max(point => point.X);
            var minY = points.Min(point => point.Y); var maxY = points.Max(point => point.Y);
            var width = Math.Max(1e-6, maxX - minX); var height = Math.Max(1e-6, maxY - minY);
            var scale = Math.Min((size - margin * 2) / width, (size - margin * 2) / height);
            using (var bitmap = new Bitmap(size, size))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var pen = new Pen(Color.FromArgb(35, 55, 72), 2f))
            {
                graphics.Clear(Color.White);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var stroke in strokes.Where(stroke => stroke.Count > 1))
                {
                    var rendered = stroke.Select(point => new PointF(
                        margin + (float)((point.X - minX) * scale),
                        size - margin - (float)((point.Y - minY) * scale))).ToArray();
                    graphics.DrawLines(pen, rendered);
                }
                bitmap.Save(previewPath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void CollectPreviewStrokes(Entity entity, ICollection<List<Point2d>> strokes, int depth)
        {
            if (entity == null || depth > 5) return;
            var line = entity as Line;
            if (line != null) { strokes.Add(new List<Point2d> { ToPoint2d(line.StartPoint), ToPoint2d(line.EndPoint) }); return; }
            var polyline = entity as Polyline;
            if (polyline != null)
            {
                var points = Enumerable.Range(0, polyline.NumberOfVertices).Select(index => polyline.GetPoint2dAt(index)).ToList();
                if (polyline.Closed && points.Count > 0) points.Add(points[0]);
                strokes.Add(points);
                return;
            }
            var circle = entity as Circle;
            if (circle != null) { strokes.Add(SampleArc(circle.Center, circle.Radius, 0, Math.PI * 2)); return; }
            var arc = entity as Arc;
            if (arc != null) { strokes.Add(SampleArc(arc.Center, arc.Radius, arc.StartAngle, arc.EndAngle)); return; }
            var block = entity as BlockReference;
            if (block != null)
            {
                var exploded = new DBObjectCollection();
                try
                {
                    block.Explode(exploded);
                    foreach (var child in exploded.OfType<Entity>())
                        using (child) CollectPreviewStrokes(child, strokes, depth + 1);
                }
                catch { AddBoundsStroke(entity, strokes); }
                return;
            }
            AddBoundsStroke(entity, strokes);
        }

        private static void AddBoundsStroke(Entity entity, ICollection<List<Point2d>> strokes)
        {
            try
            {
                var bounds = entity.GeometricExtents;
                var left = bounds.MinPoint.X; var right = bounds.MaxPoint.X;
                var bottom = bounds.MinPoint.Y; var top = bounds.MaxPoint.Y;
                strokes.Add(new List<Point2d> { new Point2d(left, bottom), new Point2d(right, bottom),
                    new Point2d(right, top), new Point2d(left, top), new Point2d(left, bottom) });
            }
            catch { }
        }

        private static List<Point2d> SampleArc(Point3d center, double radius, double start, double end)
        {
            while (end < start) end += Math.PI * 2;
            const int segments = 40;
            return Enumerable.Range(0, segments + 1).Select(index =>
            {
                var angle = start + (end - start) * index / segments;
                return new Point2d(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
            }).ToList();
        }

        private static Point2d ToPoint2d(Point3d point) { return new Point2d(point.X, point.Y); }

        private static Dictionary<string, ObjectId> ImportCellCadObjectBlocks(Database target, JObject table)
        {
            var result = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Cells(table).Select(cell => ((string)cell["cadObjectAssetPath"] ?? string.Empty).Trim())
                .Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("单元格 CAD 对象资源不存在，请重新选择该对象。", path);
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                    var name = "WL_TABLE_CELL_" + Guid.NewGuid().ToString("N");
                    result[path] = target.Insert(name, source, true);
                }
            }
            return result;
        }

        private static IEnumerable<JObject> Cells(JObject table)
        {
            if (table == null) yield break;
            foreach (var row in (table["rows"] as JArray ?? new JArray()).OfType<JObject>())
                foreach (var cell in (row["cells"] as JArray ?? new JArray()).OfType<JObject>())
                    if ((int?)cell["rowSpan"] != 0 && (int?)cell["columnSpan"] != 0) yield return cell;
        }

        private static double ResolveOriginalCadScale(JObject table)
        {
            var textHeights = Cells(table).Select(cell => (double?)cell["sourceTextHeightCadUnits"] ?? 0d)
                .Where(value => value > .001d).OrderBy(value => value).ToList();
            if (textHeights.Count > 0)
                return Math.Max(.001d, textHeights[textHeights.Count / 2] / 3.5d);

            var columns = (table?["columns"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var rows = (table?["rows"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var ratios = columns.Select(column => ((double?)column["sourceWidthCadUnits"] ?? 0d) /
                    Math.Max(.001d, (double?)column["widthMillimeters"] ?? 0d))
                .Concat(rows.Select(row => ((double?)row["sourceHeightCadUnits"] ?? 0d) /
                    Math.Max(.001d, (double?)row["heightMillimeters"] ?? 0d)))
                .Where(value => value > .001d && !double.IsInfinity(value) && !double.IsNaN(value))
                .OrderBy(value => value).ToList();
            return ratios.Count == 0 ? 1d : ratios[ratios.Count / 2];
        }

        private static JObject InsertTianzhengTable(JObject payload, Document document, Point3d insertionPoint,
            TableUpdateTarget updateTarget)
        {
            var appendedIds = new List<ObjectId>();
            ObjectEventHandler appended = (sender, args) =>
            {
                if (args.DBObject is Entity && args.DBObject.ObjectId.IsValid)
                    appendedIds.Add(args.DBObject.ObjectId);
            };
            try
            {
                using (var excel = CadTableXlsxExchange.PrepareTianzhengImport(payload))
                {
                    document.Window.Focus();
                    Application.UpdateScreen();
                    document.Editor.WriteMessage("\nExcel 已准备好。天正弹出询问时，请选择“是(Y)”新建表格。\n");
                    CadTableXlsxExchange.WriteTianzhengLog("开始调用 Excel2Sheet；插入点=" +
                        insertionPoint.X.ToString("R", CultureInfo.InvariantCulture) + "," +
                        insertionPoint.Y.ToString("R", CultureInfo.InvariantCulture) + "," +
                        insertionPoint.Z.ToString("R", CultureInfo.InvariantCulture));
                    document.Database.ObjectAppended += appended;
                    document.Editor.Command("Excel2Sheet", insertionPoint, "");
                    CadTableXlsxExchange.WriteTianzhengLog("Excel2Sheet 已结束；新增实体数=" + appendedIds.Count);
                    if (appendedIds.Count == 0)
                    {
                        excel.ShowForFallback();
                        document.Window.Focus();
                        Application.UpdateScreen();
                        document.Editor.Command("Excel2Sheet", insertionPoint, "");
                        CadTableXlsxExchange.WriteTianzhengLog("Excel2Sheet 可见回退已结束；新增实体数=" + appendedIds.Count);
                    }
                    if (appendedIds.Count == 0)
                        throw new InvalidOperationException("Excel2Sheet 已结束，但没有检测到新建的天正表格；可能在天正询问中选择了“否”或取消了命令。");
                }
                LogTianzhengEntityCapabilities(document, appendedIds);
                var visibleIds = CurrentSpaceEntityIds(document, appendedIds).ToList();
                visibleIds.AddRange(InsertTianzhengCellObjects(payload, document, visibleIds));
                GroupInsertedTable(document, visibleIds);
                if (updateTarget != null) EraseUpdateSource(document, updateTarget);
                if (visibleIds.Count > 0) document.Editor.SetImpliedSelection(visibleIds.ToArray());
                document.Window.Focus();
                Application.UpdateScreen();
            }
            catch (Exception ex)
            {
                CadTableXlsxExchange.WriteTianzhengLog("Excel2Sheet 调用失败", ex);
                throw new InvalidOperationException("天正 Excel2Sheet 插入失败。请确认使用天正 T20 打开图纸、Microsoft Excel 可正常启动。" +
                    "\r\n诊断日志：" + Path.Combine(CadArchSpec.EditorBridge.PortableDataPaths.DirectoryFor("Logs"),
                        "cad-table-tianzheng.log"), ex);
            }
            finally
            {
                document.Database.ObjectAppended -= appended;
            }

            var result = new JObject
            {
                ["action"] = updateTarget == null ? "inserted" : "updated",
                ["insertType"] = "tianzheng",
                ["insertedEntityCount"] = appendedIds.Count
            };
            CopyResultSummary(result, payload);
            return result;
        }

        private static void LogTianzhengEntityCapabilities(Document document, IEnumerable<ObjectId> ids)
        {
            try
            {
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (var id in ids.Where(value => value.IsValid && !value.IsErased).Take(3))
                    {
                        var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null) continue;
                        var type = entity.GetType();
                        var writable = string.Join(",", type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                            .Where(property => property.CanWrite).Select(property => property.Name).OrderBy(name => name));
                        CadTableXlsxExchange.WriteTianzhengLog(
                            "新增实体能力：Handle=" + SafeHandle(entity) +
                            "；RX=" + (entity.GetRXClass()?.Name ?? string.Empty) +
                            "；DXF=" + (entity.GetRXClass()?.DxfName ?? string.Empty) +
                            "；Managed=" + type.FullName +
                            "；可写属性=" + writable);
                    }
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                CadTableXlsxExchange.WriteTianzhengLog("读取天正新增实体能力失败", ex);
            }
        }

        private static IEnumerable<ObjectId> CurrentSpaceEntityIds(Document document, IEnumerable<ObjectId> source)
        {
            var result = new List<ObjectId>();
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in source.Where(id => id.IsValid && !id.IsErased).Distinct())
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null && entity.OwnerId == document.Database.CurrentSpaceId) result.Add(id);
                }
                transaction.Commit();
            }
            return result;
        }

        private static IEnumerable<ObjectId> InsertTianzhengCellObjects(JObject payload, Document document,
            IEnumerable<ObjectId> tableEntityIds)
        {
            var source = payload["table"] as JObject;
            var objectCells = Cells(source).Where(cell =>
                !string.IsNullOrWhiteSpace((string)cell["cadObjectAssetPath"])).ToList();
            if (objectCells.Count == 0) return Enumerable.Empty<ObjectId>();

            Extents3d? tableBounds = null;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in tableEntityIds.Where(id => id.IsValid && !id.IsErased))
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;
                    try
                    {
                        var bounds = entity.GeometricExtents;
                        if (!tableBounds.HasValue) tableBounds = bounds;
                        else { var combined = tableBounds.Value; combined.AddExtents(bounds); tableBounds = combined; }
                    }
                    catch { }
                }
                transaction.Commit();
            }
            if (!tableBounds.HasValue) return Enumerable.Empty<ObjectId>();

            var columns = (source?["columns"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var rows = (source?["rows"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            if (columns.Count == 0 || rows.Count == 0) return Enumerable.Empty<ObjectId>();
            var columnWeights = columns.Select(column => Math.Max(.001d,
                (double?)column["widthMillimeters"] ?? (double?)column["sourceWidthCadUnits"] ?? 1d)).ToList();
            var rowWeights = rows.Select(row => Math.Max(.001d,
                (double?)row["heightMillimeters"] ?? (double?)row["sourceHeightCadUnits"] ?? 1d)).ToList();
            var totalWidth = columnWeights.Sum();
            var totalHeight = rowWeights.Sum();
            var boundsValue = tableBounds.Value;
            var tableWidth = Math.Abs(boundsValue.MaxPoint.X - boundsValue.MinPoint.X);
            var tableHeight = Math.Abs(boundsValue.MaxPoint.Y - boundsValue.MinPoint.Y);
            if (tableWidth <= .001d || tableHeight <= .001d) return Enumerable.Empty<ObjectId>();

            var blocks = ImportCellCadObjectBlocks(document.Database, source);
            var inserted = new List<ObjectId>();
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var cells = (rows[rowIndex]["cells"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    for (var columnIndex = 0; columnIndex < Math.Min(columns.Count, cells.Count); columnIndex++)
                    {
                        var cell = cells[columnIndex];
                        var path = ((string)cell["cadObjectAssetPath"] ?? string.Empty).Trim();
                        ObjectId blockId;
                        if (path.Length == 0 || !blocks.TryGetValue(path, out blockId)) continue;
                        var rowSpan = Math.Max(1, (int?)cell["rowSpan"] ?? 1);
                        var columnSpan = Math.Max(1, (int?)cell["columnSpan"] ?? 1);
                        var leftRatio = columnWeights.Take(columnIndex).Sum() / totalWidth;
                        var rightRatio = columnWeights.Take(Math.Min(columns.Count, columnIndex + columnSpan)).Sum() / totalWidth;
                        var topRatio = rowWeights.Take(rowIndex).Sum() / totalHeight;
                        var bottomRatio = rowWeights.Take(Math.Min(rows.Count, rowIndex + rowSpan)).Sum() / totalHeight;
                        var cellLeft = boundsValue.MinPoint.X + tableWidth * leftRatio;
                        var cellRight = boundsValue.MinPoint.X + tableWidth * rightRatio;
                        var cellTop = boundsValue.MaxPoint.Y - tableHeight * topRatio;
                        var cellBottom = boundsValue.MaxPoint.Y - tableHeight * bottomRatio;
                        var reference = new BlockReference(new Point3d((cellLeft + cellRight) * .5d,
                            (cellTop + cellBottom) * .5d, boundsValue.MinPoint.Z), blockId);
                        reference.SetDatabaseDefaults(document.Database);
                        var definition = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                        Extents3d? contentBounds = null;
                        foreach (ObjectId definitionId in definition)
                        {
                            var content = transaction.GetObject(definitionId, OpenMode.ForRead, false) as Entity;
                            if (content == null) continue;
                            try
                            {
                                var current = content.GeometricExtents;
                                if (!contentBounds.HasValue) contentBounds = current;
                                else { var combined = contentBounds.Value; combined.AddExtents(current); contentBounds = combined; }
                            }
                            catch { }
                        }
                        if (contentBounds.HasValue)
                        {
                            var contentWidth = Math.Abs(contentBounds.Value.MaxPoint.X - contentBounds.Value.MinPoint.X);
                            var contentHeight = Math.Abs(contentBounds.Value.MaxPoint.Y - contentBounds.Value.MinPoint.Y);
                            var availableWidth = Math.Max(.001d, (cellRight - cellLeft) * .82d);
                            var availableHeight = Math.Max(.001d, (cellTop - cellBottom) * .72d);
                            var factor = Math.Min(contentWidth > .001d ? availableWidth / contentWidth : 1d,
                                contentHeight > .001d ? availableHeight / contentHeight : 1d);
                            if (factor > .001d && !double.IsInfinity(factor) && !double.IsNaN(factor))
                                reference.ScaleFactors = new Scale3d(factor);
                        }
                        currentSpace.AppendEntity(reference);
                        transaction.AddNewlyCreatedDBObject(reference, true);
                        inserted.Add(reference.ObjectId);
                    }
                }
                transaction.Commit();
            }
            return inserted;
        }

        private static void GroupInsertedTable(Document document, IEnumerable<ObjectId> entityIds)
        {
            var ids = entityIds.Where(id => id.IsValid && !id.IsErased).Distinct().ToArray();
            if (ids.Length < 2) return;
            try
            {
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var dictionary = (DBDictionary)transaction.GetObject(document.Database.GroupDictionaryId, OpenMode.ForWrite);
                    var group = new Group("万落表格", true);
                    var name = "WL_TABLE_" + Guid.NewGuid().ToString("N");
                    dictionary.SetAt(name, group);
                    transaction.AddNewlyCreatedDBObject(group, true);
                    group.Append(new ObjectIdCollection(ids));
                    transaction.Commit();
                }
            }
            catch (Exception ex) { CadTableXlsxExchange.WriteTianzhengLog("天正表格与单元格对象编组失败", ex); }
        }

        private static Autodesk.AutoCAD.Colors.Color ReadCadIndexedColor(JToken token)
        {
            var index = (short?)token;
            return index.HasValue && index.Value >= 1 && index.Value <= 255
                ? Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, index.Value)
                : null;
        }

        private static void ApplyCellColors(object cell, Autodesk.AutoCAD.Colors.Color fill,
            Autodesk.AutoCAD.Colors.Color content)
        {
            if (cell == null) return;
            if (fill != null) TrySetProperty(cell, "BackgroundColor", fill);
            if (content != null) TrySetProperty(cell, "ContentColor", content);
        }

        private static void ApplyCellBorders(object cell, Autodesk.AutoCAD.Colors.Color cellColor,
            Autodesk.AutoCAD.Colors.Color outerColor, Autodesk.AutoCAD.Colors.Color innerColor,
            double outerWeight, double innerWeight, int row, int column, int rowCount, int columnCount)
        {
            if (cell == null) return;
            var borders = TryGetProperty(cell, "Borders");
            if (borders == null) return;
            ApplyBorderEdge(borders, "Top", cellColor ?? (row == 0 ? outerColor : innerColor),
                cellColor != null ? innerWeight : row == 0 ? outerWeight : innerWeight);
            ApplyBorderEdge(borders, "Bottom", cellColor ?? (row == rowCount - 1 ? outerColor : innerColor),
                cellColor != null ? innerWeight : row == rowCount - 1 ? outerWeight : innerWeight);
            ApplyBorderEdge(borders, "Left", cellColor ?? (column == 0 ? outerColor : innerColor),
                cellColor != null ? innerWeight : column == 0 ? outerWeight : innerWeight);
            ApplyBorderEdge(borders, "Right", cellColor ?? (column == columnCount - 1 ? outerColor : innerColor),
                cellColor != null ? innerWeight : column == columnCount - 1 ? outerWeight : innerWeight);
            ApplyBorderEdge(borders, "Horizontal", cellColor ?? innerColor, cellColor != null ? innerWeight : innerWeight);
            ApplyBorderEdge(borders, "Vertical", cellColor ?? innerColor, cellColor != null ? innerWeight : innerWeight);
        }

        private static void ApplyBorderEdge(object borders, string name, Autodesk.AutoCAD.Colors.Color color, double weight)
        {
            var edge = TryGetProperty(borders, name);
            if (edge == null) return;
            if (color != null) TrySetProperty(edge, "Color", color);
            TrySetEnumProperty(edge, "LineWeight", Math.Max(0, (int)Math.Round(weight * 100d)));
        }

        private static void TrySetEnumProperty(object target, string name, int numericValue)
        {
            try
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanWrite && property.PropertyType.IsEnum)
                    property.SetValue(target, Enum.ToObject(property.PropertyType, numericValue), null);
            }
            catch { }
        }

        private static object TryGetProperty(object target, string name)
        {
            try { return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target, null); }
            catch { return null; }
        }

        private static void TrySetProperty(object target, string name, object value)
        {
            try
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanWrite) property.SetValue(target, value, null);
            }
            catch { }
        }

        private static List<string> ReadTextStyles(out string currentTextStyle)
        {
            currentTextStyle = "Standard";
            var result = new List<string>();
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return result;
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var styles = (TextStyleTable)transaction.GetObject(document.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in styles)
                {
                    var style = transaction.GetObject(id, OpenMode.ForRead) as TextStyleTableRecord;
                    if (style != null && !string.IsNullOrWhiteSpace(style.Name)) result.Add(style.Name);
                }
                var current = transaction.GetObject(document.Database.Textstyle, OpenMode.ForRead) as TextStyleTableRecord;
                if (current != null && !string.IsNullOrWhiteSpace(current.Name)) currentTextStyle = current.Name;
                transaction.Commit();
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
        }

        private static ObjectId ResolveTextStyle(Database database, Transaction transaction, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return database.Textstyle;
            var styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            return styles.Has(name) ? styles[name] : database.Textstyle;
        }

        private static CellAlignment CadAlignment(string value)
        {
            if (string.Equals(value, "left", StringComparison.OrdinalIgnoreCase)) return CellAlignment.MiddleLeft;
            if (string.Equals(value, "right", StringComparison.OrdinalIgnoreCase)) return CellAlignment.MiddleRight;
            return CellAlignment.MiddleCenter;
        }

        private static string PadCadTableText(string value, string alignment, double padding, double textHeight)
        {
            if (padding <= 0.001d || string.IsNullOrEmpty(value)) return value ?? string.Empty;
            if (!string.Equals(alignment, "left", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(alignment, "right", StringComparison.OrdinalIgnoreCase)) return value;
            var unitWidth = Math.Max(0.1d, textHeight * 0.55d);
            var count = Math.Max(1, Math.Min(20, (int)Math.Ceiling(padding / unitWidth)));
            var spaces = string.Concat(Enumerable.Repeat("\\~", count));
            return string.Equals(alignment, "right", StringComparison.OrdinalIgnoreCase)
                ? value + spaces : spaces + value;
        }

        private static void CopyResultSummary(JObject target, JObject source)
        {
            target["rowCount"] = (int?)source["rowCount"] ?? 0;
            target["columnCount"] = (int?)source["columnCount"] ?? 0;
            target["nativeTable"] = (bool?)source["nativeTable"] ?? false;
            target["warnings"] = source["warnings"] == null ? new JArray() : source["warnings"].DeepClone();
        }

        private static TableUpdateTarget ResolveTableUpdateTarget(JObject payload, Document document)
        {
            var source = payload == null ? null : payload["sourceEdit"] as JObject;
            if (source == null) return null;
            var drawingPath = ((string)source["drawingPath"] ?? string.Empty).Trim();
            if (drawingPath.Length > 0 && !SamePath(drawingPath, document.Name))
                throw new InvalidOperationException("要更新的源表格位于另一张图纸，请切换到：\r\n" + drawingPath);
            var result = new TableUpdateTarget
            {
                Position = new Point3d((double?)source["x"] ?? 0d, (double?)source["y"] ?? 0d, (double?)source["z"] ?? 0d),
                RotationRadians = ((double?)source["rotationDegrees"] ?? 0d) * Math.PI / 180d
            };
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                if (string.Equals((string)source["kind"], "nativeTable", StringComparison.OrdinalIgnoreCase))
                {
                    var id = ResolveHandle(document.Database, (string)source["handle"]);
                    var table = id.IsNull ? null : transaction.GetObject(id, OpenMode.ForRead, false) as Table;
                    if (table == null) throw new InvalidOperationException("原表格已不存在，无法原位更新，请重新拾取表格。");
                    result.NativeTableId = id;
                    result.Position = table.Position;
                    result.LayerId = table.LayerId;
                }
                else
                {
                    foreach (var handle in (source["handles"] as JArray ?? new JArray()).Values<string>())
                    {
                        var id = ResolveHandle(document.Database, handle);
                        var entity = id.IsNull ? null : transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null) continue;
                        result.LooseEntityIds.Add(id);
                        if (result.LayerId.IsNull) result.LayerId = entity.LayerId;
                    }
                    if (result.LooseEntityIds.Count == 0)
                        throw new InvalidOperationException("原表格对象已不存在，无法原位更新，请重新拾取表格。");
                }
                transaction.Commit();
            }
            return result;
        }

        private static void EraseUpdateSource(Document document, TableUpdateTarget updateTarget)
        {
            var ids = new List<ObjectId>();
            if (!updateTarget.NativeTableId.IsNull) ids.Add(updateTarget.NativeTableId);
            ids.AddRange(updateTarget.LooseEntityIds);
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids.Distinct().Where(id => id.IsValid && !id.IsErased))
                {
                    var entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (entity != null) entity.Erase();
                }
                transaction.Commit();
            }
        }

        private static ObjectId ResolveHandle(Database database, string handleText)
        {
            long value;
            if (database == null || !long.TryParse(handleText, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out value)) return ObjectId.Null;
            try { return database.GetObjectId(false, new Handle(value), 0); }
            catch { return ObjectId.Null; }
        }

        private static void ClearNativeTableMerges(Table table)
        {
            var ranges = new Dictionary<string, CellRange>(StringComparer.Ordinal);
            for (var row = 0; row < table.Rows.Count; row++)
                for (var column = 0; column < table.Columns.Count; column++)
                {
                    var cell = table.Cells[row, column];
                    if (cell.IsMerged != true) continue;
                    var range = cell.GetMergeRange();
                    var key = range.TopRow + ":" + range.LeftColumn + ":" + range.BottomRow + ":" + range.RightColumn;
                    if (!ranges.ContainsKey(key)) ranges[key] = range;
                }
            foreach (var range in ranges.Values) table.UnmergeCells(range);
        }

        private sealed class TableUpdateTarget
        {
            public ObjectId NativeTableId = ObjectId.Null;
            public readonly List<ObjectId> LooseEntityIds = new List<ObjectId>();
            public Point3d Position = Point3d.Origin;
            public double RotationRadians;
            public ObjectId LayerId = ObjectId.Null;
        }

        public static async Task<JObject> ReadSelectedTableAsync(bool includeHiddenLayers = false)
        {
            JObject result = null;
            await Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = ReadSelectedTableCore(includeHiddenLayers, false);
                    await Task.CompletedTask;
                }, null);
            return result;
        }

        public static async Task<JObject> LocateSourcesAsync(JObject payload)
        {
            JObject result = null;
            await Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = LocateSourcesCore(payload ?? new JObject());
                    await Task.CompletedTask;
                }, null);
            return result;
        }

        private static JObject LocateSourcesCore(JObject payload)
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) throw new InvalidOperationException("当前没有活动的 CAD 图纸。");
            var sourceDrawingPath = ((string)payload["drawingPath"] ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(sourceDrawingPath) &&
                !SamePath(sourceDrawingPath, document.Name))
                throw new InvalidOperationException("该单元格来源于另一张图纸，请先打开并切换到：\r\n" + sourceDrawingPath);

            var handles = (payload["handles"] as JArray ?? new JArray())
                .Values<string>().Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (handles.Count == 0) throw new InvalidOperationException("当前单元格没有可定位的 CAD 来源 Handle。");

            var ids = new System.Collections.Generic.List<ObjectId>();
            Extents3d? extents = null;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var handleText in handles)
                {
                    long handleValue;
                    if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handleValue)) continue;
                    try
                    {
                        var id = document.Database.GetObjectId(false, new Handle(handleValue), 0);
                        var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null) continue;
                        ids.Add(id);
                        try
                        {
                            var current = entity.GeometricExtents;
                            if (extents == null) extents = current;
                            else
                            {
                                var combined = extents.Value;
                                combined.AddExtents(current);
                                extents = combined;
                            }
                        }
                        catch
                        {
                        }
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                    }
                }
            }
            if (ids.Count == 0) throw new InvalidOperationException("当前图纸中已找不到该单元格的来源对象，可能已被删除或替换。");

            document.Editor.SetImpliedSelection(ids.ToArray());
            if (extents != null)
            {
                var bounds = extents.Value;
                using (var view = document.Editor.GetCurrentView())
                {
                    var transform = Matrix3d.PlaneToWorld(view.ViewDirection);
                    transform = Matrix3d.Displacement(view.Target - Point3d.Origin) * transform;
                    transform = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * transform;
                    transform = transform.Inverse();
                    var minimum = bounds.MinPoint.TransformBy(transform);
                    var maximum = bounds.MaxPoint.TransformBy(transform);
                    var width = Math.Max(Math.Abs(maximum.X - minimum.X), 1d);
                    var height = Math.Max(Math.Abs(maximum.Y - minimum.Y), 1d);
                    var viewRatio = view.Height <= 1e-9 ? 1d : view.Width / view.Height;
                    if (width / height > viewRatio) height = width / viewRatio;
                    else width = height * viewRatio;
                    view.CenterPoint = new Point2d(
                        (minimum.X + maximum.X) * 0.5d,
                        (minimum.Y + maximum.Y) * 0.5d);
                    view.Width = width * 1.35d;
                    view.Height = height * 1.35d;
                    document.Editor.SetCurrentView(view);
                }
            }
            document.Window.Focus();
            Application.UpdateScreen();
            return new JObject { ["locatedCount"] = ids.Count };
        }

        private static bool SamePath(string first, string second)
        {
            try
            {
                return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static JObject ReadSelectedTableCore(bool includeHiddenLayers, bool markForUpdate)
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) throw new InvalidOperationException("当前没有活动的 CAD 图纸。");
            document.Window.Focus();
            var selection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\n框选一张表格的边框线、文字和天正文字：",
                MessageForRemoval = "\n移除不属于该表格的对象："
            });
            if (selection.Status != PromptStatus.OK)
                return new JObject { ["cancelled"] = true };

            var selectedIds = selection.Value.GetObjectIds();
            CadTableEntityReadResult read;
            var likelyTianzhengTable = false;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var nativeTables = selectedIds.Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as Table)
                    .Where(table => table != null).ToList();
                if (nativeTables.Count > 1)
                    throw new InvalidOperationException("一次只能读取一张 AutoCAD 原生表格。");
                if (nativeTables.Count == 1)
                {
                    var nativeResult = BuildNativeTablePayload(nativeTables[0], document, markForUpdate);
                    transaction.Commit();
                    return nativeResult;
                }
                likelyTianzhengTable = selectedIds.Select(id => transaction.GetObject(id, OpenMode.ForRead, false))
                    .Any(IsLikelyTianzhengTableEntity);
                read = CadTableEntityReader.Read(transaction, selectedIds, includeHiddenLayers);
                transaction.Commit();
            }
            if (read.Input.Segments.Count == 0)
                throw new InvalidOperationException("所选对象中没有可识别的表格边框线。");

            var segmentSourceHandles = new HashSet<string>(read.Input.Segments
                .Select(item => item.SourceHandle).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            var selectedCompoundGraphicHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var blockTransaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in selectedIds.Where(id => id.IsValid && !id.IsNull))
                {
                    var entity = blockTransaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity is Curve) continue;
                    var handle = SafeHandle(entity);
                    if (segmentSourceHandles.Contains(handle)) selectedCompoundGraphicHandles.Add(handle);
                }
                blockTransaction.Commit();
            }
            var textHeight = read.Input.TextFragments.Where(item => item.Height > 0.001d &&
                    !selectedCompoundGraphicHandles.Contains(item.SourceHandle))
                .Select(item => item.Height).OrderBy(value => value).ToList();
            if (textHeight.Count == 0)
                textHeight = read.Input.TextFragments.Where(item => item.Height > 0.001d)
                    .Select(item => item.Height).OrderBy(value => value).ToList();
            var medianHeight = textHeight.Count == 0 ? 3.5d : textHeight[textHeight.Count / 2];
            var detector = new OrthogonalCadTableDetector();
            var detectionOptions = new CadTableDetectionOptions
            {
                CoordinateTolerance = Math.Max(0.1d, medianHeight * 0.04d),
                MaximumBorderGap = Math.Max(0.5d, medianHeight * 0.2d),
                OrthogonalAngleToleranceDegrees = 1d
            };
            // Blocks, raster images, wipeouts, OLE frames and proxy entities can all
            // expose child strokes when exploded. Those strokes belong to the cell
            // content; only top-level curve entities may define a loose table grid.
            var detectionInput = CadTableDetectionInputFilter.ExcludeSourceHandles(read.Input,
                selectedCompoundGraphicHandles);
            var ignoredCompoundSegmentCount = read.Input.Segments.Count - detectionInput.Segments.Count;
            var ignoredCompoundTextCount = read.Input.TextFragments.Count - detectionInput.TextFragments.Count;
            var detected = detector.Detect(detectionInput, detectionOptions);
            var usedCompoundGeometryFallback = false;
            if (!HasDetectedTable(detected) && ignoredCompoundSegmentCount > 0)
            {
                // Preserve support for drawings where the complete table itself is
                // supplied as one selected block reference.
                detected = detector.Detect(read.Input, detectionOptions);
                usedCompoundGeometryFallback = true;
            }
            WriteRecognitionLog("识别完成：源对象=" + read.SourceEntityCount +
                "，原线段=" + read.Input.Segments.Count +
                "，排除复合对象=" + selectedCompoundGraphicHandles.Count +
                "，排除复合对象线段=" + ignoredCompoundSegmentCount +
                "，排除复合对象文字=" + ignoredCompoundTextCount +
                "，复合对象回退=" + usedCompoundGeometryFallback +
                "，行=" + Math.Max(0, detected.RowBoundaries.Count - 1) +
                "，列=" + Math.Max(0, detected.ColumnBoundaries.Count - 1) +
                "，提示=" + string.Join("；", detected.Warnings));
            if (detected.ColumnBoundaries.Count < 2 || detected.RowBoundaries.Count < 2 || detected.Cells.Count == 0)
                throw new InvalidOperationException("没有识别到闭合表格。请只框选一张表格，并确认边框线已连接。");

            var warnings = read.Warnings.Concat(detected.Warnings).Distinct().ToArray();
            var columnCount = detected.ColumnBoundaries.Count - 1;
            var rowCount = detected.RowBoundaries.Count - 1;
            var tableId = "table-" + Guid.NewGuid().ToString("N");
            var columns = new JArray();
            var sourceWidths = Enumerable.Range(0, columnCount)
                .Select(index => Math.Abs(detected.ColumnBoundaries[index + 1] - detected.ColumnBoundaries[index])).ToList();
            var displayWidths = NormalizeCadDimensions(sourceWidths, medianHeight);
            var displayHeights = NormalizeCadDimensions(Enumerable.Range(0, rowCount)
                .Select(index => Math.Abs(detected.RowBoundaries[index + 1] - detected.RowBoundaries[index])), medianHeight);
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                columns.Add(new JObject
                {
                    ["key"] = "column" + (columnIndex + 1),
                    ["title"] = ColumnName(columnIndex),
                    ["unit"] = string.Empty,
                    ["widthMillimeters"] = displayWidths[columnIndex],
                    ["sourceWidthCadUnits"] = sourceWidths[columnIndex],
                    ["decimalPlaces"] = 0,
                    ["required"] = false
                });
            }

            var rows = new JArray();
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var cells = new JArray();
                for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    var detectedCell = detected.Cells.FirstOrDefault(cell =>
                        cell.RowIndex == rowIndex && cell.ColumnIndex == columnIndex);
                    var coveringCell = detectedCell ?? detected.Cells.FirstOrDefault(cell =>
                        rowIndex >= cell.RowIndex && rowIndex < cell.RowIndex + cell.RowSpan &&
                        columnIndex >= cell.ColumnIndex && columnIndex < cell.ColumnIndex + cell.ColumnSpan);
                    var value = detectedCell == null ? string.Empty : detectedCell.Text ?? string.Empty;
                    var fragments = detectedCell == null ? null : detectedCell.TextFragments;
                    var tianzhengValue = RestoreVendorFormatting(value, fragments);
                    var fragmentHeights = fragments == null
                        ? new List<double>()
                        : fragments.Where(fragment => fragment.Height > .001d)
                            .Select(fragment => fragment.Height).OrderBy(height => height).ToList();
                    var cellTextHeight = fragmentHeights.Count == 0
                        ? medianHeight : fragmentHeights[fragmentHeights.Count / 2];
                    cellTextHeight = Math.Max(medianHeight * .72d, Math.Min(medianHeight * 1.5d, cellTextHeight));
                    // Exploded Tianzheng text can expose a nominal height that is much
                    // larger than its table row. Preserve original sizes, but never let
                    // a proxy text metric turn one cell into oversized output.
                    var sourceRowHeight = Math.Abs(detected.RowBoundaries[rowIndex + 1] -
                        detected.RowBoundaries[rowIndex]);
                    if (sourceRowHeight > .001d)
                        cellTextHeight = Math.Min(cellTextHeight, sourceRowHeight * .65d);
                    double numeric;
                    var numericValue = double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numeric)
                        ? (JToken)new JValue(numeric)
                        : JValue.CreateNull();
                    cells.Add(new JObject
                    {
                        ["cellId"] = "cell-" + Guid.NewGuid().ToString("N"),
                        ["columnKey"] = "column" + (columnIndex + 1),
                        ["displayValue"] = value,
                        ["numericValue"] = numericValue,
                        ["unit"] = string.Empty,
                        ["fieldPath"] = string.Empty,
                        ["formula"] = string.Empty,
                        ["sourceTextHeightCadUnits"] = cellTextHeight,
                        ["state"] = string.IsNullOrWhiteSpace(value) ? "unknown" : "pending",
                        ["source"] = coveringCell == null ? "CAD边界待确认" : "CAD只读识别",
                        ["sourceHandles"] = coveringCell == null
                            ? new JArray()
                            : JArray.FromObject(coveringCell.SourceHandles.Distinct(StringComparer.OrdinalIgnoreCase)),
                        ["rowSpan"] = detectedCell != null ? detectedCell.RowSpan : coveringCell != null ? 0 : 1,
                        ["columnSpan"] = detectedCell != null ? detectedCell.ColumnSpan : coveringCell != null ? 0 : 1
                    });
                    var added = (JObject)cells[cells.Count - 1];
                    if (!string.Equals(tianzhengValue, value, StringComparison.Ordinal))
                    {
                        added["sourceNormalizedValue"] = value;
                        added["sourceTianzhengValue"] = tianzhengValue;
                        added["sourceAutoCadMTextValue"] = TianzhengTextCodec.ToAutoCadMText(tianzhengValue);
                    }
                    added["alignment"] = fragments != null && fragments.Count > 0
                        ? fragments.GroupBy(fragment => fragment.Alignment ?? "center")
                            .OrderByDescending(group => group.Count()).First().Key
                        : "center";
                    added["horizontalPaddingMillimeters"] = 1d;
                    added["sourceHorizontalPaddingCadUnits"] = Math.Max(.1d, medianHeight * .3d);
                }
                rows.Add(new JObject
                {
                    ["rowId"] = "row-" + Guid.NewGuid().ToString("N"),
                    ["rowType"] = "Data",
                    ["keepTogether"] = true,
                    ["heightMillimeters"] = displayHeights[rowIndex],
                    ["sourceHeightCadUnits"] = Math.Abs(detected.RowBoundaries[rowIndex + 1] - detected.RowBoundaries[rowIndex]),
                    ["cells"] = cells
                });
            }

            var drawingName = Path.GetFileNameWithoutExtension(document.Name ?? string.Empty);
            var payload = new JObject
            {
                ["sourceEntityCount"] = read.SourceEntityCount,
                ["segmentCount"] = read.Input.Segments.Count,
                ["textCount"] = read.Input.TextFragments.Count,
                ["explodedObjectCount"] = read.ExplodedObjectCount,
                ["skippedHiddenEntityCount"] = read.SkippedHiddenEntityCount,
                ["includedHiddenLayers"] = includeHiddenLayers,
                ["ignoredCompoundGraphicCount"] = selectedCompoundGraphicHandles.Count,
                ["ignoredCompoundSegmentCount"] = ignoredCompoundSegmentCount,
                ["ignoredCompoundTextCount"] = ignoredCompoundTextCount,
                ["usedCompoundGeometryFallback"] = usedCompoundGeometryFallback,
                ["rowCount"] = rowCount,
                ["columnCount"] = columnCount,
                ["nativeTable"] = false,
                ["hasOriginalCadSize"] = true,
                ["warnings"] = JArray.FromObject(warnings),
                ["drawingPath"] = document.Name ?? string.Empty,
                ["table"] = new JObject
                {
                    ["tableId"] = tableId,
                    ["schemaVersion"] = 1,
                    ["tableType"] = "custom",
                    ["tableNumber"] = string.Empty,
                    ["title"] = string.IsNullOrWhiteSpace(drawingName) ? "CAD识别表格" : drawingName + "－CAD识别表格",
                    ["sourceDrawingPath"] = document.Name ?? string.Empty,
                    ["repeatHeader"] = true,
                    ["allowSplitAcrossPages"] = true,
                    ["columns"] = columns,
                    ["rows"] = rows,
                    ["formulaAudits"] = new JArray()
                }
            };
            payload["suggestedInsertType"] = likelyTianzhengTable ? "tianzheng" : "autocad";
            var topLeft = RotatePoint(new Point3d(detected.ColumnBoundaries.Min(),
                detected.RowBoundaries.Max(), 0d), detected.DetectedRotationDegrees);
            if (markForUpdate)
                payload["sourceEdit"] = new JObject
                {
                    ["kind"] = "looseEntities",
                    ["drawingPath"] = document.Name ?? string.Empty,
                    ["handles"] = new JArray(selectedIds.Select(SafeHandle).Where(value => value.Length > 0)),
                    ["x"] = topLeft.X,
                    ["y"] = topLeft.Y,
                    ["z"] = topLeft.Z,
                    ["rotationDegrees"] = detected.DetectedRotationDegrees
                };
            AttachAutomaticallyDetectedCadObjects(payload, document, selectedIds, detected,
                read.Input.TextFragments, medianHeight);
            return payload;
        }

        private static bool HasDetectedTable(CadTableDetectionResult detected)
        {
            return detected != null && detected.ColumnBoundaries.Count >= 2 &&
                detected.RowBoundaries.Count >= 2 && detected.Cells.Count > 0;
        }

        private static bool IsLikelyTianzhengTableEntity(DBObject value)
        {
            if (value == null) return false;
            try
            {
                var rx = value.GetRXClass();
                var signature = ((rx == null ? string.Empty : rx.Name) + " " +
                    (rx == null ? string.Empty : rx.DxfName) + " " + value.GetType().FullName).ToUpperInvariant();
                return (signature.Contains("TCH") || signature.Contains("TIANZHENG")) &&
                    (signature.Contains("TABLE") || signature.Contains("SHEET"));
            }
            catch { return false; }
        }

        private static void WriteRecognitionLog(string message)
        {
            try
            {
                var path = Path.Combine(PortableDataPaths.DirectoryFor("Logs"),
                    "cad-table-recognition.log");
                File.AppendAllText(path, DateTime.Now.ToString("O") + " | " + message +
                    Environment.NewLine);
            }
            catch
            {
            }
        }

        private static string RestoreVendorFormatting(string normalizedValue, IEnumerable<CadTextFragment> fragments)
        {
            var result = normalizedValue ?? string.Empty;
            if (fragments == null) return result;
            foreach (var fragment in fragments.Where(fragment =>
                TianzhengTextCodec.ContainsTianzhengFormatting(fragment.Text)))
            {
                var plain = TianzhengTextCodec.ToPlainText(fragment.Text);
                var index = plain.Length == 0 ? -1 : result.IndexOf(plain, StringComparison.Ordinal);
                if (index >= 0)
                    result = result.Substring(0, index) + fragment.Text + result.Substring(index + plain.Length);
            }
            return result;
        }

        public static async Task<JObject> ReadSelectedTableForUpdateAsync(bool includeHiddenLayers = false)
        {
            JObject result = null;
            await Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = ReadSelectedTableCore(includeHiddenLayers, true);
                    await Task.CompletedTask;
                }, null);
            return result;
        }

        public static JObject ReadSelectedTableForUpdate(bool includeHiddenLayers = false)
        {
            return ReadSelectedTableCore(includeHiddenLayers, true);
        }

        private static string CellTextForCadInsert(JObject cell)
        {
            var value = (string)cell["displayValue"] ?? string.Empty;
            var sourceNormalized = (string)cell["sourceNormalizedValue"];
            var sourceCad = (string)cell["sourceAutoCadMTextValue"];
            if (string.IsNullOrWhiteSpace((string)cell["formula"]) &&
                !string.IsNullOrEmpty(sourceCad) && string.Equals(value, sourceNormalized, StringComparison.Ordinal))
                return sourceCad;
            return TianzhengTextCodec.ToAutoCadMText(value);
        }

        private static void AttachAutomaticallyDetectedCadObjects(JObject payload, Document document,
            IEnumerable<ObjectId> selectedIds, CadTableDetectionResult detected,
            IEnumerable<CadTextFragment> textFragments, double medianTextHeight)
        {
            var borderHandles = new HashSet<string>(detected.Cells.SelectMany(cell => cell.SourceHandles)
                .Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
            var textHandles = new HashSet<string>((textFragments ?? Enumerable.Empty<CadTextFragment>())
                .Select(fragment => fragment.SourceHandle).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);
            var cellsByKey = new Dictionary<string, DetectedCadTableCell>(StringComparer.OrdinalIgnoreCase);
            var tolerance = Math.Max(0.1d, medianTextHeight * .08d);
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in selectedIds.Where(id => !id.IsNull && id.IsValid).Distinct())
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || IsTableTextEntity(entity) || entity is Table) continue;
                    var handle = SafeHandle(entity);
                    // A Tianzheng/proxy text object is often exploded for reading. Its
                    // top-level entity must not then be captured again as cell graphics.
                    // Keep block references because symbol blocks may legitimately
                    // contain text as part of a visual icon.
                    if (textHandles.Contains(handle) && !(entity is BlockReference)) continue;
                    if (borderHandles.Contains(handle) && IsActualTableBoundary(entity, detected, tolerance)) continue;
                    Extents3d bounds;
                    try { bounds = entity.GeometricExtents; }
                    catch { continue; }
                    var normalized = NormalizeBounds(bounds, -detected.DetectedRotationDegrees);
                    var centerX = (normalized[0] + normalized[2]) * .5d;
                    var centerY = (normalized[1] + normalized[3]) * .5d;
                    var target = detected.Cells.FirstOrDefault(cell =>
                        centerX >= cell.Left - tolerance && centerX <= cell.Right + tolerance &&
                        centerY >= cell.Bottom - tolerance && centerY <= cell.Top + tolerance &&
                        normalized[2] - normalized[0] <= (cell.Right - cell.Left) * 1.25d &&
                        normalized[3] - normalized[1] <= (cell.Top - cell.Bottom) * 1.25d);
                    if (target == null) continue;
                    var key = target.RowIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                        target.ColumnIndex.ToString(CultureInfo.InvariantCulture);
                    List<ObjectId> ids;
                    if (!groups.TryGetValue(key, out ids)) { ids = new List<ObjectId>(); groups[key] = ids; cellsByKey[key] = target; }
                    ids.Add(id);
                }
                transaction.Commit();
            }

            var table = payload["table"] as JObject;
            var rows = table == null ? null : table["rows"] as JArray;
            var attached = 0;
            foreach (var group in groups)
            {
                var detectedCell = cellsByKey[group.Key];
                var cells = rows != null && detectedCell.RowIndex < rows.Count
                    ? (rows[detectedCell.RowIndex] as JObject)?["cells"] as JArray : null;
                var cell = cells != null && detectedCell.ColumnIndex < cells.Count
                    ? cells[detectedCell.ColumnIndex] as JObject : null;
                if (cell == null || group.Value.Count == 0) continue;
                var basePoint = new Point3d((detectedCell.Left + detectedCell.Right) * .5d,
                    (detectedCell.Bottom + detectedCell.Top) * .5d, 0d);
                if (Math.Abs(detected.DetectedRotationDegrees) > .001d)
                    basePoint = RotatePoint(basePoint, detected.DetectedRotationDegrees);
                cell["cadObjectAssetPath"] = WriteCadObjectSnapshot(document, group.Value, basePoint);
                cell["cadObjectPreviewPath"] = Path.ChangeExtension((string)cell["cadObjectAssetPath"], ".png");
                cell["cadObjectCount"] = group.Value.Count;
                cell["cadObjectDetectedAutomatically"] = true;
                attached++;
            }
            payload["autoCadObjectCellCount"] = attached;
        }

        private static bool IsActualTableBoundary(Entity entity, CadTableDetectionResult detected, double tolerance)
        {
            // Handles reported by the detector belong to geometry that formed the
            // grid. Exclude top-level curves (including rectangular polylines),
            // while retaining block references whose exploded child lines happen
            // to participate in detection.
            return entity is Curve;
        }

        private static bool IsTableTextEntity(Entity entity)
        {
            return entity is DBText || entity is MText || entity is AttributeReference || entity is AttributeDefinition;
        }

        private static double[] NormalizeBounds(Extents3d bounds, double degrees)
        {
            var corners = new[]
            {
                bounds.MinPoint,
                new Point3d(bounds.MinPoint.X, bounds.MaxPoint.Y, bounds.MinPoint.Z),
                new Point3d(bounds.MaxPoint.X, bounds.MinPoint.Y, bounds.MaxPoint.Z),
                bounds.MaxPoint
            }.Select(point => RotatePoint(point, degrees)).ToList();
            return new[] { corners.Min(point => point.X), corners.Min(point => point.Y),
                corners.Max(point => point.X), corners.Max(point => point.Y) };
        }

        private static Point3d RotatePoint(Point3d point, double degrees)
        {
            var radians = degrees * Math.PI / 180d;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            return new Point3d(point.X * cosine - point.Y * sine,
                point.X * sine + point.Y * cosine, point.Z);
        }

        private static JObject BuildNativeTablePayload(Table source, Document document, bool markForUpdate)
        {
            var drawingPath = document == null ? string.Empty : document.Name;
            var rowCount = source.Rows.Count;
            var columnCount = source.Columns.Count;
            if (rowCount <= 0 || columnCount <= 0)
                throw new InvalidOperationException("所选 AutoCAD 表格没有可读取的行列。");
            var sourceTextHeights = Enumerable.Range(0, rowCount)
                .SelectMany(rowIndex => Enumerable.Range(0, columnCount)
                    .Select(columnIndex => source.Cells[rowIndex, columnIndex].TextHeight ?? 0d))
                .Where(height => height > .001d).OrderBy(height => height).ToList();
            var medianTextHeight = sourceTextHeights.Count == 0 ? 3.5d : sourceTextHeights[sourceTextHeights.Count / 2];
            var columns = CreateColumns(columnCount,
                Enumerable.Range(0, columnCount).Select(index => source.Columns[index].Width), medianTextHeight);
            var rows = new JArray();
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var cells = new JArray();
                for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    var sourceCell = source.Cells[rowIndex, columnIndex];
                    var merged = sourceCell.IsMerged == true;
                    var mergeRange = merged ? sourceCell.GetMergeRange() : source.Cells[rowIndex, columnIndex];
                    var isAnchor = !merged || mergeRange.TopRow == rowIndex && mergeRange.LeftColumn == columnIndex;
                    var rowSpan = isAnchor ? (merged ? mergeRange.BottomRow - mergeRange.TopRow + 1 : 1) : 0;
                    var columnSpan = isAnchor ? (merged ? mergeRange.RightColumn - mergeRange.LeftColumn + 1 : 1) : 0;
                    var sourceValue = isAnchor ? (sourceCell.TextString ?? string.Empty).Trim() : string.Empty;
                    var value = isAnchor ? MTextContentNormalizer.Normalize(sourceValue, sourceValue) : string.Empty;
                    cells.Add(CreateCell(columnIndex, value, rowSpan, columnSpan, "AutoCAD原生表格",
                        new[] { SafeHandle(source) }, sourceCell.TextHeight));
                    var added = (JObject)cells[cells.Count - 1];
                    if (!string.Equals(sourceValue, value, StringComparison.Ordinal))
                    {
                        added["sourceNormalizedValue"] = value;
                        added["sourceAutoCadMTextValue"] = sourceValue;
                        if (TianzhengTextCodec.ContainsTianzhengFormatting(sourceValue))
                            added["sourceTianzhengValue"] = sourceValue;
                    }
                    added["alignment"] = CadAlignmentName(sourceCell.Alignment);
                    added["borderColorIndex"] = SafeGridColorIndex(source, rowIndex, columnIndex);
                    added["textColorIndex"] = SafeColorIndex(() => sourceCell.ContentColor);
                    added["fillColorIndex"] = SafeBackgroundColorIndex(sourceCell);
                    added["sourceHorizontalPaddingCadUnits"] = SafeHorizontalMargin(source, rowIndex, columnIndex);
                    if (isAnchor)
                    {
                        var blockAsset = WriteNativeCellBlockSnapshot(document, source, rowIndex, columnIndex);
                        if (!string.IsNullOrWhiteSpace(blockAsset))
                        {
                            added["cadObjectAssetPath"] = blockAsset;
                            added["cadObjectPreviewPath"] = Path.ChangeExtension(blockAsset, ".png");
                            added["cadObjectCount"] = 1;
                        }
                    }
                }
                var row = CreateRow(cells);
                row["heightMillimeters"] = NormalizeCadDimension(source.Rows[rowIndex].Height, medianTextHeight);
                row["sourceHeightCadUnits"] = source.Rows[rowIndex].Height;
                rows.Add(row);
            }
            var payload = CreatePayload(columns, rows, rowCount, columnCount, drawingPath,
                new JArray(), 1, 0, 0, 0, "CAD原生表格", true);
            payload["suggestedInsertType"] = "autocad";
            if (markForUpdate)
                payload["sourceEdit"] = new JObject
                {
                    ["kind"] = "nativeTable",
                    ["drawingPath"] = drawingPath ?? string.Empty,
                    ["handle"] = SafeHandle(source)
                };
            return payload;
        }

        private static string WriteNativeCellBlockSnapshot(Document document, Table source, int row, int column)
        {
            if (document == null || source == null) return string.Empty;
            ObjectId blockId = ObjectId.Null;
            try
            {
#pragma warning disable CS0618
                var count = source.GetNumberOfContents(row, column);
                for (var contentIndex = 0; contentIndex < count; contentIndex++)
                {
                    var candidate = source.GetBlockTableRecordId(row, column, contentIndex);
                    if (!candidate.IsNull && candidate.IsValid) { blockId = candidate; break; }
                }
#pragma warning restore CS0618
            }
            catch { return string.Empty; }
            if (blockId.IsNull || !blockId.IsValid) return string.Empty;

            var entityIds = new List<ObjectId>();
            Extents3d? extents = null;
            var active = document.Database.TransactionManager.TopTransaction;
            if (active == null) return string.Empty;
            var definition = active.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
            if (definition == null) return string.Empty;
            foreach (ObjectId id in definition)
            {
                var entity = active.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity == null) continue;
                entityIds.Add(id);
                try
                {
                    var current = entity.GeometricExtents;
                    if (!extents.HasValue) extents = current;
                    else { var combined = extents.Value; combined.AddExtents(current); extents = combined; }
                }
                catch { }
            }
            if (entityIds.Count == 0) return string.Empty;
            var center = extents.HasValue
                ? new Point3d((extents.Value.MinPoint.X + extents.Value.MaxPoint.X) * .5d,
                    (extents.Value.MinPoint.Y + extents.Value.MaxPoint.Y) * .5d,
                    (extents.Value.MinPoint.Z + extents.Value.MaxPoint.Z) * .5d)
                : Point3d.Origin;
            var directory = CadArchSpec.EditorBridge.PortableDataPaths.DirectoryFor(Path.Combine("表格模板", "单元格对象"));
            var path = Path.Combine(directory, "cell-block-" + Guid.NewGuid().ToString("N") + ".dwg");
            using (var snapshot = new Database(true, true))
            {
                document.Database.Wblock(snapshot, new ObjectIdCollection(entityIds.ToArray()), center,
                    DuplicateRecordCloning.Ignore);
                snapshot.SaveAs(path, DwgVersion.Current);
            }
            WriteCadObjectPreview(document, entityIds, Path.ChangeExtension(path, ".png"));
            return path;
        }

        private static JArray CreateColumns(int columnCount, IEnumerable<double> sourceWidths = null,
            double sourceTextHeightCadUnits = 3.5d)
        {
            var columns = new JArray();
            var rawWidths = (sourceWidths ?? Enumerable.Repeat(1d, columnCount)).ToList();
            var displayWidths = NormalizeCadDimensions(rawWidths, sourceTextHeightCadUnits);
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                columns.Add(new JObject
                {
                    ["key"] = "column" + (columnIndex + 1),
                    ["title"] = ColumnName(columnIndex),
                    ["unit"] = string.Empty,
                    ["widthMillimeters"] = displayWidths[columnIndex],
                    ["sourceWidthCadUnits"] = rawWidths[columnIndex],
                    ["decimalPlaces"] = 0,
                    ["required"] = false
                });
            }
            return columns;
        }

        private static List<double> NormalizeCadDimensions(IEnumerable<double> values, double sourceTextHeightCadUnits)
        {
            return (values ?? Enumerable.Empty<double>())
                .Select(value => NormalizeCadDimension(value, sourceTextHeightCadUnits)).ToList();
        }

        private static double NormalizeCadDimension(double value, double sourceTextHeightCadUnits)
        {
            var scaleToPaper = 3.5d / Math.Max(.001d, sourceTextHeightCadUnits);
            return Math.Round(Math.Max(1d, value * scaleToPaper), 1);
        }

        private static JObject CreateCell(int columnIndex, string value, int rowSpan, int columnSpan,
            string source, IEnumerable<string> sourceHandles = null, double? sourceTextHeight = null)
        {
            double numeric;
            var numericValue = double.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numeric)
                ? (JToken)new JValue(numeric)
                : JValue.CreateNull();
            return new JObject
            {
                ["cellId"] = "cell-" + Guid.NewGuid().ToString("N"),
                ["columnKey"] = "column" + (columnIndex + 1),
                ["displayValue"] = value ?? string.Empty,
                ["numericValue"] = numericValue,
                ["unit"] = string.Empty,
                ["fieldPath"] = string.Empty,
                ["formula"] = string.Empty,
                ["sourceTextHeightCadUnits"] = sourceTextHeight.HasValue ? (JToken)sourceTextHeight.Value : JValue.CreateNull(),
                ["state"] = string.IsNullOrWhiteSpace(value) ? "unknown" : "pending",
                ["source"] = source,
                ["sourceHandles"] = JArray.FromObject((sourceHandles ?? Enumerable.Empty<string>())
                    .Where(handle => !string.IsNullOrWhiteSpace(handle))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
                ["rowSpan"] = rowSpan,
                ["columnSpan"] = columnSpan,
                ["alignment"] = "center",
                ["horizontalPaddingMillimeters"] = 1d
            };
        }

        private static string CadAlignmentName(CellAlignment? alignment)
        {
            if (!alignment.HasValue) return "center";
            var name = alignment.Value.ToString();
            if (name.EndsWith("Left", StringComparison.OrdinalIgnoreCase)) return "left";
            if (name.EndsWith("Right", StringComparison.OrdinalIgnoreCase)) return "right";
            return "center";
        }

        private static JToken CadColorIndex(Autodesk.AutoCAD.Colors.Color color)
        {
            return color != null && color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci &&
                color.ColorIndex >= 1 && color.ColorIndex <= 255
                ? (JToken)color.ColorIndex : JValue.CreateNull();
        }

        private static JToken SafeColorIndex(Func<Autodesk.AutoCAD.Colors.Color> read)
        {
            try { return CadColorIndex(read()); }
            catch { return JValue.CreateNull(); }
        }

        private static JToken SafeBackgroundColorIndex(Cell cell)
        {
            try
            {
                return cell != null && cell.IsBackgroundColorNone == false
                    ? CadColorIndex(cell.BackgroundColor)
                    : JValue.CreateNull();
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        private static JToken SafeGridColorIndex(Table table, int row, int column)
        {
#pragma warning disable CS0618
            try { return CadColorIndex(table.GetGridColor(row, column, GridLineType.AllGridLines)); }
            catch { return JValue.CreateNull(); }
#pragma warning restore CS0618
        }

        private static double SafeHorizontalMargin(Table table, int row, int column)
        {
#pragma warning disable CS0618
            try { return Math.Max(table.GetMargin(row, column, CellMargins.Left),
                table.GetMargin(row, column, CellMargins.Right)); }
            catch { return Math.Max(0d, table.HorizontalCellMargin); }
#pragma warning restore CS0618
        }

        private static JObject CreateRow(JArray cells)
        {
            return new JObject
            {
                ["rowId"] = "row-" + Guid.NewGuid().ToString("N"),
                ["rowType"] = "Data",
                ["keepTogether"] = true,
                ["cells"] = cells
            };
        }

        private static JObject CreatePayload(JArray columns, JArray rows, int rowCount, int columnCount,
            string drawingPath, JArray warnings, int sourceEntityCount, int segmentCount, int textCount,
            int explodedObjectCount, string titleSuffix, bool nativeTable)
        {
            var drawingName = Path.GetFileNameWithoutExtension(drawingPath ?? string.Empty);
            return new JObject
            {
                ["sourceEntityCount"] = sourceEntityCount,
                ["segmentCount"] = segmentCount,
                ["textCount"] = textCount,
                ["explodedObjectCount"] = explodedObjectCount,
                ["rowCount"] = rowCount,
                ["columnCount"] = columnCount,
                ["nativeTable"] = nativeTable,
                ["hasOriginalCadSize"] = nativeTable,
                ["warnings"] = warnings,
                ["drawingPath"] = drawingPath ?? string.Empty,
                ["table"] = new JObject
                {
                    ["tableId"] = "table-" + Guid.NewGuid().ToString("N"),
                    ["schemaVersion"] = 1,
                    ["tableType"] = "custom",
                    ["tableNumber"] = string.Empty,
                    ["title"] = string.IsNullOrWhiteSpace(drawingName) ? titleSuffix : drawingName + "－" + titleSuffix,
                    ["sourceDrawingPath"] = drawingPath ?? string.Empty,
                    ["repeatHeader"] = true,
                    ["allowSplitAcrossPages"] = true,
                    ["columns"] = columns,
                    ["rows"] = rows,
                    ["formulaAudits"] = new JArray()
                }
            };
        }

        private static string SafeHandle(DBObject value)
        {
            try { return value.Handle.ToString(); }
            catch { return string.Empty; }
        }

        private static string SafeHandle(ObjectId value)
        {
            try { return value.Handle.ToString(); }
            catch { return string.Empty; }
        }

        private static string ColumnName(int index)
        {
            var value = index + 1;
            var name = string.Empty;
            while (value > 0) { value--; name = (char)('A' + value % 26) + name; value /= 26; }
            return name;
        }
    }
}
