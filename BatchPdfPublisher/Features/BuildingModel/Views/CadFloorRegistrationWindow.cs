using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadFloorRegistrationRow : INotifyPropertyChanged
    {
        public StoreyModel Storey { get; set; }
        public string Name => Storey.Name;
        public double Elevation => Storey.Elevation;
        public double Height => Storey.Height;
        public bool CanRegister => string.IsNullOrWhiteSpace(Storey.TemplateStoreyId);
        public string SourceName { get; set; }
        private string _status;
        public string Status { get { return _status; } set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status")); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class CadFloorRegistrationWindow : Window
    {
        private BuildingModelDocument _model;
        private string _path;
        private CadFloorPlanRegistry _registry;
        private readonly Dictionary<string, CadFloorRegistrationContext> _contexts = new Dictionary<string, CadFloorRegistrationContext>(StringComparer.OrdinalIgnoreCase);
        private readonly DataGrid _floors = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, MinHeight = 130 };
        private readonly TextBlock _modelLabel = new TextBlock { Text = "请先打开建筑模型并保存楼层。", TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _x = new TextBox { Text = "0" }, _y = new TextBox { Text = "0" }, _unitScale = new TextBox { Text = "1" };
        private readonly Func<Window, CadFloorRegistrationContext, bool> _pickDatum;
        private readonly Func<Window, CadFloorRegistrationContext, CadFloorPlanCapture> _capture;
        private readonly Func<Window,CadFloorPlanRegistry,BuildingModelDocument,bool,bool> _editOpenings;
        private readonly Action<CadFloorPlanRegistry> _readPlan;

        public CadFloorRegistrationWindow(string modelPath = null,
            Func<Window, CadFloorRegistrationContext, bool> pickDatum = null,
            Func<Window, CadFloorRegistrationContext, CadFloorPlanCapture> capture = null, Action<CadFloorPlanRegistry> generateModel = null,
            Func<Window,CadFloorPlanRegistry,BuildingModelDocument,bool,bool> editOpenings=null,Action<CadFloorPlanRegistry> readPlan=null)
        {
            _pickDatum = pickDatum; _capture = capture;_editOpenings=editOpenings;_readPlan=readPlan;
            Title = "CAD 楼层登记核对"; Width = 1100; Height = 610; MinWidth = 820; MinHeight = 500;
            UseLayoutRounding = true; FontSize = 14; FontFamily = new FontFamily("Microsoft YaHei UI");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 251));
            var root = new Grid { Margin = new Thickness(20) };
            foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                root.RowDefinitions.Add(new RowDefinition { Height = h });
            root.Children.Add(new TextBlock { Text = "逐层登记建筑平面", FontSize = 24, FontWeight = FontWeights.SemiBold });
            _modelLabel.Margin = new Thickness(0, 12, 0, 14); Grid.SetRow(_modelLabel, 1); root.Children.Add(_modelLabel);
            foreach (var c in new[] { new[] { "楼层", "Name" }, new[] { "标高 mm", "Elevation" }, new[] { "层高 mm", "Height" }, new[] { "平面来源", "SourceName" }, new[] { "登记状态", "Status" } })
                _floors.Columns.Add(new DataGridTextColumn { Header = c[0], Binding = new Binding(c[1]), MinWidth = c[1] == "Status" ? 120 : 90,
                    Width = new DataGridLength(c[1] == "Status" ? 2 : 1, DataGridLengthUnitType.Star) });
            AddAction("拾取基点", PickDatum); AddAction("框选登记平面", Capture);
            Grid.SetRow(_floors, 2); root.Children.Add(_floors);
            var settings = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
            settings.Children.Add(new TextBlock { Text = "每层拾取同一个建筑位置（如同一轴线交点），再框选该层平面。拾取后自动返回本窗口，可继续登记下一层。\n方向沿当前 CAD 坐标系 X 轴，无需再拾取方向点。标准层来源登记一次，引用层自动复用并分别统计。",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
            var advanced = new Expander { Header = "坐标与单位设置", Margin = new Thickness(0, 10, 0, 0) };
            var coords = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            AddInput(coords, "模型基点 X", _x); AddInput(coords, "Y", _y); AddInput(coords, "1 CAD 单位 = mm", _unitScale); advanced.Content = coords; settings.Children.Add(advanced);
            Grid.SetRow(settings, 3); root.Children.Add(settings); Grid.SetRow(_error, 4); root.Children.Add(_error);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            foreach(var label in new[] { "门窗表（编号与尺寸）","补充门窗位置" }) {
                var open=new Button { Content=label,Padding=new Thickness(10,8,10,8),Margin=new Thickness(0,0,8,0) };
                open.Click+=(s,e)=> { try { EditOpeningTable(label=="补充门窗位置"); } catch(Exception ex) { _error.Text=ex.Message; } };actions.Children.Add(open);
            }
            var generate = new Button { Content = "生成建筑模型", Padding = new Thickness(14, 8, 14, 8), IsEnabled = generateModel != null };
            generate.Click += (s,e) => { try { _readPlan?.Invoke(_registry);generateModel?.Invoke(_registry); Close(); } catch (Exception ex) { _error.Text = ex.Message; } }; actions.Children.Add(generate);
            var close = new Button { Content = "关闭", IsCancel = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(8, 0, 0, 0) };
            close.Click += (s, e) => Close(); actions.Children.Add(close); Grid.SetRow(actions, 5); root.Children.Add(actions);
            Content = root;
            if (modelPath != null) LoadModel(modelPath);
            Loaded += (s, e) => { Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32); Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32); };
        }
        private void AddAction(string label, Action<CadFloorRegistrationRow> action)
        {
            var button = new FrameworkElementFactory(typeof(Button)); button.SetValue(Button.ContentProperty, label);
            button.SetBinding(Button.IsEnabledProperty, new Binding("CanRegister"));
            button.SetValue(Button.PaddingProperty, new Thickness(8, 5, 8, 5)); button.SetValue(Button.MarginProperty, new Thickness(3));
            button.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                try { _error.Text = ""; action((CadFloorRegistrationRow)((Button)s).DataContext); }
                catch (Exception ex) { _error.Text = ex.Message; }
            }));
            _floors.Columns.Add(new DataGridTemplateColumn { Header = label, CellTemplate = new DataTemplate { VisualTree = button }, Width = new DataGridLength(label == "拾取基点" ? 110 : 150) });
        }
        private static void AddInput(Panel panel, string label, TextBox input)
        {
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            input.Width = 90; input.Padding = new Thickness(5); input.Margin = new Thickness(0, 0, 12, 0); panel.Children.Add(input);
        }
        public void LoadModel(string path)
        {
            var model = BuildingModelJson.LoadModel(path);
            if (model.Storeys == null || model.Storeys.Count == 0) throw new InvalidDataException("模型没有楼层，请先在建筑模型中设置并保存。");
            _path = Path.GetFullPath(path); _model = model; _registry = CadFloorPlanRegistry.Load(_path);
            _contexts.Clear();
            foreach (var floor in model.Storeys.Where(f => f != null && string.IsNullOrWhiteSpace(f.TemplateStoreyId)))
            {
                var context = CadFloorRegistrationContext.Create(_path, model, floor.Id, new PointModel(0, 0), 1);
                var previous = _registry.FindDatum(floor.Id);
                if (previous != null) { context.Alignment = previous.Alignment; context.DirectionPoint = previous.DirectionPoint; }
                _contexts.Add(floor.Id, context);
            }
            if (model.Storeys.Any(f => f == null || (!string.IsNullOrWhiteSpace(f.TemplateStoreyId) && !_contexts.ContainsKey(f.TemplateStoreyId))))
                throw new InvalidDataException("标准层来源无效，请在建筑模型中修正并保存。");
            _floors.ItemsSource = model.Storeys.Select(f => new CadFloorRegistrationRow { Storey = f,
                SourceName = string.IsNullOrWhiteSpace(f.TemplateStoreyId) ? "本层" : model.Storeys.First(s => string.Equals(s.Id, f.TemplateStoreyId, StringComparison.OrdinalIgnoreCase)).Name }).ToList();
            _modelLabel.Text = "当前建筑模型：" + model.Name;
            var saved = _contexts.Values.FirstOrDefault(c => c.DirectionPoint != null);
            if (saved != null) { _x.Text = saved.Alignment.ModelBase.X.ToString(CultureInfo.InvariantCulture); _y.Text = saved.Alignment.ModelBase.Y.ToString(CultureInfo.InvariantCulture); _unitScale.Text = saved.Alignment.MillimetresPerCadUnit.ToString(CultureInfo.InvariantCulture); }
            RefreshStatus();
        }
        private CadFloorRegistrationContext Context(CadFloorRegistrationRow row) =>
            _contexts[string.IsNullOrWhiteSpace(row.Storey.TemplateStoreyId) ? row.Storey.Id : row.Storey.TemplateStoreyId];
        private void EditOpeningTable(bool locations)
        {
            if(_registry==null || _registry.Floors.Count==0)throw new InvalidDataException("请先框选登记楼层平面。");
            var saved=_editOpenings!=null ? _editOpenings(this,_registry,_model,locations)
                : new CadRegisteredOpeningTableWindow(_registry,_model,locations:locations) { Owner=this }.ShowDialog()==true;
            if(saved) { _registry=CadFloorPlanRegistry.Load(_path);RefreshStatus();_error.Text="门窗表已保存，可以继续生成建筑模型。"; }
        }
        private void PickDatum(CadFloorRegistrationRow row)
        {
            var context = Context(row); double x, y, scale;
            if (!double.TryParse(_x.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !double.TryParse(_y.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !double.TryParse(_unitScale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
                throw new InvalidDataException("请输入有效基点坐标和单位倍率。");
            var next = CadFloorRegistrationContext.Create(_path, _model, context.Storey.Id, new PointModel(x, y), scale);
            if (_pickDatum == null || !_pickDatum(this, next)) return;
            _registry.SaveDatum(next); _contexts[context.Storey.Id] = next; RefreshStatus();
        }
        private void Capture(CadFloorRegistrationRow row)
        {
            var context = Context(row);
            if (context.DirectionPoint == null) throw new InvalidDataException("请先点击本层的“拾取基点”。");
            if (_capture == null) return;
            var draft = CadFloorRegistrationContext.Create(_path, _model, context.Storey.Id, context.Alignment.ModelBase, context.Alignment.MillimetresPerCadUnit);
            draft.SetSourceDatum(context.Alignment.CadBase, context.DirectionPoint);
            var captured = _capture(this, draft);
            if (captured == null) return;
            _registry.SaveFloor(captured); _contexts[context.Storey.Id] = captured.Floor; RefreshStatus();
        }
        private void RefreshStatus()
        {
            foreach (CadFloorRegistrationRow row in _floors.Items)
            {
                var context = Context(row); var capture = _registry.Find(context.Storey.Id);
                row.Status = capture != null && SameDatum(context.Alignment, capture.Floor.Alignment)
                    ? "已登记 · 门窗 " + capture.Openings.Count(x => x.Include)
                    : context.DirectionPoint == null ? "未拾取基点" : "基点已拾取 · 待框选";
                if (!string.IsNullOrWhiteSpace(row.Storey.TemplateStoreyId)) row.Status = "复用 " + row.SourceName + " · " + row.Status;
            }
        }
        private static bool SameDatum(CadFloorRegistrationAlignment a, CadFloorRegistrationAlignment b) =>
            a.CadBase.X == b.CadBase.X && a.CadBase.Y == b.CadBase.Y && a.ModelBase.X == b.ModelBase.X && a.ModelBase.Y == b.ModelBase.Y
            && a.RotationRadians == b.RotationRadians && a.MillimetresPerCadUnit == b.MillimetresPerCadUnit;
    }
}
