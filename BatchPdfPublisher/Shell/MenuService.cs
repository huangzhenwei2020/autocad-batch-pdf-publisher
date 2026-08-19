using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace BatchPdfPublisher.Services
{
    /// <summary>Creates a small pull-down menu through AutoCAD's command-driven MNU loader.</summary>
    public static class MenuService
    {
        private static EventHandler _idle;
        private static bool _mnuRequested;
        private static int _installAttempts;

        public static void InstallWhenReady()
        {
            if (_idle != null) return;
            _idle = (s, e) =>
            {
                if (!_mnuRequested && _installAttempts < 5) Install();
            };
            Application.Idle += _idle;
            Install();
        }

        public static void Install()
        {
            _installAttempts++;
            // ActiveX menu collections are version-sensitive and throw
            // DISP_E_TYPEMISMATCH on several AutoCAD/T20 builds. The command
            // driven MNU loader below is the stable classic-menu path.
            LoadPartialMenu();
        }

        public static bool RefreshNow()
        {
            _mnuRequested = false;
            _installAttempts = 0;
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document != null)
                document.SendStringToExecute("_.-MENUUNLOAD\nBPP\n", true, false, false);
            LoadPartialMenu();
            return _mnuRequested;
        }

        public static void Remove()
        {
            if (_idle != null) { Application.Idle -= _idle; _idle = null; }
        }

        private static void LoadPartialMenu()
        {
            if (_mnuRequested) return;
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BPP_批量打印.mnu");
                var shortcuts = ShortcutSettingsService.Load();
                var builder = new StringBuilder("***MENUGROUP=BPP\r\n***POP16\r\n万落建筑工具\r\n");
                foreach (var feature in FeatureRegistry.All)
                {
                    string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
                    builder.Append('[').Append(feature.Name).Append("（").Append(shortcut).Append("）]^C^C_").Append(feature.Command).Append(" \r\n");
                }
                var content = builder.ToString();
                // MNU is an ANSI file; AutoCAD on Chinese installations expects the
                // system GBK code page rather than UTF-8/default .NET encoding.
                System.IO.File.WriteAllText(path, content, System.Text.Encoding.GetEncoding(936));
                var document = Application.DocumentManager.MdiActiveDocument;
                if (document == null) return;
                var escaped = path.Replace("\\", "/").Replace("\"", "\\\"");
                // -MENULOAD is the command-line variant.  Supplying the menu group
                // explicitly is important: AutoCAD otherwise leaves the command at
                // the "menu group name" prompt and the following menucmd expression
                // is never executed (which is why the Ribbon appeared but the classic
                // menu did not).
                var macro = "_.-MENULOAD\n\"" + escaped + "\"\nBPP\n(menucmd \"P16=+BPP.POP16\")\n";
                document.SendStringToExecute(macro, true, false, false);
                _mnuRequested = true;
            }
            catch (Exception exception) { Trace(exception); }
        }

        private static void Trace(Exception exception)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BatchPdfPublisher.ui.log"), DateTime.Now.ToString("O") + " Menu: " + exception + Environment.NewLine); } catch { }
        }
    }
}
