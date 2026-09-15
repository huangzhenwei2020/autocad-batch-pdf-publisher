using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BatchPdfPublisher.Services
{
    internal sealed class LineVisionPaddleOcrWorkerClient : LineVisionOcrWorkerClient
    {
        public LineVisionPaddleOcrWorkerClient()
            : base(ResolveWorkerPath())
        {
        }

        private static string ResolveWorkerPath()
        {
            var managed = LineVisionPaddleOcrComponentService.ResolveWorkerPath();
            if (!string.IsNullOrWhiteSpace(managed)) return managed;
            var assemblyDirectory = Path.GetDirectoryName(typeof(LineVisionPaddleOcrWorkerClient).Assembly.Location);
            var fileName = "LineVisionPaddleOcrWorker.exe";
            var candidates = new[]
            {
                Path.Combine(assemblyDirectory, fileName),
                Path.Combine(assemblyDirectory, "LineVisionPaddleOcrWorker", fileName),
                Path.Combine(UserDataPaths.PluginDirectory, "OcrEngine", "LineVisionPaddleOcrWorker", fileName),
                Path.Combine(UserDataPaths.RootDirectory, "运行文件", "LineVisionPaddleOcrWorker", fileName)
            };
            foreach (var candidate in candidates) if (File.Exists(candidate)) return candidate;
            return candidates[0];
        }

        public override string EngineId { get { return "paddleocr-worker"; } }
        public override string DisplayName { get { return "PaddleOCR 增强（独立进程）"; } }
        public override LineVisionOcrEngineCapabilities Capabilities
        {
            get
            {
                return new LineVisionOcrEngineCapabilities
                {
                    EngineId = EngineId,
                    DisplayName = DisplayName,
                    EngineVersion = "3.7.0/PP-OCRv6-small",
                    ProtocolVersion = LineVisionOcrProtocol.CurrentVersion,
                    SupportsPolygon = true,
                    SupportsConfidence = true,
                    SupportsRotation = true,
                    SupportsProgress = true,
                    Languages = new List<string> { "zh-Hans-CN", "en-US" }
                };
            }
        }
    }

    internal sealed class LineVisionFallbackOcrEngine : ILineVisionOcrEngine
    {
        private readonly ILineVisionOcrEngine _primary;
        private readonly ILineVisionOcrEngine _fallback;

        public LineVisionFallbackOcrEngine(ILineVisionOcrEngine primary, ILineVisionOcrEngine fallback)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        }

        public string EngineId { get { return "automatic-ocr"; } }
        public string DisplayName { get { return "自动选择 OCR"; } }
        public bool IsAvailable { get { return _primary.IsAvailable || _fallback.IsAvailable; } }
        public LineVisionOcrEngineCapabilities Capabilities { get { return _primary.IsAvailable ? _primary.Capabilities : _fallback.Capabilities; } }

        public async Task<LineVisionOcrPageResult> RecognizeAsync(string imagePath, LineVisionOcrOptions options, CancellationToken cancellationToken)
        {
            Exception primaryFailure = null;
            if (_primary.IsAvailable)
            {
                try { return await _primary.RecognizeAsync(imagePath, options, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                    if (options != null && options.Progress != null)
                        options.Progress.Report(new LineVisionOcrWorkerProgress { ProtocolVersion = LineVisionOcrProtocol.CurrentVersion, Type = "progress", Percent = 0, Stage = "fallback", Message = "增强 OCR 未完成，正在回退 Windows OCR……" });
                }
            }

            if (_fallback.IsAvailable)
                return await _fallback.RecognizeAsync(imagePath, options, cancellationToken).ConfigureAwait(false);

            if (primaryFailure != null)
                throw new InvalidOperationException("PaddleOCR 增强识别失败，且 Windows OCR Worker 不可用。", primaryFailure);
            throw new FileNotFoundException("未找到可用的 OCR Worker。请重新运行最新版启动器；图片仍可只识别线条。");
        }
    }

    internal static class LineVisionOcrEngineSelector
    {
        public static ILineVisionOcrEngine Create(LineVisionOcrMode mode)
        {
            if (mode == LineVisionOcrMode.Paddle) return new LineVisionPaddleOcrWorkerClient();
            if (mode == LineVisionOcrMode.Windows) return new LineVisionOcrWorkerClient();
            return CreateAutomatic();
        }

        public static ILineVisionOcrEngine CreateAutomatic()
        {
            return new LineVisionFallbackOcrEngine(new LineVisionPaddleOcrWorkerClient(), new LineVisionOcrWorkerClient());
        }
    }
}
