using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        public string Name => Storey.StandardFloorRange ?? Storey.Name;
        public string ElevationDisplay => StandardStoreyLayout.TryParseRange(Storey.StandardFloorRange,out var first,out var last)
            ? Storey.Elevation.ToString("0.##") + "～" + (Storey.Elevation+(last-first)*Storey.Height).ToString("0.##")
            : Storey.Elevation.ToString("0.##");
        public double Elevation => Storey.Elevation;
        public double Height => Storey.Height;
        public bool CanRegister => string.IsNullOrWhiteSpace(Storey.TemplateStoreyId);
        public string SourceName { get; set; }
        private string _status;
        public string Status { get { return _status; } set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status")); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed partial class CadFloorRegistrationWindow : Window
    {
        private BuildingModelDocument _model;
        private string _path;
        private CadFloorPlanRegistry _registry;
        private readonly Dictionary<string, CadFloorRegistrationContext> _contexts = new Dictionary<string, CadFloorRegistrationContext>(StringComparer.OrdinalIgnoreCase);
        private readonly DataGrid _floors = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, MinHeight = 130 };
        private readonly TextBlock _modelLabel = new TextBlock { Text = "尚无模型，请新建或打开模型。", TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _x = new TextBox { Text = "0" }, _y = new TextBox { Text = "0" }, _unitScale = new TextBox { Text = "1" };
        private readonly TextBox _slabThickness = new TextBox { Name="CadSlabThickness",Text = "100" };
        private readonly ComboBox _generationMode=new ComboBox {Name="CadGenerationMode",MinWidth=230,SelectedIndex=0,
            ItemsSource=new[] {"按 CAD 替换登记楼层","保留模型编辑，安全更新"}};
        private readonly Func<Window, CadFloorRegistrationContext, bool> _pickDatum;
        private readonly Func<Window, CadFloorRegistrationContext, CadFloorPlanCapture> _capture;
        private readonly Func<Window,CadFloorPlanRegistry,BuildingModelDocument,bool,bool> _editOpenings;
        private readonly Action<CadFloorPlanRegistry> _readPlan;
        private readonly Action<CadFloorPlanRegistry> _updateUnderlay;
        private bool _editingProject;
        private long _modelStamp, _registryStamp;

        public CadFloorRegistrationWindow(string modelPath = null,
            Func<Window, CadFloorRegistrationContext, bool> pickDatum = null,
            Func<Window, CadFloorRegistrationContext, CadFloorPlanCapture> capture = null, Action<CadFloorPlanRegistry> generateModel = null,
            Func<Window,CadFloorPlanRegistry,BuildingModelDocument,bool,bool> editOpenings=null,Action<CadFloorPlanRegistry> readPlan=null,
            Action<CadFloorPlanRegistry> updateUnderlay=null, Func<Window,string> createModel=null,
            Func<Window,string> openModel=null, Func<Window,string,Task> editStoreys=null,Func<Window,string> manageModels=null,
            CadModelWorkspaceHost workspace=null)
        {
            _pickDatum = pickDatum; _capture = capture;_editOpenings=editOpenings;_readPlan=readPlan;
            _updateUnderlay=updateUnderlay;
            if(workspace!=null)
            {
                _workspace=workspace;_workspaceGenerate=generateModel;_workspaceRead=readPlan;
                BuildWorkspace();
                if(modelPath!=null)
                {
                    try{LoadModel(modelPath);}
                    catch(Exception ex) when(ex is IOException||ex is ArgumentException||ex is InvalidOperationException||ex is System.Runtime.Serialization.SerializationException)
                    {_workspaceStatus.Text="上次模型无法读取，请新建或打开模型："+ex.Message;}
                }
                RefreshModels();
                return;
            }
            Title = "CAD 楼层登记核对"; Width = 1100; Height = 610; MinWidth = 820; MinHeight = 500;
            UseLayoutRounding = true; FontSize = 14; FontFamily = new FontFamily("Microsoft YaHei UI");
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 251));
            var root = new Grid { Margin = new Thickness(20) };
            foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                root.RowDefinitions.Add(new RowDefinition { Height = h });
            root.Children.Add(new TextBlock { Text = "逐层登记建筑平面", FontSize = 24, FontWeight = FontWeights.SemiBold });
            var projectBar = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            projectBar.Children.Add(_modelLabel);
            var projectActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            void ProjectAction(string label, Func<Task> action, bool enabled)
            {
                var button = new Button { Content=label,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,8,0),IsEnabled=enabled };
                button.Click += async (s,e) => {
                    _editingProject=true;root.IsEnabled=false;
                    try { _error.Text="";await action(); }
                    catch(Exception ex) { _error.Text=ex.Message; }
                    finally { root.IsEnabled=true;_editingProject=false; }
                };projectActions.Children.Add(button);
            }
            ProjectAction("新建模型",()=> { var next=createModel?.Invoke(this);if(next!=null)LoadModel(next);return Task.CompletedTask; },createModel!=null);
            ProjectAction("模型管理",()=> { var next=manageModels?.Invoke(this);if(next!=null)LoadModel(next);return Task.CompletedTask; },manageModels!=null);
            ProjectAction("打开模型",()=> { var next=openModel?.Invoke(this);if(next!=null)LoadModel(next);return Task.CompletedTask; },openModel!=null);
            ProjectAction("楼层设置",async()=> { RequireModel();await editStoreys(this,_path);LoadModel(_path); },editStoreys!=null);
            ProjectAction("刷新楼层",()=> { RequireModel();LoadModel(_path);return Task.CompletedTask; },true);
            projectBar.Children.Add(projectActions);Grid.SetRow(projectBar,1);root.Children.Add(projectBar);
            foreach (var c in new[] { new[] { "楼层 / 范围", "Name" }, new[] { "标高 mm", "ElevationDisplay" }, new[] { "每层层高 mm", "Height" }, new[] { "平面", "SourceName" }, new[] { "登记状态", "Status" } })
                _floors.Columns.Add(new DataGridTextColumn { Header = c[0], Binding = new Binding(c[1]), MinWidth = c[1] == "Status" ? 120 : 90,
                    Width = new DataGridLength(c[1] == "Status" ? 2 : 1, DataGridLengthUnitType.Star) });
            AddAction("拾取基点", PickDatum); AddAction("框选登记平面", Capture);
            Grid.SetRow(_floors, 2); root.Children.Add(_floors);
            var settings = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
            settings.Children.Add(new TextBlock { Text = "每层拾取同一个建筑位置（如同一轴线交点），再框选该层平面。拾取后自动返回本窗口，可继续登记下一层。\n方向沿当前 CAD 坐标系 X 轴，无需再拾取方向点。标准层来源登记一次，引用层自动复用并分别统计。",
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
            var advanced = new Expander { Header = "坐标与单位设置", Margin = new Thickness(0, 10, 0, 0) };
            var slabSettings=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0) };
            AddInput(slabSettings,"楼板厚度 mm",_slabThickness);
            slabSettings.Children.Add(new TextBlock { Text="外墙围合自动生成楼板；楼梯、电梯、井按房间边界开洞。",VerticalAlignment=VerticalAlignment.Center,Foreground=Brushes.DimGray });
            settings.Children.Add(slabSettings);
            var generationSettings=new WrapPanel {Margin=new Thickness(0,8,0,0)};
            generationSettings.Children.Add(new TextBlock {Text="重新生成",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});
            generationSettings.Children.Add(_generationMode);
            generationSettings.Children.Add(new TextBlock {Text="同编号、同洞口尺寸保留门窗做法；位置按 CAD。",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0),Foreground=Brushes.DimGray});
            settings.Children.Add(generationSettings);
            var coords = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            AddInput(coords, "模型基点 X", _x); AddInput(coords, "Y", _y); AddInput(coords, "1 CAD 单位 = mm", _unitScale); advanced.Content = coords; settings.Children.Add(advanced);
            Grid.SetRow(settings, 3); root.Children.Add(settings); Grid.SetRow(_error, 4); root.Children.Add(_error);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var updateReference=new Button {Content="更新参照底图",Padding=new Thickness(10,8,10,8),Margin=new Thickness(0,0,8,0),IsEnabled=updateUnderlay!=null};
            updateReference.Click+=(s,e)=> {try {RequireModel();if(_registry.Floors.Count==0)throw new InvalidDataException("请先登记楼层平面。");_updateUnderlay(_registry);RefreshStatus();_error.Text="参照底图已更新，模型构件未改变。";}catch(Exception ex){_error.Text=ex.Message;}};
            actions.Children.Add(updateReference);
            foreach(var label in new[] { "门窗表（编号与尺寸）","补充门窗位置" }) {
                var open=new Button { Content=label,Padding=new Thickness(10,8,10,8),Margin=new Thickness(0,0,8,0) };
                open.Click+=(s,e)=> { try { EditOpeningTable(label=="补充门窗位置"); } catch(Exception ex) { _error.Text=ex.Message; } };actions.Children.Add(open);
            }
            var generate = new Button { Content = "生成建筑模型", Padding = new Thickness(14, 8, 14, 8), IsEnabled = generateModel != null };
            generate.Click += (s,e) => { try {
                RequireModel();
                double thickness;
                string warning=null;
                if(!double.TryParse(_slabThickness.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out thickness)
                    || double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness<=0.5)
                    warning="楼板厚度无效，本次保留已有模型，可进入模型后调整。";
                else _registry.SlabThickness=thickness;
                _registry.GenerationWarning=null;
                if(_readPlan!=null)
                {
                    try { var draft=_registry.Clone();_readPlan(draft);_registry=draft; }
                    catch(Exception ex) { warning=(warning==null?"":warning+"；")+"CAD 平面重新读取未完成："+ex.Message; }
                }
                _registry.GenerationWarning=warning;
                _registry.ReplaceRegisteredConstruction=_generationMode.SelectedIndex==0;
                generateModel?.Invoke(_registry); Close();
            } catch (Exception ex) { _error.Text = ex.Message; } }; actions.Children.Add(generate);
            var close = new Button { Content = "关闭", IsCancel = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(8, 0, 0, 0) };
            close.Click += (s, e) => Close(); actions.Children.Add(close); Grid.SetRow(actions, 5); root.Children.Add(actions);
            Content = root;
            if (modelPath != null) LoadModel(modelPath);
            Activated += (s,e) => {
                if(_editingProject || _path==null)return;
                try { var file=CadFloorPlanRegistry.FilePath(_path);
                    if(File.GetLastWriteTimeUtc(_path).Ticks!=_modelStamp || (File.Exists(file)?File.GetLastWriteTimeUtc(file).Ticks:0)!=_registryStamp)LoadModel(_path);
                } catch(Exception ex) { _error.Text="楼层刷新失败："+ex.Message; }
            };
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
            var nextPath=Path.GetFullPath(path);CadFloorPlanRegistry registry;CadRegistryRecovery recovery=null;
            try {registry=CadFloorPlanRegistry.Load(nextPath);}
            catch(CadRegistryModelMismatchException) when(_workspace!=null)
            {
                recovery=CadRegistryRecovery.Read(nextPath);
                registry=new CadFloorPlanRegistry {ModelPath=nextPath};
            }
            registry.ReconcileStoreys(model);
            var contexts=new Dictionary<string,CadFloorRegistrationContext>(StringComparer.OrdinalIgnoreCase);
            foreach (var floor in model.Storeys.Where(f => f != null && string.IsNullOrWhiteSpace(f.TemplateStoreyId)))
            {
                var context = CadFloorRegistrationContext.Create(nextPath, model, floor.Id, new PointModel(0, 0), 1);
                var previous = registry.FindDatum(floor.Id);
                if (previous != null) { context.Alignment = previous.Alignment; context.DirectionPoint = previous.DirectionPoint; }
                contexts.Add(floor.Id, context);
            }
            if (model.Storeys.Any(f => f == null || (!string.IsNullOrWhiteSpace(f.TemplateStoreyId) && !contexts.ContainsKey(f.TemplateStoreyId))))
                throw new InvalidDataException("标准层来源无效，请在建筑模型中修正并保存。");
            if(_workspace!=null || _model==null || !string.Equals(_path,nextPath,StringComparison.OrdinalIgnoreCase))_slabThickness.Text=registry.ResolvedSlabThickness.ToString(CultureInfo.InvariantCulture);
            if(_workspace!=null) { _floorEditor=null;_workspaceOriginalHash=null;_generationMode.SelectedIndex=!File.Exists(CadFloorPlanRegistry.FilePath(nextPath))||registry.ReplaceRegisteredConstruction?0:1; }
            _path=nextPath;_model=model;_registry=registry;_registryRecovery=recovery;
            _registryRecoveryRows=recovery?.Rows(model);
            _contexts.Clear();foreach(var pair in contexts)_contexts.Add(pair.Key,pair.Value);
            _floors.ItemsSource = model.Storeys.Where(f=>string.IsNullOrWhiteSpace(f.StandardGroupId) || f.StandardGroupId==f.Id).Select(f => new CadFloorRegistrationRow { Storey = f,
                SourceName = !string.IsNullOrWhiteSpace(f.StandardFloorRange) ? "共用标准层平面" : string.IsNullOrWhiteSpace(f.TemplateStoreyId) ? "本层" : model.Storeys.First(s => string.Equals(s.Id, f.TemplateStoreyId, StringComparison.OrdinalIgnoreCase)).Name }).ToList();
            _modelLabel.Text = "当前建筑模型：" + model.Name;
            var saved = _contexts.Values.FirstOrDefault(c => c.DirectionPoint != null);
            if (saved != null) { _x.Text = saved.Alignment.ModelBase.X.ToString(CultureInfo.InvariantCulture); _y.Text = saved.Alignment.ModelBase.Y.ToString(CultureInfo.InvariantCulture); _unitScale.Text = saved.Alignment.MillimetresPerCadUnit.ToString(CultureInfo.InvariantCulture); }
            RefreshStatus();
            _modelStamp=File.GetLastWriteTimeUtc(_path).Ticks;
            var registryFile=CadFloorPlanRegistry.FilePath(_path);
            _registryStamp=File.Exists(registryFile)?File.GetLastWriteTimeUtc(registryFile).Ticks:0;
        }
        private void RequireModel() { if(_model==null || _registry==null)throw new InvalidDataException("请先新建或打开建筑模型。"); }
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
            if(_workspace!=null){_workspace.EnsureDocument?.Invoke();CheckWorkspaceFiles();}
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
            if(_workspace!=null){_workspace.EnsureDocument?.Invoke();CheckWorkspaceFiles();}
            _registry.SaveFloor(captured); _contexts[context.Storey.Id] = captured.Floor; RefreshStatus();
        }
        private void RefreshStatus()
        {
            foreach (CadFloorRegistrationRow row in _floors.Items)
            {
                var context = Context(row); var capture = _registry.Find(context.Storey.Id);
                row.Status = capture != null && SameDatum(context.Alignment, capture.Floor.Alignment)
                    ? "已登记 · 门窗 " + capture.Openings.Count(x => x.Include)
                        + " · 柱 " + capture.Probe.Entities.Count(e=>CadStructuralRegistration.IsColumn(e.DxfName))
                        + " · 梁 " + capture.Probe.Entities.Count(e=>CadStructuralRegistration.IsBeam(e.DxfName))
                    : context.DirectionPoint == null ? "未拾取基点" : "基点已拾取 · 待框选";
                if (!string.IsNullOrWhiteSpace(row.Storey.TemplateStoreyId)) row.Status = "复用 " + row.SourceName + " · " + row.Status;
            }
            RefreshWorkspace();
        }
        private static bool SameDatum(CadFloorRegistrationAlignment a, CadFloorRegistrationAlignment b) =>
            a.CadBase.X == b.CadBase.X && a.CadBase.Y == b.CadBase.Y && a.ModelBase.X == b.ModelBase.X && a.ModelBase.Y == b.ModelBase.Y
            && a.RotationRadians == b.RotationRadians && a.MillimetresPerCadUnit == b.MillimetresPerCadUnit;
    }
}
