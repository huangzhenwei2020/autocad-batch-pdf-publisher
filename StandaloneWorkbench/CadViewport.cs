using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace Wanluo.StandaloneWorkbench
{
    internal sealed class CadViewport : HwndHost
    {
        internal IntPtr Session { get; set; }
        public event Action<string> TextRequested;
        public event Action<int, bool> ShortcutRequested;
        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            CadNative.Check(CadNative.wl_attach(Session, parent.Handle));
            return new HandleRef(this, CadNative.wl_window(Session));
        }
        protected override void DestroyWindowCore(HandleRef window) => CadNative.wl_detach(Session);
        protected override IntPtr WndProc(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x8000 + 610)
            {
                TextRequested?.Invoke(((char)wparam.ToInt32()).ToString());
                handled = true;
            }
            if (message == 0x8000 + 611)
            {
                ShortcutRequested?.Invoke(wparam.ToInt32(), lparam != IntPtr.Zero);
                handled = true;
            }
            return IntPtr.Zero;
        }
        internal void FocusDrawing()
        {
            Focus();
            if (Handle != IntPtr.Zero) SetFocus(Handle);
        }
        protected override bool TabIntoCore(TraversalRequest request) { FocusDrawing(); return true; }
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    }
}
