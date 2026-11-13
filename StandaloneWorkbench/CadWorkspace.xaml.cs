using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Wanluo.StandaloneWorkbench
{
    public partial class CadWorkspace : UserControl, IDisposable
    {
        internal CadClient Client { get; }
        public static readonly DependencyProperty CanUndoProperty = DependencyProperty.Register(nameof(CanUndo), typeof(bool), typeof(CadWorkspace));
        public static readonly DependencyProperty CanRedoProperty = DependencyProperty.Register(nameof(CanRedo), typeof(bool), typeof(CadWorkspace));
        public bool CanUndo { get => (bool)GetValue(CanUndoProperty); private set => SetValue(CanUndoProperty, value); }
        public bool CanRedo { get => (bool)GetValue(CanRedoProperty); private set => SetValue(CanRedoProperty, value); }
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        private bool _updating;
        private bool _disposed;
        private bool _initialized;
        private Task _operation = Task.CompletedTask;
        private string _documents = "", _layers = "", _layouts = "", _properties = "";
        internal event Action<string> DrawingOpened;

        public CadWorkspace()
        {
            Client = new CadClient();
            InitializeComponent();
            BuildRibbon();
            Viewport.Session = Client.Handle;
            Viewport.TextRequested += text => { CommandInput.Focus(); CommandInput.AppendText(text); CommandInput.CaretIndex = CommandInput.Text.Length; };
            Viewport.ShortcutRequested += async (key, shift) =>
            {
                if (key == 'S') await SaveWithDialogAsync(shift);
                if (key == 'O') await PickDrawingAsync();
                if (key == 'N') NewDrawing();
                if (key == '0') UiActionRequested?.Invoke("clean");
                if (key == '1') ToggleProperties();
                if (key == 0x70) ToggleRibbon();
                if (key == 0x71) ToggleHistory();
                if (key == 0x09) NextDocument(shift);
            };
            _timer.Tick += (sender, args) => RefreshState();
            Loaded += (sender, args) =>
            {
                if (!_initialized)
                {
                    _initialized = true;
                    if (!Client.IsBusy && Client.State.ActiveId == 0) NewDrawing();
                }
                _timer.Start();
                RefreshState();
            };
            Unloaded += (sender, args) => _timer.Stop();
        }

        internal void RefreshState()
        {
            if (_disposed) return;
            if (Client.IsBusy)
            {
                Progress.Value = Client.OpenProgress;
                return;
            }
            try
            {
                var state = Client.State;
                _updating = true;
                var documents = JsonSerializer.Serialize(state.Documents);
                if (documents != _documents)
                {
                    Documents.ItemsSource = state.Documents;
                    Documents.SelectedItem = state.Documents.FirstOrDefault(item => item.Id == state.ActiveId);
                    if (Documents.SelectedItem != null) Documents.ScrollIntoView(Documents.SelectedItem);
                    _documents = documents;
                }
                else Documents.SelectedItem = Documents.Items.Cast<CadDrawing>().FirstOrDefault(item => item.Id == state.ActiveId);
                var layers = JsonSerializer.Serialize(state.Layers);
                if (layers != _layers)
                {
                    var selected = (Layers.SelectedItem as CadLayer)?.Id;
                    Layers.ItemsSource = state.Layers;
                    Layers.SelectedItem = state.Layers.FirstOrDefault(item => item.Id == selected);
                    _currentLayer.ItemsSource = state.Layers;
                    _currentLayer.SelectedItem = state.Layers.FirstOrDefault(item => item.Current);
                    _layers = layers;
                }
                var layouts = JsonSerializer.Serialize(state.Layouts);
                if (layouts != _layouts)
                {
                    Layouts.ItemsSource = state.Layouts;
                    Layouts.SelectedItem = state.Layouts.FirstOrDefault(item => item.Current);
                    _layouts = layouts;
                }
                var properties = JsonSerializer.Serialize(state.Properties);
                if (properties != _properties && !Properties.IsKeyboardFocusWithin)
                {
                    foreach (var property in state.Properties.Where(item => item.Kind != 2 && item.Value != null))
                        if (double.TryParse(property.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) property.Value = value.ToString("0.########", CultureInfo.CurrentCulture);
                    Properties.ItemsSource = state.Properties;
                    _properties = properties;
                }
                foreach (var button in _undoButtons) button.IsEnabled = state.CanUndo;
                foreach (var button in _redoButtons) button.IsEnabled = state.CanRedo;
                CanUndo = state.CanUndo; CanRedo = state.CanRedo;
                var label = state.Documents.FirstOrDefault(item => item.Id == state.ActiveId)?.Label ?? "";
                if (_activeDrawingLabel != label) { _activeDrawingLabel = label; ActiveDrawingChanged?.Invoke(label); }
                SelectionInfo.Text = $"对象 {state.EntityCount} · 已选择 {state.SelectionCount}";
                Coordinates.Text = $"X {state.X:0.##}  Y {state.Y:0.##}";
                Snap.IsChecked = state.Snap; Ortho.IsChecked = state.Ortho; Polar.IsChecked = state.Polar;
                Prompt.Text = string.IsNullOrWhiteSpace(state.Prompt) ? "命令" : state.Prompt;
                var log = string.Join(Environment.NewLine, state.Log);
                if (History.Text != log) { History.Text = log; History.ScrollToEnd(); }
                if (!string.IsNullOrEmpty(state.Error)) Status.Text = state.Error;
                CheckToolDialog();
            }
            catch (Exception error) { Status.Text = error.Message; }
            finally { _updating = false; }
        }

        private bool Run(Action action)
        {
            if (Client.IsBusy) { Status.Text = "请先完成当前文件操作。"; return false; }
            try { action(); RefreshState(); return true; }
            catch (Exception error) { Status.Text = error.Message; return false; }
        }
        internal bool NewDrawing()
        {
            if (!Run(Client.New)) return false;
            Status.Text = "新图纸已创建，可以开始绘图。";
            Viewport.FocusDrawing();
            return true;
        }
        private void New_Click(object sender, RoutedEventArgs e) => NewDrawing();
        internal async Task PickDrawingAsync()
        {
            if (Client.IsBusy) return;
            var dialog = new OpenFileDialog { Title = "打开 CAD 图纸", Filter = "CAD 图纸 (*.dwg;*.dxf)|*.dwg;*.dxf", Multiselect = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                foreach (var path in dialog.FileNames) await OpenDrawingAsync(path);
        }
        internal Task OpenDrawingAsync(string path)
        {
            if (Client.IsBusy) { Status.Text = "正在处理图纸，请等待完成或取消打开。"; return Task.CompletedTask; }
            _operation = OpenCoreAsync(path);
            return _operation;
        }
        private async Task OpenCoreAsync(string path)
        {
            SetBusyUi(true, true);
            Status.Text = "正在打开“" + Path.GetFileName(path) + "”…";
            try
            {
                await Client.OpenAsync(path);
                DrawingOpened?.Invoke(path);
                Status.Text = "已打开“" + Path.GetFileName(path) + "”。";
                Viewport.FocusDrawing();
            }
            catch (Exception error) { Status.Text = error.Message; }
            finally { SetBusyUi(false, false); RefreshState(); }
        }
        private void SetBusyUi(bool busy, bool opening)
        {
            FileTools.IsEnabled = EditTools.IsEnabled = Documents.IsEnabled = !busy;
            Properties.IsEnabled = Layers.IsEnabled = Layouts.IsEnabled = CommandInput.IsEnabled = !busy;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Progress.IsIndeterminate = busy && !opening;
            CancelOpen.Visibility = busy && opening ? Visibility.Visible : Visibility.Collapsed;
        }
        private void CancelOpen_Click(object sender, RoutedEventArgs e) { Client.CancelOpen(); Status.Text = "正在取消打开图纸…"; }
        internal async Task<bool> SaveWithDialogAsync(bool saveAs)
        {
            if (Client.IsBusy) return false;
            var state = Client.State;
            var doc = state.Documents.FirstOrDefault(item => item.Id == state.ActiveId);
            if (doc == null) { Status.Text = "请先新建或打开图纸。"; return false; }
            var path = doc.Path;
            if (!saveAs && !string.IsNullOrEmpty(state.SaveNotice) && string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase))
            {
                var choice = MessageBox.Show(Window.GetWindow(this), state.SaveNotice + "\n\n是 — 另存一份后核对特殊对象\n否 — 保存至当前文件\n取消 — 返回编辑", "保存格式变化", MessageBoxButton.YesNoCancel, MessageBoxImage.Information, MessageBoxResult.Yes);
                if (choice == MessageBoxResult.Cancel) return false;
                saveAs = choice == MessageBoxResult.Yes;
            }
            if (saveAs || string.IsNullOrWhiteSpace(path))
            {
                var dialog = new SaveFileDialog { Title = "保存 CAD 图纸", Filter = "AutoCAD DWG (*.dwg)|*.dwg|DXF (*.dxf)|*.dxf", DefaultExt = ".dwg", FileName = string.IsNullOrWhiteSpace(path) ? "未命名.dwg" : Path.GetFileName(path) };
                if (!string.IsNullOrEmpty(state.SaveNotice)) dialog.Title += " · " + state.SaveNotice;
                if (string.Equals(Path.GetExtension(path), ".dxf", StringComparison.OrdinalIgnoreCase)) dialog.FilterIndex = 2;
                if (!string.IsNullOrWhiteSpace(path)) dialog.InitialDirectory = Path.GetDirectoryName(path);
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
                path = dialog.FileName;
            }
            var task = SaveCoreAsync(path);
            _operation = task;
            return await task;
        }
        private async Task<bool> SaveCoreAsync(string path)
        {
            SetBusyUi(true, false);
            Status.Text = "正在保存图纸…";
            try
            {
                await Client.SaveAsync(path);
                DrawingOpened?.Invoke(path);
                Status.Text = "图纸已保存“" + Path.GetFileName(path) + "”。";
                return true;
            }
            catch (Exception error) { Status.Text = error.Message; return false; }
            finally { SetBusyUi(false, false); RefreshState(); }
        }
        private async Task<bool> ConfirmDrawingAsync(CadDrawing doc)
        {
            if (!doc.Dirty) return true;
            Client.Activate(doc.Id); RefreshState();
            var answer = MessageBox.Show(Window.GetWindow(this), "保存图纸“" + doc.Name + "”的修改？", "图纸尚未保存", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            return answer == MessageBoxResult.No || await SaveWithDialogAsync(false);
        }
        internal async Task<bool> ConfirmCloseAllAsync(bool close = false)
        {
            Client.CancelOpen();
            await _operation;
            var docs = Client.State.Documents;
            foreach (var doc in docs) if (!await ConfirmDrawingAsync(doc)) return false;
            if (close) foreach (var doc in docs) Client.Close(doc.Id);
            return true;
        }
        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCommand((string)((Button)sender).Tag, true);
        }
        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_updating) Run(() => Client.Command((string)((CheckBox)sender).Tag));
        }
        private void Documents_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_updating && Documents.SelectedItem is CadDrawing doc) Run(() => Client.Activate(doc.Id));
        }
        private void Layouts_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_updating && Layouts.SelectedItem is CadLayout layout) Run(() => CadNative.Check(CadNative.wl_layout(Client.Handle, layout.Id)));
        }
        private void Layer_Click(object sender, RoutedEventArgs e)
        {
            if (!_updating && ((FrameworkElement)sender).DataContext is CadLayer layer) ApplyLayer(layer, false);
        }
        private void ApplyLayer(CadLayer layer, bool current) => Run(() => CadNative.Check(CadNative.wl_layer(Client.Handle, layer.Id, layer.Visible ? 1 : 0, layer.Locked ? 1 : 0, current ? 1 : 0)));
        private void CurrentLayer_Click(object sender, RoutedEventArgs e) { if (Layers.SelectedItem is CadLayer layer) ApplyLayer(layer, true); }
        private void ApplyProperty_Click(object sender, RoutedEventArgs e)
        {
            Properties.CommitEdit();
            if (!(Properties.SelectedItem is CadProperty property)) { Status.Text = "请选择需要修改的参数行。"; return; }
            var value = property.Value;
            if (property.Kind != 2)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)) { Status.Text = "请输入有效数值。"; return; }
                value = number.ToString("R", CultureInfo.InvariantCulture);
            }
            if (Run(() => CadNative.Check(CadNative.wl_property(Client.Handle, property.Name, value, property.Kind))))
            { _properties = ""; Viewport.FocusDrawing(); RefreshState(); }
        }
        private void CommandInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (ExecuteCommand(CommandInput.Text.Trim())) CommandInput.Clear();
                e.Handled = true;
            }
            if (e.Key == Key.Escape) { Run(() => Client.Command("@cancel")); CommandInput.Clear(); Viewport.FocusDrawing(); e.Handled = true; }
        }
        private async void Workspace_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (e.Key == Key.F1) { e.Handled = true; ToggleRibbon(); return; }
                if (e.Key == Key.D1) { e.Handled = true; ToggleProperties(); return; }
                if (e.Key == Key.Tab) { e.Handled = true; NextDocument((Keyboard.Modifiers & ModifierKeys.Shift) != 0); return; }
                if ((e.Key == Key.Z || e.Key == Key.Y) && !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase))
                {
                    e.Handled = true;
                    Run(() => Client.Command(e.Key == Key.Z ? "@undo" : "@redo"));
                }
                if (e.Key == Key.S) { e.Handled = true; await SaveWithDialogAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0); }
                if (e.Key == Key.O) { e.Handled = true; await PickDrawingAsync(); }
                if (e.Key == Key.N) { e.Handled = true; NewDrawing(); }
                return;
            }
            if (e.Key == Key.F2) { e.Handled = true; ToggleHistory(); return; }
            var command = e.Key == Key.F3 ? "@snap" : e.Key == Key.F8 ? "@ortho" : e.Key == Key.F10 ? "@polar" : null;
            if (command != null) { e.Handled = true; Run(() => Client.Command(command)); }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _timer.Stop();
            Viewport.Dispose();
            Viewport.Session = IntPtr.Zero;
            Client.Dispose();
            _disposed = true;
        }
    }
}
