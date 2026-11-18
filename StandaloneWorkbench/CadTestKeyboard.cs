using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadTestKeyboard
    {
        internal static void WithModifiers(Action action, bool control = false, bool shift = false)
        {
            var previous = new byte[256];
            if (!GetKeyboardState(previous)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var state = new byte[256];
            if (control) state[0x11] = 0x80;
            if (shift) state[0x10] = 0x80;
            try
            {
                if (!SetKeyboardState(state)) throw new Win32Exception(Marshal.GetLastWin32Error());
                action();
            }
            finally
            {
                if (!SetKeyboardState(previous)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetKeyboardState(byte[] state);
    }
}
