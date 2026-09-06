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
                read = CadTableEntityReader.Read(transaction, selection.Value.GetObjectIds());
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
    }
}
