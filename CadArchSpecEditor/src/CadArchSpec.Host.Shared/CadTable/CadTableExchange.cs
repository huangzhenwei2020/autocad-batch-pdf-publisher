using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
            var recognized = ReadSelectedTableCore(false);
            if ((bool?)recognized["cancelled"] == true) return recognized;

            var exported = CadTableXlsxExchange.Export(recognized, null);
            if ((bool?)exported["cancelled"] == true) return exported;

            exported["rowCount"] = (int?)recognized["rowCount"] ?? 0;
            exported["columnCount"] = (int?)recognized["columnCount"] ?? 0;
            exported["nativeTable"] = (bool?)recognized["nativeTable"] ?? false;
            exported["warnings"] = recognized["warnings"] == null
                ? new JArray()
                : recognized["warnings"].DeepClone();
            return exported;
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
            var displayWidths = CadTableColumnWidthNormalizer.Normalize(Enumerable.Range(0, columnCount)
                .Select(index => Math.Abs(detected.ColumnBoundaries[index + 1] - detected.ColumnBoundaries[index])));
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                columns.Add(new JObject
                {
                    ["key"] = "column" + (columnIndex + 1),
                    ["title"] = "列" + (columnIndex + 1),
                    ["unit"] = string.Empty,
                    ["widthMillimeters"] = displayWidths[columnIndex],
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
                        new[] { SafeHandle(source) }));
                }
                rows.Add(CreateRow(cells));
            }
            return CreatePayload(columns, rows, rowCount, columnCount, drawingPath,
                new JArray(), 1, 0, 0, 0, "CAD原生表格", true);
        }

        private static JArray CreateColumns(int columnCount, IEnumerable<double> sourceWidths = null)
        {
            var columns = new JArray();
            var displayWidths = CadTableColumnWidthNormalizer.Normalize(sourceWidths ?? Enumerable.Repeat(1d, columnCount));
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                columns.Add(new JObject
                {
                    ["key"] = "column" + (columnIndex + 1),
                    ["title"] = "列" + (columnIndex + 1),
                    ["unit"] = string.Empty,
                    ["widthMillimeters"] = displayWidths[columnIndex],
                    ["decimalPlaces"] = 0,
                    ["required"] = false
                });
            }
            return columns;
        }

        private static JObject CreateCell(int columnIndex, string value, int rowSpan, int columnSpan,
            string source, IEnumerable<string> sourceHandles = null)
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
    }
}
