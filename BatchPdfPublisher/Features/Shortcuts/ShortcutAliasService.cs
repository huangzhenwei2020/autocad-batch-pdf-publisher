using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Text;

namespace BatchPdfPublisher.Services
{
    /// <summary>用 AutoLISP c:别名包装固定命令，使快捷键无需修改 PGP 或重启 CAD 即可生效。</summary>
    public static class ShortcutAliasService
    {
        private static string _lastSignature;
        private static Document _lastDocument;
        private static EventHandler _idleHandler;
        private static DateTime _lastSettingsWriteUtc;
        private static readonly HashSet<string> InstalledAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void InstallWhenReady()
        {
            if (_idleHandler == null)
            {
                _idleHandler = (sender, args) => Install();
                Application.Idle += _idleHandler;
            }
            Install();
        }

        public static void Remove()
        {
            if (_idleHandler != null) { Application.Idle -= _idleHandler; _idleHandler = null; }
            _lastDocument = null; _lastSignature = null; InstalledAliases.Clear();
        }

        public static void Install(Document document = null, bool force = false)
        {
            document = document ?? Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DateTime settingsWriteUtc;
            try { settingsWriteUtc = System.IO.File.Exists(ShortcutSettingsService.SettingsPath) ? System.IO.File.GetLastWriteTimeUtc(ShortcutSettingsService.SettingsPath) : DateTime.MinValue; }
            catch { settingsWriteUtc = DateTime.MinValue; }
            if (!force && ReferenceEquals(document, _lastDocument) && settingsWriteUtc == _lastSettingsWriteUtc) return;
            var shortcuts = ShortcutSettingsService.Load();
            var signature = BuildSignature(shortcuts);
            if (!force && ReferenceEquals(document, _lastDocument) && string.Equals(signature, _lastSignature, StringComparison.Ordinal)) return;
            var lisp = new StringBuilder();
            foreach (var alias in InstalledAliases) lisp.Append("(defun c:").Append(alias).Append(" () (princ)) ");
            InstalledAliases.Clear();
            foreach (var feature in FeatureRegistry.All)
            {
                string shortcut;
                if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
                shortcut = ShortcutSettingsService.Normalize(shortcut);
                if (!ShortcutSettingsService.IsValid(shortcut)) continue;
                // 快捷键与固定内部命令相同时直接使用 .NET 命令，避免生成自调用别名。
                if (string.Equals(shortcut, feature.Command, StringComparison.OrdinalIgnoreCase)) continue;
                // 建筑说明、楼梯等外置组件已经注册 JZSM/LTDY；覆盖它们会让包装命令递归调用自己。
                if (!string.IsNullOrWhiteSpace(feature.NativeCommand) && string.Equals(shortcut, feature.NativeCommand, StringComparison.OrdinalIgnoreCase)) continue;
                lisp.Append("(defun c:").Append(shortcut).Append(" () (command \"")
                    .Append(feature.Command.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\") (princ)) ");
                InstalledAliases.Add(shortcut);
            }
            lisp.Append("(princ) ");
            document.SendStringToExecute(lisp.ToString(), true, false, false);
            _lastSignature = signature;
            _lastDocument = document;
            _lastSettingsWriteUtc = settingsWriteUtc;
        }

        public static void Refresh()
        {
            _lastSignature = null;
            _lastDocument = null;
            _lastSettingsWriteUtc = DateTime.MinValue;
            Install(null, true);
            RibbonService.RefreshNow();
            MenuService.RefreshNow();
        }

        private static string BuildSignature(IDictionary<string, string> values)
        {
            var text = new StringBuilder();
            foreach (var feature in FeatureRegistry.All)
            {
                string shortcut; values.TryGetValue(feature.Id, out shortcut);
                text.Append(feature.Id).Append('=').Append(shortcut).Append(';');
            }
            return text.ToString();
        }
    }
}
