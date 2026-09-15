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
            try
            {
                // Build the initial payload while the CE command still owns the
                // AutoCAD command context. Re-entering that context from the
                // modeless window can remain pending after the command returns.
                HostPalette.ShowCadTableEditor(CadTableExchange.CreateStandaloneEditorPayload());
            }
            catch (System.Exception exception)
            {
                Application.ShowAlertDialog("CAD 表格操作失败：\r\n" + exception.Message);
            }
        }
    }
}
