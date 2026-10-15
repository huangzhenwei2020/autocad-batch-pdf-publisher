using System.Diagnostics;
using System.Reflection;
using BuildingModelStudio.AvaloniaProbe;

internal static class BlenderLaunchTests
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--prepare"))
        {
            var scene = args[Array.IndexOf(args, "--prepare") + 2];
            File.WriteAllText(scene, "fixture");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(scene)!, "prepared.flag"), "ok");
            await Task.Delay(300);
            return 0;
        }
        if (args.Contains("--show"))
        {
            var log = args[Array.IndexOf(args, "--log") + 1];
            File.WriteAllText(log, Environment.ProcessId.ToString());
            if (log.Contains("missing-ready")) return 9;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(log)!, "ready.flag"), "ok");
            await Task.Delay(20000);
            return 0;
        }
        if (args.Length == 1 && args[0].StartsWith("--child="))
        {
            Console.WriteLine("CHILD_PID=" + Environment.ProcessId);
            if (args[0] == "--child=success") { Console.WriteLine("CHILD_OK"); return 0; }
            if (args[0] == "--child=failure") return 7;
            if (args[0] == "--child=memory")
            {
                var held = new List<byte[]>();
                while (true) { var block = new byte[16 * 1024 * 1024]; Array.Fill(block, (byte)1); held.Add(block); await Task.Delay(20); }
            }
            await Task.Delay(20000);
            return 0;
        }
        try
        {
            if (args.Length == 5 && args[0].StartsWith("--blender-"))
            {
                var scene = await BlenderLaunchService.PrepareAsync(args[1], args[2], args[3], args[4], null, CancellationToken.None);
                Console.WriteLine("BLENDER_PREPARE_OK " + scene.ScenePath);
                if (args[0] == "--blender-ui-fixture")
                {
                    var pid = await BlenderLaunchService.OpenPreparedAsync(args[1], args[3], scene, null, CancellationToken.None, true);
                    try
                    {
                        using var child = Process.GetProcessById(pid);
                        if (!child.WaitForExit(10000)) { child.Kill(); throw new InvalidOperationException("Fixture did not exit"); }
                    }
                    catch (ArgumentException) { }
                    Require(!File.ReadAllText(scene.LogPath).Contains("\"stage\": \"ERROR\""), "Frontend script failed");
                    Console.WriteLine("BLENDER_FIRST_FRAME_OK " + scene.LogPath);
                }
                return 0;
            }
            var root = Path.Combine(Path.GetTempPath(), "WanLuoBlenderGuard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var executable = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
            Require(File.Exists(executable), "Test apphost missing");
            var arguments = BlenderLaunchService.StartInfo(executable, "C:/scripts with spaces/测试.py", true,
                "--prepare", "H:/资料/8#商业.glb", "C:/tmp/模型.blend", "--log", "C:/tmp/log.jsonl");
            Require(!arguments.UseShellExecute && arguments.CreateNoWindow && arguments.ArgumentList.Contains("H:/资料/8#商业.glb")
                && arguments.ArgumentList.Contains("--factory-startup") && arguments.ArgumentList.Contains("--disable-autoexec")
                && arguments.ArgumentList[arguments.ArgumentList.IndexOf("--threads") + 1] == "2", "Unsafe or incorrectly quoted launch arguments");
            async Task Run(string mode, string filename, TimeSpan timeout, CancellationToken cancellation, long memory = 128L << 20)
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--child=" + mode);
                await BlenderLaunchService.RunBackgroundAsync(start, Path.Combine(root, filename), timeout, memory, cancellation);
            }
            await Run("success", "success.log", TimeSpan.FromSeconds(5), CancellationToken.None);
            Require(File.ReadAllText(Path.Combine(root, "success.log")).Contains("CHILD_OK"), "Missing process log");
            async Task Reject(Func<Task> action, Type expected)
            {
                try { await action(); throw new Exception("Failure was accepted"); }
                catch (Exception ex) when (expected.IsAssignableFrom(ex.GetType())) { }
            }
            await Reject(() => Run("failure", "failure.log", TimeSpan.FromSeconds(5), CancellationToken.None), typeof(InvalidOperationException));
            await Reject(() => Run("slow", "timeout.log", TimeSpan.FromMilliseconds(700), CancellationToken.None), typeof(TimeoutException));
            using (var cancel = new CancellationTokenSource(700))
                await Reject(() => Run("slow", "cancel.log", TimeSpan.FromSeconds(5), cancel.Token), typeof(OperationCanceledException));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Reject(() => Run("success", "not-started.log", TimeSpan.FromSeconds(5), cancelled.Token), typeof(OperationCanceledException));
                Require(!File.Exists(Path.Combine(root, "not-started.log")), "Pre-cancelled operation started a child");
            }
            if (OperatingSystem.IsWindows())
                await Reject(() => Run("memory", "memory.log", TimeSpan.FromSeconds(10), CancellationToken.None), typeof(InvalidOperationException));
            foreach (var file in new[] { "timeout.log", "cancel.log", "memory.log" })
            {
                if (!File.Exists(Path.Combine(root, file))) continue;
                var line = File.ReadAllLines(Path.Combine(root, file)).FirstOrDefault(l => l.StartsWith("CHILD_PID="));
                if (line == null) continue;
                try { using var child = Process.GetProcessById(int.Parse(line.Split('=')[1])); Require(child.HasExited, "Orphan child survived guard"); }
                catch (ArgumentException) { }
            }
            foreach (var mode in new[] { "ready", "missing-ready" })
            {
                var run = Path.Combine(root, mode); Directory.CreateDirectory(run);
                var scene = new BlenderPreparedScene(Path.Combine(run, "fixture.blend"), run, Path.Combine(run, mode + ".log"));
                if (mode == "missing-ready")
                    await Reject(() => BlenderLaunchService.OpenPreparedAsync(executable, "unused.py", scene, null, CancellationToken.None), typeof(InvalidOperationException));
                else
                {
                    var pid = await BlenderLaunchService.OpenPreparedAsync(executable, "unused.py", scene, null, CancellationToken.None);
                    using var child = Process.GetProcessById(pid);
                    Require(!child.HasExited, "Ready Blender was killed when guard released");
                    child.Kill(); child.WaitForExit();
                    await Reject(() => BlenderLaunchService.OpenPreparedAsync(executable, "unused.py", scene, null, CancellationToken.None), typeof(InvalidOperationException));
                }
            }
            var source = Path.Combine(root, "fixture.glb"); File.WriteAllText(source, "source");
            var script = Path.Combine(root, "fixture.py"); File.WriteAllText(script, "# fixture");
            var composite = Path.Combine(root, "composite");
            var first = BlenderLaunchService.OpenAsync(executable, source, script, composite, null, CancellationToken.None);
            await Reject(() => BlenderLaunchService.OpenAsync(executable, source, script, composite, null, CancellationToken.None), typeof(InvalidOperationException));
            await first;
            var pidLog = Directory.GetFiles(composite, "stages.jsonl", SearchOption.AllDirectories).Single();
            using (var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidLog)))) { child.Kill(); child.WaitForExit(); }
            Require(File.ReadAllText(source) == "source", "Source GLB changed during automatic open");
            Console.WriteLine("BLENDER_GUARD_TESTS_OK arguments success stderr exitFailure memoryCap timeout cancellation preCancel noOrphans firstFrame missingFrame readyReleased staleRejected composite concurrentRejected sourceUnchanged");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
