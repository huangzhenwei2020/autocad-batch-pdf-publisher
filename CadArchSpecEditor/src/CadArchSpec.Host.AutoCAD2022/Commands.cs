using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using CadArchSpec.Host.Shared.CadTable;

namespace CadArchSpec.Host.AutoCAD2022
{
    public sealed class Commands
    {
        [CommandMethod("JZSM", CommandFlags.Session)]
        public void ShowEditor()
        {
            HostPalette.Show();
        }

        [CommandMethod("CAD2XLSX")]
        [CommandMethod("CE")]
        public void ExportSelectedCadTableToXlsx()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try
            {
                var result = CadTableExchange.ExportSelectedTableToXlsx();
                if ((bool?)result["cancelled"] == true)
                {
                    document.Editor.WriteMessage("\n已取消 CAD 表格操作。\n");
                    return;
                }

                var warnings = result["warnings"] as Newtonsoft.Json.Linq.JArray;
                if (string.Equals((string)result["action"], "inserted", System.StringComparison.OrdinalIgnoreCase))
                {
                    document.Editor.WriteMessage("\nCAD 表格已重新插入：{0} 行 × {1} 列{2}\n",
                        (int?)result["rowCount"] ?? 0,
                        (int?)result["columnCount"] ?? 0,
                        warnings == null || warnings.Count == 0 ? string.Empty : "；待复核提示 " + warnings.Count + " 项");
                    return;
                }
                document.Editor.WriteMessage(
                    "\nCAD 表格已导出：{0}\n行列：{1} 行 × {2} 列{3}\n",
                    (string)result["filePath"] ?? string.Empty,
                    (int?)result["rowCount"] ?? 0,
                    (int?)result["columnCount"] ?? 0,
                    warnings == null || warnings.Count == 0 ? string.Empty : "；待复核提示 " + warnings.Count + " 项");
            }
            catch (System.Exception exception)
            {
                Application.ShowAlertDialog("CAD 表格操作失败：\r\n" + exception.Message);
            }
        }
    }
}
