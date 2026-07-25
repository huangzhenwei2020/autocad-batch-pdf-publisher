using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace BatchPdfPublisher.Services
{
    public static class CsvExportService
    {
        public static bool Save(IWin32Window owner, string defaultName, IEnumerable<string> lines, out string path)
        {
            path = string.Empty;
            using (var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = defaultName })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
                path = dialog.FileName;
                return true;
            }
        }

        public static string Cell(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

        public static void Reveal(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch { }
        }
    }
}
