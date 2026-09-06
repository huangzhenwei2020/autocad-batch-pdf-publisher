using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CadArchSpec.CadTable;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal static class CadTableXlsxExchange
    {
        public static JObject Export(JObject payload, IWin32Window owner)
        {
            var source = payload?["table"] as JObject;
            if (source == null) throw new InvalidDataException("没有收到可导出的表格数据。");
            var table = Convert(source);
            using (var dialog = new SaveFileDialog
            {
                Title = "导出 Excel 表格",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                DefaultExt = "xlsx",
                AddExtension = true,
                FileName = SafeFileName(((string)source["tableNumber"] + " " + (string)source["title"]).Trim()) + ".xlsx"
            })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                    return new JObject { ["cancelled"] = true };
                using (var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None))
                    new XlsxTableExporter().Write(stream, table);
                return new JObject { ["filePath"] = dialog.FileName };
            }
        }

        private static SpreadsheetTable Convert(JObject source)
        {
            var table = new SpreadsheetTable { Title = (string)source["title"] ?? "表格" };
            table.Columns.AddRange(((JArray)source["columns"] ?? new JArray()).OfType<JObject>()
                .Select(column => (string)column["title"] ?? string.Empty));
            foreach (var rowSource in ((JArray)source["rows"] ?? new JArray()).OfType<JObject>())
            {
                var row = new SpreadsheetRow();
                foreach (var cell in ((JArray)rowSource["cells"] ?? new JArray()).OfType<JObject>())
                {
                    row.Cells.Add(new SpreadsheetCell
                    {
                        Value = (string)cell["displayValue"] ?? string.Empty,
                        RowSpan = Math.Max(0, (int?)cell["rowSpan"] ?? 1),
                        ColumnSpan = Math.Max(0, (int?)cell["columnSpan"] ?? 1)
                    });
                }
                table.Rows.Add(row);
            }
            return table;
        }

        private static string SafeFileName(string value)
        {
            var result = string.IsNullOrWhiteSpace(value) ? "专业表格" : value;
            foreach (var invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
            return result;
        }
    }
}
