using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.Views;

namespace BatchPdfPublisher
{
    public sealed class Commands : IExtensionApplication
    {
        private static PaletteSet _palette;
        public void Initialize() { }
        public void Terminate() { }

        [CommandMethod("BPPUBLISH")]
        public void OpenPublisher()
        {
            if (_palette == null)
            {
                _palette = new PaletteSet("批量 PDF 发布")
                {
                    Style = PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowPropertiesMenu,
                    MinimumSize = new System.Drawing.Size(760, 460)
                };
                _palette.AddVisual("发布中心", new PublisherControl());
            }
            _palette.Visible = true;
        }

        [CommandMethod("BPPICKFRAME")]
        public void PickFrameForRegistration()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var result = document.Editor.GetEntity(new PromptEntityOptions("\n请选择要登记的图框图块: "));
            if (result.Status != PromptStatus.OK) return;
            new FrameRegistrationService().Register(document, result.ObjectId);
        }
    }
}
