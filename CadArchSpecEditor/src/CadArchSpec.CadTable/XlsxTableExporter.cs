using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace CadArchSpec.CadTable
{
    public sealed class SpreadsheetCell
    {
        public string Value { get; set; } = string.Empty;
        public string Formula { get; set; } = string.Empty;
        public int RowSpan { get; set; } = 1;
        public int ColumnSpan { get; set; } = 1;
        public string Alignment { get; set; } = "center";
        public int? BorderColorRgb { get; set; }
        public int? FillColorRgb { get; set; }
        public int? TextColorRgb { get; set; }
        public double HorizontalPaddingMillimeters { get; set; } = 1d;
    }

    public sealed class SpreadsheetRow
    {
        public List<SpreadsheetCell> Cells { get; set; } = new List<SpreadsheetCell>();
    }

    public sealed class SpreadsheetTable
    {
        public string Title { get; set; } = "表格";
        public List<string> Columns { get; set; } = new List<string>();
        public List<double> ColumnWidthsMillimeters { get; set; } = new List<double>();
        public List<SpreadsheetRow> Rows { get; set; } = new List<SpreadsheetRow>();
        public int? BorderColorRgb { get; set; }
        public int? FillColorRgb { get; set; }
        public int? TextColorRgb { get; set; }
    }

    public sealed class XlsxTableExporter
    {
        private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        public void Write(Stream output, SpreadsheetTable table, bool includeColumnHeader = true)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (table.Columns == null || table.Columns.Count == 0) throw new InvalidOperationException("表格没有可导出的列。");
            var styles = BuildStyleCatalog(table);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                WriteText(archive, "[Content_Types].xml", ContentTypes());
                WriteText(archive, "_rels/.rels", RootRelationships());
                WriteText(archive, "xl/workbook.xml", Workbook(SafeSheetName(table.Title)));
                WriteText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
                WriteText(archive, "xl/styles.xml", Styles(styles));
                WriteWorksheet(archive, table, includeColumnHeader, styles);
            }
        }

        private static void WriteWorksheet(ZipArchive archive, SpreadsheetTable table, bool includeColumnHeader,
            IList<CellStyleSpec> styles)
        {
            var excelColumnWidths = Enumerable.Range(0, table.Columns.Count)
                .Select(index => ToExcelColumnWidth(index < table.ColumnWidthsMillimeters.Count
                    ? table.ColumnWidthsMillimeters[index] : 36d)).ToList();
            var rowHeights = CalculateRowHeights(table, excelColumnWidths);
            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("worksheet", SpreadsheetNamespace);
                if (includeColumnHeader)
                {
                    writer.WriteStartElement("sheetViews");
                    writer.WriteStartElement("sheetView");
                    writer.WriteAttributeString("workbookViewId", "0");
                    writer.WriteStartElement("pane");
                    writer.WriteAttributeString("ySplit", "1");
                    writer.WriteAttributeString("topLeftCell", "A2");
                    writer.WriteAttributeString("activePane", "bottomLeft");
                    writer.WriteAttributeString("state", "frozen");
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }
                writer.WriteStartElement("cols");
                for (var index = 0; index < table.Columns.Count; index++)
                {
                    writer.WriteStartElement("col");
                    writer.WriteAttributeString("min", (index + 1).ToString());
                    writer.WriteAttributeString("max", (index + 1).ToString());
                    writer.WriteAttributeString("width", excelColumnWidths[index].ToString("0.##", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("customWidth", "1");
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteStartElement("sheetData");
                var firstDataRow = includeColumnHeader ? 2 : 1;
                if (includeColumnHeader)
                    WriteRow(writer, 1, table.Columns.Select(value => new SpreadsheetCell { Value = value }).ToList(),
                        1, 24d, false, table, styles);
                for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                    WriteRow(writer, rowIndex + firstDataRow, table.Rows[rowIndex].Cells, -1,
                        rowHeights[rowIndex], includeColumnHeader, table, styles);
                writer.WriteEndElement();

                var merges = new List<string>();
                for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                {
                    var cells = table.Rows[rowIndex].Cells;
                    for (var columnIndex = 0; columnIndex < cells.Count; columnIndex++)
                    {
                        var cell = cells[columnIndex];
                        if (cell.RowSpan <= 0 || cell.ColumnSpan <= 0 || cell.RowSpan == 1 && cell.ColumnSpan == 1) continue;
                        var from = CellReference(rowIndex + firstDataRow, columnIndex + 1);
                        var to = CellReference(rowIndex + firstDataRow + cell.RowSpan - 1, columnIndex + cell.ColumnSpan);
                        merges.Add(from + ":" + to);
                    }
                }
                if (includeColumnHeader)
                {
                    writer.WriteStartElement("autoFilter");
                    writer.WriteAttributeString("ref", "A1:" + CellReference(Math.Max(1, table.Rows.Count + 1), table.Columns.Count));
                    writer.WriteEndElement();
                }
                if (merges.Count > 0)
                {
                    writer.WriteStartElement("mergeCells");
                    writer.WriteAttributeString("count", merges.Count.ToString());
                    foreach (var merge in merges)
                    {
                        writer.WriteStartElement("mergeCell");
                        writer.WriteAttributeString("ref", merge);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }

        private static void WriteRow(XmlWriter writer, int rowNumber, IList<SpreadsheetCell> cells,
            int styleIndex, double heightPoints, bool shiftFormulaRows, SpreadsheetTable table,
            IList<CellStyleSpec> styles)
        {
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", rowNumber.ToString());
            writer.WriteAttributeString("ht", heightPoints.ToString("0.##", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customHeight", "1");
            for (var columnIndex = 0; columnIndex < cells.Count; columnIndex++)
            {
                var cell = cells[columnIndex];
                if (cell == null) continue;
                writer.WriteStartElement("c");
                writer.WriteAttributeString("r", CellReference(rowNumber, columnIndex + 1));
                var effectiveStyle = styleIndex >= 0 ? styleIndex : StyleIndex(CellStyle(cell, table), styles);
                writer.WriteAttributeString("s", effectiveStyle.ToString());
                if (cell.RowSpan == 0 || cell.ColumnSpan == 0)
                {
                    // Excel builds a merged range's outside border from the
                    // styles of every cell in that range, not only its anchor.
                    // Keep covered cells empty but styled so no edge disappears.
                    writer.WriteEndElement();
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(cell.Formula))
                {
                    writer.WriteStartElement("f");
                    writer.WriteString(ExcelFormula(cell.Formula, shiftFormulaRows));
                    writer.WriteEndElement();
                }
                else
                {
                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteStartElement("is");
                    writer.WriteStartElement("t");
                    writer.WriteAttributeString("xml", "space", null, "preserve");
                    writer.WriteString(cell.Value ?? string.Empty);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        private static List<double> CalculateRowHeights(SpreadsheetTable table, IList<double> excelColumnWidths)
        {
            var heights = Enumerable.Repeat(18d, table.Rows.Count).ToList();
            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                var cells = table.Rows[rowIndex].Cells;
                for (var columnIndex = 0; columnIndex < cells.Count && columnIndex < excelColumnWidths.Count; columnIndex++)
                {
                    var cell = cells[columnIndex];
                    if (cell == null || cell.RowSpan <= 0 || cell.ColumnSpan <= 0 || string.IsNullOrEmpty(cell.Value)) continue;
                    var effectiveWidth = Enumerable.Range(columnIndex,
                            Math.Min(cell.ColumnSpan, excelColumnWidths.Count - columnIndex))
                        .Sum(index => excelColumnWidths[index]);
                    var visualLines = EstimateVisualLineCount(cell.Value, effectiveWidth);
                    var requiredPerRow = Math.Min(300d, Math.Max(18d, visualLines * 15d + 3d)) /
                        Math.Max(1, cell.RowSpan);
                    for (var offset = 0; offset < cell.RowSpan && rowIndex + offset < heights.Count; offset++)
                        heights[rowIndex + offset] = Math.Max(heights[rowIndex + offset], requiredPerRow);
                }
            }
            return heights.Select(value => Math.Round(value, 2)).ToList();
        }

        private static int EstimateVisualLineCount(string value, double excelColumnWidth)
        {
            var capacity = Math.Max(1, (int)Math.Floor(excelColumnWidth));
            var total = 0;
            foreach (var line in (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.None))
            {
                var units = line.Sum(character => character <= 0x7f ? 1 : 2);
                total += Math.Max(1, (int)Math.Ceiling(units / (double)capacity));
            }
            return Math.Max(1, total);
        }

        private static double ToExcelColumnWidth(double millimeters)
        {
            if (double.IsNaN(millimeters) || double.IsInfinity(millimeters) || millimeters <= 0d) millimeters = 36d;
            return Math.Round(Math.Max(4d, Math.Min(80d, millimeters * 0.54d)), 2);
        }

        private static string CellReference(int row, int column)
        {
            var name = string.Empty;
            while (column > 0)
            {
                column--;
                name = (char)('A' + column % 26) + name;
                column /= 26;
            }
            return name + row;
        }

        private static string SafeSheetName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "表格" : value.Trim();
            foreach (var invalid in new[] { ':', '\\', '/', '?', '*', '[', ']' }) name = name.Replace(invalid, ' ');
            return name.Length > 31 ? name.Substring(0, 31) : name;
        }

        private static void WriteText(ZipArchive archive, string path, string text)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(text);
        }

        private static string ContentTypes() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>";
        private static string RootRelationships() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
        private static string WorkbookRelationships() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
        private static string Workbook(string sheetName) => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"" + Escape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets><calcPr calcMode=\"auto\" fullCalcOnLoad=\"1\" forceFullCalc=\"1\"/></workbook>";
        private static string ExcelFormula(string formula, bool shiftRows)
        {
            var value = (formula ?? string.Empty).Trim();
            if (value.StartsWith("=", StringComparison.Ordinal)) value = value.Substring(1);
            if (!shiftRows) return value;
            return System.Text.RegularExpressions.Regex.Replace(value, @"(?<![A-Z0-9_])(\$?[A-Z]+)(\$?)(\d+)", match =>
                match.Groups[1].Value + match.Groups[2].Value + (int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) + 1)
                    .ToString(CultureInfo.InvariantCulture), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static List<CellStyleSpec> BuildStyleCatalog(SpreadsheetTable table)
        {
            var result = new List<CellStyleSpec>
            {
                new CellStyleSpec { Alignment = "center" },
                new CellStyleSpec
                {
                    Alignment = "center", Bold = true, BorderColorRgb = table.BorderColorRgb,
                    FillColorRgb = table.FillColorRgb, TextColorRgb = table.TextColorRgb,
                    HorizontalPaddingMillimeters = 1d
                },
                LegacyDataStyle("left", table),
                LegacyDataStyle("center", table),
                LegacyDataStyle("right", table)
            };
            foreach (var cell in table.Rows.SelectMany(row => row.Cells))
            {
                var style = CellStyle(cell, table);
                if (!result.Any(existing => existing.Key == style.Key)) result.Add(style);
            }
            return result;
        }

        private static CellStyleSpec LegacyDataStyle(string alignment, SpreadsheetTable table)
        {
            return new CellStyleSpec
            {
                Alignment = alignment,
                BorderColorRgb = table.BorderColorRgb,
                FillColorRgb = table.FillColorRgb,
                TextColorRgb = table.TextColorRgb,
                HorizontalPaddingMillimeters = 1d
            };
        }

        private static CellStyleSpec CellStyle(SpreadsheetCell cell, SpreadsheetTable table)
        {
            return new CellStyleSpec
            {
                Alignment = string.IsNullOrWhiteSpace(cell.Alignment) ? "center" : cell.Alignment.ToLowerInvariant(),
                BorderColorRgb = cell.BorderColorRgb ?? table.BorderColorRgb,
                FillColorRgb = cell.FillColorRgb ?? table.FillColorRgb,
                TextColorRgb = cell.TextColorRgb ?? table.TextColorRgb,
                HorizontalPaddingMillimeters = Math.Max(0d, cell.HorizontalPaddingMillimeters)
            };
        }

        private static int StyleIndex(CellStyleSpec style, IList<CellStyleSpec> styles)
        {
            for (var index = 0; index < styles.Count; index++) if (styles[index].Key == style.Key) return index;
            return 0;
        }

        private static string Styles(IList<CellStyleSpec> styles)
        {
            var fonts = new StringBuilder();
            var fills = new StringBuilder("<fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>");
            var borders = new StringBuilder("<border/>");
            var xfs = new StringBuilder("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
            var fillIds = new int[styles.Count];
            var borderIds = new int[styles.Count];
            var nextFill = 2;
            var nextBorder = 1;
            for (var index = 0; index < styles.Count; index++)
            {
                var style = styles[index];
                fonts.Append("<font>").Append(style.Bold ? "<b/>" : string.Empty)
                    .Append("<sz val=\"11\"/><name val=\"等线\"/>").Append(ColorXml(style.TextColorRgb)).Append("</font>");
                if (style.FillColorRgb.HasValue)
                {
                    fillIds[index] = nextFill++;
                    fills.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"")
                        .Append(Argb(style.FillColorRgb.Value)).Append("\"/><bgColor indexed=\"64\"/></patternFill></fill>");
                }
                if (index > 0)
                {
                    borderIds[index] = nextBorder++;
                    var color = ColorXml(style.BorderColorRgb);
                    borders.Append("<border><left style=\"thin\">").Append(color).Append("</left><right style=\"thin\">")
                        .Append(color).Append("</right><top style=\"thin\">").Append(color)
                        .Append("</top><bottom style=\"thin\">").Append(color).Append("</bottom></border>");
                }
            }
            for (var index = 1; index < styles.Count; index++)
            {
                var style = styles[index];
                var indent = Math.Max(0, Math.Min(15, (int)Math.Round(style.HorizontalPaddingMillimeters)));
                xfs.Append("<xf numFmtId=\"0\" fontId=\"").Append(index).Append("\" fillId=\"").Append(fillIds[index])
                    .Append("\" borderId=\"").Append(borderIds[index])
                    .Append("\" xfId=\"0\" applyAlignment=\"1\" applyFill=\"1\"><alignment horizontal=\"")
                    .Append(Escape(style.Alignment)).Append("\" vertical=\"center\" wrapText=\"1\"")
                    .Append(indent > 0 && (style.Alignment == "left" || style.Alignment == "right")
                        ? " indent=\"" + indent.ToString(CultureInfo.InvariantCulture) + "\"" : string.Empty)
                    .Append("/></xf>");
            }
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"" + styles.Count + "\">" + fonts + "</fonts><fills count=\"" + nextFill + "\">" + fills +
                "</fills><borders count=\"" + nextBorder + "\">" + borders +
                "</borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"" + styles.Count + "\">" + xfs +
                "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
        }

        private sealed class CellStyleSpec
        {
            public string Alignment { get; set; }
            public bool Bold { get; set; }
            public int? BorderColorRgb { get; set; }
            public int? FillColorRgb { get; set; }
            public int? TextColorRgb { get; set; }
            public double HorizontalPaddingMillimeters { get; set; }
            public string Key { get { return Alignment + "|" + Bold + "|" + BorderColorRgb + "|" + FillColorRgb + "|" + TextColorRgb + "|" + HorizontalPaddingMillimeters.ToString("R", CultureInfo.InvariantCulture); } }
        }

        private static string ColorXml(int? rgb) => rgb.HasValue ? "<color rgb=\"" + Argb(rgb.Value) + "\"/>" : string.Empty;
        private static string Argb(int rgb) => "FF" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
        private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }
}
