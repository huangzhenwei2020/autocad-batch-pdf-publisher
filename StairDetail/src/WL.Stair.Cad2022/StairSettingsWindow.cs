using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WL.Stair.Core.Calculation;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Geometry;
using WL.Stair.Core.Layout;
using WL.Stair.CadShared.PlanCapture;

namespace WL.Stair.Cad2022
{
    internal sealed class StairSettingsWindow : Window
    {
        private static readonly object WebViewEnvironmentSync = new object();
        private static Task<CoreWebView2Environment> _webViewEnvironmentTask;
        private readonly WebView2 _webView = new WebView2();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly StairProjectCalculator _calculator = new StairProjectCalculator();
        private readonly StairProjectGeometryBuilder _geometryBuilder = new StairProjectGeometryBuilder();
        private readonly StairProjectConstraintService _constraints = new StairProjectConstraintService();
        private readonly StairProjectStorage _storage = new StairProjectStorage();
        private readonly IDictionary<string, IList<StairLayoutPreviewLine>> _planPreviewLines
            = new Dictionary<string, IList<StairLayoutPreviewLine>>(StringComparer.OrdinalIgnoreCase);
        private UiState _state;
        private bool _isClosing;
        private bool _initializationStarted;
        private bool _cadInteractionActive;
        private string _lastSuccessfulPreviewSvg = string.Empty;
        private string _lastOpeningPreviewTrace = string.Empty;

        public StairSettingsWindow()
        {
            _state = UiState.Create(_storage.LoadOrDefault());
            _state.SelectedLayoutFrameId = _storage.LoadLastLayoutFrameId();
            LoadRegisteredLayoutFrames();
            _constraints.Normalize(_state.Project);
            _constraints.Apply(_state.Project);
            MigrateLegacyPlanCacheFingerprints();
            Title = "万落建筑 - 楼梯构件设置";
            Width = 1380;
            Height = 850;
            MinWidth = 1120;
            MinHeight = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Content = _webView;
            Loaded += OnLoaded;
            Closing += OnClosing;
            Closed += OnClosed;
        }

        public StairProjectDefinition Project { get; private set; }

        public StairProjectCalculationResult ConfirmedCalculation { get; private set; }

        public bool GenerateCombinedLayout { get; private set; }

        public bool IsConfirmed { get; private set; }

        public event EventHandler Completed;

