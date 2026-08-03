using Autodesk.AutoCAD.Runtime;

namespace CadArchSpec.Host.AutoCAD2026
{
    public sealed class Commands
    {
        [CommandMethod("JZSM", CommandFlags.Session)]
        public void ShowEditor()
        {
            HostPalette.Show();
        }
    }
}
