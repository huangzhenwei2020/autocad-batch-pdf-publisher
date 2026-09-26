using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>
    /// 把 AutoCAD 窗口切到前台，并**自动输入 LTTZ**（"推到 CAD"按钮用的）。
    ///
    /// 为什么这么做：建模程序与 CAD 是两个进程，程序没法直接调 CAD 的命令；
    /// 最省事的"一键"就是——把 CAD 激活，再把 LTTZ 三个字母+回车送过去（CAD 会在命令行接住它）。
    ///
    /// 安全约束：
    /// 1. 只在**确认前台窗口已经属于 AutoCAD** 之后才发送按键（否则宁可不发，只留待落图清单）；
    /// 2. 最小化时先还原；找不到 acad 进程就返回 false，由调用方提示"回 CAD 手动敲 LTTZ"；
    /// 3. 任何异常都吞掉 —— 这只是个便利功能，绝不能把程序弄崩。
    /// </summary>
    internal static class CadWindow
    {
        private const int SwRestore = 9;

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>找到 AutoCAD 主窗口并切过去，然后自动输入 LTTZ。返回是否切过去了。</summary>
        public static bool TryActivate()
        {
            try
            {
                var cad = FindCadProcess();
                if (cad == null) return false;
                var handle = cad.MainWindowHandle;
                if (handle == IntPtr.Zero) return false;

                if (IsIconic(handle)) ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                Thread.Sleep(250);                      // 等窗口真的到前台，再判断一次

                if (!IsForegroundOf(cad.Id)) return false;
                try { SendKeys.SendWait("LTTZ{ENTER}"); }
                catch { return true; }                  // 切过去了、只是没送成命令，也算成功
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Process FindCadProcess()
        {
            try
            {
                return Process.GetProcesses()
                    .Where(process =>
                    {
                        try
                        {
                            var name = process.ProcessName ?? string.Empty;
                            return name.StartsWith("acad", StringComparison.OrdinalIgnoreCase)
                                && process.MainWindowHandle != IntPtr.Zero;
                        }
                        catch { return false; }
                    })
                    .OrderByDescending(process => process.MainWindowHandle != IntPtr.Zero)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsForegroundOf(int processId)
        {
            try
            {
                var foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero) return false;
                int owner;
                GetWindowThreadProcessId(foreground, out owner);
                return owner == processId;
            }
            catch
            {
                return false;
            }
        }
    }
}
