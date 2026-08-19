using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 从 CSV / TSV / Excel(.xlsx) 文件导入门窗表数据。
    /// 表头识别与行解析复用 TianzhengDoorWindowService.ParseRows，保证与天正门窗表行为一致。
    /// </summary>
    internal static class CsvDoorWindowImportService
    {
        /// <summary>读取门窗表文件，返回与天正读取一致的结果对象。</summary>
        public static DoorWindowScheduleReadResult Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidOperationException("请选择存在的门窗表文件。");
            var extension = Path.GetExtension(path).ToLowerInvariant();
            List<List<string>> rows;
            string adapter;
            if (extension == ".xlsx")
            {
                rows = ReadXlsx(path);
                adapter = "Excel 工作表";
            }
            else
            {
                rows = ReadDelimited(path);
                adapter = extension == ".tsv" || extension == ".txt" ? "TSV 文本" : "CSV 文本";
            }
            if (rows.Count == 0) throw new InvalidOperationException("文件中没有可读取的数据行。");
            var result = new DoorWindowScheduleReadResult
            {
                SourceId = ObjectId.Null,
                SourceHandle = string.Empty,
                SourceClassName = "文件导入",
                SourceDxfName = extension.TrimStart('.'),
                Adapter = adapter,
                Diagnostic = "文件=" + Path.GetFileName(path) + "；行=" + rows.Count
            };
            result.RawRows.AddRange(rows);
            result.Items.AddRange(TianzhengDoorWindowService.AssignSizeSuffixes(TianzhengDoorWindowService.Consolidate(TianzhengDoorWindowService.ParseRows(rows))));
            TianzhengDoorWindowService.Validate(result.Items);
            if (result.Items.Count == 0)
                throw new InvalidOperationException("已经读取 " + rows.Count + " 行，但没有找到可识别的门窗数据行。请确认表头包含“编号”和“洞口尺寸/宽×高”列。");
            return result;
        }

        private static List<List<string>> ReadDelimited(string path)
        {
            var text = ReadAllText(path);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            var delimiter = DetectDelimiter(text);
            var rows = new List<List<string>>();
            foreach (var line in SplitLines(text))
            {
                var cells = ParseDelimitedLine(line, delimiter);
                if (cells.Count == 0 || cells.All(x => string.IsNullOrWhiteSpace(x))) continue;
                rows.Add(cells);
            }
            return rows;
        }

        private static string ReadAllText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // UTF-8 BOM。
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            // 尝试 UTF-8 严格解码；失败按 GBK（Windows 中文编码）解码。
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(936).GetString(bytes); }
        }

        private static char DetectDelimiter(string text)
        {
            var first = text.IndexOfAny(new[] { '\r', '\n' });
            var head = first < 0 ? text : text.Substring(0, first);
            var tab = head.Count(x => x == '\t');
            var comma = CountUnquoted(head, ',');
            var semicolon = CountUnquoted(head, ';');
            if (tab > comma && tab > semicolon) return '\t';
            return semicolon > comma ? ';' : ',';
        }

        private static int CountUnquoted(string text, char target)
        {
            var count = 0; var inQuotes = false;
            foreach (var ch in text)
            {
                if (ch == '"') inQuotes = !inQuotes;
                else if (ch == target && !inQuotes) count++;
            }
            return count;
        }

        private static List<string> ParseDelimitedLine(string line, char delimiter)
        {
            var cells = new List<string>(); var builder = new StringBuilder(); var inQuotes = false;
            for (var index = 0; index < line.Length; index++)
            {
                var ch = line[index];
                if (ch == '"')
                {
                    if (inQuotes && index + 1 < line.Length && line[index + 1] == '"') { builder.Append('"'); index++; }
                    else inQuotes = !inQuotes;
                }
                else if (ch == delimiter && !inQuotes) { cells.Add(builder.ToString().Trim()); builder.Length = 0; }
                else builder.Append(ch);
            }
            cells.Add(builder.ToString().Trim());
            return cells;
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            return Regex.Split(text, "\r\n|\n|\r").Where(x => !string.IsNullOrWhiteSpace(x));
        }

        /// <summary>读取 .xlsx 第一个工作表（支持共享字符串与内联字符串）。</summary>
        private static List<List<string>> ReadXlsx(string path)
        {
            var rows = new List<List<string>>();
            using (var archive = ZipFile.OpenRead(path))
            {
                var sharedEntry = archive.Entries.FirstOrDefault(x => x.FullName == "xl/sharedStrings.xml");
                var shared = new List<string>();
                if (sharedEntry != null)
                {
                    var document = XDocument.Load(sharedEntry.Open());
                    shared = document.Descendants(XName.Get("si", SheetNamespace))
                        .Select(si => string.Concat(si.Descendants(XName.Get("t", SheetNamespace)).Select(t => t.Value)))
                        .ToList();
                }
                var sheetEntry = archive.Entries.FirstOrDefault(x => x.FullName == "xl/worksheets/sheet1.xml")
                    ?? archive.Entries.FirstOrDefault(x => x.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                if (sheetEntry == null) throw new InvalidOperationException("Excel 文件中没有工作表。");
                var sheet = XDocument.Load(sheetEntry.Open());
                foreach (var rowElement in sheet.Descendants(XName.Get("row", SheetNamespace)))
                {
                    var cells = new List<string>();
                    var cellsByRef = rowElement.Elements(XName.Get("c", SheetNamespace)).ToList();
                    // 按列号补齐空单元格，保持列对齐。
                    var lastColumn = cellsByRef.Count == 0 ? 0 : ColumnIndex(cellsByRef.Last().Attribute("r") == null ? string.Empty : cellsByRef.Last().Attribute("r").Value);
                    var column = 0;
                    foreach (var cellElement in cellsByRef)
                    {
                        var reference = cellElement.Attribute("r") == null ? string.Empty : cellElement.Attribute("r").Value;
                        var target = ColumnIndex(reference);
                        while (column < target) { cells.Add(string.Empty); column++; }
                        cells.Add(CellText(cellElement, shared));
                        column++;
                    }
                    while (column <= lastColumn) { cells.Add(string.Empty); column++; }
                    if (cells.Count == 0 || cells.All(x => string.IsNullOrWhiteSpace(x))) continue;
                    rows.Add(cells);
                }
            }
            return rows;
        }

        private const string SheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        private static string CellText(XElement cell, List<string> shared)
        {
            var type = cell.Attribute("t") == null ? string.Empty : cell.Attribute("t").Value;
            var value = cell.Element(XName.Get("v", SheetNamespace)) == null ? string.Empty : cell.Element(XName.Get("v", SheetNamespace)).Value;
            if (type == "s")
            {
                int index; if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < shared.Count) return shared[index];
                return string.Empty;
            }
            if (type == "inlineStr")
            {
                var inline = cell.Element(XName.Get("is", SheetNamespace));
                return inline == null ? string.Empty : string.Concat(inline.Descendants(XName.Get("t", SheetNamespace)).Select(t => t.Value));
            }
            if (type == "b") return value == "1" ? "1" : "0";
            if (type == "str" || type == "e") return value;
            // 数字日期：把 Excel 序列化日期（如 45000）原样输出，由解析层忽略。
            return value;
        }

        private static int ColumnIndex(string reference)
        {
            var letters = new string((reference ?? string.Empty).TakeWhile(char.IsLetter).ToArray());
            var index = 0;
            foreach (var ch in letters) index = index * 26 + (ch - 'A' + 1);
            return index - 1;
        }
    }
}
