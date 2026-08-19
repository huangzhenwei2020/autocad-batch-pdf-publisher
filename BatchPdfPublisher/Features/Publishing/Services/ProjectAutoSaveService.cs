using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    /// <summary>Creates openable DWG snapshots for the active engineering project.</summary>
    public static class ProjectAutoSaveService
    {
        private static readonly PublishPlanStore Store = new PublishPlanStore();
        private static EventHandler _idleHandler;
        private static DateTime _nextSaveUtc = DateTime.MaxValue;
        private static bool _saving;

        public static void Install()
        {
            if (_idleHandler != null) return;
            _idleHandler = OnIdle;
            Autodesk.AutoCAD.ApplicationServices.Application.Idle += _idleHandler;
            Reschedule();
        }

        public static void Remove()
        {
            if (_idleHandler != null)
                Autodesk.AutoCAD.ApplicationServices.Application.Idle -= _idleHandler;
            _idleHandler = null;
            _nextSaveUtc = DateTime.MaxValue;
        }

        public static int CurrentCadMinutes()
        {
            try { return Math.Max(0, Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("SAVETIME"))); }
            catch { return 10; }
        }

        public static void ApplyInterval(int minutes)
        {
            minutes = Math.Max(0, Math.Min(600, minutes));
            try { Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("SAVETIME", minutes); } catch { }
            Reschedule(minutes);
        }

        public static void Reschedule()
        {
            var project = Store.GetActiveProject();
            var minutes = project != null && project.AutoSaveMinutes.HasValue ? project.AutoSaveMinutes.Value : CurrentCadMinutes();
            Reschedule(minutes);
        }

        private static void Reschedule(int minutes)
        {
            _nextSaveUtc = minutes <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(minutes);
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            if (_saving || DateTime.UtcNow < _nextSaveUtc) return;
            if (!CanSaveAtIdle()) return;
            var project = Store.GetActiveProject();
            var minutes = project != null && project.AutoSaveMinutes.HasValue ? project.AutoSaveMinutes.Value : CurrentCadMinutes();
            Reschedule(minutes);
            if (project == null || minutes <= 0) return;
            List<string> failures;
            var saved = SaveNow(project, out failures);
            try
            {
                var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (document != null && (saved > 0 || failures.Count > 0))
                    document.Editor.WriteMessage("\n项目自动保存：已生成 " + saved + " 个 DWG 备份"
                        + (failures.Count == 0 ? "。" : "，失败 " + failures.Count + " 个。") + "\n");
            }
            catch { }
        }

        public static int SaveNow(ProjectProfile project, out List<string> failures)
        {
            failures = new List<string>();
            if (project == null || _saving) return 0;
            _saving = true;
            var saved = 0;
            try
            {
                var folder = Path.Combine(Store.GetProjectFolder(project), "自动保存");
                Directory.CreateDirectory(folder);
                var projectPaths = new HashSet<string>(
                    (project.CadFiles ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizePath),
                    StringComparer.OrdinalIgnoreCase);
                var documents = new List<Document>();
                foreach (Document document in Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager)
                    if (document != null && document.Database != null) documents.Add(document);
                if (projectPaths.Count == 0)
                {
                    var active = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    documents = active == null ? new List<Document>() : new List<Document> { active };
                }

                foreach (var document in documents)
                {
                    var sourcePath = SafePath(document);
                    if (projectPaths.Count > 0 && !projectPaths.Contains(NormalizePath(sourcePath))) continue;
                    var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
                    if (string.IsNullOrWhiteSpace(sourceName)) sourceName = Path.GetFileNameWithoutExtension(document.Name);
                    if (string.IsNullOrWhiteSpace(sourceName)) sourceName = "未命名图纸";
                    var destination = Path.Combine(folder, SafeFileName(sourceName) + "_自动保存.dwg");
                    var temporary = Path.Combine(folder, "." + SafeFileName(sourceName) + "_" + Guid.NewGuid().ToString("N") + ".tmp.dwg");
                    try
                    {
                        using (document.LockDocument())
                        using (var snapshot = document.Database.Wblock())
                            snapshot.SaveAs(temporary, DwgVersion.Current);
                        if (File.Exists(destination))
                        {
                            try { File.Replace(temporary, destination, null, true); }
                            catch
                            {
                                File.Copy(temporary, destination, true);
                                File.Delete(temporary);
                            }
                        }
                        else File.Move(temporary, destination);
                        saved++;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(sourceName + "：" + exception.Message);
                    }
                    finally
                    {
                        try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                    }
                }
            }
            finally { _saving = false; }
            return saved;
        }

        private static bool CanSaveAtIdle()
        {
            try
            {
                if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting) return false;
                var commandNames = Convert.ToString(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDNAMES"));
                if (!string.IsNullOrWhiteSpace(commandNames)) return false;
            }
            catch { }
            return true;
        }

        private static string SafePath(Document document)
        {
            try { return string.IsNullOrWhiteSpace(document.Database.Filename) ? document.Name : document.Database.Filename; }
            catch { return document == null ? string.Empty : document.Name; }
        }

        private static string NormalizePath(string path)
        {
            try { return Path.GetFullPath((path ?? string.Empty).Trim()); }
            catch { return (path ?? string.Empty).Trim(); }
        }

        private static string SafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string((value ?? string.Empty).Select(x => invalid.Contains(x) ? '_' : x).ToArray());
        }
    }
}
