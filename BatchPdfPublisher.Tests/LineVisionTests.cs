using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class LineVisionTests
{
    private static int _executed;

    private static void Main()
    {
        Run("RecognizesOrthogonalAndDiagonalLines", RecognizesOrthogonalAndDiagonalLines);
        Run("HonorsCropRegion", HonorsCropRegion);
        Run("MergesSmallCollinearGap", MergesSmallCollinearGap);
        Run("DoesNotMergeOppositeDiagonals", DoesNotMergeOppositeDiagonals);
        Run("PreservesArbitraryLineAngle", PreservesArbitraryLineAngle);
        Run("UsesConfigurableOrthogonalTolerance", UsesConfigurableOrthogonalTolerance);
        Run("RecognizesClosedCircle", RecognizesClosedCircle);
        Run("RecognizesQuarterArc", RecognizesQuarterArc);
        Run("DoesNotTreatBentLineAsArc", DoesNotTreatBentLineAsArc);
        Run("BuildsClosedPolylineFromRectangle", BuildsClosedPolylineFromRectangle);
        Run("KeepsTIntersectionAsLines", KeepsTIntersectionAsLines);
        Run("ReconstructsPolylineFromImage", ReconstructsPolylineFromImage);
        Run("CanDisablePolylineReconstruction", CanDisablePolylineReconstruction);
        Run("SnapsWorkerPolylineUsingUserTolerance", SnapsWorkerPolylineUsingUserTolerance);
        Run("MasksRecognizedTextBeforeLineDetection", MasksRecognizedTextBeforeLineDetection);
        Run("UsesPolygonInsteadOfBoundingBoxForTextMask", UsesPolygonInsteadOfBoundingBoxForTextMask);
        Run("ClassifiesLowConfidenceTextUsingUserThreshold", ClassifiesLowConfidenceTextUsingUserThreshold);
        Run("AcceptsOnlyMatchingOcrProgressEvents", AcceptsOnlyMatchingOcrProgressEvents);
        Run("ExposesVersionedOcrEngineCapabilities", ExposesVersionedOcrEngineCapabilities);
        Run("FallsBackWhenEnhancedOcrFails", FallsBackWhenEnhancedOcrFails);
        Run("SelectsRequestedOcrEngine", SelectsRequestedOcrEngine);
        Run("NormalizesEngineeringOcrSymbols", NormalizesEngineeringOcrSymbols);
        Run("DoesNotRewriteNarrativeLetterX", DoesNotRewriteNarrativeLetterX);
        Run("PlacesRotatedOcrTextFromPolygon", PlacesRotatedOcrTextFromPolygon);
        Run("PlacesUpsideDownOcrTextFromPolygon", PlacesUpsideDownOcrTextFromPolygon);
        Run("DeduplicatesOverlappingOcrRegionsSafely", DeduplicatesOverlappingOcrRegionsSafely);
        Run("KeepsDistinctOrSeparatedOcrRegions", KeepsDistinctOrSeparatedOcrRegions);
        var worker = Environment.GetEnvironmentVariable("WANLUO_LINEVISION_OCR_WORKER");
        if (!string.IsNullOrWhiteSpace(worker) && File.Exists(worker)) Run("RecognizesTextThroughIsolatedWorker", () => RecognizesTextThroughIsolatedWorker(worker));
        var paddleWorker = Environment.GetEnvironmentVariable("WANLUO_LINEVISION_PADDLE_WORKER");
        if (!string.IsNullOrWhiteSpace(paddleWorker) && File.Exists(paddleWorker))
        {
            Run("RecognizesTextThroughPaddleWorker", () => RecognizesTextThroughPaddleWorker(paddleWorker));
            Run("RecognizesRotatedTextThroughPaddleWorker", () => RecognizesRotatedTextThroughPaddleWorker(paddleWorker));
        }
        var vectorWorker = Environment.GetEnvironmentVariable("WANLUO_LINEVISION_VECTOR_WORKER");
        if (!string.IsNullOrWhiteSpace(vectorWorker) && File.Exists(vectorWorker)) Run("VectorizesThroughIsolatedWorker", () => VectorizesThroughIsolatedWorker(vectorWorker));
        Console.WriteLine("Executed " + _executed + " LineVision tests; 0 failed.");
    }

    private static void RecognizesOrthogonalAndDiagonalLines()
    {
        WithImage(260, 200, graphics =>
        {
            graphics.DrawLine(Pens.Black, 20, 35, 230, 35);
            graphics.DrawLine(Pens.Black, 55, 20, 55, 180);
            graphics.DrawLine(Pens.Black, 80, 170, 190, 60);
        }, path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, null, Settings()))
            {
                True(result.Segments.Any(x => x.Direction == LineVisionDirection.Horizontal && x.Length > 180), "未识别主水平线");
                True(result.Segments.Any(x => x.Direction == LineVisionDirection.Vertical && x.Length > 130), "未识别主垂直线");
                True(result.Segments.Any(x => x.Direction == LineVisionDirection.Diagonal && x.Length > 120), "未识别45度斜线");
            }
        });
    }

    private static void HonorsCropRegion()
    {
        WithImage(300, 160, graphics =>
        {
            graphics.DrawLine(Pens.Black, 10, 25, 130, 25);
            graphics.DrawLine(Pens.Black, 170, 115, 290, 115);
        }, path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, new Rectangle(150, 80, 145, 70), Settings()))
            {
                Equal(145, result.Width); Equal(70, result.Height);
                True(result.Segments.Any(x => x.Direction == LineVisionDirection.Horizontal && x.Length > 100), "裁剪范围内线段丢失");
            }
        });
    }

    private static void ClassifiesLowConfidenceTextUsingUserThreshold()
    {
        True(LineVisionOcrConfidence.IsLow(0.69d, 0.70d), "低于用户阈值的文字应标记为待复核");
        True(!LineVisionOcrConfidence.IsLow(0.70d, 0.70d), "达到用户阈值的文字不应标记为低置信度");
        True(LineVisionOcrConfidence.IsLow(double.NaN, 0.70d), "无效置信度必须保守标记为待复核");
        Equal("待复核", LineVisionOcrConfidence.StatusText(0.30d, 0.70d));
        Equal("正常", LineVisionOcrConfidence.StatusText(0.95d, 0.70d));
    }

    private static void AcceptsOnlyMatchingOcrProgressEvents()
    {
        var matching = "{\"ProtocolVersion\":" + LineVisionOcrProtocol.CurrentVersion + ",\"RequestId\":\"job-1\",\"Type\":\"progress\",\"Percent\":130,\"Stage\":\"inference\",\"Message\":\"正在识别\"}";
        var parsed = LineVisionOcrWorkerClient.TryParseProgressLine(matching, "job-1");
        True(parsed != null, "当前任务的有效进度事件应被接受");
        Equal(100, parsed.Percent);
        Equal("inference", parsed.Stage);
        True(LineVisionOcrWorkerClient.TryParseProgressLine(matching, "job-2") == null, "其他任务的进度不得污染当前 UI");
        True(LineVisionOcrWorkerClient.TryParseProgressLine("Paddle runtime log", "job-1") == null, "第三方标准输出日志不应被当作进度");
        var oldProtocol = matching.Replace("\"ProtocolVersion\":" + LineVisionOcrProtocol.CurrentVersion, "\"ProtocolVersion\":1");
        True(LineVisionOcrWorkerClient.TryParseProgressLine(oldProtocol, "job-1") == null, "旧协议进度不得被接受");
    }

    private static void MergesSmallCollinearGap()
    {
        var lines = new[]
        {
            Segment(0, 10, 40, 10, LineVisionDirection.Horizontal),
            Segment(43, 11, 90, 11, LineVisionDirection.Horizontal)
        };
        var merged = LineVisionProcessor.MergeSegments(lines, 2, 4);
        Equal(1, merged.Count); True(merged[0].Length >= 89, "共线线段没有跨越小间隙合并");
        Equal(merged[0].Y1, merged[0].Y2);
    }

    private static void DoesNotMergeOppositeDiagonals()
    {
        var lines = new[]
        {
            Segment(0, 0, 50, 50, LineVisionDirection.Diagonal),
            Segment(0, 50, 50, 0, LineVisionDirection.Diagonal)
        };
        Equal(2, LineVisionProcessor.MergeSegments(lines, 3, 5).Count);
    }

    private static void RecognizesClosedCircle()
    {
        WithImage(180, 180, graphics => graphics.DrawEllipse(Pens.Black, 45, 45, 90, 90), path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, null, Settings()))
            {
                True(result.Circles.Any(circle => Math.Abs(circle.CenterX - 90) < 5 && Math.Abs(circle.CenterY - 90) < 5 && Math.Abs(circle.Radius - 45) < 7), "未识别闭合圆形");
                Equal(0, result.Arcs.Count);
            }
        });
    }

    private static void PreservesArbitraryLineAngle()
    {
        WithImage(260, 180, graphics =>
        {
            using (var pen = new Pen(Color.Black, 3f)) graphics.DrawLine(pen, 25, 145, 225, 70);
        }, path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, null, Settings()))
            {
                var line = result.Segments.Where(item => item.Direction == LineVisionDirection.Angled).OrderByDescending(item => item.Length).FirstOrDefault();
                True(line != null && line.Length > 170, "任意角度直线没有保留");
                var angle = Math.Abs(Math.Atan2(line.Y2 - line.Y1, line.X2 - line.X1) * 180d / Math.PI);
                True(Math.Abs(angle - 20.6d) < 3d, "任意角度直线被错误拉成横线、竖线或45度线");
            }
        });
    }

    private static void UsesConfigurableOrthogonalTolerance()
    {
        WithImage(280, 120, graphics =>
        {
            using (var pen = new Pen(Color.Black, 3f)) graphics.DrawLine(pen, 20, 70, 255, 58);
        }, path =>
        {
            var strict = Settings(); strict.OrthogonalToleranceDegrees = 2d;
            using (var result = LineVisionProcessor.Analyze(path, null, strict))
                True(result.Segments.Any(item => item.Direction == LineVisionDirection.Angled && item.Length > 190), "严格容差下斜线被错误吸附为水平线");
            var loose = Settings(); loose.OrthogonalToleranceDegrees = 5d;
            using (var result = LineVisionProcessor.Analyze(path, null, loose))
                True(result.Segments.Any(item => item.Direction == LineVisionDirection.Horizontal && item.Length > 190), "宽松容差没有按设置吸附为水平线");
        });
    }

    private static void RecognizesQuarterArc()
    {
        WithImage(220, 220, graphics =>
        {
            using (var pen = new Pen(Color.Black, 3f)) graphics.DrawArc(pen, 40, 40, 140, 140, 5, 95);
        }, path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, null, Settings()))
            {
                var arc = result.Arcs.OrderByDescending(item => item.Confidence).FirstOrDefault();
                True(arc != null && Math.Abs(arc.Radius - 70d) < 10d, "未识别常见门扇圆弧");
                True(arc.SweepAngleDegrees > 70d && arc.SweepAngleDegrees < 120d, "圆弧角度范围不正确");
            }
        });
    }

    private static void DoesNotTreatBentLineAsArc()
    {
        WithImage(220, 160, graphics =>
        {
            using (var pen = new Pen(Color.Black, 3f)) { graphics.DrawLine(pen, 20, 120, 105, 35); graphics.DrawLine(pen, 105, 35, 195, 120); }
        }, path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, null, Settings())) Equal(0, result.Arcs.Count);
        });
    }

    private static void BuildsClosedPolylineFromRectangle()
    {
        var lines = new List<LineVisionSegment>
        {
            Segment(10, 10, 100, 10, LineVisionDirection.Horizontal), Segment(100, 10, 100, 70, LineVisionDirection.Vertical),
            Segment(100, 70, 10, 70, LineVisionDirection.Horizontal), Segment(10, 70, 10, 10, LineVisionDirection.Vertical)
        };
        var result = LineVisionProcessor.BuildPolylines(lines, 1.5d);
        Equal(1, result.Count); True(result[0].IsClosed && result[0].Points.Count == 4, "矩形没有重构为闭合折线");
        True(lines.All(line => !line.IsEnabled), "已进入折线的源线段仍会重复插入");
    }

    private static void KeepsTIntersectionAsLines()
    {
        var lines = new List<LineVisionSegment>
        {
            Segment(10, 40, 70, 40, LineVisionDirection.Horizontal), Segment(70, 40, 130, 40, LineVisionDirection.Horizontal),
            Segment(70, 40, 70, 100, LineVisionDirection.Vertical)
        };
        Equal(0, LineVisionProcessor.BuildPolylines(lines, 1.5d).Count);
        True(lines.All(line => line.IsEnabled), "T形交叉被错误合并成折线");
    }

    private static void ReconstructsPolylineFromImage()
    {
        WithImage(220, 160, graphics =>
        {
            using (var pen = new Pen(Color.Black, 3f)) graphics.DrawRectangle(pen, 35, 30, 145, 95);
        }, path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, null, Settings()))
            {
                if (!result.Polylines.Any(item => item.IsClosed && item.Points.Count >= 4))
                    Console.WriteLine("DEBUG segments=" + string.Join(" | ", result.Segments.Select(item => string.Format("{0}:({1:0.0},{2:0.0})-({3:0.0},{4:0.0}) enabled={5}", item.Direction, item.X1, item.Y1, item.X2, item.Y2, item.IsEnabled))));
                True(result.Polylines.Any(item => item.IsClosed && item.Points.Count >= 4), "图像中的闭合矩形没有重构为折线");
            }
        });
    }

    private static void CanDisablePolylineReconstruction()
    {
        WithImage(220, 160, graphics => { using (var pen = new Pen(Color.Black, 3f)) graphics.DrawRectangle(pen, 35, 30, 145, 95); }, path =>
        {
            var settings = Settings(); settings.BuildPolylines = false;
            using (var result = LineVisionProcessor.Analyze(path, null, settings))
            {
                Equal(0, result.Polylines.Count); True(result.Segments.Any(item => item.IsEnabled), "关闭折线重构后源直线没有保留");
            }
        });
    }

    private static void SnapsWorkerPolylineUsingUserTolerance()
    {
        var strict = new LineVisionPolyline { Points = new List<PointF> { new PointF(0, 0), new PointF(100, 5) } };
        LineVisionVectorWorkerClient.SnapOrthogonal(strict, 2d);
        True(Math.Abs(strict.Points[1].Y - 5) < 0.01, "严格容差错误拉平骨架折线");
        var loose = new LineVisionPolyline { Points = new List<PointF> { new PointF(0, 0), new PointF(100, 5) } };
        LineVisionVectorWorkerClient.SnapOrthogonal(loose, 5d);
        True(Math.Abs(loose.Points[1].Y) < 0.01, "宽松容差没有拉平骨架折线");
    }

    private static void MasksRecognizedTextBeforeLineDetection()
    {
        WithImage(240, 100, graphics => graphics.DrawLine(Pens.Black, 10, 50, 230, 50), path =>
        {
            var text = new LineVisionOcrTextRegion
            {
                Text = "3600", Confidence = 0.9, IsEnabled = true,
                Polygon = new[] { new PointF(95, 38), new PointF(145, 38), new PointF(145, 62), new PointF(95, 62) }
            };
            using (var result = LineVisionProcessor.Analyze(path, null, Settings(), new[] { text }, true, 2, CancellationToken.None))
                True(!result.Segments.Any(line => line.Direction == LineVisionDirection.Horizontal && Math.Min(line.X1, line.X2) < 95 && Math.Max(line.X1, line.X2) > 145), "文字遮罩后仍生成了穿过文字的直线");
        });
    }

    private static void RecognizesTextThroughIsolatedWorker(string workerPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoLineVisionOcrTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); UserDataPaths.TestRootDirectory = root;
        try
        {
            WithImage(900, 260, graphics =>
            {
                using (var font = new Font("Arial", 68f, FontStyle.Bold)) graphics.DrawString("ROOM 3600", font, Brushes.Black, 25, 70);
            }, path =>
            {
                var engine = new LineVisionOcrWorkerClient(workerPath);
                var progress = new RecordingProgress<LineVisionOcrWorkerProgress>();
                var result = engine.RecognizeAsync(path, new LineVisionOcrOptions { Language = "en-US", MinimumConfidence = 0.5, Progress = progress }, CancellationToken.None).GetAwaiter().GetResult();
                Equal(LineVisionOcrProtocol.CurrentVersion, result.ProtocolVersion);
                Equal("windows-ocr-worker", result.EngineId);
                True(result.TextRegions.Any(), "独立 OCR Worker 没有返回文字区域");
                True(result.TextRegions.Any(item => (item.Text ?? string.Empty).IndexOf("3600", StringComparison.OrdinalIgnoreCase) >= 0), "独立 OCR Worker 没有识别尺寸数字 3600");
                True(progress.Snapshot().Any(item => item.Stage == "inference"), "Windows OCR Worker 没有报告推理阶段");
                True(progress.Snapshot().Any(item => item.Percent == 100), "Windows OCR Worker 没有报告完成进度");
            });
        }
        finally { UserDataPaths.TestRootDirectory = null; try { Directory.Delete(root, true); } catch { } }
    }

    private static void UsesPolygonInsteadOfBoundingBoxForTextMask()
    {
        using (var bitmap = new Bitmap(200, 200, PixelFormat.Format24bppRgb))
        {
            var dark = Enumerable.Repeat(true, bitmap.Width * bitmap.Height).ToArray();
            var text = OcrRegion("旋转文字", 0.95d, new[]
            {
                new PointF(100, 20), new PointF(180, 100), new PointF(100, 180), new PointF(20, 100)
            });
            LineVisionTextMasker.Apply(bitmap, dark, new[] { text }, 1d, 0d);
            True(!dark[100 * bitmap.Width + 100], "多边形内部没有被遮罩");
            True(dark[30 * bitmap.Width + 30], "多边形外但外包矩形内的线稿被误删");
        }
    }

    private static void RecognizesTextThroughPaddleWorker(string workerPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoLineVisionPaddleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); UserDataPaths.TestRootDirectory = root;
        try
        {
            WithImage(900, 260, graphics =>
            {
                using (var font = new Font("Arial", 68f, FontStyle.Bold)) graphics.DrawString("ROOM 3600", font, Brushes.Black, 25, 70);
            }, path =>
            {
                // The production client resolves the worker through the same
                // environment variable that the release script sets here.
                var engine = new LineVisionPaddleOcrWorkerClient();
                var progress = new RecordingProgress<LineVisionOcrWorkerProgress>();
                var result = engine.RecognizeAsync(path, new LineVisionOcrOptions { Language = "en-US", MinimumConfidence = 0.5, Progress = progress }, CancellationToken.None).GetAwaiter().GetResult();
                Equal(LineVisionOcrProtocol.CurrentVersion, result.ProtocolVersion);
                Equal("paddleocr-worker", result.EngineId);
                True(result.TextRegions.Any(), "PaddleOCR 用户组件没有返回文字区域");
                True(result.TextRegions.Any(item => (item.Text ?? string.Empty).IndexOf("3600", StringComparison.OrdinalIgnoreCase) >= 0), "PaddleOCR 用户组件没有识别尺寸数字 3600");
                True(result.TextRegions.All(item => item.Polygon != null && item.Polygon.Length == 4), "PaddleOCR 用户组件没有返回四点文字框");
                True(result.TextRegions.All(item => !string.IsNullOrWhiteSpace(item.OriginalText)), "PaddleOCR 用户组件没有保留 OCR 原文");
                True(progress.Snapshot().Any(item => item.Stage == "inference"), "PaddleOCR 用户组件没有报告推理阶段");
                True(progress.Snapshot().Any(item => item.Percent == 100), "PaddleOCR 用户组件没有报告完成进度");
            });
        }
        finally { UserDataPaths.TestRootDirectory = null; try { Directory.Delete(root, true); } catch { } }
    }

    private static void RecognizesRotatedTextThroughPaddleWorker(string workerPath)
    {
        var previous = Environment.GetEnvironmentVariable("WANLUO_LINEVISION_PADDLE_WORKER");
        Environment.SetEnvironmentVariable("WANLUO_LINEVISION_PADDLE_WORKER", workerPath);
        try
        {
            foreach (var expectedAngle in new[] { 90f, 180f, 270f })
            {
                WithImage(900, 900, graphics =>
                {
                    graphics.TranslateTransform(450f, 450f); graphics.RotateTransform(expectedAngle);
                    using (var font = new Font("Arial", 68f, FontStyle.Bold)) graphics.DrawString("ROOM 3600", font, Brushes.Black, -260f, -50f);
                    graphics.ResetTransform();
                }, path =>
                {
                    var result = new LineVisionPaddleOcrWorkerClient().RecognizeAsync(path, new LineVisionOcrOptions { Language = "en-US", MinimumConfidence = 0.4 }, CancellationToken.None).GetAwaiter().GetResult();
                    var text = result.TextRegions.FirstOrDefault(item => (item.Text ?? string.Empty).IndexOf("3600", StringComparison.OrdinalIgnoreCase) >= 0);
                    True(text != null, "PaddleOCR 没有识别 " + expectedAngle + " 度文字");
                    True(Math.Abs(LineVisionOcrGeometry.NormalizeDegrees(text.RotationDegrees - expectedAngle)) < 15d, "PaddleOCR 返回的 " + expectedAngle + " 度文字方向不正确：" + text.RotationDegrees);
                });
            }
        }
        finally { Environment.SetEnvironmentVariable("WANLUO_LINEVISION_PADDLE_WORKER", previous); }
    }

    private static void ExposesVersionedOcrEngineCapabilities()
    {
        var engine = new LineVisionOcrWorkerClient("missing-worker.exe");
        Equal("windows-ocr-worker", engine.EngineId);
        Equal(LineVisionOcrProtocol.CurrentVersion, engine.Capabilities.ProtocolVersion);
        True(engine.Capabilities.SupportsRotation, "Windows OCR 能力信息没有声明页面旋转支持");
        True(engine.Capabilities.SupportsProgress, "Windows OCR 能力信息没有声明分阶段进度支持");
        True(engine.Capabilities.Languages.Contains("zh-Hans-CN"), "Windows OCR 能力信息缺少简体中文");
    }

    private static void FallsBackWhenEnhancedOcrFails()
    {
        var primary = new FakeOcrEngine("paddleocr-worker", true, () => throw new InvalidOperationException("simulated Paddle failure"));
        var fallback = new FakeOcrEngine("windows-ocr-worker", true, () => new LineVisionOcrPageResult { ProtocolVersion = LineVisionOcrProtocol.CurrentVersion, EngineId = "windows-ocr-worker" });
        var result = new LineVisionFallbackOcrEngine(primary, fallback)
            .RecognizeAsync("unused.png", new LineVisionOcrOptions(), CancellationToken.None).GetAwaiter().GetResult();
        Equal("windows-ocr-worker", result.EngineId);
        Equal(1, primary.CallCount);
        Equal(1, fallback.CallCount);
    }

    private static void SelectsRequestedOcrEngine()
    {
        Equal("automatic-ocr", LineVisionOcrEngineSelector.Create(LineVisionOcrMode.Automatic).EngineId);
        Equal("paddleocr-worker", LineVisionOcrEngineSelector.Create(LineVisionOcrMode.Paddle).EngineId);
        Equal("windows-ocr-worker", LineVisionOcrEngineSelector.Create(LineVisionOcrMode.Windows).EngineId);
    }

    private static void NormalizesEngineeringOcrSymbols()
    {
        var source = "  １２００ X １５００　\n φ 100  + / - 0。030  1 ： 2  ";
        var normalized = LineVisionOcrTextNormalizer.Normalize(source);
        Equal("1200×1500" + Environment.NewLine + "Φ100 ±0.030 1:2", normalized);
    }

    private static void DoesNotRewriteNarrativeLetterX()
    {
        Equal("X轴与 A X B 保持原文", LineVisionOcrTextNormalizer.Normalize("X轴与 A X B 保持原文"));
    }

    private static void PlacesRotatedOcrTextFromPolygon()
    {
        var region = new LineVisionOcrTextRegion
        {
            Text = "竖排文字", RotationDegrees = 90d,
            Polygon = new[] { new PointF(80, 20), new PointF(80, 180), new PointF(50, 180), new PointF(50, 20) }
        };
        var placement = LineVisionOcrGeometry.GetPlacement(region);
        Near(50d, placement.BaselineOrigin.X); Near(20d, placement.BaselineOrigin.Y);
        Near(30d, placement.TextHeightPixels); Near(90d, placement.RotationDegrees);
    }

    private static void PlacesUpsideDownOcrTextFromPolygon()
    {
        var region = new LineVisionOcrTextRegion
        {
            Text = "倒置文字", RotationDegrees = 180d,
            Polygon = new[] { new PointF(20, 30), new PointF(180, 30), new PointF(180, 55), new PointF(20, 55) }
        };
        var placement = LineVisionOcrGeometry.GetPlacement(region);
        Near(180d, placement.BaselineOrigin.X); Near(30d, placement.BaselineOrigin.Y);
        Near(25d, placement.TextHeightPixels); Near(180d, placement.RotationDegrees);
    }

    private static void DeduplicatesOverlappingOcrRegionsSafely()
    {
        var weaker = OcrRegion("3600", 0.72d, new[] { new PointF(20, 20), new PointF(180, 20), new PointF(180, 50), new PointF(20, 50) });
        var stronger = OcrRegion("3 600", 0.96d, new[] { new PointF(22, 19), new PointF(182, 19), new PointF(182, 51), new PointF(22, 51) });
        var result = LineVisionOcrRegionDeduplicator.Deduplicate(new[] { weaker, stronger });
        Equal(1, result.Count); True(object.ReferenceEquals(stronger, result[0]), "重叠重复文字没有保留高置信度结果");
        var rotatedFirst = OcrRegion("±0.000", 0.80d, LineVisionOcrGeometry.CreatePolygon(new RectangleF(250, 40, 180, 28), 30d));
        var rotatedSecond = OcrRegion("±0.000", 0.91d, LineVisionOcrGeometry.CreatePolygon(new RectangleF(252, 41, 180, 28), 30d));
        var rotated = LineVisionOcrRegionDeduplicator.Deduplicate(new[] { rotatedFirst, rotatedSecond });
        Equal(1, rotated.Count); True(object.ReferenceEquals(rotatedSecond, rotated[0]), "旋转重复文字框没有安全去重");
    }

    private static void KeepsDistinctOrSeparatedOcrRegions()
    {
        var first = OcrRegion("3600", 0.92d, new[] { new PointF(20, 20), new PointF(180, 20), new PointF(180, 50), new PointF(20, 50) });
        var differentText = OcrRegion("3800", 0.94d, new[] { new PointF(22, 20), new PointF(182, 20), new PointF(182, 50), new PointF(22, 50) });
        var separated = OcrRegion("3600", 0.95d, new[] { new PointF(20, 80), new PointF(180, 80), new PointF(180, 110), new PointF(20, 110) });
        Equal(3, LineVisionOcrRegionDeduplicator.Deduplicate(new[] { first, differentText, separated }).Count);
    }

    private static LineVisionOcrTextRegion OcrRegion(string text, double confidence, PointF[] polygon)
    {
        return new LineVisionOcrTextRegion { Text = text, OriginalText = text, Confidence = confidence, Polygon = polygon, IsEnabled = true };
    }

    private static void VectorizesThroughIsolatedWorker(string workerPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoLineVisionVectorTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); UserDataPaths.TestRootDirectory = root;
        try
        {
            WithImage(360, 260, graphics => { using (var pen = new Pen(Color.Black, 7f)) { graphics.DrawRectangle(pen, 35, 35, 285, 185); graphics.DrawLine(pen, 175, 35, 175, 220); } }, path =>
            {
                var settings = Settings(); settings.VectorMode = LineVisionVectorMode.Centerline;
                var center = new LineVisionVectorWorkerClient(workerPath).VectorizeAsync(path, null, settings, null, 0, CancellationToken.None).GetAwaiter().GetResult();
                True(center.Polylines.Any(item => item.Source == "骨架中心线" && item.Points.Count >= 2), "独立矢量 Worker 没有返回中心线");
                settings.VectorMode = LineVisionVectorMode.Hybrid;
                var hybrid = new LineVisionVectorWorkerClient(workerPath).VectorizeAsync(path, null, settings, null, 0, CancellationToken.None).GetAwaiter().GetResult();
                True(hybrid.Polylines.Any(item => item.Source == "VTracer轮廓" && !item.IsEnabled), "混合模式没有返回默认关闭的 VTracer 候选");
                True(hybrid.WallRegions.Any(item => item.Outer.Count >= 3 && !item.IsEnabled), "混合模式没有返回默认关闭的墙体填充候选");
            });
        }
        finally { UserDataPaths.TestRootDirectory = null; try { Directory.Delete(root, true); } catch { } }
    }

    private static LineVisionSettings Settings()
    {
        return new LineVisionSettings { Threshold = 128, MinimumLineLengthPixels = 14, CloseGapPixels = 2, CollinearTolerancePixels = 3, MergeGapPixels = 5, DetectDiagonals = true, OrthogonalToleranceDegrees = 2d, BuildPolylines = true, VectorMode = LineVisionVectorMode.Legacy };
    }

    private static LineVisionSegment Segment(double x1, double y1, double x2, double y2, LineVisionDirection direction)
    {
        return new LineVisionSegment { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Direction = direction, Confidence = 1d };
    }

    private static void WithImage(int width, int height, Action<Graphics> draw, Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "WanluoLineVision-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White); graphics.SmoothingMode = SmoothingMode.None; draw(graphics); bitmap.Save(path, ImageFormat.Png);
            }
            test(path);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static void Run(string name, Action test) { test(); _executed++; Console.WriteLine("PASS " + name); }
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Expected " + expected + ", actual " + actual); }
    private static void Near(double expected, double actual) { if (Math.Abs(expected - actual) > 0.01d) throw new InvalidOperationException("Expected near " + expected + ", actual " + actual); }

    private sealed class FakeOcrEngine : ILineVisionOcrEngine
    {
        private readonly Func<LineVisionOcrPageResult> _recognize;
        public FakeOcrEngine(string engineId, bool available, Func<LineVisionOcrPageResult> recognize) { EngineId = engineId; IsAvailable = available; _recognize = recognize; }
        public int CallCount { get; private set; }
        public string EngineId { get; private set; }
        public string DisplayName { get { return EngineId; } }
        public bool IsAvailable { get; private set; }
        public LineVisionOcrEngineCapabilities Capabilities { get { return new LineVisionOcrEngineCapabilities { EngineId = EngineId, DisplayName = DisplayName, ProtocolVersion = 1 }; } }
        public Task<LineVisionOcrPageResult> RecognizeAsync(string imagePath, LineVisionOcrOptions options, CancellationToken cancellationToken)
        {
            CallCount++;
            try { return Task.FromResult(_recognize()); }
            catch (Exception exception) { return Task.FromException<LineVisionOcrPageResult>(exception); }
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        private readonly List<T> _values = new List<T>();
        public void Report(T value) { lock (_values) _values.Add(value); }
        public List<T> Snapshot() { lock (_values) return new List<T>(_values); }
    }
}
