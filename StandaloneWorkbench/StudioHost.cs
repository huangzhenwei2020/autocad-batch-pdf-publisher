using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.StandaloneWorkbench
{
    internal sealed class StudioHost : HwndHost
    {
        private Process _process;
        private string _pipeName;
        private IntPtr _child;
        private readonly SemaphoreSlim _requests = new SemaphoreSlim(1, 1);
        internal bool IsRunning => _process != null && !_process.HasExited;
        internal event Action Exited;

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            var handle = CreateWindowEx(0, "static", "", 0x56000000, 0, 0, 1, 1,
                parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            return new HandleRef(this, handle);
        }

        protected override void DestroyWindowCore(HandleRef window)
        {
            // A visual-tree teardown must not destroy an editor with unsaved work.
            if (_child != IntPtr.Zero) SetParent(_child, IntPtr.Zero);
            DestroyWindow(window.Handle);
        }

        internal async Task<StudioWorkspaceState> OpenAsync(string modelPath)
        {
            if (IsRunning) throw new InvalidOperationException("请先关闭当前三维模型。");
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("三维工作区正在准备，请稍后重试。");
            _process?.Dispose();
            _process = null;
            _pipeName = "WanLuo-Studio-" + Guid.NewGuid().ToString("N");
            var start = WorkbenchRuntime.EditorStart(modelPath);
            start.ArgumentList.Add("--workbench-host");
            start.ArgumentList.Add(Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(_pipeName);
            if (App.IsSelfTest) start.ArgumentList.Add("--workbench-check");
            var process = Process.Start(start) ?? throw new IOException("三维编辑器未能启动。");
            _process = process;
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_process != process) return;
                _child = IntPtr.Zero;
                Exited?.Invoke();
            }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                while (!process.HasExited)
                {
                    var state = await RequestAsync("status", timeout.Token);
                    _child = new IntPtr(state.Window);
                    ResizeChild();
                    if (!state.Success) throw new IOException(state.Message);
                    if (state.Ready) return state;
                    await Task.Delay(100, timeout.Token);
                }
                throw new IOException("三维编辑器已退出，请检查模型文件后重试。");
            }
            catch
            {
                // Ask the editor to close normally. Never terminate a user's model.
                if (!process.HasExited && _child != IntPtr.Zero) PostMessage(_child, 0x0010, IntPtr.Zero, IntPtr.Zero);
                throw;
            }
        }

        internal async Task<StudioWorkspaceState> RequestAsync(string command, CancellationToken cancellation = default)
        {
            await _requests.WaitAsync(cancellation);
            try
            {
                if (!IsRunning) throw new IOException("三维模型窗口已关闭。");
                using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(10000, cancellation);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(command.AsMemory(), cancellation);
                var line = await reader.ReadLineAsync(cancellation);
                return JsonSerializer.Deserialize<StudioWorkspaceState>(line ?? throw new IOException("三维连接已关闭。"))
                    ?? throw new IOException("三维编辑器返回了空状态。");
            }
            finally { _requests.Release(); }
        }

        internal async Task<StudioWorkspaceState> CloseAsync()
        {
            if (!IsRunning) return new StudioWorkspaceState { Success = true };
            var process = _process;
            var state = await RequestAsync("close");
            if (!state.Success) return state;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            _child = IntPtr.Zero;
            return state;
        }

        protected override void OnWindowPositionChanged(Rect rectangle)
        {
            base.OnWindowPositionChanged(rectangle);
            ResizeChild();
        }

        private void ResizeChild()
        {
            if (_child == IntPtr.Zero || Handle == IntPtr.Zero) return;
            GetClientRect(Handle, out var rect);
            MoveWindow(_child, 0, 0, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom), true);
        }

        internal IntPtr ChildWindow => _child;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int ex, string type, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr module, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    }
}
