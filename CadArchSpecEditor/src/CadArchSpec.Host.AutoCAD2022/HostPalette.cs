using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CadArchSpec.EditorBridge;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadArchSpec.Host.AutoCAD2022
{
    // The historical class name is retained for command and shutdown compatibility.
    // The editor itself is now an independent modeless window, like Stair Detail.
    internal static class HostPalette
    {
        private static Form _architectureWindow;
        private static EditorHostControl _architectureHostControl;
        private static Form _cadTableWindow;
        private static EditorHostControl _cadTableHostControl;

        public static void Show()
        {
            try
            {
                WriteDiagnostic("JZSM entered");
                if (_architectureWindow != null && !_architectureWindow.IsDisposed)
                {
                    Activate(_architectureWindow);
                    WriteDiagnostic("Existing architecture editor window activated");
                    return;
                }

                WriteDiagnostic("Creating independent architecture editor window");
                _architectureHostControl = new EditorHostControl { Dock = DockStyle.Fill };
                _architectureWindow = CreateWindow("万落建筑工具 · 建筑设计说明助手", _architectureHostControl, "architecture-editor-window-bounds.txt", OnArchitectureWindowClosed);
                CadApplication.ShowModelessDialog(_architectureWindow);
            }
            catch (Exception exception)
            {
                WriteDiagnostic("Architecture editor window open failed", exception);
                CloseArchitectureWindow();
                CadApplication.ShowAlertDialog(
                    "建筑设计说明助手打开失败：\r\n" + exception.Message +
                    "\r\n\r\n诊断日志已保存到：\r\n" + DiagnosticLogPath());
            }
        }

        public static void ShowCadTableEditor(Newtonsoft.Json.Linq.JObject payload)
        {
            try
            {
                WriteDiagnostic("CE entered");
                if (_cadTableWindow != null && !_cadTableWindow.IsDisposed)
                {
                    _cadTableHostControl?.StartCadTableEdit(payload);
                    Activate(_cadTableWindow);
                    WriteDiagnostic("Existing independent CE window activated");
                    return;
                }

                WriteDiagnostic("Creating independent CE window");
                _cadTableHostControl = new EditorHostControl { Dock = DockStyle.Fill };
                _cadTableHostControl.StartCadTableEdit(payload);
                _cadTableWindow = CreateWindow("万落建筑工具 · CAD 表格编辑/Excel", _cadTableHostControl, "cad-table-editor-window-bounds.txt", OnCadTableWindowClosed);
                CadApplication.ShowModelessDialog(_cadTableWindow);
            }
            catch (Exception exception)
            {
                WriteDiagnostic("CE window open failed", exception);
                CloseCadTableWindow();
                CadApplication.ShowAlertDialog(
                    "CAD 表格编辑/Excel 打开失败：\r\n" + exception.Message +
                    "\r\n\r\n诊断日志已保存到：\r\n" + DiagnosticLogPath());
            }
        }

        public static void Close()
        {
            CloseArchitectureWindow();
            CloseCadTableWindow();
        }

        internal static void CloseCadTableWindowFromHost()
        {
            CloseCadTableWindow();
        }

        private static Form CreateWindow(string title, EditorHostControl control, string boundsFileName, FormClosedEventHandler closedHandler)
        {
            var window = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterScreen,
                Width = 1280,
                Height = 820,
                MinimumSize = new Size(640, 480),
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimizeBox = true,
                MaximizeBox = true,
                ShowIcon = false,
                ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.Dpi
            };
            RestoreWindowBounds(window, boundsFileName);
            window.Controls.Add(control);
            window.FormClosed += closedHandler;
            return window;
        }

        private static void Activate(Form window)
        {
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            window.BringToFront();
            window.Activate();
        }

        private static void CloseArchitectureWindow()
        {
            var window = _architectureWindow;
            _architectureWindow = null;
            if (window != null && !window.IsDisposed)
            {
                window.FormClosed -= OnArchitectureWindowClosed;
                SaveWindowBounds(window, "architecture-editor-window-bounds.txt");
                window.Close();
                window.Dispose();
            }
            _architectureHostControl?.Dispose();
            _architectureHostControl = null;
        }

        private static void CloseCadTableWindow()
        {
            var window = _cadTableWindow;
            _cadTableWindow = null;
            if (window != null && !window.IsDisposed)
            {
                window.FormClosed -= OnCadTableWindowClosed;
                SaveWindowBounds(window, "cad-table-editor-window-bounds.txt");
                window.Close();
                window.Dispose();
            }
            _cadTableHostControl?.Dispose();
            _cadTableHostControl = null;
        }

        private static void OnArchitectureWindowClosed(object sender, FormClosedEventArgs eventArgs)
        {
            var window = sender as Form;
            if (window != null) SaveWindowBounds(window, "architecture-editor-window-bounds.txt");
            _architectureHostControl = null;
            _architectureWindow = null;
        }

        private static void OnCadTableWindowClosed(object sender, FormClosedEventArgs eventArgs)
        {
            var window = sender as Form;
            if (window != null) SaveWindowBounds(window, "cad-table-editor-window-bounds.txt");
            _cadTableHostControl = null;
            _cadTableWindow = null;
        }

        private static string WindowBoundsPath(string fileName)
        {
            return Path.Combine(PortableDataPaths.DirectoryFor("Settings"), fileName);
        }

        private static void RestoreWindowBounds(Form window, string fileName)
        {
            try
            {
                var parts = File.ReadAllText(WindowBoundsPath(fileName)).Split(',');
                if (parts.Length != 4) return;
                int left, top, width, height;
                if (!int.TryParse(parts[0], out left) || !int.TryParse(parts[1], out top)
                    || !int.TryParse(parts[2], out width) || !int.TryParse(parts[3], out height)) return;
                width = Math.Max(window.MinimumSize.Width, width);
                height = Math.Max(window.MinimumSize.Height, height);
                var bounds = new Rectangle(left, top, width, height);
                if (!Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds))) return;
                window.StartPosition = FormStartPosition.Manual;
                window.Bounds = bounds;
            }
            catch
            {
                // Invalid layout data falls back to a centered window.
            }
        }

        private static void SaveWindowBounds(Form window, string fileName)
        {
            try
            {
                var bounds = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;
                if (bounds.Width < 1 || bounds.Height < 1) return;
                File.WriteAllText(WindowBoundsPath(fileName), string.Join(",",
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            }
            catch
            {
                // Layout persistence must never prevent the editor from closing.
            }
        }

        private static string DiagnosticLogPath()
        {
            return Path.Combine(PortableDataPaths.DirectoryFor("Logs"),
                "window-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        }

        private static void WriteDiagnostic(string message, Exception exception = null)
        {
            try
            {
                var path = DiagnosticLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path,
                    DateTime.Now.ToString("O") + " | AutoCAD 2022 | " + message +
                    (exception == null ? string.Empty : Environment.NewLine + exception) +
                    Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never prevent the CAD command from returning.
            }
        }
    }
}
