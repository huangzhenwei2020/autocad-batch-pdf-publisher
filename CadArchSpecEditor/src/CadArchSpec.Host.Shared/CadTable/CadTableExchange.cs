using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using CadArchSpec.CadTable;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal static class CadTableExchange
    {
        public static async Task<JObject> ReadSelectedTableAsync()
        {
            JObject result = null;
            await Application.DocumentManager.ExecuteInCommandContextAsync(
                async _ =>
                {
                    result = ReadSelectedTableCore();
                    await Task.CompletedTask;
                }, null);
            return result;
        }

        private static JObject ReadSelectedTableCore()
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
                read = CadTableEntityReader.Read(transaction, ids);
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
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                columns.Add(new JObject
                {
                    ["key"] = "column" + (columnIndex + 1),
                    ["title"] = "列" + (columnIndex + 1),
                    ["unit"] = string.Empty,
                    ["widthMillimeters"] = 36,
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
                        ["source"] = "CAD只读识别",
                        ["rowSpan"] = 1,
                        ["columnSpan"] = 1
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
                ["rowCount"] = rowCount,
                ["columnCount"] = columnCount,
                ["warnings"] = JArray.FromObject(warnings),
                ["drawingPath"] = document.Name ?? string.Empty,
                ["table"] = new JObject
                {
                    ["tableId"] = tableId,
                    ["schemaVersion"] = 1,
                    ["tableType"] = "custom",
                    ["tableNumber"] = string.Empty,
                    ["title"] = string.IsNullOrWhiteSpace(drawingName) ? "CAD识别表格" : drawingName + "－CAD识别表格",
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
            var columns = CreateColumns(columnCount);
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
                    cells.Add(CreateCell(columnIndex, value, rowSpan, columnSpan, "AutoCAD原生表格"));
                }
                rows.Add(CreateRow(cells));
            }
            return CreatePayload(columns, rows, rowCount, columnCount, drawingPath,
                new JArray(), 1, 0, 0, 0, "CAD原生表格", true);
        }

        private static JArray CreateColumns(int columnCount)
        {
            var columns = new JArray();
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                columns.Add(new JObject
                {
                    ["key"] = "column" + (columnIndex + 1),
                    ["title"] = "列" + (columnIndex + 1),
                    ["unit"] = string.Empty,
                    ["widthMillimeters"] = 36,
                    ["decimalPlaces"] = 0,
                    ["required"] = false
                });
            }
            return columns;
        }

        private static JObject CreateCell(int columnIndex, string value, int rowSpan, int columnSpan, string source)
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
                    ["repeatHeader"] = true,
                    ["allowSplitAcrossPages"] = true,
                    ["columns"] = columns,
                    ["rows"] = rows,
                    ["formulaAudits"] = new JArray()
                }
            };
        }
    }
}
