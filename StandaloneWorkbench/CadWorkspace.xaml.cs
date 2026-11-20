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
        private (ulong Document, int Layout, ulong Viewport, ulong Selection, ulong Object) _propertyTarget;
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
                RefreshProperties(state);
                foreach (var button in _undoButtons) button.IsEnabled = state.CanUndo;
                foreach (var button in _redoButtons) button.IsEnabled = state.CanRedo;
                CanUndo = state.CanUndo; CanRedo = state.CanRedo;
                var label = state.Documents.FirstOrDefault(item => item.Id == state.ActiveId)?.Label ?? "";
                if (_activeDrawingLabel != label) { _activeDrawingLabel = label; ActiveDrawingChanged?.Invoke(label); }
                SelectionInfo.Text = (state.InlineViewport ? "原位编辑 · " : "") + $"对象 {state.EntityCount} · 已选择 {state.SelectionCount}";
                ViewportEditingButton.Content = state.EditingViewport != 0 ? "返回布局" : "视口编辑";
                ViewportEditingButton.ToolTip = state.InlineViewport ? "双击视口外或输入PSPACE返回纸空间" : "原位编辑布局视口中的模型，MSPACE／PSPACE";
                ViewportEditingButton.IsEnabled = !Client.IsBusy && state.ActiveId != 0 && (state.EditingViewport != 0 || state.Layouts.Any(item => item.Current && !item.Model));
                Coordinates.Text = $"X {state.LocalX:0.##}  Y {state.LocalY:0.##}  Z {state.LocalZ:0.##}";
                Coordinates.ToolTip = $"世界坐标 X {state.X:0.######}  Y {state.Y:0.######}  Z {state.Z:0.######}";
                WorkingPlaneButton.Content = state.WorkPlane.World ? "UCS 世界" : "UCS 自定义";
                WorkingPlaneButton.ToolTip = $"工作平面原点（世界坐标）X {state.WorkPlane.OriginX:0.##}  Y {state.WorkPlane.OriginY:0.##}  Z {state.WorkPlane.OriginZ:0.##}";
                Snap.IsChecked = state.Snap; Ortho.IsChecked = state.Ortho; Polar.IsChecked = state.Polar;
                _standardViews.SelectedIndex = state.StandardView;
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
            if (!CommitFocusedProperty() || !Run(Client.New)) return false;
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
            if (Client.IsBusy || !CommitFocusedProperty()) return false;
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
            if (!CommitFocusedProperty()) return false;
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
            if (!_updating && Layouts.SelectedItem is CadLayout layout && Run(() => CadNative.Check(CadNative.wl_layout(Client.Handle, layout.Id))))
                Status.Text = layout.Model ? "已切换到模型空间。" : "已切换到布局“" + layout.Name + "”。";
        }
        private void Layer_Click(object sender, RoutedEventArgs e)
        {
            if (!_updating && ((FrameworkElement)sender).DataContext is CadLayer layer) ApplyLayer(layer, false);
        }
        private void ApplyLayer(CadLayer layer, bool current) => Run(() => CadNative.Check(CadNative.wl_layer(Client.Handle, layer.Id, layer.Visible ? 1 : 0, layer.Locked ? 1 : 0, current ? 1 : 0)));
        private void CurrentLayer_Click(object sender, RoutedEventArgs e) { if (Layers.SelectedItem is CadLayer layer) ApplyLayer(layer, true); }
        private static (ulong Document, int Layout, ulong Viewport, ulong Selection, ulong Object) PropertyTarget(CadState state) =>
            (state.ActiveId, state.Layouts.FindIndex(layout => layout.Current), state.EditingViewport, state.SelectionVersion, state.PropertySelectionId);

        private static string DisplayPropertyValue(CadProperty property) =>
            property.Kind < 2 && double.TryParse(property.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value.ToString("G", CultureInfo.CurrentCulture) : property.Value;

        private TextBox FocusedPropertyText() =>
            Properties.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox input && input.DataContext is CadProperty
                ? input : null;

        private void RefreshProperties(CadState state)
        {
            var target = PropertyTarget(state);
            var snapshot = JsonSerializer.Serialize(state.Properties);
            if (target == _propertyTarget && snapshot == _properties) return;
            var input = FocusedPropertyText();
            if (target == _propertyTarget && input != null && input.Text != ((CadProperty)input.DataContext).Value) return;
            if (target == _propertyTarget && Properties.ItemsSource is System.Collections.Generic.List<CadProperty> current &&
                current.Count == state.Properties.Count &&
                current.Zip(state.Properties).All(pair => pair.First.Name == pair.Second.Name && pair.First.Kind == pair.Second.Kind))
            {
                foreach (var (oldProperty, property) in current.Zip(state.Properties))
                {
                    oldProperty.Editable = property.Editable;
                    oldProperty.Value = DisplayPropertyValue(property);
                }
            }
            else
            {
                foreach (var property in state.Properties) property.Value = DisplayPropertyValue(property);
                Properties.ItemsSource = state.Properties;
            }
            _propertyTarget = target;
            _properties = snapshot;
        }

        private bool CommitProperty(CadProperty property, string value)
        {
            if (value == (property.Value ?? "")) return true;
            if (_updating || _disposed || !property.Editable || !Properties.Items.Contains(property)) return false;
            if (property.Kind != 2)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) || !double.IsFinite(number))
                { Status.Text = "请输入有效数值。"; return false; }
                if (double.TryParse(property.Value, NumberStyles.Float, CultureInfo.CurrentCulture, out var original) && number == original)
                    return true;
                value = number.ToString("R", CultureInfo.InvariantCulture);
            }
            return Run(() =>
            {
                if (PropertyTarget(Client.State) != _propertyTarget)
                    throw new InvalidOperationException("选择已改变，请重新选中对象后修改属性。");
                CadNative.Check(CadNative.wl_property(Client.Handle, property.Name, value, property.Kind));
                var updated = Client.State.Properties.FirstOrDefault(item => item.Name == property.Name && item.Kind == property.Kind);
                if (updated != null) property.Value = DisplayPropertyValue(updated);
            });
        }

        private bool CommitPropertyText(TextBox input)
        {
            var applied = input.DataContext is not CadProperty property || CommitProperty(property, input.Text);
            input.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            return applied;
        }

        private bool CommitFocusedProperty() => FocusedPropertyText() is not TextBox input || CommitPropertyText(input);

        private void PropertyText_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_updating) CommitPropertyText((TextBox)sender);
        }

        private void PropertyText_KeyDown(object sender, KeyEventArgs e)
        {
            var input = (TextBox)sender;
            if (e.Key == Key.Enter) CommitPropertyText(input);
            else if (e.Key == Key.Escape) input.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            else return;
            e.Handled = true;
        }

        private void PropertyChoice_Changed(object sender, SelectionChangedEventArgs e)
        {
            var combo = (ComboBox)sender;
            if (_updating || combo.DataContext is not CadProperty property || e.AddedItems.Count != 1 ||
                e.AddedItems[0] is not ComboBoxItem { Tag: string value }) return;
            if (!CommitProperty(property, value)) combo.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateTarget();
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
                if ((e.Key == Key.C || e.Key == Key.V || e.Key == Key.X) && !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase))
                {
                    e.Handled = true;
                    var clipboardCommand = e.Key == Key.V ? "@paste" : e.Key == Key.X ? "@cut" : (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? "@copybase" : "@copyclip";
                    Run(() => Client.Command(clipboardCommand)); Viewport.FocusDrawing(); return;
                }
                if (e.Key == Key.F1) { e.Handled = true; ToggleRibbon(); return; }
                if (e.Key == Key.D1) { e.Handled = true; ToggleProperties(); return; }
                if (e.Key == Key.Tab) { e.Handled = true; NextDocument((Keyboard.Modifiers & ModifierKeys.Shift) != 0); return; }
                var propertyInput = FocusedPropertyText();
                var committedProperty = propertyInput != null && propertyInput.Text == (((CadProperty)propertyInput.DataContext).Value ?? "");
                if ((e.Key == Key.Z || e.Key == Key.Y) &&
                    (committedProperty || Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase))
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
            if (e.Key == Key.F7) { e.Handled = true; Run(() => Client.Command("@grid")); return; }
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
