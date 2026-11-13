using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BatchPdfPublisher.Services;

namespace Wanluo.StandaloneWorkbench
{
    internal sealed class DrawingSnapshot
    {
        public int SchemaVersion { get; set; }
        public int ObjectCount { get; set; }
        public List<DrawingLayer> Layers { get; set; } = new List<DrawingLayer>();
        public List<DrawingSpace> Spaces { get; set; } = new List<DrawingSpace>();

        public static async Task<DrawingSnapshot> ReadAsync(string source, CancellationToken token)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("找不到图纸文件。", source);
            if (!File.Exists(WorkbenchRuntime.ReaderPath)) throw new FileNotFoundException("缺少独立读图引擎，请使用完整发布包。", WorkbenchRuntime.ReaderPath);
            var jobs = Path.Combine(UserDataPaths.TemporaryDirectory, "StandaloneDrawing");
            Directory.CreateDirectory(jobs);
            var job = Path.Combine(jobs, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            var result = Path.Combine(job, "snapshot.json");
            try
            {
                var start = new ProcessStartInfo(WorkbenchRuntime.ReaderPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                start.ArgumentList.Add(Path.GetFullPath(source));
                start.ArgumentList.Add(result);
                using (var process = Process.Start(start) ?? throw new IOException("读图引擎未能启动。"))
                using (token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }))
                {
                    var error = process.StandardError.ReadToEndAsync();
                    var output = process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    var message = await error;
                    await output;
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0 || !File.Exists(result))
                        throw new IOException("图纸读取未完成。" + message.Trim());
                }
                using (var stream = File.OpenRead(result))
                {
                    var snapshot = await JsonSerializer.DeserializeAsync<DrawingSnapshot>(stream,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, token);
                    if (snapshot == null || snapshot.SchemaVersion != 1)
                        throw new InvalidDataException("读图结果版本不匹配。");
                    return snapshot;
                }
            }
            finally
            {
                if (File.Exists(result)) File.Delete(result);
                Directory.Delete(job);
            }
        }
    }

    internal sealed class DrawingLayer
    {
        public string Name { get; set; }
        public bool Visible { get; set; }
    }

    internal sealed class DrawingSpace
    {
        public string Name { get; set; }
        public List<DrawingLine> Lines { get; set; } = new List<DrawingLine>();
        public List<DrawingText> Texts { get; set; } = new List<DrawingText>();
        public Dictionary<string, int> Unsupported { get; set; } = new Dictionary<string, int>();
    }

    internal sealed class DrawingLine
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public string Layer { get; set; }
    }

    internal sealed class DrawingText
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Height { get; set; }
        public string Value { get; set; }
        public string Layer { get; set; }
    }
}
