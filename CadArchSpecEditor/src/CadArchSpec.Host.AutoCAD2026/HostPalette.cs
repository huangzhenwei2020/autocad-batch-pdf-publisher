using System;
using System.Drawing;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using CadArchSpec.EditorBridge;

namespace CadArchSpec.Host.AutoCAD2026
{
    internal static class HostPalette
    {
        private static readonly Guid PaletteId = new Guid("7D4FAD4E-4C8D-481C-8B91-B330D952BDF4");
        private static PaletteSet _paletteSet;
        private static EditorHostControl _hostControl;

        public static void Show()
        {
            try
            {
                WriteDiagnostic("JZSM entered");
                if (_paletteSet == null)
                {
                    WriteDiagnostic("Creating editor control");
                    _hostControl = new EditorHostControl();
                    _paletteSet = new PaletteSet("万落建筑工具 · 建筑设计说明", PaletteId)
                    {
                        DockEnabled = DockSides.Left | DockSides.Right,
                        MinimumSize = new Size(420, 520),
                        Size = new Size(720, 820),
                        Style = PaletteSetStyles.ShowAutoHideButton |
                                PaletteSetStyles.ShowCloseButton |
                                PaletteSetStyles.ShowPropertiesMenu
                    };
                    _paletteSet.Add("建筑设计说明", _hostControl);
                    _paletteSet.PaletteSetDestroy += OnPaletteSetDestroy;
                }

                _paletteSet.Visible = true;
                _paletteSet.Dock = DockSides.Right;
                _paletteSet.Size = new Size(720, 820);
                _paletteSet.Activate(0);
                WriteDiagnostic("Palette visible and docked right");
            }
            catch (Exception exception)
            {
                WriteDiagnostic("Palette open failed", exception);
                Close();
                Application.ShowAlertDialog(
                    "建筑设计说明助手打开失败：\r\n" + exception.Message +
                    "\r\n\r\n诊断日志已保存到：\r\n" + DiagnosticLogPath());
            }
        }

        public static void Close()
        {
            if (_paletteSet == null)
            {
                return;
            }

            _paletteSet.PaletteSetDestroy -= OnPaletteSetDestroy;
            _hostControl?.Dispose();
            _hostControl = null;
            _paletteSet.Dispose();
            _paletteSet = null;
        }

        private static void OnPaletteSetDestroy(object sender, EventArgs e)
        {
            _hostControl?.Dispose();
            _hostControl = null;
            _paletteSet = null;
        }

        private static string DiagnosticLogPath()
        {
            return Path.Combine(PortableDataPaths.DirectoryFor("Logs"),
                "palette-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        }

        private static void WriteDiagnostic(string message, Exception exception = null)
        {
            try
            {
                var path = DiagnosticLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(
                    path,
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
