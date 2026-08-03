using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(CadArchSpec.Host.AutoCAD2022.ExtensionEntry))]
[assembly: CommandClass(typeof(CadArchSpec.Host.AutoCAD2022.Commands))]

namespace CadArchSpec.Host.AutoCAD2022
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
