using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(CadArchSpec.Host.AutoCAD2026.ExtensionEntry))]
[assembly: CommandClass(typeof(CadArchSpec.Host.AutoCAD2026.Commands))]

namespace CadArchSpec.Host.AutoCAD2026
{
    public sealed class ExtensionEntry : IExtensionApplication
    {
        public void Initialize()
        {
        }

        public void Terminate()
        {
            HostPalette.Close();
        }
    }
}