        public LayoutFrameOption SelectedLayoutFrame
        {
            get
            {
                return (_state.LayoutFrames ?? new List<LayoutFrameOption>())
                    .FirstOrDefault(item => item != null && string.Equals(
                        item.RegistrationId, _state.SelectedLayoutFrameId,
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
        {
            if (_initializationStarted) return;
            _initializationStarted = true;

            try
            {
                var environment = await GetWebViewEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);
                if (_isClosing) return;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.NavigateToString(BuildHtml());
            }
            catch (Exception exception)
            {
                if (_isClosing) return;
                MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private static Task<CoreWebView2Environment> GetWebViewEnvironmentAsync()
        {
            lock (WebViewEnvironmentSync)
            {
                if (_webViewEnvironmentTask == null)
                {
                    var userDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "WanluoArchitectureTools",
                        "WebView2",
                        "StairDetail-R24");
                    Directory.CreateDirectory(userDataFolder);
                    _webViewEnvironmentTask = CoreWebView2Environment.CreateAsync(null, userDataFolder);
                }

                return _webViewEnvironmentTask;
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
        {
            try
            {
                ProcessWebMessageReceived(sender, eventArgs);
            }
            catch (Exception exception)
            {
                if (!_isClosing)
                {
                    var actual = UnwrapException(exception);
                    WriteEditorLog(actual);
                    SendPreview(null, "操作失败：" + actual.Message, false);
                }
            }
        }

        private static Exception UnwrapException(Exception exception)
        {
            var current = exception;
            while (current != null && current.InnerException != null
                && (current is System.Reflection.TargetInvocationException
                    || current is AggregateException))
                current = current.InnerException;
            return current ?? exception;
        }

        private static void WriteEditorLog(Exception exception)
        {
            try
            {
                var root = Environment.GetEnvironmentVariable(
                    "WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (string.IsNullOrWhiteSpace(root)) return;
                var directory = Path.Combine(root, "用户配置文件", "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "stair-editor.log"),
                    DateTime.Now.ToString("O", CultureInfo.InvariantCulture)
                    + Environment.NewLine + exception + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        private void TracePlatformOpeningPreview(string action)
        {
            if (!string.Equals(action, "preview", StringComparison.OrdinalIgnoreCase)
                || _state == null || _state.Project == null) return;
            var openings = (_state.Project.Floors ?? new List<StairFloorDefinition>())
                .Where(item => item != null && item.DoorWindowElevation != null
                    && item.DoorWindowElevation.Type != WallOpeningType.None)
                .Select(item => new { item.Id, Opening = item.DoorWindowElevation })
                .Concat((_state.Project.Storeys ?? new List<StairStoreyDefinition>())
                    .Where(item => item != null)
                    .SelectMany(item => item.Landings ?? new List<StairLandingDefinition>())
                    .Where(item => item != null && item.DoorWindowElevation != null
                        && item.DoorWindowElevation.Type != WallOpeningType.None)
                    .Select(item => new { item.Id, Opening = item.DoorWindowElevation }))
                .Select(item => string.Format(CultureInfo.InvariantCulture,
                    "{0}:type={1},offset={2:0.###},width={3:0.###},height={4:0.###},sill={5:0.###}",
                    item.Id, (int)item.Opening.Type, item.Opening.DistanceFromWall,
                    item.Opening.Width, item.Opening.Height, item.Opening.SillHeight))
                .ToArray();
            var signature = string.Join(";", openings);
            if (signature.Length == 0 || string.Equals(signature, _lastOpeningPreviewTrace,
                StringComparison.Ordinal)) return;
            _lastOpeningPreviewTrace = signature;
            try
            {
                var root = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (string.IsNullOrWhiteSpace(root)) return;
                var directory = Path.Combine(root, "用户配置文件", "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "stair-opening-preview.log"),
                    DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + " "
                    + signature + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private async void RunCadInteraction(Action action)
        {
            if (_isClosing || action == null) return;
            if (_cadInteractionActive)
            {
                SendPreview(null, "请先完成当前 CAD 拾取操作。", false);
                return;
            }

            _cadInteractionActive = true;
            Exception captured = null;
            try
            {
                await Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager
                    .ExecuteInCommandContextAsync(
                    unused =>
                    {
                        try { action(); }
                        catch (Exception exception) { captured = exception; }
                        return Task.FromResult(0);
                    },
                    null);
            }
            catch (Exception exception)
            {
                captured = exception;
            }
            finally
            {
                _cadInteractionActive = false;
            }

            if (captured == null || _isClosing) return;
            var actual = UnwrapException(captured);
            WriteEditorLog(actual);
            SendPreview(null, "操作失败：" + actual.Message, false);
        }

        private void ProcessWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
        {
            if (_isClosing) return;
            UiMessage message;
            try
            {
                message = _serializer.Deserialize<UiMessage>(eventArgs.WebMessageAsJson);
            }
            catch (Exception exception)
            {
                SendPreview(null, exception.Message, false);
                return;
            }

            if (message == null) return;
            if (message.Action == "cancel")
            {
                Close();
                return;
            }
            if (message.State != null)
            {
                MergePlanCacheMetadata(
                    _state == null || _state.Project == null
                        ? null : _state.Project.PlanSources,
                    message.State.Project == null
                        ? null : message.State.Project.PlanSources);
                DeleteRemovedPlanCaches(_state == null || _state.Project == null
                    ? null : _state.Project.PlanSources,
                    message.State.Project == null ? null : message.State.Project.PlanSources);
                _state = message.State;
                _storage.SaveLastLayoutFrameId(_state.SelectedLayoutFrameId);
            }

            TracePlatformOpeningPreview(message.Action);

            if (message.Action == "measure")
            {
                RunCadInteraction(() => MeasureFromCad(message.Target));
                return;
            }

            if (message.Action == "edit-door-window-division")
            {
                _constraints.Normalize(_state.Project);
                EditDoorWindowDivision(message.Target);
                _constraints.Apply(_state.Project);
                SendDoorWindowDivisionState(message.Target);
                return;
            }

            if (message.Action == "pick-plan-source")
            {
                RunCadInteraction(() => PickPlanSourceFromCad(message.Target));
                return;
            }

            if (message.Action == "clear-plan-source")
            {
                ClearPlanSource(message.Target);
                return;
            }

            if (message.Action == "inspect-plan-source")
            {
                RunCadInteraction(() => InspectPlanSource(message.Target));
                return;
            }

            if (message.Action == "create-plan-working-copy")
            {
                RunCadInteraction(() => CreatePlanWorkingCopy(message.Target));
                return;
            }

            _constraints.Normalize(_state.Project);
            _constraints.Apply(_state.Project);
            RefreshPlatformOpeningGeometry();

            var outcome = _calculator.Calculate(_state.Project);
            if (!outcome.IsSuccess)
            {
                SendPreview(null, JoinIssues(outcome), false);
                return;
            }

            if (string.Equals(message.Action, "layout-move", StringComparison.OrdinalIgnoreCase))
                MoveCombinedLayoutItem(message.Target);

            if (string.Equals(message.Action, "layout-preview", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.Action, "layout-move", StringComparison.OrdinalIgnoreCase))
            {
                ValidatePlanCaches();
                var layoutPreview = BuildCombinedLayoutPreview(outcome.Result);
                SendPreviewSvg(
                    layoutPreview.Svg,
                    layoutPreview.Summary,
                    true,
                    "整套排版预览");
                return;
            }

            if (message.Action == "confirm" || message.Action == "confirm-layout")
            {
                if (message.Action == "confirm-layout" && SelectedLayoutFrame == null)
                {
                    SendPreview(null, "请先选择已登记排版范围的图框。", false);
                    return;
                }
                if (message.Action == "confirm-layout") ValidatePlanCaches();
                Project = _state.Project;
                ConfirmedCalculation = outcome.Result;
                GenerateCombinedLayout = message.Action == "confirm-layout";
                // Persisting a large scheme synchronously here blocks the
                // WebView close callback and makes AutoCAD appear frozen.
                // The immutable confirmed project is safe to persist off the
                // UI thread; explicit "保存参数" remains synchronous so its
                // completion semantics do not change.
                var projectToSave = Project;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _storage.Save(projectToSave); }
                    catch { /* Auto-save failure must not cancel CAD generation. */ }
                });
                IsConfirmed = true;
                Close();
                return;
            }

            if (message.Action == "save")
            {
                _storage.Save(_state.Project);
            }

            if (message.Action == "reset")
            {
                _state = UiState.Create(StairProjectDefinition.CreateDefault());
                SendState();
                return;
            }

            var planStoreyIndex = 0;
            StairFloorDefinition previewFloor = null;
            StairStoreyDefinition previewStorey = null;
            var isPlanPreview = false;
            if (string.Equals(message.Action, "plan-preview", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(message.Target, out planStoreyIndex)
                && planStoreyIndex >= 0 && planStoreyIndex < _state.Project.Storeys.Count)
            {
                previewStorey = _state.Project.Storeys[planStoreyIndex];
                previewFloor = _state.Project.Floors.FirstOrDefault(item => item != null
                    && string.Equals(item.Id, previewStorey.LowerFloorId, StringComparison.OrdinalIgnoreCase));
                isPlanPreview = true;
            }
            else if (string.Equals(message.Action, "plan-floor-preview", StringComparison.OrdinalIgnoreCase)
                && TryResolvePlanFloor(message.Target, out previewFloor, out previewStorey, out planStoreyIndex))
            {
                isPlanPreview = true;
            }
            DrawingView view;
            if (isPlanPreview)
            {
                var source = previewFloor == null ? null
                    : FindPlanSourceForFloor(previewFloor.Id);
                var title = previewFloor == null ? string.Empty
                    : (previewFloor.PlanFloorLabel ?? string.Empty) + "楼梯平面图";
                var cache = new StairPlanCacheService();
                if (source != null && cache.IsValid(source, title))
                {
                    var lines = GetPlanPreviewLines(cache, source).Select(line =>
                        new DrawingLine(new Point2D(line.X1, line.Y1),
                            new Point2D(line.X2, line.Y2),
                            StairLineRole.CutFlightProfile, line.Dashed,
                            previewFloor.Id));
                    view = new DrawingView("CachedPlan-" + previewFloor.Id, lines);
                }
                else view = _geometryBuilder.BuildPlan(_state.Project,
                    outcome.Result, planStoreyIndex);
            }
            else view = _geometryBuilder.BuildSection(_state.Project, outcome.Result);
            var summary = string.Join("；", outcome.Result.Storeys.Select(result => string.Format(
                CultureInfo.CurrentCulture,
                "{0}: {1}跑/{2}级/h={3:0.0}",
                result.Id,
                result.Flights.Count,
                result.TotalRiserCount,
                result.RiserHeight)));
            var previewFloorLabel = isPlanPreview && previewFloor != null
                ? previewFloor.PlanFloorLabel
                : null;
            SendPreview(view, summary, true, previewFloorLabel, isPlanPreview);
        }

        private void SendPreview(DrawingView view, string summary, bool success,
            string previewFloorLabel = null, bool resetView = false)
        {
            var svg = view == null ? string.Empty : BuildSvg(view, _state, previewFloorLabel);
            if (success && !string.IsNullOrWhiteSpace(svg))
                _lastSuccessfulPreviewSvg = svg;
            else if (!success && string.IsNullOrWhiteSpace(svg))
                svg = _lastSuccessfulPreviewSvg;
            SendPreviewSvg(
                svg,
                summary,
                success,
                string.IsNullOrWhiteSpace(previewFloorLabel) ? "剖面预览" : previewFloorLabel + " · 平面预览",
                resetView);
        }

        private void SendPreviewSvg(string svg, string summary, bool success,
            string toolbarTitle, bool resetView = false)
        {
            if (_isClosing || _webView.CoreWebView2 == null) return;
            var payload = new Dictionary<string, object>
            {
                { "type", "preview" },
                { "success", success },
                { "summary", summary },
                { "svg", svg ?? string.Empty },
                { "toolbarTitle", toolbarTitle ?? "剖面预览" },
                { "resetView", resetView }
            };
            _webView.CoreWebView2.PostWebMessageAsJson(_serializer.Serialize(payload));
        }

        private LayoutPreviewResult BuildCombinedLayoutPreview(StairProjectCalculationResult calculation)
        {
            var scale = Math.Max(1, _state.Project.DrawingScale);
            var items = new List<StairLayoutItem>();
            var registeredFloorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var floor in _state.Project.Floors.Where(item => item != null))
            {
                var source = FindPlanSourceForFloor(floor.Id);
                if (source == null || source.Mode == StairPlanSourceMode.None
                    || source.CropBoundaryPoints == null || source.CropBoundaryPoints.Count < 3)
                    continue;
                var label = !string.IsNullOrWhiteSpace(floor.PlanFloorLabel)
                    ? floor.PlanFloorLabel
                    : (!string.IsNullOrWhiteSpace(source.FloorLabel) ? source.FloorLabel : floor.Name);
                var title = (label ?? string.Empty) + "楼梯平面图";
                var cache = new StairPlanCacheService();
                var hasCache = cache.IsValid(source, title);
                var minX = source.CropBoundaryPoints.Min(point => point.X);
                var maxX = source.CropBoundaryPoints.Max(point => point.X);
                var minY = source.CropBoundaryPoints.Min(point => point.Y);
                var maxY = source.CropBoundaryPoints.Max(point => point.Y);
                double layoutOffsetX, layoutOffsetY, layoutWidth, layoutHeight;
                StairPlanCacheService.GetLayoutRange(source, out layoutOffsetX,
                    out layoutOffsetY, out layoutWidth, out layoutHeight);
                items.Add(new StairLayoutItem
                {
                    Key = floor.Id,
                    Name = title,
                    Width = hasCache ? layoutWidth : Math.Max(1.0, maxX - minX + 50.0 * scale),
                    Height = hasCache ? layoutHeight
                        : Math.Max(1.0, maxY - minY + 50.0 * scale),
                    IsSection = false,
                    PreviewLines = hasCache ? GetPlanPreviewLines(cache, source) : null
                });
                registeredFloorIds.Add(floor.Id ?? string.Empty);
            }

            var section = _geometryBuilder.BuildSection(_state.Project, calculation);
            double sectionMinX;
            double sectionMinY;
            double sectionMaxX;
            double sectionMaxY;
            GetDrawingBounds(section, out sectionMinX, out sectionMinY, out sectionMaxX, out sectionMaxY);
            items.Add(new StairLayoutItem
            {
                Key = "SECTION",
                Name = (_state.Project.StairNumber ?? "LT") + " 楼梯剖面图",
                Width = Math.Max(1.0, sectionMaxX - sectionMinX),
                Height = Math.Max(1.0, sectionMaxY - sectionMinY),
                IsSection = true,
                PreviewLines = section.Lines.Select(line => new StairLayoutPreviewLine
                {
                    X1 = line.Start.X - sectionMinX,
                    Y1 = line.Start.Y - sectionMinY,
                    X2 = line.End.X - sectionMinX,
                    Y2 = line.End.Y - sectionMinY,
                    Color = line.IsHidden ? "#26cbd0" : "#f0f3f5",
                    Dashed = line.IsHidden
                }).ToList()
            });

            items = ApplyCombinedLayoutOrder(items, _state.Project.CombinedLayoutItemOrder);

            var frame = SelectedLayoutFrame;
            var layout = StairCombinedLayout.Compute(items, new StairLayoutOptions
            {
                PageWidth = (frame == null ? 841.0 : frame.PageWidth) * scale,
                PageHeight = (frame == null ? 594.0 : frame.PageHeight) * scale,
                LeftMargin = (frame == null ? 30.0 : frame.LeftMargin) * scale,
                RightMargin = (frame == null ? 60.0 : frame.RightMargin) * scale,
                TopMargin = (frame == null ? 20.0 : frame.TopMargin) * scale,
                BottomMargin = (frame == null ? 20.0 : frame.BottomMargin) * scale,
                ItemGap = 10.0 * scale,
                GridColumns = _state.Project.CombinedLayoutGridColumns,
                GridRows = _state.Project.CombinedLayoutGridRows,
                ColumnRatios = _state.Project.CombinedLayoutColumnRatios,
                RowRatios = _state.Project.CombinedLayoutRowRatios
            });
            StairCombinedLayout.ApplyPlacements(layout,
                _state.Project.CombinedLayoutPlacements);
            _state.Project.CombinedLayoutGridColumns = layout.Columns;
            _state.Project.CombinedLayoutGridRows = layout.Rows;
            // Persist the dimensions actually accepted by the layout engine.  When a
            // dragged divider would make a merged cell too small, Compute falls back
            // to a valid grid; writing those values back keeps preview, subsequent
            // drags and final insertion on the same topology.
            _state.Project.CombinedLayoutColumnRatios = layout.ColumnWidths
                .Select(value => value / Math.Max(1.0, layout.ContentRight - layout.ContentLeft)).ToList();
            _state.Project.CombinedLayoutRowRatios = layout.RowHeights
                .Select(value => value / Math.Max(1.0, layout.ContentTop - layout.ContentBottom)).ToList();
            var missing = _state.Project.Floors.Count(floor => floor != null
                && !registeredFloorIds.Contains(floor.Id ?? string.Empty));
            return new LayoutPreviewResult
            {
                Svg = BuildCombinedLayoutSvg(layout, scale),
                Summary = string.Format(
                    CultureInfo.CurrentCulture,
                    "已排入 {0} 个楼层平面 + 1 个楼梯剖面，共 {1} 页（{2}）{3}",
                    items.Count - 1,
                    layout.PageCount,
                    frame == null ? "A1 横向试排" : frame.DisplayName,
                    missing > 0 ? "；另有 " + missing + " 个平面层尚未登记" : string.Empty)
            };
        }

        private IList<StairLayoutPreviewLine> GetPlanPreviewLines(
            StairPlanCacheService cache, StairPlanSourceDefinition source)
        {
            var key = (source.CacheRelativePath ?? string.Empty) + "|"
                + (source.CacheFingerprint ?? string.Empty) + "|"
                + source.CacheLayoutOffsetX.ToString("R", CultureInfo.InvariantCulture) + "|"
                + source.CacheLayoutOffsetY.ToString("R", CultureInfo.InvariantCulture);
            IList<StairLayoutPreviewLine> lines;
            if (_planPreviewLines.TryGetValue(key, out lines)) return lines;
            lines = cache.ReadPreviewLines(source, 1800);
            _planPreviewLines[key] = lines;
            return lines;
        }

        private void EnsurePlanCaches()
        {
            // Kept as a guard for older call sites. It deliberately never
            // builds a cache: only PickPlanSourceFromCad may crop/trim.
            ValidatePlanCaches();
        }

        /// <summary>
        /// 拾取平面是唯一允许生成裁剪缓存的入口。排版预览、切换图框和
        /// 整套插入只读取缓存，绝不能再次进入天正墙修剪流程。
        /// </summary>
        private void ValidatePlanCaches()
        {
            var cache = new StairPlanCacheService();
            var invalid = new List<string>();
            foreach (var floor in _state.Project.Floors.Where(value => value != null))
            {
                var source = FindPlanSourceForFloor(floor.Id);
                if (source == null || source.Mode == StairPlanSourceMode.None) continue;
                var label = !string.IsNullOrWhiteSpace(floor.PlanFloorLabel)
                    ? floor.PlanFloorLabel
                    : (!string.IsNullOrWhiteSpace(source.FloorLabel)
                        ? source.FloorLabel : floor.Name);
                var title = (label ?? string.Empty) + "楼梯平面图";
                if (!cache.IsValid(source, title)) invalid.Add(label ?? floor.Name ?? floor.Id);
            }
            if (invalid.Count > 0)
                throw new InvalidOperationException(
                    "以下楼层的小平面缓存不存在或参数已变化："
                    + string.Join("、", invalid)
                    + "。请只对这些楼层执行“重新拾取平面”；换图框、排版预览和整套插入不会自动裁剪。");
        }

        private sealed class CacheProgressWindow : Window
        {
            private readonly TextBlock _title = new TextBlock
            {
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            private readonly TextBlock _stage = new TextBlock
            {
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 12)
            };
            private readonly ProgressBar _bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 18
            };

            public CacheProgressWindow(int total)
            {
                Title = "楼梯平面处理中";
                Width = 470;
                Height = 165;
                ResizeMode = ResizeMode.NoResize;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ShowInTaskbar = false;
                Topmost = true;
                // Keep CAD as the active window so synchronous TRIM/clone
                // commands are not interrupted by the progress UI.
                ShowActivated = false;
                Content = new StackPanel
                {
                    Margin = new Thickness(22),
                    Children = { _title, _stage, _bar }
                };
                _title.Text = "正在准备 " + total + " 个楼层平面缓存…";
            }

            public void UpdateProgress(int current, int total, string floorTitle,
                string stage, int stagePercent)
            {
                _title.Text = string.IsNullOrWhiteSpace(floorTitle)
                    ? stage
                    : string.Format(CultureInfo.CurrentCulture,
                        "第 {0}/{1} 层：{2}", current, total, floorTitle);
                _stage.Text = stage ?? string.Empty;
                _bar.Value = total <= 0 ? stagePercent
                    : Math.Min(100.0, Math.Max(0.0,
                        ((current - 1) + stagePercent / 100.0) / total * 100.0));
                Dispatcher.Invoke(DispatcherPriority.Background,
                    new Action(delegate { }));
            }
        }

        private static void DeleteRemovedPlanCaches(
            IEnumerable<StairPlanSourceDefinition> previous,
            IEnumerable<StairPlanSourceDefinition> current)
        {
            var retained = new HashSet<string>((current
                    ?? Enumerable.Empty<StairPlanSourceDefinition>())
                .Where(value => value != null
                    && !string.IsNullOrWhiteSpace(value.CacheRelativePath))
                .Select(value => value.CacheRelativePath),
                StringComparer.OrdinalIgnoreCase);
            var cache = new StairPlanCacheService();
            foreach (var removed in (previous
                ?? Enumerable.Empty<StairPlanSourceDefinition>())
                .Where(value => value != null
                    && !string.IsNullOrWhiteSpace(value.CacheRelativePath)
                    && !retained.Contains(value.CacheRelativePath)))
                cache.Delete(removed);
        }

        private static void MergePlanCacheMetadata(
            IEnumerable<StairPlanSourceDefinition> authoritative,
            IEnumerable<StairPlanSourceDefinition> incoming)
        {
            if (authoritative == null || incoming == null) return;
            Func<StairPlanSourceDefinition, string> key = value => value == null
                ? string.Empty
                : (!string.IsNullOrWhiteSpace(value.FloorId)
                    ? value.FloorId : value.StoreyId);
            var lookup = authoritative.Where(value => value != null
                    && !string.IsNullOrWhiteSpace(key(value)))
                .GroupBy(key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (var target in incoming.Where(value => value != null
                && !string.IsNullOrWhiteSpace(key(value))))
            {
                StairPlanSourceDefinition source;
                if (!lookup.TryGetValue(key(target), out source)) continue;
                if (!string.IsNullOrWhiteSpace(target.CacheRelativePath)) continue;
                target.CacheRelativePath = source.CacheRelativePath;
                target.CacheFingerprint = source.CacheFingerprint;
                target.CacheWidth = source.CacheWidth;
                target.CacheHeight = source.CacheHeight;
                target.CacheLayoutOffsetX = source.CacheLayoutOffsetX;
                target.CacheLayoutOffsetY = source.CacheLayoutOffsetY;
                target.CacheLayoutWidth = source.CacheLayoutWidth;
                target.CacheLayoutHeight = source.CacheLayoutHeight;
                target.CacheObjectCount = source.CacheObjectCount;
                target.CachedUtc = source.CachedUtc;
            }
        }

        private void LoadRegisteredLayoutFrames()
        {
            _state.LayoutFrames = new List<LayoutFrameOption>();
            try
            {
                var bridgeType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("BatchPdfPublisher.Views.StairLayoutFrameBridge", false))
                    .FirstOrDefault(type => type != null);
                var method = bridgeType == null ? null : bridgeType.GetMethod("GetRegisteredFrames");
                var values = method == null ? null : method.Invoke(null, null) as IEnumerable;
                if (values != null)
                {
                    foreach (var value in values)
                    {
                        if (value == null) continue;
                        var type = value.GetType();
                        Func<string, object> read = name => type.GetProperty(name).GetValue(value, null);
                        _state.LayoutFrames.Add(new LayoutFrameOption
                        {
                            RegistrationId = Convert.ToString(read("RegistrationId"), CultureInfo.InvariantCulture),
                            DisplayName = Convert.ToString(read("DisplayName"), CultureInfo.CurrentCulture),
                            PageWidth = Convert.ToDouble(read("PageWidth"), CultureInfo.InvariantCulture),
                            PageHeight = Convert.ToDouble(read("PageHeight"), CultureInfo.InvariantCulture),
                            LeftMargin = Convert.ToDouble(read("LeftMargin"), CultureInfo.InvariantCulture),
                            RightMargin = Convert.ToDouble(read("RightMargin"), CultureInfo.InvariantCulture),
                            TopMargin = Convert.ToDouble(read("TopMargin"), CultureInfo.InvariantCulture),
                            BottomMargin = Convert.ToDouble(read("BottomMargin"), CultureInfo.InvariantCulture)
                        });
                    }
                }
            }
            catch { }
            if (string.IsNullOrWhiteSpace(_state.SelectedLayoutFrameId)
                || !_state.LayoutFrames.Any(item => string.Equals(item.RegistrationId,
                    _state.SelectedLayoutFrameId, StringComparison.OrdinalIgnoreCase)))
                _state.SelectedLayoutFrameId = _state.LayoutFrames.Select(item => item.RegistrationId).FirstOrDefault();
            _storage.SaveLastLayoutFrameId(_state.SelectedLayoutFrameId);
        }

        private void MigrateLegacyPlanCacheFingerprints()
        {
            var changed = false;
            var cache = new StairPlanCacheService();
            foreach (var floor in (_state.Project.Floors
                ?? new List<StairFloorDefinition>()).Where(value => value != null))
            {
                var source = FindPlanSourceForFloor(floor.Id);
                if (source == null || string.IsNullOrWhiteSpace(source.CacheFingerprint)) continue;
                var title = (!string.IsNullOrWhiteSpace(floor.PlanFloorLabel)
                    ? floor.PlanFloorLabel : source.FloorLabel) + "楼梯平面图";
                if (!cache.IsValid(source, title)) continue;
                var current = StairPlanCacheService.ComputeFingerprint(source, title);
                if (string.Equals(source.CacheFingerprint, current,
                    StringComparison.OrdinalIgnoreCase)) continue;
                source.CacheFingerprint = current;
                changed = true;
            }
            if (changed) _storage.Save(_state.Project);
        }

        private StairPlanSourceDefinition FindPlanSourceForFloor(string floorId)
        {
            var source = (_state.Project.PlanSources ?? new List<StairPlanSourceDefinition>())
                .FirstOrDefault(item => item != null && !string.IsNullOrWhiteSpace(item.FloorId)
                    && string.Equals(item.FloorId, floorId, StringComparison.OrdinalIgnoreCase));
            if (source != null) return source;
            var storey = _state.Project.Storeys.FirstOrDefault(item => item != null
                && string.Equals(item.LowerFloorId, floorId, StringComparison.OrdinalIgnoreCase));
            return storey == null
                ? null
                : (_state.Project.PlanSources ?? new List<StairPlanSourceDefinition>())
                    .FirstOrDefault(item => item != null && string.IsNullOrWhiteSpace(item.FloorId)
                        && string.Equals(item.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase));
        }

        private static void GetDrawingBounds(
            DrawingView view,
            out double minX,
            out double minY,
            out double maxX,
            out double maxY)
        {
            var points = new List<Point2D>();
            points.AddRange(view.Lines.SelectMany(line => new[] { line.Start, line.End }));
            points.AddRange(view.HatchRegions.SelectMany(region => region.Boundary));
            points.AddRange(view.Texts.Select(text => text.Position));
            points.AddRange(view.Dimensions.SelectMany(dimension => new[]
            {
                dimension.FirstExtensionOrigin,
                dimension.SecondExtensionOrigin,
                dimension.DimensionLinePoint
            }));
            points.AddRange(view.Leaders.SelectMany(leader => leader.Vertices));
            foreach (var table in view.Tables)
            {
                points.Add(table.Position);
                points.Add(new Point2D(
                    table.Position.X + table.ColumnWidths.Sum(),
                    table.Position.Y - table.RowHeight * table.Rows.Count));
            }
            if (view.Title != null)
            {
                points.Add(view.Title.Position);
                points.Add(new Point2D(view.Title.Position.X + view.Title.TargetWidth, view.Title.Position.Y));
            }
            if (points.Count == 0) points.Add(new Point2D(0, 0));
            minX = points.Min(point => point.X);
            minY = points.Min(point => point.Y);
            maxX = points.Max(point => point.X);
            maxY = points.Max(point => point.Y);
        }

        private static string BuildCombinedLayoutSvg(StairLayoutPlan layout, int scale)
        {
            var pageGap = 25.0 * scale;
            var totalWidth = layout.PageCount * layout.PageWidth
                + Math.Max(0, layout.PageCount - 1) * pageGap;
            var builder = new StringBuilder();
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "<svg id='sectionSvg' viewBox='0 0 {0} {1}' preserveAspectRatio='xMidYMid meet' data-layout-columns='{2}' data-layout-rows='{3}' data-column-ratios='{4}' data-row-ratios='{5}' data-content-width='{6}' data-content-height='{7}'>",
                totalWidth,
                layout.PageHeight,
                layout.Columns,
                layout.Rows,
                string.Join(",", layout.ColumnWidths.Select(value => (value / Math.Max(1.0, layout.ContentRight - layout.ContentLeft)).ToString("R", CultureInfo.InvariantCulture))),
                string.Join(",", layout.RowHeights.Select(value => (value / Math.Max(1.0, layout.ContentTop - layout.ContentBottom)).ToString("R", CultureInfo.InvariantCulture))),
                layout.ContentRight - layout.ContentLeft,
                layout.ContentTop - layout.ContentBottom);
            builder.Append("<style>.layout-divider-guide{pointer-events:none;opacity:.8;stroke:#63f39a;stroke-linecap:round}.layout-divider-handle{pointer-events:none;opacity:.55;fill:#63f39a;stroke:#101820}.layout-divider-guide.active{opacity:1;stroke-width:10}.layout-divider-handle.active{opacity:1}.layout-drop-cell{fill:transparent;pointer-events:all;stroke:transparent}.layout-drop-cell.target{fill:#54ef9222;stroke:#54ef92;stroke-width:6}.layout-item{cursor:move;pointer-events:all}.layout-item:hover{stroke:#54ef92!important}.layout-item.selected{stroke:#ff5b5b!important;stroke-width:9!important;stroke-dasharray:none!important}.layout-item.dragging{opacity:.55;stroke:#f4e74f!important;stroke-width:9!important}</style>");
            builder.Append("<rect width='100%' height='100%' fill='#10161d'/>");
            for (var page = 0; page < layout.PageCount; page++)
            {
                var pageX = page * (layout.PageWidth + pageGap);
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<rect x='{0}' y='0' width='{1}' height='{2}' fill='#151e27' stroke='#dce5ed' stroke-width='{3}'/>",
                    pageX, layout.PageWidth, layout.PageHeight, Math.Max(2.0, scale * 0.12));
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<rect x='{0}' y='{1}' width='{2}' height='{3}' fill='none' stroke='#58ef70' stroke-width='{4}' stroke-dasharray='{5} {6}'/>",
                    pageX + layout.ContentLeft,
                    layout.PageHeight - layout.ContentTop,
                    layout.ContentRight - layout.ContentLeft,
                    layout.ContentTop - layout.ContentBottom,
                    Math.Max(1.0, scale * 0.08),
                    4 * scale,
                    2 * scale);
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<text x='{0}' y='{1}' text-anchor='start' style='font-size:{2}px;fill:#dce5ed'>第 {3} 页 · A1 横向</text>",
                    pageX + 12 * scale, 15 * scale, 5 * scale, page + 1);
                var gridX = pageX + layout.ContentLeft;
                var gridTop = layout.PageHeight - layout.ContentTop;
                var running = 0.0;
                for (var column = 0; column < layout.ColumnWidths.Count - 1; column++)
                {
                    running += layout.ColumnWidths[column];
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<line x1='{0}' y1='{1}' x2='{0}' y2='{2}' stroke='#708191' stroke-width='{3}' stroke-dasharray='{4} {5}'/>",
                        gridX + running, gridTop, layout.PageHeight - layout.ContentBottom,
                        Math.Max(2.5, scale * 0.12), 4 * scale, 2 * scale);
                }
                running = 0.0;
                for (var row = 0; row < layout.RowHeights.Count - 1; row++)
                {
                    running += layout.RowHeights[row];
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<line x1='{0}' y1='{1}' x2='{2}' y2='{1}' stroke='#708191' stroke-width='{3}' stroke-dasharray='{4} {5}'/>",
                        gridX, gridTop + running, pageX + layout.ContentRight,
                        Math.Max(2.5, scale * 0.12), 4 * scale, 2 * scale);
                }
                var rowTop = gridTop;
                for (var row = 0; row < layout.RowHeights.Count; row++)
                {
                    var columnX = gridX;
                    for (var column = 0; column < layout.ColumnWidths.Count; column++)
                    {
                        builder.AppendFormat(CultureInfo.InvariantCulture,
                            "<rect class='layout-drop-cell' data-layout-page='{0}' data-layout-row='{1}' data-layout-column='{2}' x='{3}' y='{4}' width='{5}' height='{6}'/>",
                            page, row, column, columnX, rowTop,
                            layout.ColumnWidths[column], layout.RowHeights[row]);
                        columnX += layout.ColumnWidths[column];
                    }
                    rowTop += layout.RowHeights[row];
                }
            }

            foreach (var slot in layout.Slots)
            {
                var pageX = slot.Page * (layout.PageWidth + pageGap);
                var x = pageX + slot.X;
                var y = layout.PageHeight - slot.Y - slot.Height;
                var cellX = pageX + slot.CellX;
                var cellY = layout.PageHeight - slot.CellY - slot.CellHeight;
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<rect x='{0}' y='{1}' width='{2}' height='{3}' fill='#151e27' stroke='#708191' stroke-width='{4}' stroke-dasharray='{5} {6}'/>",
                    cellX, cellY, slot.CellWidth, slot.CellHeight,
                    Math.Max(2.5, scale * 0.12), 4 * scale, 2 * scale);
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<rect class='layout-item' data-layout-item='{8}' x='{0}' y='{1}' width='{2}' height='{3}' fill='transparent' stroke='{4}' stroke-width='{5}' stroke-dasharray='{6} {7}'/>",
                    x, y, slot.Width, slot.Height,
                    slot.Item.IsSection ? "#f4e74f" : "#26cbd0",
                    Math.Max(1.0, scale * 0.06), 3 * scale, 2 * scale,
                    Escape(slot.Item.Key));
                var previewLines = slot.Item.PreviewLines;
                if (previewLines != null && previewLines.Count > 0)
                {
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<g stroke-width='{0}' fill='none' pointer-events='none'>", Math.Max(1.0, scale * 0.045));
                    foreach (var line in previewLines)
                    {
                        builder.AppendFormat(CultureInfo.InvariantCulture,
                            "<line x1='{0}' y1='{1}' x2='{2}' y2='{3}' stroke='{4}'{5}/>",
                            x + line.X1, y + slot.Height - line.Y1,
                            x + line.X2, y + slot.Height - line.Y2,
                            string.IsNullOrWhiteSpace(line.Color) ? "#d8e0e6" : line.Color,
                            line.Dashed ? " stroke-dasharray='" + (3 * scale).ToString(CultureInfo.InvariantCulture)
                                + " " + (2 * scale).ToString(CultureInfo.InvariantCulture) + "'" : string.Empty);
                    }
                    builder.Append("</g>");
                }
                else
                {
                    var inset = Math.Min(slot.Width, slot.Height) * 0.1;
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<rect x='{0}' y='{1}' width='{2}' height='{3}' fill='none' stroke='#58ef70' stroke-width='{4}' stroke-dasharray='{5} {6}'/>",
                        x + inset, y + inset, Math.Max(1.0, slot.Width - 2 * inset),
                        Math.Max(1.0, slot.Height - 2 * inset), Math.Max(1.0, scale * 0.07),
                        3 * scale, 2 * scale);
                }
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<text x='{0}' y='{1}' text-anchor='middle' style='font-size:{2}px;font-weight:700;fill:#ffffff;stroke:#101820;stroke-width:{3};paint-order:stroke'>{4}</text>",
                    x + slot.Width / 2.0, y + slot.Height - 3 * scale,
                    5 * scale, Math.Max(1.0, scale * 0.08), Escape(slot.Item.Name));
            }
            if (layout.Columns > 1 || layout.Rows > 1)
            {
                for (var page = 0; page < layout.PageCount; page++)
                {
                    var pageX = page * (layout.PageWidth + pageGap);
                    var gridX = pageX + layout.ContentLeft;
                    var gridTop = layout.PageHeight - layout.ContentTop;
                    var running = 0.0;
                    for (var column = 0; column < layout.ColumnWidths.Count - 1; column++)
                    {
                        running += layout.ColumnWidths[column];
                        builder.AppendFormat(CultureInfo.InvariantCulture,
                            "<line class='layout-divider-guide' data-layout-axis='x' data-layout-index='{0}' x1='{1}' y1='{2}' x2='{1}' y2='{3}' stroke-width='{4}' stroke-dasharray='{5} {6}'/>",
                            column, gridX + running, gridTop,
                            layout.PageHeight - layout.ContentBottom, Math.Max(2.0, scale * 0.12),
                            4 * scale, 2 * scale);
                        builder.AppendFormat(CultureInfo.InvariantCulture,
                            "<rect class='layout-divider-handle' data-layout-axis='x' data-layout-index='{0}' x='{1}' y='{2}' width='{3}' height='{4}' rx='{5}'/>",
                            column, gridX + running - Math.Max(4.0, scale * 0.2),
                            (gridTop + layout.PageHeight - layout.ContentBottom) / 2.0 - Math.Max(9.0, scale * 0.45),
                            Math.Max(8.0, scale * 0.4), Math.Max(18.0, scale * 0.9), Math.Max(2.0, scale * 0.1));
                        builder.AppendFormat(CultureInfo.InvariantCulture,
                            "<line class='layout-divider' data-layout-axis='x' data-layout-index='{0}' x1='{1}' y1='{2}' x2='{1}' y2='{3}' stroke='transparent' stroke-width='{4}' pointer-events='stroke' style='cursor:ew-resize'/>",
                            column, gridX + running, gridTop,
                            layout.PageHeight - layout.ContentBottom, Math.Max(18.0, scale * 0.9));
                    }
                    running = 0.0;
                    for (var row = 0; row < layout.RowHeights.Count - 1; row++)
                    {
                        running += layout.RowHeights[row];
                        builder.AppendFormat(CultureInfo.InvariantCulture,
                            "<line class='layout-divider-guide' data-layout-axis='y' data-layout-index='{0}' x1='{1}' y1='{2}' x2='{3}' y2='{2}' stroke-width='{4}' stroke-dasharray='{5} {6}'/>",
                            row, gridX, gridTop + running,
                            pageX + layout.ContentRight, Math.Max(2.0, scale * 0.12),
                            4 * scale, 2 * scale);
                        builder.AppendFormat(CultureInfo.InvariantCulture,
                            "<rect class='layout-divider-handle' data-layout-axis='y' data-layout-index='{0}' x='{1}' y='{2}' width='{3}' height='{4}' rx='{5}'/>",
                            row, (gridX + pageX + layout.ContentRight) / 2.0 - Math.Max(9.0, scale * 0.45),
                            gridTop + running - Math.Max(4.0, scale * 0.2),
                            Math.Max(18.0, scale * 0.9), Math.Max(8.0, scale * 0.4), Math.Max(2.0, scale * 0.1));
                        builder.AppendFormat(CultureInfo.InvariantCulture,
                            "<line class='layout-divider' data-layout-axis='y' data-layout-index='{0}' x1='{1}' y1='{2}' x2='{3}' y2='{2}' stroke='transparent' stroke-width='{4}' pointer-events='stroke' style='cursor:ns-resize'/>",
                            row, gridX, gridTop + running,
                            pageX + layout.ContentRight, Math.Max(18.0, scale * 0.9));
                    }
                }
            }
            builder.Append("</svg>");
            return builder.ToString();
        }

