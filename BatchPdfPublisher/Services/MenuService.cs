using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Reflection;

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
                var content = "***MENUGROUP=BPP\r\n***POP16\r\n万落建筑工具\r\n[批量 PDF 面板（BPP）]^C^C_BPP \r\n[创建图框（TKK）]^C^C_TKK \r\n[插入目录（ML1）]^C^C_ML1 \r\n[批量改属性（SBB）]^C^C_SBB \r\n[图块属性定义编辑器（BPA）]^C^C_BPA \r\n[建筑设计说明助手（JZSM）]^C^C_WLJZSM \r\n[一键楼梯大样（LTDY）]^C^C_WLLTDY \r\n[批量门窗立面（MCLM）]^C^C_MCLM \r\n[制图标准设置（BZS）]^C^C_BZS \r\n[比例管理（BL1）]^C^C_BL1 \r\n[批量修改房间名称（FJGM）]^C^C_FJGM \r\n";
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
