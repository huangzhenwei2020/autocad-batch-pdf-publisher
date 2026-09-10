using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace BatchPdfPublisher.Services
{
    /// <summary>Connects successful native AutoCAD saves to the sync queue.</summary>
    public static class CadSaveCloudSyncService
    {
        private static readonly HashSet<Document> Attached = new HashSet<Document>();
        private static readonly HashSet<Document> PendingSnapshots = new HashSet<Document>();
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            var manager = Application.DocumentManager;
            manager.DocumentCreated += OnDocumentCreated;
            manager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            Application.Idle += OnIdle;
            foreach (Document document in manager) Attach(document);
            CloudSyncPendingFileService.RegisterOpenPathProbe(IsDrawingOpen);
        }

        public static void Remove()
        {
            if (!_installed) return;
            _installed = false;
            var manager = Application.DocumentManager;
            manager.DocumentCreated -= OnDocumentCreated;
            manager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            Application.Idle -= OnIdle;
            foreach (var document in new List<Document>(Attached)) Detach(document);
            lock (PendingSnapshots) PendingSnapshots.Clear();
            CloudSyncPendingFileService.ClearOpenPathProbe();
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs args)
        {
            Attach(args.Document);
        }

        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
        {
            lock (PendingSnapshots) PendingSnapshots.Remove(args.Document);
            Detach(args.Document);
            CloudSyncCoordinator.RequestSynchronization(false);
        }

        private static void Attach(Document document)
        {
            if (document == null || !Attached.Add(document)) return;
            document.CommandEnded += OnCommandEnded;
        }

        private static void Detach(Document document)
        {
            if (document == null || !Attached.Remove(document)) return;
            try { document.CommandEnded -= OnCommandEnded; } catch { }
        }

        private static void OnCommandEnded(object sender, CommandEventArgs args)
        {
            var command = (args == null ? string.Empty : args.GlobalCommandName) ?? string.Empty;
            command = command.Trim().TrimStart('_', '.').ToUpperInvariant();
            if (command == "SAVE" || command == "QSAVE" || command == "SAVEAS")
            {
                var document = sender as Document;
                if (document != null) lock (PendingSnapshots) PendingSnapshots.Add(document);
            }
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(Convert.ToString(Application.GetSystemVariable("CMDNAMES")))) return;
            }
            catch { return; }
            Document[] documents;
            lock (PendingSnapshots)
            {
                documents = new List<Document>(PendingSnapshots).ToArray();
                PendingSnapshots.Clear();
            }
            var attempted = documents.Length > 0;
            foreach (var document in documents)
            {
                if (document == null || document.Database == null) continue;
                var source = string.IsNullOrWhiteSpace(document.Database.Filename) ? document.Name : document.Database.Filename;
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) continue;
                string temporary = null;
                try
                {
                    temporary = CloudSyncSavedDrawingSnapshotStore.TemporaryPath(source);
                    using (document.LockDocument())
                    using (var snapshot = document.Database.Wblock())
                        snapshot.SaveAs(temporary, DwgVersion.Current);
                    CloudSyncSavedDrawingSnapshotStore.Commit(source, temporary);
                }
                catch
                {
                    if (!string.IsNullOrWhiteSpace(temporary)) CloudSyncSavedDrawingSnapshotStore.TryDelete(temporary);
                }
            }
            if (attempted) CloudSyncCoordinator.RequestSynchronization(false);
        }

        private static bool IsDrawingOpen(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string target;
            try { target = Path.GetFullPath(path); }
            catch { target = path.Trim(); }
            try
            {
                foreach (Document document in Application.DocumentManager)
                {
                    if (document == null || document.Database == null) continue;
                    var candidate = string.IsNullOrWhiteSpace(document.Database.Filename) ? document.Name : document.Database.Filename;
                    try { candidate = Path.GetFullPath(candidate); } catch { }
                    if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
