using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CadArchSpec.EditorBridge;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadArchSpec.Host.AutoCAD2026
{
    // The historical class name is retained for command and shutdown compatibility.
    // The editor itself is now an independent modeless window, like Stair Detail.
    internal static class HostPalette
    {
        private static Form _window;
        private static EditorHostControl _hostControl;

        public static void Show()
        {
            try
            {
                WriteDiagnostic("JZSM entered");
                if (_window != null && !_window.IsDisposed)
                {
                    if (_window.WindowState == FormWindowState.Minimized)
                        _window.WindowState = FormWindowState.Normal;
                    _window.BringToFront();
                    _window.Activate();
                    WriteDiagnostic("Existing editor window activated");
                    return;
                }

                WriteDiagnostic("Creating modeless editor window");
                _hostControl = new EditorHostControl { Dock = DockStyle.Fill };
                _window = new Form
                {
                    Text = "万落建筑工具 · 建筑设计说明助手",
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
                RestoreWindowBounds(_window);
                _window.Controls.Add(_hostControl);
                _window.FormClosed += OnWindowClosed;
                CadApplication.ShowModelessDialog(_window);
                WriteDiagnostic("Modeless editor window visible");
            }
            catch (Exception exception)
            {
                WriteDiagnostic("Editor window open failed", exception);
                Close();
                CadApplication.ShowAlertDialog(
                    "建筑设计说明助手打开失败：\r\n" + exception.Message +
                    "\r\n\r\n诊断日志已保存到：\r\n" + DiagnosticLogPath());
            }
        }

        public static void Close()
        {
            var window = _window;
            _window = null;
            if (window != null && !window.IsDisposed)
            {
                window.FormClosed -= OnWindowClosed;
                SaveWindowBounds(window);
                window.Close();
                window.Dispose();
            }

            _hostControl?.Dispose();
            _hostControl = null;
        }

        private static void OnWindowClosed(object sender, FormClosedEventArgs eventArgs)
        {
            var window = sender as Form;
            if (window != null) SaveWindowBounds(window);
            _hostControl = null;
            _window = null;
        }

        private static string WindowBoundsPath()
        {
            return Path.Combine(PortableDataPaths.DirectoryFor("Settings"), "editor-window-bounds.txt");
        }

        private static void RestoreWindowBounds(Form window)
        {
            try
            {
                var parts = File.ReadAllText(WindowBoundsPath()).Split(',');
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

        private static void SaveWindowBounds(Form window)
        {
            try
            {
                var bounds = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;
                if (bounds.Width < 1 || bounds.Height < 1) return;
                File.WriteAllText(WindowBoundsPath(), string.Join(",",
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
                    DateTime.Now.ToString("O") + " | AutoCAD 2026 | " + message +
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
