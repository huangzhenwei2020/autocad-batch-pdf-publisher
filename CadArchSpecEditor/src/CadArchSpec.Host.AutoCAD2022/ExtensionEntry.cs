using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(CadArchSpec.Host.AutoCAD2022.ExtensionEntry))]
[assembly: CommandClass(typeof(CadArchSpec.Host.AutoCAD2022.Commands))]

namespace CadArchSpec.Host.AutoCAD2022
{
    public sealed class ExtensionEntry : IExtensionApplication
    {
        public void Initialize()
        {
            // Only the shared WebView2 environment is warmed up here. CE and the
            // architecture assistant still create independent windows, controls and
            // CoreWebView2 instances.
            EditorHostControl.WarmUpWebViewEnvironment();
        }

        public void Terminate()
        {
            HostPalette.Close();
        }
    }
}
