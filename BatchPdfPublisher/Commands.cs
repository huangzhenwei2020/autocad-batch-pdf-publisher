using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Services;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Views;

namespace BatchPdfPublisher
{
    public sealed class Commands : IExtensionApplication
    {
        private static PublisherForm _publisherForm;
        private static TianzhengRoomRenameForm _roomRenameForm;
        private static IList<SheetItem> _catalogSheets;
        private static CatalogSettings _catalogSettings;
        private static Action _catalogDone;
        private static readonly string DiagnosticLog = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher.trace.log");
        public void Initialize()
        {
            WriteStartupReceipt("Initialize");
            try { Application.SetSystemVariable("RIBBONSTATE", 1); } catch { }
            RibbonService.InstallWhenReady();
            MenuService.InstallWhenReady();
            ProjectAutoSaveService.Install();
        }
        public void Terminate() { ProjectAutoSaveService.Remove(); RibbonService.Remove(); }

        [CommandMethod("BPP")]
        public void BppCommand() => OpenPublisher();

        [CommandMethod("WLJZSM", CommandFlags.Session)]
        public void OpenArchitectureAssistant()
        {
            var loaded = System.AppDomain.CurrentDomain.GetAssemblies().Any(x =>
                x.GetName().Name.StartsWith("CadArchSpec.Host.AutoCAD", System.StringComparison.OrdinalIgnoreCase));
            if (!loaded)
            {
                Application.ShowAlertDialog("建筑设计说明助手尚未加载。\r\n\r\n请使用最新版“万落建筑工具启动器”安装完整组件。建筑说明功能当前支持 AutoCAD 2021–2026；AutoCAD 2014 仍可使用批量打印、图框和目录功能。");
                return;
            }
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document != null) document.SendStringToExecute("JZSM ", true, false, false);
        }

