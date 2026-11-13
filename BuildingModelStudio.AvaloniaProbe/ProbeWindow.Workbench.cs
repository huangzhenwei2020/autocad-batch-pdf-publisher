using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private CancellationTokenSource? _workbenchLifetime;
    private bool _workbenchAttached;
    private bool _workbenchReady;
    private string? _workbenchStartupError;

    private void ConfigureWorkbenchHost()
    {
        if (Program.WorkbenchPipe == null) return;
        if (!NativeIsWindow(Program.WorkbenchParent))
            throw new InvalidOperationException("独立工作台窗口已关闭。");
        GetWindowThreadProcessId(Program.WorkbenchParent, out var parentPid);
        if (parentPid != Program.WorkbenchProcess)
            throw new InvalidOperationException("独立工作台窗口已更换，请重新打开模型。");
        ShowInTaskbar = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        _workbenchLifetime = new CancellationTokenSource();
        var token = _workbenchLifetime.Token;
        Opened += (_, _) =>
        {
            var handle = TryGetPlatformHandle()!.Handle;
            var style = GetWindowLongPtr(handle, -16).ToInt64();
            SetWindowLongPtr(handle, -16, new IntPtr((style & ~0x80CF0000L) | 0x46000000L));
            Marshal.SetLastPInvokeError(0);
            SetParent(handle, Program.WorkbenchParent);
            if (Marshal.GetLastPInvokeError() != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            _workbenchAttached = true;
            ResizeWorkbenchChild(handle);
        };
        var parentMonitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        parentMonitor.Tick += (_, _) =>
        {
            if (!_workbenchAttached || NativeIsWindow(Program.WorkbenchParent)) return;
            // Keep unsaved work available if the workbench unexpectedly exits.
            var handle = TryGetPlatformHandle()!.Handle;
            SetParent(handle, IntPtr.Zero);
            SetWindowLongPtr(handle, -16, new IntPtr((GetWindowLongPtr(handle, -16).ToInt64() & ~0x40000000L) | 0x00CF0000L));
            _workbenchAttached = false;
            ShowInTaskbar = true;
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
            Width = 1200;
            Height = 800;
            Activate();
            parentMonitor.Stop();
        };
        parentMonitor.Start();
        Closed += (_, _) => { parentMonitor.Stop(); _workbenchLifetime.Cancel(); };
        _ = ServeWorkbenchAsync(token);
    }

    private static void ResizeWorkbenchChild(IntPtr handle)
    {
        GetClientRect(Program.WorkbenchParent, out var rect);
        SetWindowPos(handle, IntPtr.Zero, 0, 0, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom), 0x0034);
    }

    private async Task ServeWorkbenchAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(Program.WorkbenchPipe!, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                try
                {
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    var command = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (command == null) continue;
                    StudioWorkspaceState result;
                    try { result = await Dispatcher.UIThread.InvokeAsync(() => WorkbenchCommandAsync(command)); }
                    catch (Exception error) { result = new StudioWorkspaceState { Success = false, Message = error.Message }; }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result).AsMemory(), token).ConfigureAwait(false);
                    if (command == "close" && result.Success)
                        Dispatcher.UIThread.Post(Close);
                }
                catch (IOException) when (!token.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() => _status.Text = "工作台连接已中断，可继续保存模型。" + error.Message);
        }
    }

    private async Task<StudioWorkspaceState> WorkbenchCommandAsync(string? command)
    {
        if (command == "self-test" && Program.WorkbenchCheck)
            return await CheckWorkbenchEditingAsync();
        var success = true;
        if (command != "status")
        {
            if (command == "close" && _workbenchStartupError != null && !_loadingModel && !HasChanges)
            {
                _closeConfirmed = true;
                return new StudioWorkspaceState { Success = true, ModelPath = _filePath, Message = _workbenchStartupError };
            }
            if (!_workbenchReady || _loadingModel || _savingModel || _cadImportBusy || _storeyRequestBusy
                || _modelCommandBusy || _modelManagerOpen || OwnedWindows.Any(window => window.IsVisible))
                return WorkbenchState(false, "请先完成当前模型操作或关闭参数窗口。");
            if (command == "save" || command == "save-as")
                success = await SaveModelAsync(command == "save-as");
            else if (command == "undo") await UndoModelAsync();
            else if (command == "redo") await RedoModelAsync();
            else if (command == "close")
            {
                success = await ConfirmSavedAsync();
                if (success) _closeConfirmed = true;
            }
            else return WorkbenchState(false, "无法识别工作台请求。");
        }
        return WorkbenchState(success, _workbenchStartupError ?? _status.Text);
    }

    private StudioWorkspaceState WorkbenchState(bool success, string? message) => new()
    {
        Success = success && _workbenchStartupError == null,
        Ready = _workbenchAttached && _workbenchReady,
        Window = TryGetPlatformHandle()?.Handle.ToInt64() ?? 0,
        ModelPath = _filePath,
        Dirty = HasChanges,
        Message = message
    };

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool NativeIsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
