using System.Diagnostics;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed record BlenderPreparedScene(string ScenePath, string RunDirectory, string LogPath);
internal sealed record BlenderLaunchProgress(string Message, string RunDirectory);

internal static class BlenderLaunchService
{
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);

    internal static async Task OpenAsync(string executable, string model, string script, string runRoot,
        IProgress<BlenderLaunchProgress>? progress, CancellationToken cancellation)
    {
        if (!await LaunchGate.WaitAsync(0, cancellation))
            throw new InvalidOperationException("已有 Blender 自动打开任务正在进行，GLB 已保留。");
        try
        {
            var prepared = await PrepareAsync(executable, model, script, runRoot, progress, cancellation);
            await OpenPreparedAsync(executable, script, prepared, progress, cancellation);
        }
        finally { LaunchGate.Release(); }
    }

    internal static ProcessStartInfo StartInfo(string executable, string script, bool background, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            CreateNoWindow = background,
            WindowStyle = background ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            RedirectStandardOutput = background,
            RedirectStandardError = background
        };
        if (background) start.ArgumentList.Add("--background");
        foreach (var value in new[] { "--factory-startup", "--disable-autoexec", "--threads", "2", "--python-exit-code", "1", "--python", script, "--" })
            start.ArgumentList.Add(value);
        foreach (var value in arguments) start.ArgumentList.Add(value);
        return start;
    }

    internal static async Task<BlenderPreparedScene> PrepareAsync(string executable, string model, string script,
        string runRoot, IProgress<BlenderLaunchProgress>? progress, CancellationToken cancellation)
    {
        if (!File.Exists(model) || !string.Equals(Path.GetExtension(model), ".glb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("导出的 GLB 文件不存在。");
        if (!File.Exists(script)) throw new FileNotFoundException("缺少 Blender 导入脚本。", script);
        cancellation.ThrowIfCancellationRequested();
        var run = Path.Combine(runRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var scene = Path.Combine(run, Path.GetFileNameWithoutExtension(model) + ".blend");
        var log = Path.Combine(run, "stages.jsonl");
        progress?.Report(new("Blender：正在后台准备模型…", run));
        try
        {
            var start = StartInfo(executable, script, true, "--prepare", Path.GetFullPath(model), scene, "--log", log);
            IsolateTemporaryFiles(start, run);
            await RunBackgroundAsync(start, Path.Combine(run, "process.log"), TimeSpan.FromSeconds(90), 2L << 30, cancellation);
            if (!File.Exists(scene) || !File.Exists(Path.Combine(run, "prepared.flag")))
                throw new InvalidOperationException("Blender 未完成模型准备，未打开前台窗口。");
            return new(scene, run, log);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException("GLB 已保存；Blender 准备失败：" + ex.Message + " 日志：" + run, ex); }
    }

    internal static async Task RunBackgroundAsync(ProcessStartInfo start, string logPath, TimeSpan timeout,
        long memoryLimit, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        using var log = new StreamWriter(logPath) { AutoFlush = true };
        var gate = new object();
        void Write(string value) { lock (gate) log.WriteLine(value); }
        if (!process.Start()) throw new InvalidOperationException("Blender 未启动。");
        try
        {
            using var guard = new BlenderProcessGuard(process, memoryLimit);
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            Write($"START pid={process.Id} memoryLimit={memoryLimit} timeoutSeconds={timeout.TotalSeconds}");
            async Task Drain(StreamReader reader)
            { while (await reader.ReadLineAsync() is { } line) Write(line); }
            var output = Drain(process.StandardOutput);
            var errors = Drain(process.StandardError);
            var clock = Stopwatch.StartNew();
            long peak = 0;
            try
            {
                while (!process.HasExited)
                {
                    cancellation.ThrowIfCancellationRequested();
                    process.Refresh();
                    peak = Math.Max(peak, process.PrivateMemorySize64);
                    if (peak > memoryLimit) throw new InvalidOperationException("超过内存安全限额，已停止本次准备。");
                    if (clock.Elapsed > timeout) throw new TimeoutException("准备超时，已停止本次 Blender 进程。");
                    await Task.Delay(100, cancellation);
                }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            finally
            {
                guard.Dispose();
                StopOwnedProcess(process);
                await Task.WhenAll(output, errors);
                Write($"END peakPrivateBytes={peak} seconds={clock.Elapsed.TotalSeconds:F3}");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("Blender 异常退出或触及安全限额，退出码 " + process.ExitCode + "。");
        }
        finally { StopOwnedProcess(process); }
    }

    internal static async Task<int> OpenPreparedAsync(string executable, string script, BlenderPreparedScene prepared,
        IProgress<BlenderLaunchProgress>? progress, CancellationToken cancellation, bool testClose = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (File.Exists(Path.Combine(prepared.RunDirectory, "ready.flag")))
            throw new InvalidOperationException("本次准备场景已打开，请重新准备后再启动。");
        var start = StartInfo(executable, script, false, "--show", prepared.ScenePath, "--log", prepared.LogPath);
        IsolateTemporaryFiles(start, prepared.RunDirectory);
        if (testClose) start.ArgumentList.Add("--test-close");
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Blender 前台未启动。");
        var released = false;
        long peak = 0;
        void Log(string message) => File.AppendAllText(Path.Combine(prepared.RunDirectory, "process.log"),
            DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        try
        {
            using var guard = new BlenderProcessGuard(process, 4L << 30);
            Log($"FRONTEND_START pid={process.Id} memoryLimit={4L << 30} timeoutSeconds=30");
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            progress?.Report(new("Blender：模型已准备，正在检查前台首帧…", prepared.RunDirectory));
            var clock = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(prepared.RunDirectory, "ready.flag")))
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("Blender 在前台首帧前退出。");
                process.Refresh();
                peak = Math.Max(peak, process.PrivateMemorySize64);
                if (process.PrivateMemorySize64 > (4L << 30) || clock.Elapsed > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException("Blender 前台未能在安全限额内显示首帧，已停止本次启动。");
                await Task.Delay(100, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("Blender 首帧后异常退出。");
            guard.Release();
            process.PriorityClass = ProcessPriorityClass.Normal;
            released = true;
            Log($"FIRST_FRAME_CONFIRMED peakPrivateBytes={peak} seconds={clock.Elapsed.TotalSeconds:F3} limitsReleased=true");
            progress?.Report(new("Blender：模型已打开（实体显示）。", prepared.RunDirectory));
            return process.Id;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            try { Log("FRONTEND_FAILED " + ex); } catch (IOException) { }
            throw new InvalidOperationException(ex.Message + " GLB 已保留；日志：" + prepared.RunDirectory, ex);
        }
        finally { if (!released) StopOwnedProcess(process); }
    }

    private static void StopOwnedProcess(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
        catch (InvalidOperationException) { }
    }

    private static void IsolateTemporaryFiles(ProcessStartInfo start, string directory)
    {
        var scratch = Path.Combine(directory, "scratch");
        Directory.CreateDirectory(scratch);
        start.Environment["TMP"] = start.Environment["TEMP"] = start.Environment["TMPDIR"] = scratch;
    }
}