        [CommandMethod("WLLTDY", CommandFlags.Session)]
        public void OpenStairDetailAssistant()
        {
            var loaded = System.AppDomain.CurrentDomain.GetAssemblies().Any(x =>
                x.GetName().Name.StartsWith("WL.Stair.Cad", System.StringComparison.OrdinalIgnoreCase));
            if (!loaded)
            {
                Application.ShowAlertDialog("一键楼梯大样组件尚未加载。\r\n\r\n请使用最新版“万落建筑工具启动器”安装完整组件。该功能当前支持 AutoCAD 2021–2026；旧版 CAD 仍可使用批量打印、图框和目录功能。");
                return;
            }
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document != null) document.SendStringToExecute("LTDY ", true, false, false);
        }

        [CommandMethod("TKK")]
        public void CreateFrameCommand()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Application.ShowModelessDialog(new FrameCreationForm(document, null));
        }

        [CommandMethod("FJGM")]
        [CommandMethod("WLROOMNAME")]
        public void BatchRenameTianzhengRooms()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var editor = document.Editor;
            try
            {
                var pickOptions = new PromptEntityOptions("\n请选择一个天正房间作为匹配样板：");
                var picked = editor.GetEntity(pickOptions);
                if (picked.Status != PromptStatus.OK) return;

                TianzhengRoomInfo sample;
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var selectedObject = transaction.GetObject(picked.ObjectId, OpenMode.ForRead, false);
                    if (!TianzhengRoomService.IsRoom(selectedObject))
                    {
                        Application.ShowAlertDialog("所选对象不是原生天正房间。\r\n\r\n请点选由天正“房间面积”功能创建的房间对象；已炸开或导出为普通文字的对象暂不支持。");
                        return;
                    }
                    sample = TianzhengRoomService.Read(selectedObject);
                }

                if (_roomRenameForm != null && !_roomRenameForm.IsDisposed) _roomRenameForm.Close();
                _roomRenameForm = new TianzhengRoomRenameForm(document, sample);
                _roomRenameForm.FormClosed += (sender, args) => _roomRenameForm = null;
                Application.ShowModelessDialog(_roomRenameForm);
            }
            catch (System.Exception exception)
            {
                try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "tianzheng-room.log"), DateTime.Now.ToString("O") + " " + exception + Environment.NewLine); } catch { }
                Application.ShowAlertDialog("批量修改房间名称失败：\r\n" + exception.Message + "\r\n\r\n请确认当前使用天正打开的是包含原生房间对象的图纸。");
            }
        }

        [CommandMethod("ML1")]
        public void InsertCatalogShortcut() => ShowPublisher(form => form.OpenCatalogInsertForCommand(), false);

        [CommandMethod("BZS")]
        [CommandMethod("WLSTANDARDS")]
        public void InitializeDraftingStandards()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try
            {
                Application.ShowModalDialog(new DraftingStandardForm(document));
            }
            catch (System.Exception exception)
            {
                Application.ShowAlertDialog("初始化制图标准失败：\r\n" + exception.Message);
            }
        }

        [CommandMethod("BZSAPPLY")]
        public void ApplyDraftingStandards()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try
            {
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var profile = DraftingStandardService.LoadProfile();
                    DraftingStandardService.ApplyConfiguredResources(document.Database, transaction, profile, profile.UpdateExisting);
                    transaction.Commit();
                    document.Editor.WriteMessage("\n已按制图标注设置创建勾选的图层、文字样式、标注样式和引线样式。\n");
                }
            }
            catch (System.Exception exception) { Application.ShowAlertDialog("应用制图标准失败：\r\n" + exception.Message); }
        }

        [CommandMethod("BZSINITARROWLIB")]
        public void InitializeArrowLibrary()
        {
            try
            {
                var path = DraftingStandardService.ArrowLibraryPath;
                if (File.Exists(path)) { Application.ShowAlertDialog("箭头图块库已经存在：\r\n" + path); return; }
                DraftingStandardService.CreateDefaultArrowLibrary(path);
                Application.ShowAlertDialog("已创建默认箭头图块库：\r\n" + path);
            }
            catch (System.Exception exception) { Application.ShowAlertDialog("创建箭头图块库失败：\r\n" + exception.Message); }
        }

        [CommandMethod("BL1")]
        [CommandMethod("WLSCALE")]
        public void ManageDrawingScale()
        {
            try
            {
                var document = Application.DocumentManager.MdiActiveDocument;
                if (document == null) return;
                using (var form = new DrawingScaleForm(document))
                {
                    if (Application.ShowModalDialog(form) != System.Windows.Forms.DialogResult.OK) return;
                    if (form.SelectedAction == DrawingScaleAction.Selection) ApplyScaleToSelection(document, form.TargetScale);
                }
            }
            catch (System.Exception exception) { try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "scale-manager.log"), DateTime.Now.ToString("O") + " " + exception + Environment.NewLine); } catch { } Application.ShowAlertDialog("比例管理失败：\r\n" + exception.Message); }
        }

        private static void ApplyScaleToSelection(Document document, int targetScale)
        {
            var editor = document.Editor;
            var selection = editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n选择要修改为 1:" + targetScale + " 的对象：" });
            if (selection.Status != PromptStatus.OK) return;
            var changed = 0; var failed = 0;
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var profile = DraftingStandardService.LoadProfile();
                var resources = DraftingStandardService.EnsureAll(document.Database, transaction, profile, profile.UpdateExisting);
                var dimensionStyle = DraftingStandardService.EnsureDimensionStyleForScale(document.Database, transaction, targetScale);
                var autoLayers = AutoLayerSettings.Load();
                foreach (var id in selection.Value.GetObjectIds())
                {
                    try { var entity = transaction.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite, false) as Autodesk.AutoCAD.DatabaseServices.Entity; if (entity == null) continue; if (DrawingScaleService.ApplyStandardizedScale(document.Database, transaction, entity, targetScale, resources, dimensionStyle, autoLayers)) changed++; }
                    catch { failed++; }
                }
                transaction.Commit();
            }
            editor.WriteMessage("\n比例修改完成：" + changed + " 个对象已转换为 1:" + targetScale + (failed > 0 ? "，" + failed + " 个对象不支持缩放。" : "。"));
        }


        [CommandMethod("BPPUI")]
        public void RefreshPluginUi()
        {
            var ribbonReady = RibbonService.RefreshNow();
            MenuService.InstallWhenReady();
            WriteStartupReceipt("BPPUI");
            Application.ShowAlertDialog(ribbonReady
                ? "“万落建筑工具”Ribbon 已加载。"
                : "当前工作空间尚未创建 Ribbon。请先执行 RIBBON 命令，再执行 BPPUI。");
        }

        [CommandMethod("BPPSTARTUP", CommandFlags.Session)]
        public void CompleteStartup()
        {
            RibbonService.RefreshNow();
            MenuService.InstallWhenReady();
            WriteStartupReceipt("BPPSTARTUP");
        }

        private static void WriteStartupReceipt(string stage)
        {
            try
            {
                var assembly = typeof(Commands).Assembly.Location;
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "WanluoArchitectureTools.loaded.log"),
                    DateTime.Now.ToString("O") + " | " + stage + " | PID=" + System.Diagnostics.Process.GetCurrentProcess().Id + " | " + assembly + Environment.NewLine);
            }
            catch { }
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

        [CommandMethod("BPPATTDEF")]
        [CommandMethod("BPA")]
        public void EditAttributeDefinitions()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) { Application.ShowAlertDialog("请先打开一个 CAD 文件。"); return; }
            Application.ShowModalDialog(new AttributeDefinitionEditorForm(document));
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
            var inserted = false;
            try
            {
                if (document == null || _catalogSheets == null || _catalogSettings == null) return;
                document.Editor.WriteMessage("\n批量打印插件：请指定目录左上角插入点。\n");
                inserted = CatalogInsertionService.Insert(document, _catalogSheets, _catalogSettings);
            }
            catch (System.Exception ex)
            {
                Trace("BPPINSERTCATALOG failed: " + ex);
                Application.ShowAlertDialog("插入目录失败：\n" + ex.Message);
                return;
            }
            finally
            {
                _catalogSheets = null; _catalogSettings = null; _catalogDone = null;
            }

            if (!inserted)
            {
                Application.ShowAlertDialog("未插入目录，可能取消了插入点选择。");
                return;
            }

            // The drawing operation has already completed at this point. A modeless
            // publisher window may have been closed while the insertion point was
            // being selected, so its completion callback must not turn a successful
            // insertion into a misleading "插入目录失败" message.
            try { done?.Invoke(); }
            catch (System.Exception ex) { Trace("Catalog inserted; nonessential publisher refresh was skipped: " + ex); }
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