        private sealed class LayoutPreviewResult
        {
            public string Svg { get; set; }
            public string Summary { get; set; }
        }

        private void SendState()
        {
            if (_isClosing || _webView.CoreWebView2 == null) return;
            var payload = new Dictionary<string, object>
            {
                { "type", "state" },
                { "state", _state }
            };
            _webView.CoreWebView2.PostWebMessageAsJson(_serializer.Serialize(payload));
        }

        private void SendDoorWindowDivisionState(string target)
        {
            if (_isClosing || _webView.CoreWebView2 == null) return;
            var payload = new Dictionary<string, object>
            {
                { "type", "door-window-division" },
                { "target", target ?? string.Empty },
                { "state", _state }
            };
            _webView.CoreWebView2.PostWebMessageAsJson(_serializer.Serialize(payload));
        }

        private void EditDoorWindowDivision(string componentId)
        {
            var opening = FindPlatformOpening(componentId);
            if (opening == null || opening.Type == WallOpeningType.None) return;
            var bridgeType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(
                    "BatchPdfPublisher.Views.DoorWindowDivisionEditorBridge", false))
                .FirstOrDefault(type => type != null);
            if (bridgeType == null)
                throw new InvalidOperationException("门窗立面的“编辑当前分格”组件尚未加载，请使用万落建筑工具启动器重新加载插件。");
            var method = bridgeType.GetMethod("Edit", System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException("当前门窗立面模块不支持共享分格编辑，请重新运行最新版启动器。");
            var result = method.Invoke(null, new object[]
            {
                componentId,
                (int)opening.Type,
                opening.Width,
                opening.Height,
                opening.CustomCellLayout,
                opening.CellOpeningModes,
                opening.HasInstallationGap,
                opening.InstallationGap,
                opening.HasOuterFrame,
                opening.OuterFrameWidth,
                opening.HasMullion,
                opening.MullionWidth,
                opening.DoorFrameType,
                opening.DoorFrameWidth,
                opening.Material
            });
            if (result == null) return;
            CopyBridgeProperty(result, "CustomCellLayout", value => opening.CustomCellLayout = value as string);
            CopyBridgeProperty(result, "CellOpeningModes", value => opening.CellOpeningModes = value as string);
            CopyBridgeProperty(result, "HasInstallationGap", value => opening.HasInstallationGap = Convert.ToBoolean(value));
            CopyBridgeProperty(result, "InstallationGap", value => opening.InstallationGap = Convert.ToDouble(value));
            CopyBridgeProperty(result, "HasOuterFrame", value => opening.HasOuterFrame = Convert.ToBoolean(value));
            CopyBridgeProperty(result, "OuterFrameWidth", value => opening.OuterFrameWidth = Convert.ToDouble(value));
            CopyBridgeProperty(result, "HasMullion", value => opening.HasMullion = Convert.ToBoolean(value));
            CopyBridgeProperty(result, "MullionWidth", value => opening.MullionWidth = Convert.ToDouble(value));
            CopyBridgeProperty(result, "DoorFrameType", value => opening.DoorFrameType = value as string);
            CopyBridgeProperty(result, "DoorFrameWidth", value => opening.DoorFrameWidth = Convert.ToDouble(value));
            CopyBridgeProperty(result, "Material", value => opening.Material = value as string);
            CopyBridgeProperty(result, "GeometryLines", value => opening.GeometryLines = value as string);
        }

