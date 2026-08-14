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
        private static DoorWindowElevationForm _doorWindowElevationForm;
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
                    if (form.SelectedAction == DrawingScaleAction.Selection) ApplyScaleToSelection(document, form.SourceScale, form.TargetScale, form.SyncTianzhengScale);
                }
            }
            catch (System.Exception exception) { try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "scale-manager.log"), DateTime.Now.ToString("O") + " " + exception + Environment.NewLine); } catch { } Application.ShowAlertDialog("比例管理失败：\r\n" + exception.Message); }
        }

        private static void ApplyScaleToSelection(Document document, int sourceScale, int targetScale, bool syncTianzhengScale)
        {
            var editor = document.Editor;
            var selection = editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n选择要修改为 1:" + targetScale + " 的对象：" });
            if (selection.Status != PromptStatus.OK) return;
            var changed = 0; var failed = 0; var tianzhengChanged = 0;
            // Freeze one de-duplicated selection scope. Every following stage
            // must work only on these ids; never enumerate model/layout space.
            var selectedIds = selection.Value.GetObjectIds()
                .Where(id => !id.IsNull && id.IsValid)
                .Distinct()
                .ToArray();
            if (selectedIds.Length == 0) return;
            var tianzhengIds = new System.Collections.Generic.List<Autodesk.AutoCAD.DatabaseServices.ObjectId>();
            var tianzhengDimensionIds = new System.Collections.Generic.List<Autodesk.AutoCAD.DatabaseServices.ObjectId>();
            var tianzhengTextIds = new System.Collections.Generic.List<Autodesk.AutoCAD.DatabaseServices.ObjectId>();
            var registeredFrames = new System.Collections.Generic.Dictionary<Autodesk.AutoCAD.DatabaseServices.ObjectId, BatchPdfPublisher.Models.FrameDefinition>();
            var frameBlocksToSynchronize = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var frameDefinitions = new PublishPlanStore().LoadFrames();
            var tianzhengSettings = TianzhengDimensionSettings.Load();
            var geometryChanged = 0;
            var hasStandardScalableObjects = false;
            var scaleTiming = System.Diagnostics.Stopwatch.StartNew();
            long recognitionMilliseconds = 0, objectUpdateMilliseconds = 0, geometryMilliseconds = 0;
            using (var progress = new ScaleProgressForm())
            {
                Application.ShowModelessDialog(progress);
                progress.ReportStage("正在同步天正当前比例……", 3);
                string tianzhengScaleError;
                if (syncTianzhengScale && TianzhengScaleService.IsLoaded() && !TianzhengScaleService.TrySetCurrentScale(targetScale, out tianzhengScaleError) && !string.IsNullOrWhiteSpace(tianzhengScaleError))
                {
                    try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "tianzheng-scale.log"), DateTime.Now.ToString("O") + " set current scale failed: " + tianzhengScaleError + Environment.NewLine); } catch { }
                }
                System.Collections.Generic.Dictionary<Autodesk.AutoCAD.DatabaseServices.ObjectId, TianzhengScaleService.DimensionGeometryTarget> dimensionGeometryPlan;
                using (var read = document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    progress.ReportStage("正在识别天正尺寸和轴号……", 8);
                    var originalAxisFreeEndpoints = TianzhengScaleService.CollectAxisFreeEndpoints(read, selectedIds);
                    var scanned = 0;
                    foreach (var id in selectedIds)
                    {
                        var value = read.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead, false);
                        var blockReference = value as Autodesk.AutoCAD.DatabaseServices.BlockReference;
                        var registeredFrame = blockReference == null ? null : RegisteredFrameScaleService.Match(blockReference, read, frameDefinitions);
                        if (registeredFrame != null)
                        {
                            registeredFrames[id] = registeredFrame;
                        }
                        else if (TianzhengScaleService.IsTianzhengObject(value))
                        {
                            if (TianzhengScaleService.IsTianzhengDimension(value)) { tianzhengIds.Add(id); tianzhengDimensionIds.Add(id); }
                            else if (TianzhengScaleService.IsAxisLabel(value)) tianzhengIds.Add(id);
                            if (TianzhengScaleService.IsTianzhengText(value)) tianzhengTextIds.Add(id);
                        }
                        else if (value is Autodesk.AutoCAD.DatabaseServices.Dimension ||
                                 value is Autodesk.AutoCAD.DatabaseServices.DBText ||
                                 value is Autodesk.AutoCAD.DatabaseServices.MText ||
                                 value is Autodesk.AutoCAD.DatabaseServices.AttributeReference ||
                                 value is Autodesk.AutoCAD.DatabaseServices.AttributeDefinition ||
                                 value is Autodesk.AutoCAD.DatabaseServices.BlockReference)
                            hasStandardScalableObjects = true;
                        progress.ReportRange("正在识别天正尺寸和轴号……", ++scanned, selectedIds.Length, 5, 25);
                    }
                    dimensionGeometryPlan = TianzhengScaleService.BuildDimensionGeometryPlan(read, tianzhengDimensionIds.ToArray(), targetScale, tianzhengSettings, originalAxisFreeEndpoints);
                }
                recognitionMilliseconds = scaleTiming.ElapsedMilliseconds;
                using (document.LockDocument())
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    progress.ReportStage("正在准备文字、标注和图层标准……", 25);
                    var autoLayers = AutoLayerSettings.Load();
                    DraftingStandardResources resources = null;
                    var dimensionStyle = Autodesk.AutoCAD.DatabaseServices.ObjectId.Null;
                    if (hasStandardScalableObjects)
                    {
                        var profile = DraftingStandardService.LoadProfile();
                        // Do not rewrite existing global styles here. A complex
                        // drawing can have thousands of references to one style;
                        // modifying that shared record causes a drawing-wide rebuild.
                        resources = DraftingStandardService.EnsureAll(document.Database, transaction, profile, false);
                        dimensionStyle = DraftingStandardService.EnsureDimensionStyleForScale(document.Database, transaction, targetScale, profile, resources, false);
                    }
                    var updated = 0;
                    foreach (var id in selectedIds)
                    {
                        try
                        {
                            var entity = transaction.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead, false) as Autodesk.AutoCAD.DatabaseServices.Entity;
                            if (entity == null) continue;
                            BatchPdfPublisher.Models.FrameDefinition registeredFrame;
                            if (registeredFrames.TryGetValue(id, out registeredFrame))
                            {
                                var frameReference = entity as Autodesk.AutoCAD.DatabaseServices.BlockReference;
                                if (RegisteredFrameScaleService.UpdateScaleAttribute(frameReference, transaction, registeredFrame, targetScale)) changed++;
                                if (!string.IsNullOrWhiteSpace(registeredFrame.BlockName)) frameBlocksToSynchronize.Add(registeredFrame.BlockName.Trim());
                                continue;
                            }
                            if (TianzhengScaleService.IsTianzhengObject(entity))
                            {
                                entity.UpgradeOpen();
                                if (TianzhengScaleService.Apply(entity, targetScale, tianzhengSettings)) { changed++; entity.RecordGraphicsModified(true); }
                                else failed++;
                                tianzhengChanged++;
                                continue;
                            }
                            entity.UpgradeOpen();
                            if (DrawingScaleService.ApplyStandardizedScale(document.Database, transaction, entity, sourceScale, targetScale, resources, dimensionStyle, autoLayers)) { changed++; entity.RecordGraphicsModified(true); }
                        }
                        catch { failed++; }
                        finally { progress.ReportRange("正在更新对象比例……", ++updated, selectedIds.Length, 30, 70); }
                    }
                    transaction.Commit();
                }
                objectUpdateMilliseconds = scaleTiming.ElapsedMilliseconds - recognitionMilliseconds;
                // Tianzheng rebuilds its grips when Scale is committed. Geometry must
                // therefore be adjusted in a second transaction; doing both in the
                // first transaction lets Tianzheng overwrite the new dimension rows.
                if (tianzhengSettings.ApplyDimensionGeometry && tianzhengDimensionIds.Count > 0)
                {
                    using (document.LockDocument())
                    using (var geometryTransaction = document.Database.TransactionManager.StartTransaction())
                    {
                        progress.ReportStage("正在调整天正尺寸界线……", 72);
                        var axisFreeEndpoints = TianzhengScaleService.CollectAxisFreeEndpoints(geometryTransaction, selectedIds);
                        var geometryIndex = 0;
                        foreach (var id in tianzhengDimensionIds)
                        {
                            try
                            {
                                var entity = geometryTransaction.GetObject(id, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite, false) as Autodesk.AutoCAD.DatabaseServices.Entity;
                                if (entity == null || !TianzhengScaleService.IsTianzhengDimension(entity)) continue;
                                TianzhengScaleService.DimensionGeometryTarget geometryTarget; if (!dimensionGeometryPlan.TryGetValue(id, out geometryTarget)) { failed++; continue; }
                                string geometryError;
                                if (TianzhengScaleService.ApplyCommittedDimensionGeometry(entity, geometryTarget, axisFreeEndpoints, out geometryError)) { geometryChanged++; entity.RecordGraphicsModified(true); }
                                else if (!string.IsNullOrEmpty(geometryError)) { failed++; try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "tianzheng-scale.log"), DateTime.Now.ToString("O") + " " + entity.Handle + " geometry failed: " + geometryError + Environment.NewLine); } catch { } }
                            }
                            catch (System.Exception geometryException) { failed++; try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "tianzheng-scale.log"), DateTime.Now.ToString("O") + " geometry exception: " + geometryException + Environment.NewLine); } catch { } }
                            finally { progress.ReportRange("正在调整天正尺寸界线……", ++geometryIndex, tianzhengDimensionIds.Count, 72, 94); }
                        }
                        geometryTransaction.Commit();
                    }
                    // Regen() regenerates every visible entity and makes runtime
                    // proportional to the entire drawing. Modified entities were
                    // invalidated above, so a screen update is sufficient here.
                    Autodesk.AutoCAD.ApplicationServices.Application.UpdateScreen();
                }
                geometryMilliseconds = scaleTiming.ElapsedMilliseconds - recognitionMilliseconds - objectUpdateMilliseconds;
                progress.ReportStage(tianzhengDimensionIds.Count > 0 ? "正在调用天正“尺寸自调”整理文字……" : "比例更新完成。", 100);
            }
            // Commands are queued in reverse execution order: the last queued
            // command starts first after WLSCALE returns to AutoCAD.
            QueueFrameAttributeSync(document, frameBlocksToSynchronize);
            if (tianzhengTextIds.Count > 0) TianzhengScaleService.QueueTextAutoAdjust(document, tianzhengTextIds);
            if (tianzhengDimensionIds.Count > 0) TianzhengScaleService.QueueDimensionAutoAdjust(document, tianzhengDimensionIds);
            try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "scale-manager.log"), DateTime.Now.ToString("O") + " selection=" + selectedIds.Length + " registeredFrames=" + registeredFrames.Count + " tianzheng=" + tianzhengIds.Count + " dimensions=" + tianzhengDimensionIds.Count + " recognitionMs=" + recognitionMilliseconds + " updateMs=" + objectUpdateMilliseconds + " geometryMs=" + geometryMilliseconds + " totalMs=" + scaleTiming.ElapsedMilliseconds + Environment.NewLine); } catch { }
            editor.WriteMessage("\n比例修改完成：" + changed + " 个对象已转换为 1:" + targetScale + (tianzhengChanged > 0 ? "，其中天正对象 " + tianzhengChanged + " 个、尺寸线已调整 " + geometryChanged + " 个" : string.Empty) + (failed > 0 ? "，" + failed + " 项更新失败（详见 tianzheng-scale.log）。" : "。"));
        }

        [CommandMethod("MCLM")]
        [CommandMethod("WLMCLM")]
        public void BatchDoorWindowElevations()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            if (!CadCompatibilityService.IsTianzhengHostLoaded())
            {
                Application.ShowAlertDialog("批量门窗立面需要在已加载天正建筑的 AutoCAD 中使用。\r\n\r\n请通过“万落建筑工具启动器”选择天正建筑 + 对应 CAD 版本后重新打开图纸。");
                return;
            }
            var picked = document.Editor.GetEntity(new PromptEntityOptions("\n请选择天正门窗表："));
            if (picked.Status != PromptStatus.OK) return;
            DoorWindowScheduleReadResult source;
            try
            {
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                    source = TianzhengDoorWindowService.Read(transaction.GetObject(picked.ObjectId, OpenMode.ForRead, false));
            }
            catch (System.Exception exception)
            {
                try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "door-window-elevation.log"), DateTime.Now.ToString("O") + " read: " + exception + Environment.NewLine); } catch { }
                Application.ShowAlertDialog("读取天正门窗表失败：\r\n" + exception.Message + "\r\n\r\n请确认所选对象是由天正“门窗表”命令生成的表格。可把诊断日志发给我继续适配当前天正版本。");
                return;
            }
            try
            {
                if (_doorWindowElevationForm != null && !_doorWindowElevationForm.IsDisposed) _doorWindowElevationForm.Close();
                _doorWindowElevationForm = new DoorWindowElevationForm(document, source);
                _doorWindowElevationForm.FormClosed += (sender, args) => _doorWindowElevationForm = null;
                Application.ShowModelessDialog(_doorWindowElevationForm);
            }
            catch (System.Exception exception)
            {
                try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "door-window-elevation.log"), DateTime.Now.ToString("O") + " window: " + exception + Environment.NewLine); } catch { }
                Application.ShowAlertDialog("门窗表已经读取，但打开门窗立面窗口失败：\r\n" + exception.Message + "\r\n\r\n详细信息已写入 door-window-elevation.log。");
            }
        }

        private static void QueueFrameAttributeSync(Document document, System.Collections.Generic.IEnumerable<string> blockNames)
        {
            if (document == null || blockNames == null) return;
            var commands = new System.Text.StringBuilder();
            foreach (var blockName in blockNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Attribute synchronization is intentionally queued after the
                // transaction. ATTSYNC preserves instance values while restoring
                // positions, visibility and newly changed attribute definitions.
                commands.Append("_.-ATTSYNC _Name \"").Append(blockName.Replace("\"", string.Empty)).Append("\" ");
            }
            if (commands.Length == 0) return;
            document.SendStringToExecute(commands.ToString(), false, false, false);
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
