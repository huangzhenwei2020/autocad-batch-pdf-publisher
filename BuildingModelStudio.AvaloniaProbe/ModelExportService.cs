using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BatchPdfPublisher.BuildingModel;
using Microsoft.Win32;

namespace BuildingModelStudio.AvaloniaProbe;

internal enum ModelExportFormat { Fbx, Skp }
internal sealed record ModelExportProgress(string Message, string RunDirectory);
internal sealed record ModelExportResult(string Path, string RunDirectory, int Storeys, int Elements,
    int Meshes, int Triangles, long Bytes, int Polygons);

internal static class ModelExportService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static string? FindSketchUpApiDirectory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var candidates = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key == null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var entry = key.OpenSubKey(name);
                var display = entry?.GetValue("DisplayName") as string;
                if (display?.Contains("SketchUp 2026", StringComparison.OrdinalIgnoreCase) != true) continue;
                if (entry?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                    candidates.Add(location);
            }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            candidates.Add(System.IO.Path.Combine(drive.Name, "Program Files", "SketchUp", "SketchUp 2026"));
            candidates.Add(System.IO.Path.Combine(drive.Name, "Program Files", "su2026", "SketchUp"));
        }
        return candidates.SelectMany(p => new[] { p, System.IO.Path.Combine(p, "SketchUp") })
            .Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(IsSketchUp2026);
    }

    internal static bool IsSketchUp2026(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            var dll = System.IO.Path.Combine(directory, "SketchUpAPI.dll");
            return File.Exists(dll) && FileVersionInfo.GetVersionInfo(dll).FileMajorPart == 26
                && File.Exists(System.IO.Path.Combine(directory, "SketchUpCommonPreferences.dll"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    internal static async Task<ModelExportResult> ExportAsync(BuildingModelDocument model, string path,
        ModelExportFormat format, string? storeyId, bool nearOrigin, string? sketchUpApiDirectory,
        IProgress<ModelExportProgress>? progress, CancellationToken token, string? workerExecutable = null)
    {
        if (!await Gate.WaitAsync(0, token)) throw new InvalidOperationException("已有三维模型导出任务正在进行。");
        try
        {
            var extension = format == ModelExportFormat.Fbx ? ".fbx" : ".skp";
            var destination = System.IO.Path.GetFullPath(path);
            if (!string.Equals(System.IO.Path.GetExtension(destination), extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择 " + extension + " 文件。");
            if (format == ModelExportFormat.Skp && !IsSketchUp2026(sketchUpApiDirectory))
                throw new InvalidOperationException("未找到 SketchUp 2026 的本机接口，请选择其安装目录中的 SketchUp.exe。");
            var worker = workerExecutable ?? System.IO.Path.Combine(AppContext.BaseDirectory, "万落建筑模型.exe");
            if (!File.Exists(worker)) throw new FileNotFoundException("缺少三维模型导出组件，请重新构建或更新完整安装包。", worker);
            token.ThrowIfCancellationRequested();
            var snapshot = BuildingModelJson.ToJson(model);
            if (snapshot.Length > 40_000_000) throw new InvalidOperationException("模型数据过大，请分楼层导出。");
            var nonce = Guid.NewGuid().ToString("N");
            var run = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WanLuo", "BuildingModel", "ExportRuns", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + nonce);
            Directory.CreateDirectory(run);
            progress?.Report(new("正在准备模型副本…", run));
            var source = System.IO.Path.Combine(run, "model.json");
            var output = System.IO.Path.Combine(run, "model" + extension);
            var receipt = System.IO.Path.Combine(run, "result.json");
            var request = System.IO.Path.Combine(run, "request.json");
            await File.WriteAllTextAsync(source, snapshot, token);
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { Nonce = nonce,
                Format = extension[1..], ModelPath = source, OutputPath = output, ReceiptPath = receipt,
                StoreyId = storeyId, NearOrigin = nearOrigin, SketchUpApiDirectory = sketchUpApiDirectory }), token);
            progress?.Report(new(format == ModelExportFormat.Fbx ? "正在生成并核验 FBX…" : "正在生成并核验 SKP…", run));
            await RunWorkerAsync(worker, request, run, token);
            if (!File.Exists(receipt)) throw new InvalidDataException("转换组件没有返回完成回执，原导出文件未改动。");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, token));
            var root = result.RootElement;
            if (root.GetProperty("Nonce").GetString() != nonce || root.GetProperty("Status").GetString() != "Completed"
                || root.GetProperty("Format").GetString() != extension[1..]) throw new InvalidDataException("导出完成回执不匹配。");
            var bytes = new FileInfo(output).Length;
            await using (var stream = File.OpenRead(output))
            {
                if (bytes <= 0 || bytes != root.GetProperty("Bytes").GetInt64()
                    || Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != root.GetProperty("Sha256").GetString())
                    throw new InvalidDataException("导出文件校验失败，原文件未改动。");
            }
            var metadata = root.GetProperty("Manifest").GetRawText();
            token.ThrowIfCancellationRequested();
            await CommitAsync(output, destination, metadata, token);
            return new(destination, run, root.GetProperty("Storeys").GetInt32(), root.GetProperty("Elements").GetInt32(),
                root.GetProperty("Meshes").GetInt32(), root.GetProperty("Triangles").GetInt32(), bytes,
                root.TryGetProperty("Polygons", out var polygons) ? polygons.GetInt32() : root.GetProperty("Triangles").GetInt32());
        }
        finally { Gate.Release(); }
    }

    private static async Task RunWorkerAsync(string executable, string request, string run, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("--model-export-worker"); start.ArgumentList.Add("--request"); start.ArgumentList.Add(request);
        start.Environment["TMP"] = start.Environment["TEMP"] = run;
        using var process = new Process { StartInfo = start };
        using var log = new StreamWriter(System.IO.Path.Combine(run, "process.log")) { AutoFlush = true };
        var logGate = new object();
        void Write(string line) { lock (logGate) log.WriteLine(line); }
        if (!process.Start()) throw new InvalidOperationException("导出组件未启动。");
        Task? stdout = null, stderr = null;
        var lastError = "";
        try
        {
            using var guard = new BlenderProcessGuard(process, 2L << 30);
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            async Task Drain(StreamReader reader, bool error)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    Write(line.Length > 8192 ? line[..8192] : line);
                    if (error && string.IsNullOrWhiteSpace(lastError) && !string.IsNullOrWhiteSpace(line))
                        lastError = line.Length > 600 ? line[..600] : line;
                }
            }
            stdout = Drain(process.StandardOutput, false); stderr = Drain(process.StandardError, true);
            var clock = Stopwatch.StartNew();
            while (!process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                process.Refresh();
                if (process.PrivateMemorySize64 > (2L << 30)) throw new IOException("本次转换超过 2 GB 内存限额，请分楼层导出。");
                if (clock.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("导出超过 3 分钟，已停止本次转换；请分楼层重试。");
                await Task.Delay(100, token);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new IOException("导出组件未完成，原文件未改动。" + lastError + " 日志：" + run);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            if (stdout != null && stderr != null) await Task.WhenAll(stdout, stderr);
        }
    }

    private sealed class CommitJournal
    {
        public string Id { get; set; } = "";
        public bool HadModel { get; set; }
        public bool HadMetadata { get; set; }
        public string? OldModelHash { get; set; }
        public string? OldMetadataHash { get; set; }
        public string NewModelHash { get; set; } = "";
        public string NewMetadataHash { get; set; } = "";
    }

    private static string? FileHash(string path)
    {
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static async Task<FileStream> LockDestinationAsync(string destination, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(destination + ".wanluo.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(100, token); }
        }
    }

    // Fixed journal + per-destination file lock cover other Studio processes and interrupted commits.
    internal static async Task CommitAsync(string source, string destination, string manifest, CancellationToken token)
    {
        var folder = System.IO.Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        await using var destinationLock = await LockDestinationAsync(destination, token);
        RecoverPendingCommit(destination);
        var id = Guid.NewGuid().ToString("N");
        var temp = System.IO.Path.Combine(folder, ".wanluo-" + id + System.IO.Path.GetExtension(destination));
        var sidecar = destination + ".wanluo.json";
        var metaTemp = temp + ".json";
        var backup = temp + ".backup";
        var metaBackup = metaTemp + ".backup";
        var journalPath = destination + ".wanluo.txn.json";
        var journalWritten = false; var succeeded = false;
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, token);
            await File.WriteAllTextAsync(metaTemp, manifest, token);
            token.ThrowIfCancellationRequested();
            var journal = new CommitJournal { Id = id, HadModel = File.Exists(destination), HadMetadata = File.Exists(sidecar),
                OldModelHash = FileHash(destination), OldMetadataHash = FileHash(sidecar),
                NewModelHash = FileHash(temp)!, NewMetadataHash = FileHash(metaTemp)! };
            await using (var file = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, journal, cancellationToken: token);
                await file.FlushAsync(token);
                file.Flush(true);
            }
            journalWritten = true;
            if (File.Exists(destination)) File.Replace(temp, destination, backup); else File.Move(temp, destination);
            if (File.Exists(sidecar)) File.Replace(metaTemp, sidecar, metaBackup); else File.Move(metaTemp, sidecar);
            succeeded = true;
        }
        catch
        {
            if (journalWritten) RecoverPendingCommit(destination);
            throw;
        }
        finally
        {
            // Preserve backups if an I/O error also prevents rollback.
            foreach (var file in succeeded ? new[] { temp, metaTemp, backup, metaBackup } : new[] { temp, metaTemp })
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (succeeded || !journalWritten)
                try { File.Delete(journalPath); } catch (IOException) { }
        }
    }

    internal static void RecoverPendingCommit(string destination)
    {
        var journalPath = destination + ".wanluo.txn.json";
        if (!File.Exists(journalPath)) return;
        using var input = File.OpenRead(journalPath);
        if (input.Length > 8192) throw new IOException("导出恢复记录无效；已保留文件，未继续覆盖。");
        var journal = JsonSerializer.Deserialize<CommitJournal>(input)
            ?? throw new IOException("无法读取导出恢复记录，未继续覆盖。");
        input.Close();
        if (!Guid.TryParseExact(journal.Id, "N", out _)) throw new IOException("导出恢复记录标识无效。");
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, ".wanluo-" + journal.Id
            + System.IO.Path.GetExtension(destination));
        var metadata = destination + ".wanluo.json";
        if (FileHash(destination) != journal.NewModelHash || FileHash(metadata) != journal.NewMetadataHash)
        {
            var restoreModel = PlanRestore(destination, temp + ".backup", journal.HadModel, journal.OldModelHash, journal.NewModelHash);
            var restoreMetadata = PlanRestore(metadata, temp + ".json.backup", journal.HadMetadata, journal.OldMetadataHash, journal.NewMetadataHash);
            restoreModel();
            restoreMetadata();
        }
        foreach (var path in new[] { temp, temp + ".json", temp + ".backup", temp + ".json.backup" }) File.Delete(path);
        File.Delete(journalPath);

        static Action PlanRestore(string target, string backup, bool existed, string? oldHash, string newHash)
        {
            var current = FileHash(target);
            if (existed)
            {
                if (current == oldHash) return () => { };
                if (!File.Exists(backup) || FileHash(backup) != oldHash || (current != null && current != newHash))
                    throw new IOException("导出中断后的文件状态有冲突；已保留备份和恢复记录，请先核对文件。");
                return () => File.Move(backup, target, true);
            }
            else if (current == newHash) return () => File.Delete(target);
            else if (current != null) throw new IOException("导出恢复发现其他来源的文件，未覆盖。");
            return () => { };
        }
    }
}
