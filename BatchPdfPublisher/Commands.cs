using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using BatchPdfPublisher.Services;
using System;
using System.IO;
using System.Collections.Generic;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Views;

namespace BatchPdfPublisher
{
    public sealed class Commands : IExtensionApplication
    {
        private static PublisherForm _publisherForm;
        private static IList<SheetItem> _catalogSheets;
        private static CatalogSettings _catalogSettings;
        private static Action _catalogDone;
        private static readonly string DiagnosticLog = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher.trace.log");
        public void Initialize()
        {
            try { Application.SetSystemVariable("RIBBONSTATE", 1); } catch { }
            RibbonService.InstallWhenReady();
        }
        public void Terminate() { RibbonService.Remove(); }

        [CommandMethod("BPP")]
        public void BppCommand() => OpenPublisher();

        [CommandMethod("TKK")]
        public void CreateFrameCommand()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Application.ShowModelessDialog(new FrameCreationForm(document, null));
        }

        [CommandMethod("ML1")]
        public void InsertCatalogShortcut() => ShowPublisher(form => form.OpenCatalogInsertForCommand(), false);

        [CommandMethod("BPPUI")]
        public void RefreshPluginUi()
        {
            RibbonService.InstallWhenReady();
            Application.ShowAlertDialog("BPP Ribbon 已请求刷新。若仍未显示，请执行 RIBBON 命令后再执行 BPPUI。");
        }

        [CommandMethod("BPPUBLISH")]
        public void OpenPublisher()
        {
            Trace("BPPUBLISH entered");
            ShowPublisher(null);
        }

        // Versioned diagnostic entry point lets an updated assembly be tested
        // in a CAD process that still has an older BPPUBLISH command loaded.
        [CommandMethod("BPPUBLISH063")]
        public void OpenPublisher063() => ShowPublisher(null);

        [CommandMethod("BPPUBLISH064")]
        public void OpenPublisher064() => ShowPublisher(null);

        [CommandMethod("BPPUBLISH065")]
        public void OpenPublisher065() => ShowPublisher(null);

        [CommandMethod("BPPSCAN")]
        public void ScanFromRibbon() => ShowPublisher(form => form.ScanDrawing());

        [CommandMethod("BPPMAKEPDF")]
        public void PublishFromRibbon() => ShowPublisher(form => form.PublishPdf());

        [CommandMethod("BPPATTR")]
        [CommandMethod("SBB")]
        public void BatchAttributes()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) { Application.ShowAlertDialog("请先打开一个 CAD 文件。"); return; }
            Application.ShowModalDialog(new AttributeBatchForm(document));
        }

        [CommandMethod("BPPSELFTEST")]
        public void RunSelfTest()
        {
            var failures = AttributeBatchService.RunRegressionChecks();
            var message = failures.Count == 0
                ? "批量属性算法自检通过。"
                : "批量属性算法自检失败：\n" + string.Join("\n", failures);
            var document = Application.DocumentManager.MdiActiveDocument;
            document?.Editor.WriteMessage("\n" + message + "\n");
            Application.ShowAlertDialog(message);
        }

        public static void StartCatalogInsert(IList<SheetItem> sheets, CatalogSettings settings, Action done)
        {
            _catalogSheets = sheets; _catalogSettings = settings; _catalogDone = done;
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) throw new InvalidOperationException("没有可用的 CAD 图纸。请先打开图纸后再插入目录。");
            document.SendStringToExecute("BPPINSERTCATALOG ", true, false, false);
        }

        [CommandMethod("BPPINSERTCATALOG")]
        public void InsertCatalogCommand()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var done = _catalogDone;
            try
            {
                if (document == null || _catalogSheets == null || _catalogSettings == null) return;
                document.Editor.WriteMessage("\n批量打印插件：请指定目录左上角插入点。\n");
                if (CatalogInsertionService.Insert(document, _catalogSheets, _catalogSettings)) done?.Invoke();
                else Application.ShowAlertDialog("未插入目录，可能取消了插入点选择。");
            }
            catch (System.Exception ex)
            {
                Trace("BPPINSERTCATALOG failed: " + ex);
                Application.ShowAlertDialog("插入目录失败：\n" + ex.Message);
            }
            finally
            {
                _catalogSheets = null; _catalogSettings = null; _catalogDone = null;
            }
        }

        private static void ShowPublisher(Action<PublisherForm> afterShow, bool display = true)
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
                    if (display && !_publisherForm.Visible)
                    {
                        Application.ShowModelessDialog(_publisherForm);
                    }
                    else if (display)
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
