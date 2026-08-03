using Autodesk.AutoCAD.Runtime;

namespace CadArchSpec.Host.AutoCAD2022
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
