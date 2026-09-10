using System;
using System.Collections.Generic;
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
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal static class CadTableExchange
    {
        public static JObject ExportSelectedTableToXlsx()
        {
            while (true)
            {
                var recognized = ReadSelectedTableCore(false);
                if ((bool?)recognized["cancelled"] == true) return recognized;
                string currentTextStyle;
                var textStyles = ReadTextStyles(out currentTextStyle);
                using (var preview = new CadTablePreviewForm(recognized, textStyles, currentTextStyle))
                {
                    preview.ShowDialog();
                    if (preview.SelectedAction == CadTablePreviewAction.Repick) continue;
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
            var pointResult = document.Editor.GetPoint("\n指定重新插入 CAD 表格的位置：");
            if (pointResult.Status != PromptStatus.OK) return new JObject { ["cancelled"] = true };

            var source = payload["table"] as JObject;
            var columns = (source == null ? null : source["columns"] as JArray) ?? new JArray();
            var rows = (source == null ? null : source["rows"] as JArray) ?? new JArray();
            if (columns.Count == 0 || rows.Count == 0) throw new InvalidDataException("预览中没有可插入的表格数据。");

            var options = payload["cadInsertOptions"] as JObject ?? new JObject();
            var scale = Math.Max(.001d, (double?)options["scale"] ?? 1d);
            var useOriginalSize = (bool?)options["useOriginalCadSize"] == true && (bool?)payload["hasOriginalCadSize"] == true;
            var textHeight = Math.Max(.1d, (double?)options["textHeightMillimeters"] ?? 3.5d) * scale;
            var textStyleName = ((string)options["textStyle"] ?? string.Empty).Trim();
            var insertAsTianzheng = string.Equals((string)options["insertType"], "tianzheng", StringComparison.OrdinalIgnoreCase);
            var borderColor = ReadCadColor(options["borderColorRgb"]);
            var fillColor = ReadCadColor(options["fillColorRgb"]);
            var contentColor = ReadCadColor(options["textColorRgb"]);

            if (insertAsTianzheng)
                return InsertTianzhengTable(payload, document, pointResult.Value);

            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var currentSpace = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
                var textStyleId = ResolveTextStyle(document.Database, transaction, textStyleName);
                var table = new Table();
                table.SetDatabaseDefaults(document.Database);
                table.Position = pointResult.Value;
                table.SetSize(rows.Count, columns.Count);
                table.SetRowHeight(Math.Max(8d * scale, textHeight * 1.8d));
                table.SetColumnWidth(36d * scale);

                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    var column = columns[columnIndex] as JObject;
                    var width = useOriginalSize ? (double?)column?["sourceWidthCadUnits"] : null;
                    table.Columns[columnIndex].Width = width.HasValue && width.Value > 0.001d
                        ? width.Value : Math.Max(8d * scale, ((double?)column?["widthMillimeters"] ?? 36d) * scale);
                }

                var merges = new List<CellRange>();
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var row = rows[rowIndex] as JObject;
                    var cells = (row == null ? null : row["cells"] as JArray) ?? new JArray();
                    var originalRowHeight = useOriginalSize ? (double?)row?["sourceHeightCadUnits"] : null;
                    table.Rows[rowIndex].Height = originalRowHeight.HasValue && originalRowHeight.Value > 0.001d
                        ? originalRowHeight.Value : Math.Max(textHeight * 1.8d, ((double?)row?["heightMillimeters"] ?? 8d) * scale);
                    for (var columnIndex = 0; columnIndex < Math.Min(cells.Count, columns.Count); columnIndex++)
                    {
                        var cell = cells[columnIndex] as JObject;
                        if (cell == null) continue;
                        var rowSpan = Math.Max(0, (int?)cell["rowSpan"] ?? 1);
                        var columnSpan = Math.Max(0, (int?)cell["columnSpan"] ?? 1);
                        if (rowSpan == 0 || columnSpan == 0) continue;
                        table.Cells[rowIndex, columnIndex].TextString = (string)cell["displayValue"] ?? string.Empty;
                        var originalTextHeight = useOriginalSize ? (double?)cell["sourceTextHeightCadUnits"] : null;
                        table.Cells[rowIndex, columnIndex].TextHeight = originalTextHeight.HasValue && originalTextHeight.Value > 0.001d
                            ? originalTextHeight.Value : textHeight;
                        table.Cells[rowIndex, columnIndex].TextStyleId = textStyleId;
                        table.Cells[rowIndex, columnIndex].Alignment = CadAlignment((string)cell["alignment"]);
                        ApplyCellColors(table.Cells[rowIndex, columnIndex], borderColor, fillColor, contentColor);
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
                table.GenerateLayout();
                currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                transaction.Commit();
            }

            var result = new JObject { ["action"] = "inserted", ["insertType"] = "autocad" };
            CopyResultSummary(result, payload);
            return result;
        }

        private static JObject InsertTianzhengTable(JObject payload, Document document, Point3d insertionPoint)
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
                        throw new InvalidOperationException("Excel2Sheet 已结束，但没有检测到新建的天正表格；可能在天正询问中选择了“否”或取消了命令。");
                }
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

            var result = new JObject { ["action"] = "inserted", ["insertType"] = "tianzheng" };
            CopyResultSummary(result, payload);
            return result;
        }

        private static Autodesk.AutoCAD.Colors.Color ReadCadColor(JToken token)
        {
            var rgb = (int?)token;
            if (!rgb.HasValue) return null;
            return Autodesk.AutoCAD.Colors.Color.FromRgb(
                (byte)((rgb.Value >> 16) & 0xFF), (byte)((rgb.Value >> 8) & 0xFF), (byte)(rgb.Value & 0xFF));
        }

        private static void ApplyCellColors(object cell, Autodesk.AutoCAD.Colors.Color border,
            Autodesk.AutoCAD.Colors.Color fill, Autodesk.AutoCAD.Colors.Color content)
        {
            if (cell == null) return;
            if (fill != null) TrySetProperty(cell, "BackgroundColor", fill);
            if (content != null) TrySetProperty(cell, "ContentColor", content);
            if (border == null) return;
            var borders = TryGetProperty(cell, "Borders");
            if (borders == null) return;
            foreach (var name in new[] { "Top", "Bottom", "Left", "Right", "Horizontal", "Vertical" })
            {
                var edge = TryGetProperty(borders, name);
                if (edge != null) TrySetProperty(edge, "Color", border);
            }
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

        private static void CopyResultSummary(JObject target, JObject source)
        {
            target["rowCount"] = (int?)source["rowCount"] ?? 0;
            target["columnCount"] = (int?)source["columnCount"] ?? 0;
            target["nativeTable"] = (bool?)source["nativeTable"] ?? false;
            target["warnings"] = source["warnings"] == null ? new JArray() : source["warnings"].DeepClone();
        }

        public static async Task<JObject> ReadSelectedTableAsync(bool includeHiddenLayers = false)
        {
            JObject result = null;
            await Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = ReadSelectedTableCore(includeHiddenLayers);
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

        private static JObject ReadSelectedTableCore(bool includeHiddenLayers)
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

            CadTableEntityReadResult read;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var ids = selection.Value.GetObjectIds();
                var nativeTables = ids.Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as Table)
                    .Where(table => table != null).ToList();
                if (nativeTables.Count > 1)
                    throw new InvalidOperationException("一次只能读取一张 AutoCAD 原生表格。");
                if (nativeTables.Count == 1)
                {
                    var nativeResult = BuildNativeTablePayload(nativeTables[0], document.Name);
                    transaction.Commit();
                    return nativeResult;
                }
                read = CadTableEntityReader.Read(transaction, ids, includeHiddenLayers);
                transaction.Commit();
            }
            if (read.Input.Segments.Count == 0)
                throw new InvalidOperationException("所选对象中没有可识别的表格边框线。");

            var textHeight = read.Input.TextFragments.Where(item => item.Height > 0.001d)
                .Select(item => item.Height).OrderBy(value => value).ToList();
            var medianHeight = textHeight.Count == 0 ? 3.5d : textHeight[textHeight.Count / 2];
            var detector = new OrthogonalCadTableDetector();
            var detected = detector.Detect(read.Input, new CadTableDetectionOptions
            {
                CoordinateTolerance = Math.Max(0.1d, medianHeight * 0.04d),
                MaximumBorderGap = Math.Max(0.5d, medianHeight * 0.2d),
                OrthogonalAngleToleranceDegrees = 1d
            });
            if (detected.ColumnBoundaries.Count < 2 || detected.RowBoundaries.Count < 2 || detected.Cells.Count == 0)
                throw new InvalidOperationException("没有识别到闭合表格。请只框选一张表格，并确认边框线已连接。");

            var warnings = read.Warnings.Concat(detected.Warnings).Distinct().ToArray();
            var columnCount = detected.ColumnBoundaries.Count - 1;
            var rowCount = detected.RowBoundaries.Count - 1;
            var tableId = "table-" + Guid.NewGuid().ToString("N");
            var columns = new JArray();
            var sourceWidths = Enumerable.Range(0, columnCount)
                .Select(index => Math.Abs(detected.ColumnBoundaries[index + 1] - detected.ColumnBoundaries[index])).ToList();
            var displayWidths = CadTableColumnWidthNormalizer.Normalize(sourceWidths);
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
                        ["sourceTextHeightCadUnits"] = medianHeight,
                        ["state"] = string.IsNullOrWhiteSpace(value) ? "unknown" : "pending",
                        ["source"] = coveringCell == null ? "CAD边界待确认" : "CAD只读识别",
                        ["sourceHandles"] = coveringCell == null
                            ? new JArray()
                            : JArray.FromObject(coveringCell.SourceHandles.Distinct(StringComparer.OrdinalIgnoreCase)),
                        ["rowSpan"] = detectedCell != null ? detectedCell.RowSpan : coveringCell != null ? 0 : 1,
                        ["columnSpan"] = detectedCell != null ? detectedCell.ColumnSpan : coveringCell != null ? 0 : 1
                    });
                }
                rows.Add(new JObject
                {
                    ["rowId"] = "row-" + Guid.NewGuid().ToString("N"),
                    ["rowType"] = "Data",
                    ["keepTogether"] = true,
                    ["sourceHeightCadUnits"] = Math.Abs(detected.RowBoundaries[rowIndex + 1] - detected.RowBoundaries[rowIndex]),
                    ["cells"] = cells
                });
            }

            var drawingName = Path.GetFileNameWithoutExtension(document.Name ?? string.Empty);
            return new JObject
            {
                ["sourceEntityCount"] = read.SourceEntityCount,
                ["segmentCount"] = read.Input.Segments.Count,
                ["textCount"] = read.Input.TextFragments.Count,
                ["explodedObjectCount"] = read.ExplodedObjectCount,
                ["skippedHiddenEntityCount"] = read.SkippedHiddenEntityCount,
                ["includedHiddenLayers"] = includeHiddenLayers,
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
        }

        private static JObject BuildNativeTablePayload(Table source, string drawingPath)
        {
            var rowCount = source.Rows.Count;
            var columnCount = source.Columns.Count;
            if (rowCount <= 0 || columnCount <= 0)
                throw new InvalidOperationException("所选 AutoCAD 表格没有可读取的行列。");
            var columns = CreateColumns(columnCount,
                Enumerable.Range(0, columnCount).Select(index => source.Columns[index].Width));
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
                    var value = isAnchor ? (sourceCell.TextString ?? string.Empty).Trim() : string.Empty;
                    cells.Add(CreateCell(columnIndex, value, rowSpan, columnSpan, "AutoCAD原生表格",
                        new[] { SafeHandle(source) }, sourceCell.TextHeight));
                }
                var row = CreateRow(cells);
                row["sourceHeightCadUnits"] = source.Rows[rowIndex].Height;
                rows.Add(row);
            }
            return CreatePayload(columns, rows, rowCount, columnCount, drawingPath,
                new JArray(), 1, 0, 0, 0, "CAD原生表格", true);
        }

        private static JArray CreateColumns(int columnCount, IEnumerable<double> sourceWidths = null)
        {
            var columns = new JArray();
            var rawWidths = (sourceWidths ?? Enumerable.Repeat(1d, columnCount)).ToList();
            var displayWidths = CadTableColumnWidthNormalizer.Normalize(rawWidths);
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
                ["columnSpan"] = columnSpan
            };
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

        private static string ColumnName(int index)
        {
            var value = index + 1;
            var name = string.Empty;
            while (value > 0) { value--; name = (char)('A' + value % 26) + name; value /= 26; }
            return name;
        }
    }
}