        private void RefreshPlatformOpeningGeometry()
        {
            var openings = (_state.Project.Floors ?? new List<StairFloorDefinition>())
                .Where(item => item != null)
                .Select(item => new { item.Id, Opening = item.DoorWindowElevation })
                .Concat((_state.Project.Storeys ?? new List<StairStoreyDefinition>())
                    .Where(item => item != null)
                    .SelectMany(item => item.Landings ?? new List<StairLandingDefinition>())
                    .Where(item => item != null)
                    .Select(item => new { item.Id, Opening = item.DoorWindowElevation }));
            foreach (var item in openings)
            {
                var opening = item.Opening;
                if (opening == null || opening.Type == WallOpeningType.None) continue;
                BuildDoorWindowGeometry(item.Id, opening);
            }
        }

        private static void BuildDoorWindowGeometry(
            string componentId,
            StairPlatformOpeningDefinition opening)
        {
            var bridgeType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(
                    "BatchPdfPublisher.Views.DoorWindowDivisionEditorBridge", false))
                .FirstOrDefault(type => type != null);
            var method = bridgeType == null ? null : bridgeType.GetMethod("Build",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null) return;
            var result = method.Invoke(null, new object[]
            {
                componentId,
                (int)opening.Type,
                opening.Width,
                opening.Height,
                opening.CustomCellLayout,
                opening.CellOpeningModes,
                opening.HasInstallationGap,
                opening.InstallationGap,
                opening.HasOuterFrame,
                opening.OuterFrameWidth,
                opening.HasMullion,
                opening.MullionWidth,
                opening.DoorFrameType,
                opening.DoorFrameWidth,
                opening.Material
            });
            if (result == null) return;
            CopyBridgeProperty(result, "CustomCellLayout", value => opening.CustomCellLayout = value as string);
            CopyBridgeProperty(result, "CellOpeningModes", value => opening.CellOpeningModes = value as string);
            CopyBridgeProperty(result, "GeometryLines", value => opening.GeometryLines = value as string);
        }

        private StairPlatformOpeningDefinition FindPlatformOpening(string componentId)
        {
            var floor = (_state.Project.Floors ?? new List<StairFloorDefinition>())
                .FirstOrDefault(item => item != null && string.Equals(item.Id, componentId,
                    StringComparison.OrdinalIgnoreCase));
            if (floor != null) return floor.DoorWindowElevation;
            return (_state.Project.Storeys ?? new List<StairStoreyDefinition>())
                .Where(item => item != null)
                .SelectMany(item => item.Landings ?? new List<StairLandingDefinition>())
                .Where(item => item != null && string.Equals(item.Id, componentId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => item.DoorWindowElevation)
                .FirstOrDefault();
        }

        private static void CopyBridgeProperty(object source, string name, Action<object> assign)
        {
            var property = source.GetType().GetProperty(name);
            if (property != null) assign(property.GetValue(source, null));
        }

        private void MeasureFromCad(string target)
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var editor = document.Editor;

            using (editor.StartUserInteraction(this))
            {
                var first = editor.GetPoint("\n指定测量框第一个角点: ");
                if (first.Status != PromptStatus.OK) return;
                PromptPointResult second;
                if (string.Equals(target, "stairwell", StringComparison.OrdinalIgnoreCase))
                {
                    second = editor.GetCorner(new PromptCornerOptions("\n指定楼梯井范围另一个角点: ", first.Value));
                }
                else
                {
                    var secondOptions = new PromptPointOptions("\n指定测量终点: ") { BasePoint = first.Value, UseBasePoint = true };
                    second = editor.GetPoint(secondOptions);
                }
                if (second.Status != PromptStatus.OK) return;

                ApplyMeasurement(target, first.Value, second.Value);
                _constraints.Apply(_state.Project);
                SendMeasuredState(target);
            }
        }

        private void PickPlanSourceFromCad(string target)
        {
            _constraints.Normalize(_state.Project);
            StairFloorDefinition floor;
            StairStoreyDefinition storey;
            int storeyIndex;
            if (!TryResolvePlanFloor(target, out floor, out storey, out storeyIndex))
                throw new InvalidOperationException("没有找到要登记平面来源的楼层平面。");

            var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) throw new InvalidOperationException("当前没有打开的 CAD 图纸。");
            var existing = (_state.Project.PlanSources ?? new List<StairPlanSourceDefinition>())
                .FirstOrDefault(item => item != null
                    && ((!string.IsNullOrWhiteSpace(item.FloorId)
                            && string.Equals(item.FloorId, floor.Id, StringComparison.OrdinalIgnoreCase))
                        || (string.IsNullOrWhiteSpace(item.FloorId)
                            && string.Equals(item.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase))));
            var cropOffset = existing != null && existing.CropOffset > 0.0
                ? existing.CropOffset
                : 300.0;
            if (existing != null && !string.IsNullOrWhiteSpace(existing.CacheRelativePath)
                && MessageBox.Show(this,
                    "本层已经保存过小平面。重新拾取会替换原缓存，是否继续？",
                    "重新拾取平面", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var captureMode = ShowPlanCaptureMode();
            if (captureMode == PlanCaptureMode.Cancel) return;

            using (document.Editor.StartUserInteraction(this))
            {
                var capture = new StairPlanCaptureService();
                var captured = captureMode == PlanCaptureMode.Frame
                    ? capture.CaptureFrame(document, storey.Id,
                        floor.PlanFloorLabel, cropOffset)
                    : capture.CaptureTianzhengStair(document, storey.Id,
                        floor.PlanFloorLabel, cropOffset, () =>
                            MessageBox.Show("CAD 中已显示识别边界，是否接受当前楼梯平面？",
                                "确认楼梯平面", MessageBoxButton.YesNo,
                                MessageBoxImage.Question) == MessageBoxResult.Yes);
                if (captured == null) return;
                captured.TargetScale = _state.Project.DrawingScale > 0
                    ? _state.Project.DrawingScale
                    : 30;
                // Re-picking geometry must not erase the user's standard-floor
                // assignment. Only source identity and crop geometry are
                // replaced by the new capture.
                captured.FloorId = floor.Id;
                captured.StoreyId = storey.Id;
                captured.FloorLabel = !string.IsNullOrWhiteSpace(floor.PlanFloorLabel)
                    ? floor.PlanFloorLabel
                    : (existing != null && !string.IsNullOrWhiteSpace(existing.FloorLabel)
                        ? existing.FloorLabel
                        : storey.Name);
                captured.RepeatCount = floor.PlanRepeatCount > 0
                    ? floor.PlanRepeatCount
                    : (existing != null && existing.RepeatCount > 0 ? existing.RepeatCount : 1);
                var title = (captured.FloorLabel ?? string.Empty) + "楼梯平面图";
                var cacheService = new StairPlanCacheService();
                var progress = new CacheProgressWindow(1);
                string cacheSummary;
                try
                {
                    progress.Show();
                    progress.UpdateProgress(1, 1, captured.FloorLabel,
                        "正在准备楼梯平面缓存", 0);
                    cacheSummary = cacheService.Build(document, _state.Project,
                        captured, title, (stage, percent) =>
                            progress.UpdateProgress(1, 1, captured.FloorLabel,
                                stage, percent));
                }
                finally
                {
                    progress.Close();
                }
                if (existing != null
                    && !string.Equals(existing.CacheRelativePath,
                        captured.CacheRelativePath, StringComparison.OrdinalIgnoreCase))
                    cacheService.Delete(existing);
                if (_state.Project.PlanSources == null)
                    _state.Project.PlanSources = new List<StairPlanSourceDefinition>();
                if (existing != null) _state.Project.PlanSources.Remove(existing);
                _state.Project.PlanSources.Add(captured);
                _storage.Save(_state.Project);
                SendPlanSourceState(target,
                    "已登记并保存本层裁剪成果；源图未作任何修改。" + cacheSummary, true);
            }
        }

        private PlanCaptureMode ShowPlanCaptureMode()
        {
            var dialog = new Window
            {
                Title = "拾取平面",
                Width = 420,
                Height = 190,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            var result = PlanCaptureMode.Cancel;
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = "请选择本层小平面的取得方式：",
                Margin = new Thickness(0, 0, 0, 16),
                FontSize = 14
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center };
            var pick = new Button { Content = "拾取楼梯平面", Width = 130,
                Height = 36, Margin = new Thickness(5) };
            var frame = new Button { Content = "框选范围", Width = 110,
                Height = 36, Margin = new Thickness(5) };
            var cancel = new Button { Content = "取消", Width = 80,
                Height = 36, Margin = new Thickness(5) };
            pick.Click += (sender, args) => { result = PlanCaptureMode.Stair; dialog.Close(); };
            frame.Click += (sender, args) => { result = PlanCaptureMode.Frame; dialog.Close(); };
            cancel.Click += (sender, args) => dialog.Close();
            buttons.Children.Add(pick); buttons.Children.Add(frame); buttons.Children.Add(cancel);
            panel.Children.Add(buttons); dialog.Content = panel;
            dialog.ShowDialog();
            return result;
        }

        private void MoveCombinedLayoutItem(string target)
        {
            var parts = (target ?? string.Empty).Split('|');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])
                || string.IsNullOrWhiteSpace(parts[1])
                || string.Equals(parts[0], parts[1], StringComparison.OrdinalIgnoreCase)) return;
            var keys = (_state.Project.Floors ?? new List<StairFloorDefinition>())
                .Where(floor => floor != null && FindPlanSourceForFloor(floor.Id) != null)
                .Select(floor => floor.Id).Concat(new[] { "SECTION" }).ToList();
            var order = ApplyOrderKeys(keys, _state.Project.CombinedLayoutItemOrder);
            var first = order.FindIndex(value => string.Equals(value, parts[0],
                StringComparison.OrdinalIgnoreCase));
            var second = order.FindIndex(value => string.Equals(value, parts[1],
                StringComparison.OrdinalIgnoreCase));
            if (first < 0 || second < 0) return;
            var value = order[first]; order[first] = order[second]; order[second] = value;
            _state.Project.CombinedLayoutItemOrder = order;
        }

        private static List<StairLayoutItem> ApplyCombinedLayoutOrder(
            IEnumerable<StairLayoutItem> source, IEnumerable<string> savedOrder)
        {
            var items = (source ?? Enumerable.Empty<StairLayoutItem>()).ToList();
            var orderedKeys = ApplyOrderKeys(items.Select(value => value.Key), savedOrder);
            return orderedKeys.Select(key => items.First(value => string.Equals(value.Key,
                key, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private static List<string> ApplyOrderKeys(IEnumerable<string> current,
            IEnumerable<string> savedOrder)
        {
            var keys = (current ?? Enumerable.Empty<string>()).Where(value =>
                !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var result = (savedOrder ?? Enumerable.Empty<string>()).Where(value =>
                keys.Contains(value, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.AddRange(keys.Where(value => !result.Contains(value,
                StringComparer.OrdinalIgnoreCase)));
            return result;
        }

        private enum PlanCaptureMode
        {
            Cancel,
            Stair,
            Frame
        }

        private void ClearPlanSource(string target)
        {
            StairFloorDefinition floor;
            StairStoreyDefinition storey;
            int storeyIndex;
            if (!TryResolvePlanFloor(target, out floor, out storey, out storeyIndex))
                return;
            if (_state.Project.PlanSources != null)
            {
                var matches = _state.Project.PlanSources
                    .Where(item => item != null
                        && ((!string.IsNullOrWhiteSpace(item.FloorId)
                                && string.Equals(item.FloorId, floor.Id, StringComparison.OrdinalIgnoreCase))
                            || (string.IsNullOrWhiteSpace(item.FloorId)
                                && string.Equals(item.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                foreach (var match in matches) _state.Project.PlanSources.Remove(match);
                var cache = new StairPlanCacheService();
                foreach (var match in matches) cache.Delete(match);
            }
            _storage.Save(_state.Project);
            SendPlanSourceState(target, "已清除本层平面来源登记；源图未作任何修改。", true);
        }

        private void InspectPlanSource(string target)
        {
            _constraints.Normalize(_state.Project);
            StairFloorDefinition floor;
            StairStoreyDefinition storey;
            int storeyIndex;
            if (!TryResolvePlanFloor(target, out floor, out storey, out storeyIndex))
                throw new InvalidOperationException("没有找到要预览的楼层。");
            var source = _state.Project.PlanSources.FirstOrDefault(item => item != null
                && ((!string.IsNullOrWhiteSpace(item.FloorId)
                        && string.Equals(item.FloorId, floor.Id, StringComparison.OrdinalIgnoreCase))
                    || (string.IsNullOrWhiteSpace(item.FloorId)
                        && string.Equals(item.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase))));
            if (source == null) throw new InvalidOperationException("本层尚未登记平面来源。");
            var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) throw new InvalidOperationException("当前没有打开的 CAD 图纸。");

            using (document.Editor.StartUserInteraction(this))
            {
                var summary = new StairPlanCaptureService().InspectRegisteredSource(document, source);
                SendPlanSourceState(target, summary, true);
            }
        }

        private void CreatePlanWorkingCopy(string target)
        {
            _constraints.Normalize(_state.Project);
            StairFloorDefinition floor;
            StairStoreyDefinition storey;
            int storeyIndex;
            if (!TryResolvePlanFloor(target, out floor, out storey, out storeyIndex))
                throw new InvalidOperationException("没有找到要生成小平面的楼层。");
            var source = _state.Project.PlanSources.FirstOrDefault(item => item != null
                && ((!string.IsNullOrWhiteSpace(item.FloorId)
                        && string.Equals(item.FloorId, floor.Id, StringComparison.OrdinalIgnoreCase))
                    || (string.IsNullOrWhiteSpace(item.FloorId)
                        && string.Equals(item.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase))));
            if (source == null) throw new InvalidOperationException("本层尚未登记平面来源。");
            var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) throw new InvalidOperationException("当前没有打开的 CAD 图纸。");

            using (document.Editor.StartUserInteraction(this))
            {
                var point = document.Editor.GetPoint("\n指定本层小平面工作副本左下角插入点：");
                if (point.Status != PromptStatus.OK) return;
                source.TargetScale = _state.Project.DrawingScale > 0
                    ? _state.Project.DrawingScale
                    : (source.TargetScale > 0 ? source.TargetScale : 30);
                var floorLabel = !string.IsNullOrWhiteSpace(floor.PlanFloorLabel)
                    ? floor.PlanFloorLabel
                    : (!string.IsNullOrWhiteSpace(source.FloorLabel) ? source.FloorLabel : storey.Name);
                var title = string.IsNullOrWhiteSpace(floorLabel)
                    ? "楼梯平面图"
                    : floorLabel + "楼梯平面图";
                var summary = new StairPlanCaptureService().CreateWorkingCopy(
                    document, source, point.Value, title);
                SendPlanSourceState(target, summary, true);
            }
        }

        private bool TryResolvePlanFloor(
            string target,
            out StairFloorDefinition floor,
            out StairStoreyDefinition storey,
            out int storeyIndex)
        {
            floor = null;
            storey = null;
            storeyIndex = -1;
            int legacyIndex;
            if (int.TryParse(target, out legacyIndex)
                && legacyIndex >= 0 && legacyIndex < _state.Project.Storeys.Count)
            {
                storeyIndex = legacyIndex;
                storey = _state.Project.Storeys[storeyIndex];
                var lowerFloorId = storey.LowerFloorId;
                floor = _state.Project.Floors.FirstOrDefault(item => item != null
                    && string.Equals(item.Id, lowerFloorId, StringComparison.OrdinalIgnoreCase));
                return floor != null;
            }

            floor = _state.Project.Floors.FirstOrDefault(item => item != null
                && string.Equals(item.Id, target, StringComparison.OrdinalIgnoreCase));
            if (floor == null) return false;
            var floorId = floor.Id;
            storeyIndex = _state.Project.Storeys.ToList().FindIndex(item => item != null
                && string.Equals(item.LowerFloorId, floorId, StringComparison.OrdinalIgnoreCase));
            if (storeyIndex < 0)
                storeyIndex = _state.Project.Storeys.ToList().FindLastIndex(item => item != null
                    && string.Equals(item.UpperFloorId, floorId, StringComparison.OrdinalIgnoreCase));
            if (storeyIndex < 0) return false;
            storey = _state.Project.Storeys[storeyIndex];
            return true;
        }

        private void SendPlanSourceState(string target, string summary, bool success)
        {
            if (_isClosing || _webView.CoreWebView2 == null) return;
            var payload = new Dictionary<string, object>
            {
                { "type", "plan-source" },
                { "target", target ?? string.Empty },
                { "summary", summary ?? string.Empty },
                { "success", success },
                { "state", _state }
            };
            _webView.CoreWebView2.PostWebMessageAsJson(_serializer.Serialize(payload));
        }

        private void ApplyMeasurement(string target, Point3d first, Point3d second)
        {
            var width = Math.Abs(second.X - first.X);
            var height = Math.Abs(second.Y - first.Y);
            if (string.Equals(target, "stairwell", StringComparison.OrdinalIgnoreCase))
            {
                var longSide = Math.Max(width, height);
                var shortSide = Math.Min(width, height);
                if (shortSide > 0.001) _state.Project.Construction.StairwellWidth = shortSide;
                if (longSide > 0.001) _state.Project.Construction.StairwellDepth = longSide;
                return;
            }

            if (!string.IsNullOrWhiteSpace(target) && target.StartsWith("value:", StringComparison.OrdinalIgnoreCase))
            {
                var path = target.Substring("value:".Length);
                var valueDistance = first.DistanceTo(second);
                ApplyMeasuredValue(path, valueDistance);
                return;
            }

            var parts = (target ?? string.Empty).Split(':');
            int firstIndex;
            var measuredDistance = first.DistanceTo(second);
            if (measuredDistance <= 0.001 || parts.Length < 2 || !int.TryParse(parts[1], out firstIndex)) return;
            if (parts[0] == "floor" && firstIndex >= 0 && firstIndex < _state.Project.Floors.Count)
            {
                _constraints.SetPlatformWidth(
                    _state.Project,
                    _state.Project.Floors[firstIndex].Id,
                    measuredDistance);
            }
            else if (parts[0] == "landing" && parts.Length == 3)
            {
                int secondIndex;
                if (int.TryParse(parts[2], out secondIndex)
                    && firstIndex >= 0 && firstIndex < _state.Project.Storeys.Count
                    && secondIndex >= 0 && secondIndex < _state.Project.Storeys[firstIndex].Landings.Count)
                {
                    _constraints.SetPlatformWidth(
                        _state.Project,
                        _state.Project.Storeys[firstIndex].Landings[secondIndex].Id,
                        measuredDistance);
                }
            }
        }

        private void ApplyMeasuredValue(string path, double value)
        {
            if (string.IsNullOrWhiteSpace(path) || value <= 0.001) return;
            var parts = path.Split('.');
            if (parts.Length < 2 || parts[0] != "Project") return;
            object current = _state.Project;
            for (var index = 1; index < parts.Length - 1; index++)
            {
                if (current is IList list && int.TryParse(parts[index], out var itemIndex))
                {
                    if (itemIndex < 0 || itemIndex >= list.Count) return;
                    current = list[itemIndex];
                    continue;
                }
                var property = current.GetType().GetProperty(parts[index]);
                if (property == null) return;
                current = property.GetValue(current, null);
                if (current == null) return;
            }
            var targetProperty = current.GetType().GetProperty(parts[parts.Length - 1]);
            if (targetProperty == null || !targetProperty.CanWrite) return;
            var underlying = Nullable.GetUnderlyingType(targetProperty.PropertyType) ?? targetProperty.PropertyType;
            if (underlying == typeof(double)) targetProperty.SetValue(current, value, null);
            else if (underlying == typeof(int)) targetProperty.SetValue(current, (int)Math.Round(value), null);
        }

        private void SendMeasuredState(string target)
        {
            if (_isClosing || _webView.CoreWebView2 == null) return;
            var payload = new Dictionary<string, object>
            {
                { "type", "measured" },
                { "target", target },
                { "state", _state }
            };
            _webView.CoreWebView2.PostWebMessageAsJson(_serializer.Serialize(payload));
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs eventArgs)
        {
            _isClosing = true;
            var core = _webView.CoreWebView2;
            if (core != null)
            {
                core.WebMessageReceived -= OnWebMessageReceived;
                core.Stop();
            }
            Content = null;
        }

        private void OnClosed(object sender, EventArgs eventArgs)
        {
            var completed = Completed;
            if (completed != null) completed(this, EventArgs.Empty);

            // Release the detached browser after AutoCAD has resumed its
            // command loop; WebView2 can otherwise hold a host resource busy.
            var dispatcher = Dispatcher;
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
            {
                try
                {
                    dispatcher.BeginInvoke(
                        new Action(() => _webView.Dispose()),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
                catch (InvalidOperationException)
                {
                    // The CAD dispatcher is already shutting down.
                }
            });
        }

        private static string JoinIssues(StairProjectCalculationOutcome outcome)
        {
            return string.Join("；", outcome.Issues.Select(issue => issue.ParameterName + ": " + issue.Message));
        }

        private static string BuildSvg(DrawingView view, UiState state, string previewFloorLabel)
        {
            if (view == null || view.Lines == null || view.Lines.Count == 0)
                return "<svg id='sectionSvg' viewBox='0 0 1 1'></svg>";

            var selectedComponentId = state.SelectedComponentId;
            var xs = view.Lines.SelectMany(line => new[] { line.Start.X, line.End.X })
                .Concat(view.HatchRegions.SelectMany(region => region.Boundary.Select(point => point.X)))
                .Concat(view.Dimensions.SelectMany(dimension => new[]
                {
                    dimension.FirstExtensionOrigin.X,
                    dimension.SecondExtensionOrigin.X,
                    dimension.DimensionLinePoint.X
                }))
                .Concat(view.Tables.SelectMany(table => new[]
                {
                    table.Position.X,
                    table.Position.X + table.ColumnWidths.Sum()
                }))
                .ToArray();
            var ys = view.Lines.SelectMany(line => new[] { line.Start.Y, line.End.Y })
                .Concat(view.HatchRegions.SelectMany(region => region.Boundary.Select(point => point.Y)))
                .Concat(view.Dimensions.SelectMany(dimension => new[]
                {
                    dimension.FirstExtensionOrigin.Y,
                    dimension.SecondExtensionOrigin.Y,
                    dimension.DimensionLinePoint.Y
                }))
                .Concat(view.Tables.SelectMany(table => new[]
                {
                    table.Position.Y,
                    table.Position.Y - table.RowHeight * table.Rows.Count
                }))
                .ToArray();
            var minX = xs.Min() - 350.0;
            var maxX = xs.Max() + 350.0;
            var minY = ys.Min() - 350.0;
            var maxY = ys.Max() + 350.0;
            var builder = new StringBuilder();
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "<svg id='sectionSvg' viewBox='{0} {1} {2} {3}' preserveAspectRatio='xMidYMid meet'>",
                minX,
                -maxY,
                Math.Max(1.0, maxX - minX),
                Math.Max(1.0, maxY - minY));
            builder.Append("<defs><pattern id='sectionHatch' width='20' height='20' patternUnits='userSpaceOnUse'><path d='M-4 18L18-4M3 24L24 3' stroke='#d7b93e' stroke-width='1'/><circle cx='5' cy='5' r='1.3' fill='#d7b93e'/><circle cx='15' cy='13' r='1' fill='#d7b93e'/></pattern><pattern id='wallHatch' width='12' height='12' patternUnits='userSpaceOnUse'><path d='M-3 12L12-3M3 15L15 3' stroke='#c7d0d9' stroke-width='1'/></pattern></defs>");
            if (!string.IsNullOrWhiteSpace(previewFloorLabel))
            {
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<text x='{0}' y='{1}' style='font-size:150px;font-weight:700;fill:#58ef70;stroke:#101820;stroke-width:3;paint-order:stroke'>{2} · 楼梯平面预览</text>",
                    minX + 70.0,
                    -maxY + 190.0,
                    Escape(previewFloorLabel));
            }
            else if (state != null && state.Project != null && state.Project.Storeys != null)
            {
                // A storey is the vertical interval between two floor slabs.
                // Therefore N storeys have N+1 plan levels. Put labels on the
                // actual slab elevations instead of at storey mid-heights.
                var elevation = state.Project.BaseElevation;
                foreach (var storey in state.Project.Storeys.Where(item => item != null))
                {
                    var lowerFloor = state.Project.Floors == null
                        ? null
                        : state.Project.Floors.FirstOrDefault(item => item != null
                            && string.Equals(item.Id, storey.LowerFloorId, StringComparison.OrdinalIgnoreCase));
                    var label = lowerFloor != null && !string.IsNullOrWhiteSpace(lowerFloor.PlanFloorLabel)
                        ? lowerFloor.PlanFloorLabel
                        : (!string.IsNullOrWhiteSpace(storey.PlanFloorLabel) ? storey.PlanFloorLabel : storey.Name);
                    builder.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "<text x='{0}' y='{1}' text-anchor='end' style='font-size:115px;font-weight:700;fill:#58ef70;stroke:#101820;stroke-width:3;paint-order:stroke'>{2}</text>",
                        maxX - 70.0,
                        -elevation,
                        Escape(label));
                    elevation += Math.Max(0.0, storey.Height);
                }

                var finalStorey = state.Project.Storeys.LastOrDefault(item => item != null);
                var topFloor = finalStorey == null || state.Project.Floors == null
                    ? null
                    : state.Project.Floors.FirstOrDefault(item => item != null
                        && string.Equals(item.Id, finalStorey.UpperFloorId, StringComparison.OrdinalIgnoreCase));
                if (topFloor != null)
                {
                    builder.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "<text x='{0}' y='{1}' text-anchor='end' style='font-size:115px;font-weight:700;fill:#58ef70;stroke:#101820;stroke-width:3;paint-order:stroke'>{2}</text>",
                        maxX - 70.0,
                        -elevation,
                        Escape(topFloor.PlanFloorLabel));
                }
            }
            foreach (var line in view.Lines)
            {
                if (line.Role == StairLineRole.HatchBoundary) continue;
                var css = line.IsHidden ? "rear" : "cut";
                if (line.Role == StairLineRole.BeamBoundary) css += " beam";
                if (line.Role == StairLineRole.WallBoundary) css += " wall";
                if (line.Role == StairLineRole.OpeningBoundary) css = "opening";
                if (line.Role == StairLineRole.DoorWindowWindowMain) css = "dw-window-main";
                if (line.Role == StairLineRole.DoorWindowDoorMain) css = "dw-door-main";
                if (line.Role == StairLineRole.DoorWindowWindowSash
                    || line.Role == StairLineRole.DoorWindowDoorSash) css = "dw-sash";
                if (line.Role == StairLineRole.DoorWindowOpeningHole) css = "dw-opening";
                if (line.Role == StairLineRole.AxisLine) css = "axis";
                if (line.Role == StairLineRole.Handrail) css = "handrail";
                if (line.Role == StairLineRole.BreakLine) css = "breakline";
                if (!string.IsNullOrEmpty(selectedComponentId)
                    && string.Equals(line.ComponentId, selectedComponentId, StringComparison.OrdinalIgnoreCase))
                {
                    css += " selected";
                }
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<line class='{0}' data-component='{1}' x1='{2}' y1='{3}' x2='{4}' y2='{5}'/>",
                    css,
                    Escape(line.ComponentId),
                    line.Start.X,
                    -line.Start.Y,
                    line.End.X,
                    -line.End.Y);
            }
            foreach (var drawingText in view.Texts)
            {
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<text x='{0}' y='{1}'>{2}</text>",
                    drawingText.Position.X,
                    -drawingText.Position.Y,
                    Escape(drawingText.Content));
            }
            foreach (var dimension in view.Dimensions)
            {
                if (dimension.Orientation == DrawingDimensionOrientation.Horizontal)
                {
                    var y = dimension.DimensionLinePoint.Y;
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<line style='stroke:#58ef70;stroke-width:2' x1='{0}' y1='{1}' x2='{0}' y2='{2}'/>",
                        dimension.FirstExtensionOrigin.X, -dimension.FirstExtensionOrigin.Y, -y);
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<line style='stroke:#58ef70;stroke-width:2' x1='{0}' y1='{1}' x2='{0}' y2='{2}'/>",
                        dimension.SecondExtensionOrigin.X, -dimension.SecondExtensionOrigin.Y, -y);
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<line style='stroke:#58ef70;stroke-width:2' x1='{0}' y1='{1}' x2='{2}' y2='{1}'/>",
                        dimension.FirstExtensionOrigin.X, -y, dimension.SecondExtensionOrigin.X);
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<text x='{0}' y='{1}'>{2}</text>",
                        dimension.DimensionLinePoint.X, -y - 25.0, Escape(dimension.TextOverride));
                    continue;
                }
                var x = dimension.DimensionLinePoint.X;
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<line style='stroke:#58ef70;stroke-width:2' x1='{0}' y1='{1}' x2='{2}' y2='{3}'/>",
                    dimension.FirstExtensionOrigin.X, -dimension.FirstExtensionOrigin.Y, x, -dimension.FirstExtensionOrigin.Y);
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<line style='stroke:#58ef70;stroke-width:2' x1='{0}' y1='{1}' x2='{2}' y2='{3}'/>",
                    dimension.SecondExtensionOrigin.X, -dimension.SecondExtensionOrigin.Y, x, -dimension.SecondExtensionOrigin.Y);
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<line style='stroke:#58ef70;stroke-width:2' x1='{0}' y1='{1}' x2='{0}' y2='{2}'/>",
                    x, -dimension.FirstExtensionOrigin.Y, -dimension.SecondExtensionOrigin.Y);
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<text transform='translate({0} {1}) rotate(-90)'>{2}</text>",
                    x - 25.0, -dimension.DimensionLinePoint.Y, Escape(dimension.TextOverride));
            }
            foreach (var table in view.Tables)
            {
                var x = table.Position.X;
                var top = table.Position.Y;
                var width = table.ColumnWidths.Sum();
                var height = table.RowHeight * table.Rows.Count;
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "<rect x='{0}' y='{1}' width='{2}' height='{3}' fill='none' stroke='#f4e74f' stroke-width='2'/>",
                    x, -top, width, height);
                var columnX = x;
                for (var column = 0; column < table.ColumnWidths.Count - 1; column++)
                {
                    columnX += table.ColumnWidths[column];
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<line style='stroke:#f4e74f;stroke-width:2' x1='{0}' y1='{1}' x2='{0}' y2='{2}'/>",
                        columnX, -top, -top + height);
                }
                for (var row = 1; row < table.Rows.Count; row++)
                {
                    var y = -top + row * table.RowHeight;
                    builder.AppendFormat(CultureInfo.InvariantCulture,
                        "<line style='stroke:#f4e74f;stroke-width:2' x1='{0}' y1='{1}' x2='{2}' y2='{1}'/>",
                        x, y, x + width);
                }
                for (var row = 0; row < table.Rows.Count; row++)
                {
                    columnX = x;
                    for (var column = 0; column < table.ColumnWidths.Count; column++)
                    {
                        var content = column < table.Rows[row].Count ? table.Rows[row][column] : string.Empty;
                        builder.AppendFormat(CultureInfo.InvariantCulture,
                            "<text x='{0}' y='{1}' style='font-size:{2}px'>{3}</text>",
                            columnX + table.ColumnWidths[column] / 2.0,
                            -top + (row + 0.68) * table.RowHeight,
                            2.5 * view.Scale,
                            Escape(content));
                        columnX += table.ColumnWidths[column];
                    }
                }
            }
            AppendDragHandle(builder, view, state);
            builder.Append("</svg>");
            return builder.ToString();
        }

        private static void AppendDragHandle(StringBuilder builder, DrawingView view, UiState state)
        {
            var componentId = state.SelectedComponentId;
            if (string.IsNullOrWhiteSpace(componentId)) return;
            if (string.Equals(componentId, "WALL", StringComparison.OrdinalIgnoreCase)
                || componentId.StartsWith("WALL-", StringComparison.OrdinalIgnoreCase)) return;
            var componentLines = view.Lines
                .Where(line => string.Equals(line.ComponentId, componentId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (componentLines.Length == 0) return;

            var points = componentLines.SelectMany(line => new[] { line.Start, line.End }).ToArray();
            var mode = "depth";
            var axis = "x";
            var x = points.Max(point => point.X);
            var y = points.Max(point => point.Y);

            var flight = state.Project.Storeys
                .SelectMany(storey => storey.Flights)
                .FirstOrDefault(item => string.Equals(item.Id, componentId, StringComparison.OrdinalIgnoreCase));
            if (flight != null)
            {
                mode = "flightTread";
                x = points.OrderByDescending(point => point.Y).ThenByDescending(point => point.X).First().X;
                y = points.Max(point => point.Y);
            }
            else
            {
                var floor = state.Project.Floors.FirstOrDefault(item =>
                    string.Equals(item.Id, componentId, StringComparison.OrdinalIgnoreCase));
                if (floor != null)
                {
                    mode = "floorDepth";
                    x = floor.ProjectionDirection < 0 ? points.Min(point => point.X) : points.Max(point => point.X);
                    y = points.Max(point => point.Y);
                }
                else
                {
                    var landing = state.Project.Storeys.SelectMany(storey => storey.Landings).FirstOrDefault(item =>
                        string.Equals(item.Id, componentId, StringComparison.OrdinalIgnoreCase));
                    if (landing != null)
                    {
                        mode = "landingDepth";
                        x = landing.ProjectionDirection < 0 ? points.Min(point => point.X) : points.Max(point => point.X);
                        y = points.Max(point => point.Y);
                    }
                    else
                    {
                        mode = "beamDepth";
                        axis = "y";
                        x = points.Average(point => point.X);
                        y = points.Min(point => point.Y);
                    }
                }
            }

            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "<circle class='drag-handle' data-component='{0}' data-mode='{1}' data-axis='{2}' cx='{3}' cy='{4}' r='45'/>",
                Escape(componentId),
                mode,
                axis,
                x,
                -y);
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("'", "&#39;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private string BuildHtml()
        {
            return StairEditorPage.Build(_serializer.Serialize(_state));
        }

        private string BuildHtmlLegacy()
        {
            var initialState = _serializer.Serialize(_state);
            return @"<!doctype html><html><head><meta charset='utf-8'><style>
html,body{height:100%;margin:0;font-family:'Microsoft YaHei UI',sans-serif;background:#eef1f4;color:#18212b;overflow:hidden}.app{height:100%;display:grid;grid-template-columns:500px 1fr}.panel{background:#fff;border-right:1px solid #d8dee7;display:grid;grid-template-rows:auto auto 1fr auto;min-width:500px}.head{padding:16px 18px 10px}.head h1{font-size:19px;margin:0 0 5px}.head p{font-size:12px;color:#667085;margin:0}.tabs{display:flex;padding:0 18px;border-bottom:1px solid #e4e8ee}.tab{padding:10px 14px;border:0;background:none;color:#5d6875;cursor:pointer}.tab.active{color:#1677ff;border-bottom:2px solid #1677ff}.editor{padding:12px 18px;overflow:auto}.section-title{display:flex;align-items:center;justify-content:space-between;margin:5px 0 10px}.section-title h2{font-size:15px;margin:0}.card{border:1px solid #dce2ea;border-radius:7px;margin:8px 0;padding:10px;background:#fff}.card.selected{border-color:#1677ff;background:#f3f8ff}.card-head{display:flex;align-items:center;justify-content:space-between;font-size:13px;font-weight:600;margin-bottom:8px}.grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px 10px}.field label{display:block;font-size:11px;color:#667085;margin:0 0 3px}.field input,.field select{width:100%;box-sizing:border-box;border:1px solid #cdd5df;border-radius:5px;padding:6px;font-size:12px}.wide{grid-column:1/-1}.mini{border:1px solid #bcc6d2;background:#fff;border-radius:4px;padding:4px 8px;cursor:pointer}.danger{color:#d9363e}.actions{padding:12px 18px;border-top:1px solid #e4e8ee;display:flex;gap:10px}.actions button{flex:1;padding:9px;border-radius:5px;border:1px solid #b8c0cb;background:#fff}.actions .primary{background:#1677ff;color:#fff;border-color:#1677ff}.stage{display:grid;grid-template-rows:48px 1fr 62px;background:#14191f}.toolbar{display:flex;align-items:center;padding:0 18px;color:#e4e9ef;border-bottom:1px solid #303844;font-size:13px}.canvas{overflow:hidden}.canvas svg{width:100%;height:100%}.cut{stroke:#f1f3f5;stroke-width:5;vector-effect:non-scaling-stroke}.rear{stroke:#20d9dc;stroke-width:4;stroke-dasharray:24 14;vector-effect:non-scaling-stroke}.beam{stroke:#ff6b4a;stroke-width:6}.selected{stroke:#56f28f!important;stroke-width:9!important}.canvas line[data-component]{cursor:pointer}.drag-handle{fill:#56f28f;stroke:#0c1218;stroke-width:12;cursor:move;pointer-events:all}.canvas text{fill:#8beaaf;font-size:90px;text-anchor:middle;pointer-events:none}.status{padding:10px 18px;color:#dce3ea;border-top:1px solid #303844;font-size:13px}.status b{color:#56f28f}.status .error{color:#ff7272}.hint{color:#8996a3;font-size:12px;margin-top:4px}.empty{color:#7b8794;padding:20px;text-align:center}
</style></head><body><div class='app'><section class='panel'><div class='head'><h1>楼梯构件编辑器</h1><p>统一构造 → 楼板 → 楼层段 → 梯段/平台；所有构件均有唯一编号</p></div><div class='tabs'><button class='tab active' data-tab='storeys'>楼层与梯段</button><button class='tab' data-tab='floors'>楼板/楼板梁</button><button class='tab' data-tab='global'>统一构造</button></div><div id='editor' class='editor'></div><div class='actions'><button onclick=""post('cancel')"">取消</button><button class='primary' onclick=""post('confirm')"">确定生成</button></div></section><section class='stage'><div class='toolbar'>构件化剖面预览（点击梯段、平台、楼板或梁可定位参数）</div><div class='canvas'><div id='svgHost'></div></div><div class='status'><b id='summary'>正在计算...</b><div class='hint'>白色=剖切构件，青色虚线=后方构件，橙色=梁，绿色=当前选中构件。</div></div></section></div><script>
const state=" + initialState + @";let tab='storeys';const editor=document.getElementById('editor');const n=v=>Number(v);const esc=s=>String(s??'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/'/g,'&#39;');
function post(action){chrome.webview.postMessage({Action:action,State:state})}function input(label,value,path,step=10,type='number'){return `<div class='field'><label>${label}</label><input type='${type}' value='${esc(value??'')}' step='${step}' data-path='${path}'></div>`}function select(label,value,path,options){return `<div class='field'><label>${label}</label><select data-path='${path}'>${options.map(o=>`<option value='${o[0]}' ${String(o[0])===String(value)?'selected':''}>${o[1]}</option>`).join('')}</select></div>`}
function resolve(path){const parts=path.split('.');let o=state;for(let i=0;i<parts.length-1;i++)o=o[parts[i]];return[o,parts[parts.length-1]]}function bind(){editor.querySelectorAll('[data-path]').forEach(el=>el.onchange=()=>{const [o,k]=resolve(el.dataset.path);const nullable=['SlabThicknessOverride','BeamWidthOverride','BeamDepthOverride'];o[k]=el.type==='number'?(el.value===''&&nullable.includes(k)?null:n(el.value)):el.value;if(el.tagName==='SELECT'&&['Direction','SectionRepresentation','ProjectionDirection','PlatformType'].includes(k))o[k]=n(el.value);post('preview');render()});editor.querySelectorAll('[data-select]').forEach(el=>el.onclick=()=>{state.SelectedComponentId=el.dataset.select;post('preview');render()})}
function render(){document.querySelectorAll('.tab').forEach(x=>x.classList.toggle('active',x.dataset.tab===tab));if(tab==='global')renderGlobal();else if(tab==='floors')renderFloors();else renderStoreys();bind()}
function renderGlobal(){const c=state.Project.Construction;editor.innerHTML=`<div class='section-title'><h2>统一构造参数</h2></div><div class='card'><div class='grid'>${input('梯板厚度',c.FlightSlabThickness,'Project.Construction.FlightSlabThickness')}${input('休息平台板厚',c.LandingSlabThickness,'Project.Construction.LandingSlabThickness')}${input('楼板厚度',c.FloorSlabThickness,'Project.Construction.FloorSlabThickness')}${input('楼板梁宽',c.FloorBeam.Width,'Project.Construction.FloorBeam.Width')}${input('楼板梁高',c.FloorBeam.Depth,'Project.Construction.FloorBeam.Depth')}${input('平台梁宽',c.LandingBeam.Width,'Project.Construction.LandingBeam.Width')}${input('平台梁高',c.LandingBeam.Depth,'Project.Construction.LandingBeam.Depth')}${input('栏杆高度',c.Railing.Height,'Project.Construction.Railing.Height')}${input('墙厚',c.Wall.Thickness,'Project.Construction.Wall.Thickness')}${input('门宽',c.Door.Width,'Project.Construction.Door.Width')}${input('门高',c.Door.Height,'Project.Construction.Door.Height')}${input('窗宽',c.Window.Width,'Project.Construction.Window.Width')}${input('窗高',c.Window.Height,'Project.Construction.Window.Height')}${input('窗台高',c.Window.SillHeight,'Project.Construction.Window.SillHeight')}</div></div>`}
function renderFloors(){editor.innerHTML=`<div class='section-title'><h2>楼板平台</h2><span>共 ${state.Project.Floors.length} 个</span></div>`+state.Project.Floors.map((f,i)=>`<div class='card ${state.SelectedComponentId===f.Id?'selected':''}' data-select='${f.Id}'><div class='card-head'><span>${esc(f.Id)} · ${esc(f.Name)}</span></div><div class='grid'>${select('平台类型',f.PlatformType,`Project.Floors.${i}.PlatformType`,[[1,'平台1 · 单侧梁'],[2,'平台2 · 双侧梁'],[3,'平台3 · 双侧梁外挑']])}${input('平台宽',f.PlatformWidth,`Project.Floors.${i}.PlatformWidth`)}${input('梁宽',f.BeamWidthOverride??state.Project.Construction.FloorBeam.Width,`Project.Floors.${i}.BeamWidthOverride`)}${input('梁高',f.BeamDepthOverride??state.Project.Construction.FloorBeam.Depth,`Project.Floors.${i}.BeamDepthOverride`)}</div></div>`).join('')}
function renderStoreys(){editor.innerHTML=`<div class='section-title'><h2>楼层段</h2><button class='mini' id='addStorey'>+ 增加楼层</button></div>`+state.Project.Storeys.map((s,si)=>`<div class='card'><div class='card-head'><span>${esc(s.Id)} · ${esc(s.Name)}</span><button class='mini danger' data-remove-storey='${si}'>删除末层</button></div><div class='grid'>${input('层段编号',s.Id,`Project.Storeys.${si}.Id`,1,'text')}${input('层高',s.Height,`Project.Storeys.${si}.Height`)}${input('下楼板编号',s.LowerFloorId,`Project.Storeys.${si}.LowerFloorId`,1,'text')}${input('上楼板编号',s.UpperFloorId,`Project.Storeys.${si}.UpperFloorId`,1,'text')}</div><div class='section-title'><h2>梯段（${s.Flights.length} 跑）</h2><button class='mini' data-add-flight='${si}'>+ 增加一跑</button></div>${s.Flights.map((f,fi)=>`<div class='card ${state.SelectedComponentId===f.Id?'selected':''}' data-select='${f.Id}'><div class='card-head'><span>${esc(f.Id)} · 第 ${fi+1} 跑</span><button class='mini danger' data-remove-flight='${si}:${fi}'>删除</button></div><div class='grid'>${input('梯段编号',f.Id,`Project.Storeys.${si}.Flights.${fi}.Id`,1,'text')}${input('名称',f.Name,`Project.Storeys.${si}.Flights.${fi}.Name`,1,'text')}${input('级数(踢面)',f.RiserCount,`Project.Storeys.${si}.Flights.${fi}.RiserCount`,1)}${input('踏步宽',f.TreadDepth,`Project.Storeys.${si}.Flights.${fi}.TreadDepth`,5)}${input('梯段宽',f.Width,`Project.Storeys.${si}.Flights.${fi}.Width`)}${input('梯板厚覆盖',f.SlabThicknessOverride,`Project.Storeys.${si}.Flights.${fi}.SlabThicknessOverride`)}${select('剖面方向',f.Direction,`Project.Storeys.${si}.Flights.${fi}.Direction`,[[-1,'向左上'],[1,'向右上']])}${select('剖切关系',f.SectionRepresentation,`Project.Storeys.${si}.Flights.${fi}.SectionRepresentation`,[[0,'剖到(实线)'],[1,'后方(虚线)']])}</div></div>`).join('')}<div class='section-title'><h2>休息平台（${s.Landings.length} 个）</h2></div>${s.Landings.map((p,pi)=>`<div class='card ${state.SelectedComponentId===p.Id?'selected':''}' data-select='${p.Id}'><div class='card-head'><span>${esc(p.Id)} · ${esc(p.Name)}</span></div><div class='grid'>${select('平台类型',p.PlatformType,`Project.Storeys.${si}.Landings.${pi}.PlatformType`,[[1,'平台1 · 单侧梁'],[2,'平台2 · 双侧梁'],[3,'平台3 · 双侧梁外挑']])}${input('平台宽',p.PlatformWidth,`Project.Storeys.${si}.Landings.${pi}.PlatformWidth`)}${input('梁宽',p.BeamWidthOverride??state.Project.Construction.LandingBeam.Width,`Project.Storeys.${si}.Landings.${pi}.BeamWidthOverride`)}${input('梁高',p.BeamDepthOverride??state.Project.Construction.LandingBeam.Depth,`Project.Storeys.${si}.Landings.${pi}.BeamDepthOverride`)}</div></div>`).join('')}</div>`).join('');document.getElementById('addStorey').onclick=addStorey;editor.querySelectorAll('[data-add-flight]').forEach(x=>x.onclick=e=>{e.stopPropagation();addFlight(n(x.dataset.addFlight))});editor.querySelectorAll('[data-remove-flight]').forEach(x=>x.onclick=e=>{e.stopPropagation();const a=x.dataset.removeFlight.split(':').map(n);removeFlight(a[0],a[1])});editor.querySelectorAll('[data-remove-storey]').forEach(x=>x.onclick=e=>{e.stopPropagation();removeStorey(n(x.dataset.removeStorey))})}
function addStorey(){const i=state.Project.Storeys.length+1,prev=state.Project.Floors[state.Project.Floors.length-1],fid=`LB-${String(i+1).padStart(2,'0')}`;state.Project.Floors.push({Id:fid,Name:`${i+1}层楼板`,DepthToUpFlight:1200,DepthToDownFlight:1200,PlatformType:1,PlatformWidth:1200,ProjectionDirection:-1,SlabThicknessOverride:null,BeamWidthOverride:null,BeamDepthOverride:null,BeamId:`LL-${String(i+1).padStart(2,'0')}`});const s={Id:`LC-${String(i).padStart(2,'0')}`,Name:`第${i}层段`,LowerFloorId:prev.Id,UpperFloorId:fid,Height:3000,Flights:[],Landings:[]};state.Project.Storeys.push(s);addFlight(i-1,false);addFlight(i-1,false);render();post('preview')}
function distributeRisers(s){const total=Math.max(s.Flights.length*3,Math.round(s.Height/166.7)),base=Math.floor(total/s.Flights.length),extra=total%s.Flights.length;s.Flights.forEach((f,i)=>f.RiserCount=base+(i<extra?1:0))}
function addFlight(si,refresh=true){const s=state.Project.Storeys[si],i=s.Flights.length+1,id=`TD-${si+1}-${i}`;if(i>1){const prev=s.Flights[i-2];s.Landings.push({Id:`PT-${si+1}-${i-1}`,Name:`第${si+1}层平台${i-1}`,IncomingFlightId:prev.Id,OutgoingFlightId:id,DepthToIncomingFlight:1200,DepthToOutgoingFlight:1200,PlatformType:2,PlatformWidth:1200,ProjectionDirection:i%2===0?1:-1,SlabThicknessOverride:null,BeamWidthOverride:null,BeamDepthOverride:null,BeamId:`PTL-${si+1}-${i-1}`})}s.Flights.push({Id:id,Name:`第${si+1}层第${i}跑`,RiserCount:3,TreadDepth:280,Width:1150,SlabThicknessOverride:null,Direction:i%2===1?1:-1,SectionRepresentation:i%2===1?1:0});distributeRisers(s);if(refresh){render();post('preview')}}
function removeFlight(si,fi){const s=state.Project.Storeys[si];if(s.Flights.length<=1)return;s.Flights.splice(fi,1);s.Landings=[];for(let i=1;i<s.Flights.length;i++)s.Landings.push({Id:`PT-${si+1}-${i}`,Name:`第${si+1}层平台${i}`,IncomingFlightId:s.Flights[i-1].Id,OutgoingFlightId:s.Flights[i].Id,DepthToIncomingFlight:1200,DepthToOutgoingFlight:1200,PlatformType:2,PlatformWidth:1200,ProjectionDirection:i%2?1:-1,SlabThicknessOverride:null,BeamWidthOverride:null,BeamDepthOverride:null,BeamId:`PTL-${si+1}-${i}`});distributeRisers(s);render();post('preview')}
function removeStorey(si){if(si!==state.Project.Storeys.length-1||state.Project.Storeys.length<=1)return;state.Project.Storeys.pop();state.Project.Floors.pop();render();post('preview')}
function findComponent(id){for(const floor of state.Project.Floors){if(floor.Id===id)return{kind:'floor',value:floor};if(floor.BeamId===id)return{kind:'floorBeam',value:floor}}for(const storey of state.Project.Storeys){for(const flight of storey.Flights)if(flight.Id===id)return{kind:'flight',value:flight};for(const landing of storey.Landings){if(landing.Id===id)return{kind:'landing',value:landing};if(landing.BeamId===id)return{kind:'landingBeam',value:landing}}}return null}
function bindSvg(){document.querySelectorAll('#sectionSvg line[data-component]').forEach(x=>x.onclick=()=>{if(!x.dataset.component)return;state.SelectedComponentId=x.dataset.component;const id=state.SelectedComponentId;tab=id.startsWith('LB-')||id.startsWith('LL-')?'floors':'storeys';render();post('preview')});const h=document.querySelector('.drag-handle');if(!h)return;h.onpointerdown=ev=>{h.setPointerCapture(ev.pointerId);h.dataset.startX=ev.clientX;h.dataset.startY=ev.clientY};h.onpointerup=ev=>{if(!h.hasPointerCapture(ev.pointerId))return;const svg=h.ownerSVGElement,units=svg.viewBox.baseVal.width/svg.clientWidth,dx=(ev.clientX-n(h.dataset.startX))*units,dy=(ev.clientY-n(h.dataset.startY))*units,c=findComponent(h.dataset.component);if(!c)return;if(h.dataset.mode==='flightTread'){const count=Math.max(1,c.value.RiserCount-1),direction=c.value.Direction;c.value.TreadDepth=Math.max(200,Math.round((c.value.TreadDepth+dx*direction/count)/5)*5)}else if(h.dataset.mode==='floorDepth'){const delta=dx*c.value.ProjectionDirection,key=c.value.DepthToUpFlight>=c.value.DepthToDownFlight?'DepthToUpFlight':'DepthToDownFlight';c.value[key]=Math.max(300,Math.round((c.value[key]+delta)/10)*10)}else if(h.dataset.mode==='landingDepth'){const delta=dx*c.value.ProjectionDirection,key=c.value.DepthToIncomingFlight>=c.value.DepthToOutgoingFlight?'DepthToIncomingFlight':'DepthToOutgoingFlight';c.value[key]=Math.max(300,Math.round((c.value[key]+delta)/10)*10)}else if(h.dataset.mode==='beamDepth'){const key='BeamDepthOverride',base=c.value[key]??(c.kind==='floorBeam'?state.Project.Construction.FloorBeam.Depth:state.Project.Construction.LandingBeam.Depth);c.value[key]=Math.max(150,Math.round((base+dy)/10)*10)}render();post('preview')}}
document.querySelectorAll('.tab').forEach(x=>x.onclick=()=>{tab=x.dataset.tab;render()});window.chrome.webview.addEventListener('message',e=>{if(e.data.type!=='preview')return;document.getElementById('svgHost').innerHTML=e.data.svg;bindSvg();const s=document.getElementById('summary');s.textContent=e.data.summary;s.className=e.data.success?'':'error'});render();post('preview');
</script></body></html>";
        }

        private sealed class UiMessage
        {
            public string Action { get; set; }

            public string Target { get; set; }

            public UiState State { get; set; }
        }

        public sealed class UiState
        {
            public StairProjectDefinition Project { get; set; }

            public string SelectedComponentId { get; set; }

            public bool BaseElevationLocked { get; set; }

            public bool DirectionLinked { get; set; }

            public bool ConstructionLinked { get; set; }

            public bool SectionRepresentationLinked { get; set; }

            public IList<LayoutFrameOption> LayoutFrames { get; set; }

            public string SelectedLayoutFrameId { get; set; }

            public static UiState Create(StairProjectDefinition project)
            {
                return new UiState
                {
                    Project = project ?? StairProjectDefinition.CreateDefault(),
                    SelectedComponentId = null,
                    BaseElevationLocked = true,
                    DirectionLinked = true,
                    ConstructionLinked = true,
                    SectionRepresentationLinked = true
                };
            }
        }

        public sealed class LayoutFrameOption
        {
            public string RegistrationId { get; set; }
            public string DisplayName { get; set; }
            public double PageWidth { get; set; }
            public double PageHeight { get; set; }
            public double LeftMargin { get; set; }
            public double RightMargin { get; set; }
            public double TopMargin { get; set; }
            public double BottomMargin { get; set; }
        }
    }
}
