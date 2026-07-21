using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.Views;
using System;
using System.IO;

namespace BatchPdfPublisher
{
    public sealed class Commands : IExtensionApplication
    {
        private static PublisherForm _publisherForm;
        private static readonly string DiagnosticLog = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher.trace.log");
        public void Initialize() { RibbonService.InstallWhenReady(); }
        public void Terminate() { RibbonService.Remove(); }

        [CommandMethod("BPPUBLISH")]
        public void OpenPublisher()
        {
            Trace("BPPUBLISH entered");
            ShowPublisher(null);
        }

        [CommandMethod("BPPSCAN")]
        public void ScanFromRibbon() => ShowPublisher(form => form.ScanDrawing());

        [CommandMethod("BPPMAKEPDF")]
        public void PublishFromRibbon() => ShowPublisher(form => form.PublishPdf());

        private static void ShowPublisher(Action<PublisherForm> afterShow)
        {
            // Build the modeless UI only after AutoCAD has returned to its
            // idle message loop.
            EventHandler handler = null;
            handler = (sender, args) =>
            {
                Trace("Idle callback entered");
                Application.Idle -= handler;
                try
                {
                    if (_publisherForm == null || _publisherForm.IsDisposed)
                    {
                        Trace("Creating modeless WinForms window");
                        _publisherForm = new PublisherForm();
                        _publisherForm.FormClosed += (closedSender, closedArgs) => _publisherForm = null;
                        Trace("Modeless WinForms window created");
                    }
                    Trace("Showing modeless WinForms window");
                    if (!_publisherForm.Visible)
                    {
                        Application.ShowModelessDialog(_publisherForm);
                    }
                    else
                        _publisherForm.Activate();
                    afterShow?.Invoke(_publisherForm);
                    Trace("Modeless WinForms window visible");
                }
                catch (System.Exception exception)
                {
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "BatchPdfPublisher.error.log"), exception.ToString()); } catch { }
                    Application.ShowAlertDialog("批量打印面板加载失败：" + exception.Message);
                }
            };
            Application.Idle += handler;
            Trace("Idle callback registered");
        }

        private static void Trace(string message)
        {
            try { File.AppendAllText(DiagnosticLog, DateTime.Now.ToString("O") + " " + message + Environment.NewLine); } catch { }
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
