using System;
using System.Drawing;
using Autodesk.AutoCAD.Windows;

namespace CadArchSpec.Host.AutoCAD2026
{
    internal static class HostPalette
    {
        private static readonly Guid PaletteId = new Guid("7D4FAD4E-4C8D-481C-8B91-B330D952BDF4");
        private static PaletteSet _paletteSet;
        private static EditorHostControl _hostControl;

        public static void Show()
        {
            if (_paletteSet == null)
            {
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
            _paletteSet.Activate(0);
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
    }
}
