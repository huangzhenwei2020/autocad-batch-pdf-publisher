using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CadArchSpec.CadTable;
using CadArchSpec.EditorBridge;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal static class CadTableXlsxExchange
    {
        public static JObject Export(JObject payload, IWin32Window owner)
        {
            var source = payload?["table"] as JObject;
            if (source == null) throw new InvalidDataException("没有收到可导出的表格数据。");
            var table = Convert(payload);
            using (var dialog = new SaveFileDialog
            {
                Title = "导出 Excel 表格",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                DefaultExt = "xlsx",
                AddExtension = true,
                FileName = SafeFileName(((string)source["tableNumber"] + " " + (string)source["title"]).Trim()) + ".xlsx"
            })
            {
                var dialogResult = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                if (dialogResult != DialogResult.OK)
                    return new JObject { ["cancelled"] = true };
                WriteFile(dialog.FileName, table, true);
                return new JObject { ["filePath"] = dialog.FileName };
            }
        }

        internal static TianzhengExcelImportSession PrepareTianzhengImport(JObject payload)
        {
            var source = payload?["table"] as JObject;
            if (source == null) throw new InvalidDataException("没有收到可插入的表格数据。");
            var requestDirectory = Path.Combine(Path.GetTempPath(), "WanluoArchitectureTools", "TianzhengTable", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(requestDirectory);
            var filePath = Path.Combine(requestDirectory,
                SafeFileName(((string)source["tableNumber"] + " " + (string)source["title"]).Trim()) + ".xlsx");
            try
            {
                // 天正会把当前 Excel 选区全部视作表格内容，不能加入导出用途的 A/B/C 列标题行。
                WriteFile(filePath, Convert(payload), false);
                WriteTianzhengLog("已生成临时 Excel：" + filePath);
                return TianzhengExcelImportSession.Open(filePath);
            }
            catch (Exception ex)
            {
                WriteTianzhengLog("准备临时 Excel 失败", ex);
                TryDelete(filePath, requestDirectory);
                throw;
            }
        }

        private static void WriteFile(string filePath, SpreadsheetTable table, bool includeColumnHeader)
        {
            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                new XlsxTableExporter().Write(stream, table, includeColumnHeader);
        }

        internal static void WriteTianzhengLog(string message, Exception exception = null)
        {
            try
            {
                var path = Path.Combine(PortableDataPaths.DirectoryFor("Logs"), "cad-table-tianzheng.log");
                File.AppendAllText(path, DateTime.Now.ToString("O") + " | " + message +
                    (exception == null ? string.Empty : Environment.NewLine + exception) + Environment.NewLine);
            }
            catch
            {
                // 诊断日志不能妨碍表格插入。
            }
        }

        internal static void TryDelete(string filePath, string directoryPath)
        {
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            try { if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, false); } catch { }
        }

        private static SpreadsheetTable Convert(JObject payload)
        {
            var source = payload["table"] as JObject ?? new JObject();
            var table = new SpreadsheetTable { Title = (string)source["title"] ?? "表格" };
            var options = payload["cadInsertOptions"] as JObject ?? new JObject();
            table.BorderColorRgb = (int?)options["borderColorRgb"];
            table.FillColorRgb = (int?)options["fillColorRgb"];
            table.TextColorRgb = (int?)options["textColorRgb"];
            var columns = ((JArray)source["columns"] ?? new JArray()).OfType<JObject>().ToList();
            table.Columns.AddRange(columns.Select(column => (string)column["title"] ?? string.Empty));
            table.ColumnWidthsMillimeters.AddRange(columns.Select(column =>
                Math.Max(1d, (double?)column["widthMillimeters"] ?? 36d)));
            foreach (var rowSource in ((JArray)source["rows"] ?? new JArray()).OfType<JObject>())
            {
                var row = new SpreadsheetRow();
                foreach (var cell in ((JArray)rowSource["cells"] ?? new JArray()).OfType<JObject>())
                {
                    row.Cells.Add(new SpreadsheetCell
                    {
                        Value = (string)cell["displayValue"] ?? string.Empty,
                        Formula = (string)cell["formula"] ?? string.Empty,
                        RowSpan = Math.Max(0, (int?)cell["rowSpan"] ?? 1),
                        ColumnSpan = Math.Max(0, (int?)cell["columnSpan"] ?? 1),
                        Alignment = (string)cell["alignment"] ?? "center"
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

    internal sealed class TianzhengExcelImportSession : IDisposable
    {
        private readonly string _filePath;
        private readonly string _directoryPath;
        private object _application;
        private object _workbooks;
        private object _workbook;
        private object _worksheet;
        private object _usedRange;
        private bool _ownsApplication;

        private TianzhengExcelImportSession(string filePath)
        {
            _filePath = filePath;
            _directoryPath = Path.GetDirectoryName(filePath);
        }

        public static TianzhengExcelImportSession Open(string filePath)
        {
            var session = new TianzhengExcelImportSession(filePath);
            try
            {
                var excelType = Type.GetTypeFromProgID("Excel.Application");
                if (excelType == null)
                    throw new InvalidOperationException("没有检测到 Microsoft Excel，无法调用天正 Excel2Sheet。");
                session._application = TryGetActiveExcel(excelType.GUID);
                session._ownsApplication = session._application == null;
                if (session._ownsApplication) session._application = Activator.CreateInstance(excelType);
                dynamic application = session._application;
                application.Visible = true;
                session._workbooks = application.Workbooks;
                dynamic workbooks = session._workbooks;
                session._workbook = workbooks.Open(filePath);
                dynamic workbook = session._workbook;
                workbook.Activate();
                session._worksheet = workbook.Worksheets[1];
                dynamic worksheet = session._worksheet;
                worksheet.Activate();
                session._usedRange = worksheet.UsedRange;
                dynamic usedRange = session._usedRange;
                usedRange.Select();
                CadTableXlsxExchange.WriteTianzhengLog(
                    (session._ownsApplication ? "已启动 Excel" : "已复用正在运行的 Excel") +
                    "，并选中区域：" + usedRange.Address);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_workbook != null) ((dynamic)_workbook).Close(false);
            }
            catch (Exception ex) { CadTableXlsxExchange.WriteTianzhengLog("关闭临时 Excel 工作簿失败", ex); }
            try
            {
                if (_ownsApplication && _application != null) ((dynamic)_application).Quit();
            }
            catch (Exception ex) { CadTableXlsxExchange.WriteTianzhengLog("退出临时 Excel 进程失败", ex); }

            Release(ref _usedRange);
            Release(ref _worksheet);
            Release(ref _workbook);
            Release(ref _workbooks);
            Release(ref _application);
            CadTableXlsxExchange.TryDelete(_filePath, _directoryPath);
        }

        private static object TryGetActiveExcel(Guid classId)
        {
            try
            {
                object application;
                GetActiveObject(ref classId, IntPtr.Zero, out application);
                return application;
            }
            catch (COMException)
            {
                return null;
            }
        }

        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(ref Guid classId, IntPtr reserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object application);

        private static void Release(ref object value)
        {
            if (value == null) return;
            try { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { }
            value = null;
        }
    }
}
