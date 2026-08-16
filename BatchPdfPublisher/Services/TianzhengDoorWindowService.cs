using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    internal static class TianzhengDoorWindowService
    {
        private sealed class TextFragment
        {
            public string Text;
            public double X;
            public double Y;
            public double Height;
        }

        private static readonly string[] CodeHeaders = { "门窗编号", "编号", "代号" };
        private static readonly string[] SizeHeaders = { "洞口尺寸", "门窗尺寸", "宽×高", "宽x高", "规格" };
        private static readonly string[] WidthHeaders = { "洞口宽", "门窗宽", "宽度", "宽" };
        private static readonly string[] HeightHeaders = { "洞口高", "门窗高", "高度", "高" };
        private static readonly string[] QuantityHeaders = { "樘数", "数量", "合计" };
        private static readonly string[] CategoryHeaders = { "门窗类型", "类别", "名称", "类型" };
        private static readonly string[] NoteHeaders = { "备注", "说明", "材料", "防火等级" };

        public static DoorWindowScheduleReadResult Read(DBObject source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var result = Describe(source);
            List<List<string>> rows;
            if (TryReadNativeTable(source, out rows)) result.Adapter = "AutoCAD Table 单元格";
            else if (TryReadComGrid(source, out rows)) result.Adapter = "天正 COM 表格";
            else if (TryReadExplodedText(source as Entity, out rows)) result.Adapter = "只读分解文字";
            else throw new InvalidOperationException("无法读取所选对象的表格单元格。已记录对象诊断，请确认选择的是天正门窗表，而不是普通线条或门窗对象。");

            result.RawRows.AddRange(rows);
            result.Items.AddRange(AssignSizeSuffixes(Consolidate(ParseRows(rows))));
            Validate(result.Items);
            result.Diagnostic = BuildDiagnostic(result);
            AppendLog(result);
            if (result.Items.Count == 0)
                throw new InvalidOperationException("已经读取表格，但没有找到可识别的门窗数据行。请检查表头是否包含编号和洞口尺寸；诊断已写入 door-window-elevation.log。");
            return result;
        }

        private static DoorWindowScheduleReadResult Describe(DBObject source)
        {
            var result = new DoorWindowScheduleReadResult
            {
                SourceId = source.ObjectId,
                SourceHandle = source.Handle.ToString(),
                SourceClassName = source.GetType().FullName ?? source.GetType().Name
            };
            try { result.SourceDxfName = source.GetRXClass().DxfName; } catch { result.SourceDxfName = "未知"; }
            var entity = source as Entity;
            if (entity != null)
            {
                try
                {
                    var extents = entity.GeometricExtents;
                    result.MinPoint = extents.MinPoint; result.MaxPoint = extents.MaxPoint; result.HasExtents = true;
                }
                catch { }
            }
            return result;
        }

        private static bool TryReadNativeTable(DBObject source, out List<List<string>> rows)
        {
            rows = null;
            var table = source as Table;
            if (table == null) return false;
            var output = new List<List<string>>();
            for (var row = 0; row < table.Rows.Count; row++)
            {
                var values = new List<string>();
                for (var column = 0; column < table.Columns.Count; column++)
                {
                    string value;
                    try { value = table.Cells[row, column].TextString; }
                    catch { value = string.Empty; }
                    values.Add(Clean(value));
                }
                output.Add(values);
            }
            rows = output;
            return output.Count > 0;
        }

        private static bool TryReadComGrid(DBObject source, out List<List<string>> rows)
        {
            rows = null;
            object instance;
            try { instance = source.AcadObject; } catch { return false; }
            if (instance == null) return false;
            var rowCount = GetIntProperty(instance, "RowCount", "RowsCount", "NumRows", "Rows");
            var columnCount = GetIntProperty(instance, "ColumnCount", "ColumnsCount", "NumColumns", "Cols", "Columns");
            if (rowCount <= 0 || columnCount <= 0 || rowCount > 5000 || columnCount > 100) return false;

            foreach (var firstIndex in new[] { 0, 1 })
            {
                var output = new List<List<string>>();
                var any = false;
                for (var row = 0; row < rowCount; row++)
                {
                    var values = new List<string>();
                    for (var column = 0; column < columnCount; column++)
                    {
                        var value = InvokeCell(instance, row + firstIndex, column + firstIndex);
                        if (!string.IsNullOrWhiteSpace(value)) any = true;
                        values.Add(Clean(value));
                    }
                    output.Add(values);
                }
                if (any) { rows = output; return true; }
            }
            return false;
        }

        private static int GetIntProperty(object instance, params string[] names)
        {
            foreach (var name in names)
            {
                try
                {
                    var value = instance.GetType().InvokeMember(name, BindingFlags.GetProperty, null, instance, null, CultureInfo.CurrentCulture);
                    int result;
                    if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result)) return result;
                }
                catch { }
            }
            return 0;
        }

        private static string InvokeCell(object instance, int row, int column)
        {
            foreach (var name in new[] { "GetCellText", "GetTextString", "GetCellValue", "GetText", "CellText" })
            {
                try
                {
                    var value = instance.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, instance, new object[] { row, column }, CultureInfo.CurrentCulture);
                    if (value != null) return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                }
                catch { }
            }
            return string.Empty;
        }

        private static bool TryReadExplodedText(Entity source, out List<List<string>> rows)
        {
            rows = null;
            if (source == null) return false;
            var fragments = new List<TextFragment>();
            var objects = new DBObjectCollection();
            try { source.Explode(objects); }
            catch { return false; }
            try
            {
                CollectFragments(objects, fragments, 0);
                if (fragments.Count == 0) return false;
                var tolerance = Math.Max(1d, Median(fragments.Where(x => x.Height > 0).Select(x => x.Height).ToList()) * 0.75d);
                var bands = new List<List<TextFragment>>();
                foreach (var fragment in fragments.OrderByDescending(x => x.Y).ThenBy(x => x.X))
                {
                    var band = bands.FirstOrDefault(x => Math.Abs(x.Average(y => y.Y) - fragment.Y) <= tolerance);
                    if (band == null) { band = new List<TextFragment>(); bands.Add(band); }
                    band.Add(fragment);
                }
                rows = bands.OrderByDescending(x => x.Average(y => y.Y))
                    .Select(x => x.OrderBy(y => y.X).Select(y => Clean(y.Text)).Where(y => !string.IsNullOrWhiteSpace(y)).ToList())
                    .Where(x => x.Count > 0).ToList();
                return rows.Count > 0;
            }
            finally { foreach (DBObject item in objects) item.Dispose(); }
        }

        private static void CollectFragments(DBObjectCollection objects, List<TextFragment> output, int depth)
        {
            foreach (DBObject item in objects)
            {
                var text = item as DBText;
                if (text != null)
                {
                    output.Add(new TextFragment { Text = text.TextString, X = text.Position.X, Y = text.Position.Y, Height = text.Height });
                    continue;
                }
                var mtext = item as MText;
                if (mtext != null)
                {
                    output.Add(new TextFragment { Text = mtext.Contents, X = mtext.Location.X, Y = mtext.Location.Y, Height = mtext.TextHeight });
                    continue;
                }
                var entity = item as Entity;
                if (entity != null && depth < 2)
                {
                    var nested = new DBObjectCollection();
                    try { entity.Explode(nested); CollectFragments(nested, output, depth + 1); }
                    catch { }
                    finally { foreach (DBObject child in nested) child.Dispose(); }
                }
            }
        }

        internal static List<DoorWindowScheduleItem> ParseRows(IList<List<string>> rows)
        {
            var result = new List<DoorWindowScheduleItem>();
            if (rows == null || rows.Count == 0) return result;
            var headerIndex = -1;
            var codeColumn = -1; var sizeColumn = -1; var widthColumn = -1; var heightColumn = -1;
            var quantityColumn = -1; var categoryColumn = -1; var noteColumn = -1;
            for (var row = 0; row < Math.Min(rows.Count, 12); row++)
            {
                var candidate = rows[row];
                var code = FindColumn(candidate, CodeHeaders);
                var size = FindColumn(candidate, SizeHeaders);
                var width = FindColumn(candidate, WidthHeaders);
                var height = FindColumn(candidate, HeightHeaders);
                if (code >= 0 && (size >= 0 || (width >= 0 && height >= 0)))
                {
                    headerIndex = row; codeColumn = code; sizeColumn = size; widthColumn = width; heightColumn = height;
                    quantityColumn = FindColumn(candidate, QuantityHeaders); categoryColumn = FindColumn(candidate, CategoryHeaders); noteColumn = FindColumn(candidate, NoteHeaders);
                    break;
                }
            }
            if (headerIndex < 0)
            {
                // Some exploded Tianzheng tables lose empty cells.  Use the first
                // row containing “编号” as a soft header, then scan each data row
                // for an explicit W×H value.
                for (var row = 0; row < Math.Min(rows.Count, 12); row++)
                {
                    codeColumn = FindColumn(rows[row], CodeHeaders);
                    if (codeColumn >= 0) { headerIndex = row; break; }
                }
            }
            if (headerIndex < 0) return result;

            var carriedCategory = string.Empty;
            for (var row = headerIndex + 1; row < rows.Count; row++)
            {
                var cells = rows[row];
                var joined = string.Join("", cells.Select(Clean));
                var compactCategory = cells.Select(Clean).FirstOrDefault(IsCategoryText);
                if (!string.IsNullOrWhiteSpace(compactCategory)) carriedCategory = compactCategory;
                double width = 0, height = 0;
                var sizeCellIndex = -1;
                if (sizeColumn >= 0) ParseCombinedSize(Cell(cells, sizeColumn), out width, out height);
                if (width > 0 && height > 0) sizeCellIndex = sizeColumn;
                if (width <= 0 || height <= 0)
                {
                    if (widthColumn >= 0) width = ParseNumber(Cell(cells, widthColumn));
                    if (heightColumn >= 0) height = ParseNumber(Cell(cells, heightColumn));
                }
                if (width <= 0 || height <= 0)
                {
                    for (var index = 0; index < cells.Count; index++)
                        if (ParseCombinedSize(cells[index], out width, out height)) { sizeCellIndex = index; break; }
                }
                if ((width <= 0 || height <= 0) && IsCategoryText(joined)) { carriedCategory = joined; continue; }
                var code = Cell(cells, codeColumn);
                double ignoredWidth, ignoredHeight; var codeIsSize = ParseCombinedSize(code, out ignoredWidth, out ignoredHeight);
                if (sizeCellIndex >= 0 && (sizeCellIndex != sizeColumn || string.IsNullOrWhiteSpace(code) || codeIsSize))
                    code = InferCompactCode(cells.Take(sizeCellIndex));
                if (string.IsNullOrWhiteSpace(code) || Regex.IsMatch(code, "合计|总计|说明|备注")) continue;
                var category = Cell(cells, categoryColumn);
                if (!IsCategoryText(category)) category = !string.IsNullOrWhiteSpace(compactCategory) ? compactCategory : carriedCategory;
                var quantity = ParseNumber(Cell(cells, quantityColumn));
                if (sizeCellIndex >= 0 && (quantity <= 0 || quantityColumn <= sizeCellIndex))
                    for (var index = sizeCellIndex + 1; index < cells.Count; index++) { quantity = ParseNumber(cells[index]); if (quantity > 0) break; }
                var item = new DoorWindowScheduleItem
                {
                    Selected = true,
                    Sequence = result.Count + 1,
                    Code = Clean(code),
                    SourceCategory = category,
                    Width = width,
                    Height = height,
                    Quantity = Math.Max(1, (int)Math.Round(quantity)),
                    SourceNote = Cell(cells, noteColumn),
                    ElevationType = InferType(code, category),
                    DivisionPreset = "未设置",
                    OpeningMode = "未设置",
                    InstallationGap = 20d,
                    SourceRow = row + 1
                };
                DoorWindowElevationSuggestionService.Apply(item);
                result.Add(item);
            }
            return result;
        }

        internal static void Validate(List<DoorWindowScheduleItem> items)
        {
            foreach (var item in items)
            {
                var badSize = double.IsNaN(item.Width) || double.IsInfinity(item.Width) || double.IsNaN(item.Height) || double.IsInfinity(item.Height) || item.Width <= 0 || item.Height <= 0;
                var badGap = double.IsNaN(item.InstallationGap) || double.IsInfinity(item.InstallationGap);
                item.Status = badSize ? "缺少洞口尺寸" : badGap || item.Width <= item.InstallationGap * 2 || item.Height <= item.InstallationGap * 2 ? "尺寸小于安装缝" : "待设置分格";
            }
        }

        internal static List<DoorWindowScheduleItem> AssignSizeSuffixes(List<DoorWindowScheduleItem> items)
        {
            foreach (var group in items.GroupBy(x => (x.Code ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            {
                var ordered = group.OrderBy(x => x.SourceRow).ToList();
                for (var index = 0; index < ordered.Count; index++)
                {
                    var suffix = ToAlphabeticSuffix(index);
                    ordered[index].Code = (group.Key ?? string.Empty) + suffix;
                    ordered[index].SourceNote = string.IsNullOrWhiteSpace(ordered[index].SourceNote)
                        ? "原编号 " + group.Key + " 存在不同洞口尺寸，已自动增加后缀"
                        : ordered[index].SourceNote + "；原编号 " + group.Key + " 存在不同洞口尺寸，已自动增加后缀";
                }
            }
            for (var index = 0; index < items.Count; index++) items[index].Sequence = index + 1;
            return items;
        }

        private static string ToAlphabeticSuffix(int index)
        {
            var value = index + 1; var result = string.Empty;
            while (value > 0) { value--; result = (char)('A' + value % 26) + result; value /= 26; }
            return result;
        }

        internal static List<DoorWindowScheduleItem> Consolidate(List<DoorWindowScheduleItem> items)
        {
            var output = new List<DoorWindowScheduleItem>();
            foreach (var group in items.GroupBy(x => (x.Code ?? string.Empty).Trim().ToUpperInvariant() + "|" + x.Width.ToString("0.###", CultureInfo.InvariantCulture) + "|" + x.Height.ToString("0.###", CultureInfo.InvariantCulture)))
            {
                var first = group.First();
                first.Quantity = group.Sum(x => Math.Max(1, x.Quantity));
                first.SourceNote = string.Join("；", group.Select(x => x.SourceNote).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                first.Sequence = output.Count + 1;
                output.Add(first);
            }
            return output;
        }

        private static int FindColumn(IList<string> cells, IEnumerable<string> aliases)
        {
            for (var index = 0; index < cells.Count; index++)
            {
                var value = NormalizeHeader(cells[index]);
                if (aliases.Any(alias => value == NormalizeHeader(alias) || value.Contains(NormalizeHeader(alias)))) return index;
            }
            return -1;
        }

        private static string Cell(IList<string> cells, int index) { return index >= 0 && index < cells.Count ? Clean(cells[index]) : string.Empty; }
        private static string NormalizeHeader(string value) { return Regex.Replace(Clean(value), @"[\s:：()（）]", string.Empty).ToLowerInvariant(); }
        private static string Clean(string value)
        {
            var text = value ?? string.Empty;
            text = text.Replace("\\P", " ").Replace("\\~", " ");
            text = Regex.Replace(text, @"\\[A-Za-z][^;]*;", string.Empty);
            return Regex.Replace(text, @"[{}\r\n\t]+", " ").Trim();
        }

        private static bool ParseCombinedSize(string value, out double width, out double height)
        {
            width = 0; height = 0;
            var match = Regex.Match(value ?? string.Empty, @"(?<!\d)(\d{2,6}(?:\.\d+)?)\s*[xX×＊*]\s*(\d{2,6}(?:\.\d+)?)(?!\d)");
            if (!match.Success) return false;
            return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out width)
                && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out height);
        }

        private static double ParseNumber(string value)
        {
            var match = Regex.Match(value ?? string.Empty, @"[-+]?\d+(?:\.\d+)?");
            double result;
            return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : 0d;
        }

        private static string InferType(string code, string category)
        {
            var inferred = DoorWindowElevationSuggestionService.InferTypeFromCode(code, null);
            if (inferred != "待确认") return inferred;
            var value = (category ?? string.Empty).ToUpperInvariant();
            if (value.Contains("门联窗")) return "门联窗";
            if (value.Contains("百叶窗")) return "百叶窗";
            if (value.Contains("百叶门")) return "百叶门";
            if (value.Contains("防火窗")) return "防火窗（等级待确认）";
            if (value.Contains("防火门")) return "防火门（等级待确认）";
            if (value.Contains("窗")) return "普通窗";
            if (value.Contains("门")) return "普通门";
            return "待确认";
        }

        private static bool IsCategoryText(string value)
        {
            var text = Clean(value);
            return text.Contains("普通门") || text.Contains("普通窗") || text.Contains("防火门") || text.Contains("防火窗") || text.Contains("门联窗") || text.Contains("凸窗") || text.Contains("百叶");
        }

        private static string InferCompactCode(IEnumerable<string> prefixCells)
        {
            var parts = prefixCells.Select(Clean).Where(x => !string.IsNullOrWhiteSpace(x) && !IsCategoryText(x) && !Regex.IsMatch(x, "类型|编号|代号")).ToList();
            return string.Join(string.Empty, parts);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 2.5d;
            values.Sort(); var middle = values.Count / 2;
            return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2d : values[middle];
        }

        private static string BuildDiagnostic(DoorWindowScheduleReadResult result)
        {
            return "对象=" + result.SourceDxfName + " / " + result.SourceClassName + "；Handle=" + result.SourceHandle + "；适配器=" + result.Adapter + "；原始行=" + result.RawRows.Count + "；门窗=" + result.Items.Count + "；" + CadCompatibilityService.DescribeTianzhengHost();
        }

        private static void AppendLog(DoorWindowScheduleReadResult result)
        {
            try
            {
                var builder = new StringBuilder();
                builder.AppendLine(DateTime.Now.ToString("O") + " " + BuildDiagnostic(result));
                foreach (var row in result.RawRows) builder.AppendLine(string.Join("\t", row));
                builder.AppendLine();
                File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "door-window-elevation.log"), builder.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
