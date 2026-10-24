using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed partial class CadFloorRegistrationWindow
    {
        private CadModelWorkspaceHost _workspace;
        private Action<CadFloorPlanRegistry> _workspaceGenerate, _workspaceRead;
        private readonly ComboBox _modelPicker=new ComboBox {Width=190,IsTextSearchEnabled=true};
        private readonly ComboBox _scope=new ComboBox {ItemsSource=new[]{"当前 CAD","全部模型"},SelectedIndex=0,Width=110};
        private readonly ListBox _workFloors=new ListBox();
        private readonly ContentControl _workspaceContent=new ContentControl();
        private readonly TextBlock _currentStudio=new TextBlock(), _workspaceStatus=new TextBlock {TextWrapping=TextWrapping.Wrap};
        private readonly ProgressBar _generationProgress=new ProgressBar {Minimum=0,Maximum=100,Height=6,IsIndeterminate=true};
        private readonly TextBlock _generationStage=new TextBlock {TextWrapping=TextWrapping.Wrap},_generationElapsed=new TextBlock();
        private readonly StackPanel _generationPanel=new StackPanel {Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,10,6)};
        private readonly System.Diagnostics.Stopwatch _generationClock=new System.Diagnostics.Stopwatch();
        private readonly DispatcherTimer _generationTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        private long _generationEpoch;
        private bool _generationActive;
        private readonly TextBlock _cadContextStatus=new TextBlock {TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
        private readonly TextBlock _previewTitle=new TextBlock(), _sourceText=new TextBlock(), _selectedStatus=new TextBlock();
        private readonly TextBox _floorNameText=new TextBox(), _floorHeightText=new TextBox();
        private readonly ComboBox _planeSource=new ComboBox {MinHeight=32,SelectedValuePath="Key"};
        private readonly TextBlock _floorElevationText=new TextBlock();
        private readonly CadRegisteredPlanPreview _preview=new CadRegisteredPlanPreview();
        private readonly Grid _workspaceRoot=new Grid(), _registrationBody=new Grid();
        private readonly ContentControl _inspector=new ContentControl();
        private CadModelProjectEditor _floorEditor;
        private readonly List<Button> _navigation=new List<Button>();
        private int _workspacePage=1;
        private bool _workspaceRefreshing,_workspaceBusy,_creatingModel,_propertiesCollapsed,_closePromptPending;
        private string _workspaceOriginalHash, _workspaceFloorId;
        private string _loadedOptions;
        private static Brush WorkspaceBrush(string value)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        public string TargetModelPath => _path;
        public bool WorkspaceBusy => _workspaceBusy;
        public void SetCadContextState(string message)
        {
            _cadContextStatus.Text=message??"";
            _cadContextStatus.Visibility=string.IsNullOrEmpty(message)?Visibility.Collapsed:Visibility.Visible;
        }
        private async Task WorkspaceCad(Action action,bool hide=true)
        {
            var shown=hide&&IsVisible;
            if(shown)Hide();
            try
            {
                if(_workspace.RunCad!=null)await _workspace.RunCad(action);
                else {_workspace.EnsureDocument?.Invoke();action();}
            }
            finally
            {
                // Completion may run inline while CAD unwinds its native callback.
                // Restore the WPF window in a fresh dispatcher operation instead.
                if(shown)await Dispatcher.InvokeAsync(new Action(()=>{Show();Activate();}),DispatcherPriority.ContextIdle);
            }
        }

        private void BuildWorkspace()
        {
            Title="建筑模型";Width=1280;Height=860;MinWidth=880;MinHeight=600;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=14;UseLayoutRounding=true;
            Background=WorkspaceBrush("#1B2027");Foreground=WorkspaceBrush("#E6EAF0");
            if(_workspace.Theme!=null)Resources.MergedDictionaries.Add(_workspace.Theme());
            var modelText=new FrameworkElementFactory(typeof(TextBlock));modelText.SetBinding(TextBlock.TextProperty,new Binding("Name"));modelText.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
            modelText.SetBinding(FrameworkElement.ToolTipProperty,new Binding("ModelPath"));
            _modelPicker.ItemTemplate=new DataTemplate {VisualTree=modelText};TextSearch.SetTextPath(_modelPicker,"Name");
            var sourceText=new FrameworkElementFactory(typeof(TextBlock));sourceText.SetBinding(TextBlock.TextProperty,new Binding("Value"));_planeSource.ItemTemplate=new DataTemplate {VisualTree=sourceText};
            var input=new Style(typeof(TextBox));input.Setters.Add(new Setter(Control.BackgroundProperty,WorkspaceBrush("#222B35")));
            input.Setters.Add(new Setter(Control.ForegroundProperty,Foreground));input.Setters.Add(new Setter(Control.BorderBrushProperty,WorkspaceBrush("#435160")));
            input.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(7,5,7,5)));Resources[typeof(TextBox)]=input;
            _workspaceRoot.Background=Background;
            foreach(var h in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})
                _workspaceRoot.RowDefinitions.Add(new RowDefinition {Height=h});
            var header=new WrapPanel {Margin=new Thickness(18,12,18,8),VerticalAlignment=VerticalAlignment.Center};
            header.Children.Add(new TextBlock {Text="建筑模型",FontSize=22,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,22,0)});
            header.Children.Add(new TextBlock {Text=_workspace.ProjectName??"",ToolTip=_workspace.ProjectFolder,MaxWidth=220,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,18,0)});
            header.Children.Add(new TextBlock {Text="CAD："+Path.GetFileName(_workspace.DrawingName??""),ToolTip=_workspace.DrawingName,MaxWidth=280,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,18,0)});
            header.Children.Add(_scope);header.Children.Add(new TextBlock {Text="目标模型",Margin=new Thickness(12,8,6,0)});header.Children.Add(_modelPicker);
            header.Children.Add(WorkspaceTool("file-plus","新建模型",()=>BeginNewModel()));
            header.Children.Add(WorkspaceTool("folder-open","打开文件",()=>OpenWorkspaceFile()));
            header.Children.Add(WorkspaceTool("save","保存",()=>SaveWorkspace()));
            var more=WorkspaceTool("copy","更多",()=>Task.CompletedTask);more.Click+=(s,e)=>{more.ContextMenu.IsOpen=true;};
            more.ContextMenu=new ContextMenu();
            void Item(string caption,Func<Task> action){var item=new MenuItem {Header=caption};item.Click+=async(s,e)=>await WorkspaceRun(action);more.ContextMenu.Items.Add(item);}
            Item("另存为",SaveWorkspaceAs);Item("刷新模型与登记",()=>ReloadWorkspace());
            Item("模型文件位置",()=>{RequireModel();System.Diagnostics.Process.Start("explorer.exe","/select,\""+_path+"\"");return Task.CompletedTask;});
            Item("天正字段核验",()=>WorkspaceCad(()=>_workspace.Probe?.Invoke(this)));
            header.Children.Add(more);_workspaceRoot.Children.Add(header);
            var nav=new DockPanel {Margin=new Thickness(18,0,18,0)};
            _currentStudio.Foreground=WorkspaceBrush("#9DAAB9");_currentStudio.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(_currentStudio,Dock.Right);nav.Children.Add(_currentStudio);
            _currentStudio.MaxWidth=260;_currentStudio.TextTrimming=TextTrimming.CharacterEllipsis;
            var tabs=new StackPanel {Orientation=Orientation.Horizontal};
            foreach(var page in new[]{"模型与楼层","CAD 平面登记","生成与打开"})
            {
                var index=_navigation.Count;var button=WorkspaceTool(null,page,()=>{ShowWorkspacePage(index);return Task.CompletedTask;});
                button.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='0,0,0,2' Padding='16,10' Background='Transparent'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>");
                _navigation.Add(button);tabs.Children.Add(button);
            }
            nav.Children.Add(tabs);Grid.SetRow(nav,1);_workspaceRoot.Children.Add(nav);
            var body=new DockPanel();_cadContextStatus.Foreground=WorkspaceBrush("#E8BE70");_cadContextStatus.Margin=new Thickness(18,8,18,0);
            DockPanel.SetDock(_cadContextStatus,Dock.Top);body.Children.Add(_cadContextStatus);
            _workspaceContent.Margin=new Thickness(18,10,18,0);body.Children.Add(_workspaceContent);Grid.SetRow(body,2);_workspaceRoot.Children.Add(body);
            var footer=new Grid {Margin=new Thickness(18,10,18,14)};footer.ColumnDefinitions.Add(new ColumnDefinition());footer.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            _workspaceStatus.Foreground=WorkspaceBrush("#AEB9C8");_workspaceStatus.VerticalAlignment=VerticalAlignment.Center;
            var state=new StackPanel {VerticalAlignment=VerticalAlignment.Center};
            var progressHeader=new DockPanel {Margin=new Thickness(0,0,0,5)};DockPanel.SetDock(_generationElapsed,Dock.Right);
            _generationElapsed.Margin=new Thickness(12,0,0,0);progressHeader.Children.Add(_generationElapsed);progressHeader.Children.Add(_generationStage);
            _generationPanel.Children.Add(progressHeader);_generationPanel.Children.Add(_generationProgress);state.Children.Add(_generationPanel);state.Children.Add(_workspaceStatus);footer.Children.Add(state);
            _generationTimer.Tick+=(s,e)=>_generationElapsed.Text="已用时 "+_generationClock.Elapsed.ToString(@"mm\:ss");
            Closed+=(s,e)=>{_generationTimer.Stop();_generationActive=false;_generationEpoch++;};
            var actions=new StackPanel {Orientation=Orientation.Horizontal};
            actions.Children.Add(WorkspaceTool("box","打开三维",OpenWorkspaceStudio));
            var generate=WorkspaceTool("box","生成并打开",GenerateWorkspace);generate.Background=WorkspaceBrush("#087FDD");actions.Children.Add(generate);
            Grid.SetColumn(actions,1);footer.Children.Add(actions);Grid.SetRow(footer,3);_workspaceRoot.Children.Add(footer);Content=_workspaceRoot;
            _scope.SelectionChanged+=(s,e)=>{if(!_workspaceRefreshing)RefreshModels();};
            _modelPicker.SelectionChanged+=async(s,e)=>
            {
                if(_workspaceRefreshing||_workspaceBusy)return;
                var entry=_modelPicker.SelectedItem as BuildingModelCatalogEntry;
                if(entry==null||(!_creatingModel&&SamePath(entry.ModelPath,_path)))return;
                await WorkspaceRun(async()=>{if(await ConfirmWorkspaceDraft())LoadModel(entry.ModelPath);RefreshModels();});
            };
            Closing+=(s,e)=>
            {
                if(_workspaceBusy){e.Cancel=true;return;}
                if(HasWorkspaceDraft())
                {
                    e.Cancel=true;
                    if(_closePromptPending)return;
                    _closePromptPending=true;
                    Dispatcher.BeginInvoke(new Action(async()=>
                    {
                        var close=false;
                        await WorkspaceRun(async()=>{close=await ConfirmWorkspaceDraft();});
                        _closePromptPending=false;
                        if(close){_creatingModel=false;_floorEditor=null;_loadedOptions=null;Close();}
                    }));
                }
            };
            Activated+=(s,e)=>
            {
                if(_workspaceBusy||_workspaceRefreshing||_path==null||_creatingModel)return;
                var changed=File.GetLastWriteTimeUtc(_path).Ticks!=_modelStamp||RegistryStamp()!=_registryStamp;
                if(changed)_workspaceStatus.Text="文件已在其他窗口修改，请刷新后继续；当前草稿保留。";
                UpdateStudioLabel();
            };
            Loaded+=(s,e)=>{Width=Math.Min(Width,SystemParameters.WorkArea.Width-24);Height=Math.Min(Height,SystemParameters.WorkArea.Height-24);};
            _workFloors.SelectionChanged+=(s,e)=>{if(!_workspaceRefreshing)SelectWorkspaceFloor();};
            _workFloors.Background=Background;_workFloors.Foreground=Foreground;
            _workFloors.BorderThickness=new Thickness(0);_workFloors.MinWidth=150;
            _workFloors.ItemTemplate=WorkspaceFloorTemplate();
            _floorNameText.TextChanged+=(s,e)=>UpdateFloorProperties();_floorHeightText.TextChanged+=(s,e)=>UpdateFloorProperties();
            _planeSource.SelectionChanged+=(s,e)=>{if(!_workspaceRefreshing&&_workspaceFloorId!=null&&_planeSource.SelectedValue is string source){_floorEditor?.SetFloorSource(_workspaceFloorId,source);_workspaceStatus.Text="平面来源草稿未保存";}};
            ShowWorkspacePage(1);
        }

        private Button WorkspaceTool(string icon,string label,Func<Task> action)
        {
            var content=new StackPanel {Orientation=Orientation.Horizontal};
            if(icon!=null)
            {
                using(var stream=typeof(CadFloorRegistrationWindow).Assembly.GetManifestResourceStream("WanLuo.ModelManager."+icon+".png"))
                {
                    if(stream==null)throw new InvalidDataException("缺少工作台图标："+icon);
                    var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();
                    content.Children.Add(new Image {Source=image,Width=18,Height=18,Margin=new Thickness(0,0,7,0)});
                }
            }
            content.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});
            var button=new Button {Content=content,ToolTip=label,MinHeight=36,Padding=new Thickness(10,6,10,6),Margin=new Thickness(4),
                Foreground=Foreground,Background=WorkspaceBrush("#222B35"),BorderBrush=WorkspaceBrush("#435160")};
            button.Click+=async(s,e)=>await WorkspaceRun(action);return button;
        }
        private static DataTemplate WorkspaceFloorTemplate()
        {
            var panel=new FrameworkElementFactory(typeof(StackPanel));panel.SetValue(FrameworkElement.MarginProperty,new Thickness(10,12,10,12));
            var title=new FrameworkElementFactory(typeof(TextBlock));title.SetBinding(TextBlock.TextProperty,new Binding("Name"));title.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);panel.AppendChild(title);
            var line=new FrameworkElementFactory(typeof(TextBlock));line.SetBinding(TextBlock.TextProperty,new Binding("Status"));line.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);line.SetValue(TextBlock.FontSizeProperty,12d);line.SetValue(FrameworkElement.MarginProperty,new Thickness(0,6,0,0));panel.AppendChild(line);
            var dimensions=new FrameworkElementFactory(typeof(TextBlock));var binding=new MultiBinding {StringFormat="{0} / {1} mm"};binding.Bindings.Add(new Binding("ElevationDisplay"));binding.Bindings.Add(new Binding("Height"));dimensions.SetBinding(TextBlock.TextProperty,binding);dimensions.SetValue(TextBlock.FontSizeProperty,12d);dimensions.SetValue(FrameworkElement.MarginProperty,new Thickness(0,5,0,0));panel.AppendChild(dimensions);
            return new DataTemplate {VisualTree=panel};
        }
        private Task WorkspaceRun(Func<Task> action)
        {
            // A modeless CAD callback can carry AutoCAD's synchronization context.
            // Capture WPF for the whole operation, without leaking it back to CAD.
            var previous=SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
                return WorkspaceRunCore(action);
            }
            finally{SynchronizationContext.SetSynchronizationContext(previous);}
        }
        private async Task WorkspaceRunCore(Func<Task> action)
        {
            if(_workspaceBusy)return;
            var previousStatus=_workspaceStatus.Text;
            _workspaceBusy=true;_workspaceRoot.IsEnabled=false;
            try { _workspaceStatus.Text="正在处理…";await action(); }
            catch(Exception ex) { _workspaceStatus.Text=ex.Message; }
            finally
            {
                if(_workspaceStatus.Text=="正在处理…")
                    _workspaceStatus.Text=string.IsNullOrEmpty(previousStatus)||previousStatus=="正在处理…"?"就绪":previousStatus;
                _workspaceBusy=false;_workspaceRoot.IsEnabled=true;
            }
        }
        private void ShowWorkspacePage(int page)
        {
            _workspacePage=page;
            for(var i=0;i<_navigation.Count;i++){_navigation[i].BorderBrush=WorkspaceBrush(i==page?"#239BEF":"#1B2027");_navigation[i].Foreground=WorkspaceBrush(i==page?"#64C1FF":"#B5C1D0");}
            if(_floorEditor==null&&_model!=null)SetFloorEditor(false);
            if(page==0)
            {
                _workspaceContent.Content=_floorEditor??(object)new TextBlock {Text="尚无模型",Margin=new Thickness(20)};
                return;
            }
            if(page==2){_workspaceContent.Content=BuildGenerationPage();return;}
            BuildRegistrationPage();_workspaceContent.Content=_registrationBody;SelectWorkspaceFloor();
        }
        private void BuildRegistrationPage()
        {
            foreach(var control in new FrameworkElement[]{_workFloors,_previewTitle,_preview,_sourceText,_inspector,_selectedStatus})DetachWorkspaceControl(control);
            _registrationBody.Children.Clear();_registrationBody.ColumnDefinitions.Clear();
            _registrationBody.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(190)});
            _registrationBody.ColumnDefinitions.Add(new ColumnDefinition());
            _registrationBody.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(_propertiesCollapsed?0:260)});
            var left=new DockPanel {Margin=new Thickness(0,0,12,0)};
            var floorHeader=new StackPanel {Orientation=Orientation.Horizontal};floorHeader.Children.Add(new TextBlock {Text="楼层",FontSize=18,VerticalAlignment=VerticalAlignment.Center});
            floorHeader.Children.Add(WorkspaceTool("file-plus","设置",()=>{ShowWorkspacePage(0);return Task.CompletedTask;}));
            DockPanel.SetDock(floorHeader,Dock.Top);left.Children.Add(floorHeader);left.Children.Add(_workFloors);_registrationBody.Children.Add(left);
            var center=new Grid();center.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});center.RowDefinitions.Add(new RowDefinition());center.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var toolbar=new WrapPanel();_previewTitle.FontSize=18;_previewTitle.Margin=new Thickness(0,8,12,0);toolbar.Children.Add(_previewTitle);
            toolbar.Children.Add(WorkspaceTool("move","拾取基点",()=>CaptureWorkspace(true)));
            toolbar.Children.Add(WorkspaceTool("square-dashed","框选平面",()=>CaptureWorkspace(false)));
            toolbar.Children.Add(WorkspaceTool("rotate-ccw","缩放适应",()=>{_preview.Fit();return Task.CompletedTask;}));
            toolbar.Children.Add(WorkspaceTool(null,_propertiesCollapsed?"展开属性":"收起属性",()=>{_propertiesCollapsed=!_propertiesCollapsed;ShowWorkspacePage(1);return Task.CompletedTask;}));
            center.Children.Add(toolbar);Grid.SetRow(_preview,1);center.Children.Add(_preview);_sourceText.Margin=new Thickness(6,8,0,6);_sourceText.TextTrimming=TextTrimming.CharacterEllipsis;Grid.SetRow(_sourceText,2);center.Children.Add(_sourceText);
            Grid.SetColumn(center,1);_registrationBody.Children.Add(center);
            _inspector.Content=BuildInspector();_inspector.Margin=new Thickness(16,0,0,0);_inspector.Visibility=_propertiesCollapsed?Visibility.Collapsed:Visibility.Visible;
            Grid.SetColumn(_inspector,2);_registrationBody.Children.Add(_inspector);
        }
        private FrameworkElement BuildInspector()
        {
            var fields=new StackPanel();fields.Children.Add(new TextBlock {Text="楼层属性",FontSize=18,Margin=new Thickness(0,12,0,10)});
            WorkspaceField(fields,"楼层名称",_floorNameText);WorkspaceField(fields,"首层标高 mm",_floorElevationText);WorkspaceField(fields,"每层层高 mm",_floorHeightText);
            WorkspaceField(fields,"平面来源",_planeSource);
            fields.Children.Add(new Separator {Margin=new Thickness(0,12,0,12)});
            fields.Children.Add(new TextBlock {Text="CAD 登记",FontSize=18,Margin=new Thickness(0,0,0,8)});fields.Children.Add(_selectedStatus);
            var advanced=new StackPanel();WorkspaceField(advanced,"模型基点 X",_x);WorkspaceField(advanced,"模型基点 Y",_y);WorkspaceField(advanced,"1 CAD 单位 = mm",_unitScale);
            fields.Children.Add(new Expander {Header="坐标与单位",Content=advanced,Margin=new Thickness(0,14,0,10)});
            fields.Children.Add(WorkspaceTool(null,"门窗表",()=>EditWorkspaceOpenings(false)));
            fields.Children.Add(WorkspaceTool(null,"补充门窗位置",()=>EditWorkspaceOpenings(true)));
            return new ScrollViewer {Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        }
        private static void WorkspaceField(Panel panel,string name,FrameworkElement control)
        {
            DetachWorkspaceControl(control);
            panel.Children.Add(new TextBlock {Text=name,Margin=new Thickness(0,10,0,5)});panel.Children.Add(control);
        }
        private static void DetachWorkspaceControl(FrameworkElement control)
        {
            if(control.Parent is Panel panel)panel.Children.Remove(control);
            else if(control.Parent is ContentControl content)content.Content=null;
            else if(control.Parent is Decorator decorator)decorator.Child=null;
        }
        private FrameworkElement BuildGenerationPage()
        {
            var panel=new StackPanel {Margin=new Thickness(20),MaxWidth=820,HorizontalAlignment=HorizontalAlignment.Left};
            panel.Children.Add(new TextBlock {Text="生成与打开",FontSize=22,FontWeight=FontWeights.SemiBold});
            panel.Children.Add(new TextBlock {Text="目标模型："+(_model?.Name??"尚未选择"),Margin=new Thickness(0,16,0,12)});
            var summary=new TextBlock {Text=string.Join(Environment.NewLine,_floors.Items.Cast<CadFloorRegistrationRow>().Select(r=>r.Name+" · "+r.Status)),TextWrapping=TextWrapping.Wrap,LineHeight=28};panel.Children.Add(summary);
            WorkspaceField(panel,"重新生成",_generationMode);WorkspaceField(panel,"楼板厚度 mm",_slabThickness);
            panel.Children.Add(new TextBlock {Text="同编号、同洞口尺寸保留门窗做法；位置按 CAD 更新。未登记楼层不动。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,12),Foreground=WorkspaceBrush("#AEB9C8")});
            panel.Children.Add(WorkspaceTool("rotate-ccw","只更新参照底图",async()=>
            {
                if(!await SaveWorkspace())return;await WorkspaceCad(()=>{CheckWorkspaceFiles();_updateUnderlay?.Invoke(_registry);});ReloadWorkspaceModel();_workspaceStatus.Text="参照底图已更新，模型构件未改变。";
            }));
            return new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        }
        private void SetFloorEditor(bool creating)
        {
            _floorEditor=new CadModelProjectEditor(_model,creating,true);
            _floorEditor.Applied=async()=>await WorkspaceRun(async()=>{await SaveWorkspace();});
            _floorEditor.Cancelled=()=>{if(_creatingModel){_creatingModel=false;_floorEditor=null;if(_path!=null)LoadModel(_path);else{_model=null;ShowWorkspacePage(0);}}else SetFloorEditor(false);ShowWorkspacePage(_workspacePage);};
        }
        private void UpdateFloorProperties()
        {
            if(_workspaceRefreshing||_floorEditor==null||_workspaceFloorId==null||_workspacePage!=1)return;
            _floorEditor.SetFloorProperties(_workspaceFloorId,_floorNameText.Text,_floorHeightText.Text);
            _workspaceStatus.Text="楼层草稿未保存";
        }
        private void RefreshModels()
        {
            if(_workspace==null)return;
            _workspaceRefreshing=true;
            try
            {
                var current=_workspace.CurrentModel?.Invoke();
                var all=BuildingModelCatalog.List(_workspace.ProjectFolder,current);
                var entries=all.Where(e=>_scope.SelectedIndex!=0||BuildingModelCatalog.LinkedTo(e,_workspace.DrawingFingerprint)||SamePath(e.ModelPath,_path)).ToList();
                _modelPicker.ItemsSource=entries;_modelPicker.SelectedItem=entries.FirstOrDefault(e=>SamePath(e.ModelPath,_path));
                UpdateStudioLabel();
            }
            finally{_workspaceRefreshing=false;}
        }
        private void UpdateStudioLabel()
        {
            try {var path=_workspace.CurrentModel?.Invoke();_currentStudio.Text="当前三维："+(path!=null&&StudioLaunch.HasLiveSession(path)?BuildingModelJson.LoadModel(path).Name:"未打开");}
            catch{_currentStudio.Text="当前三维：不可用";}
        }
        private void RefreshWorkspace()
        {
            if(_workspace==null||_workspaceRefreshing)return;
            _workspaceRefreshing=true;
            try
            {
                var selected=(_workFloors.SelectedItem as CadFloorRegistrationRow)?.Storey.Id;
                _workFloors.ItemsSource=_floors.ItemsSource;
                _workFloors.SelectedItem=_floors.Items.Cast<CadFloorRegistrationRow>().FirstOrDefault(r=>r.Storey.Id==selected)??_floors.Items.Cast<CadFloorRegistrationRow>().FirstOrDefault();
                var hash=_model==null?null:BuildingModelProjectWorkflow.StoreysHash(_model);
                if(_floorEditor==null||_workspaceOriginalHash!=hash){_workspaceOriginalHash=hash;SetFloorEditor(false);}
                _loadedOptions=WorkspaceOptions();
            }
            finally{_workspaceRefreshing=false;}
            SelectWorkspaceFloor();ShowWorkspacePage(_workspacePage);RefreshModels();
            _workspaceStatus.Text="目标模型："+(_model?.Name??"未选择")+" · 已登记 "+(_registry?.Floors.Count??0)+" 组楼层";
        }
        private void SelectWorkspaceFloor()
        {
            if(_workspace==null)return;
            var row=_workFloors.SelectedItem as CadFloorRegistrationRow;
            _workspaceRefreshing=true;
            try
            {
                _workspaceFloorId=row?.Storey.Id;_previewTitle.Text=row==null?"CAD 平面":row.Name+" · CAD 平面";
                var edited=_floorEditor?.FindFloor(_workspaceFloorId);
                _floorNameText.Text=edited?.Name??row?.Name??"";_floorHeightText.Text=edited?.Height??row?.Height.ToString(CultureInfo.InvariantCulture)??"";
                _floorElevationText.Text=edited?.Elevation??row?.ElevationDisplay??"";_selectedStatus.Text=row?.Status??"尚无楼层";_selectedStatus.TextWrapping=TextWrapping.Wrap;
                _planeSource.ItemsSource=_floorEditor?.SourcesFor(_workspaceFloorId);_planeSource.SelectedValue=edited?.Source;
                var capture=row==null?null:_registry?.Find(Context(row).Storey.Id);
                _preview.SetCapture(capture);_sourceText.Text=capture?.Probe?.DrawingPath??"尚未登记平面";
            }
            finally{_workspaceRefreshing=false;}
        }
        private long RegistryStamp()=>_path!=null&&File.Exists(CadFloorPlanRegistry.FilePath(_path))?File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(_path)).Ticks:0;
        private void CheckWorkspaceFiles()
        {
            RequireModel();
            if(_creatingModel)throw new InvalidOperationException("请先创建模型。");
            if(File.GetLastWriteTimeUtc(_path).Ticks!=_modelStamp||RegistryStamp()!=_registryStamp)
                throw new IOException("模型或登记已在其他窗口修改，请先刷新。当前草稿未被覆盖。");
        }
        private string WorkspaceOptions()=>string.Join("|",_slabThickness.Text,_generationMode.SelectedIndex.ToString());
        private bool HasWorkspaceDraft()=>_creatingModel||(_floorEditor?.HasChanges??false)||(_loadedOptions!=null&&WorkspaceOptions()!=_loadedOptions);
        private async Task<bool> ConfirmWorkspaceDraft()
        {
            if(!HasWorkspaceDraft())return true;
            var result=AskWorkspace("建筑模型","当前模型有未保存设置。是否保存后继续？",MessageBoxButton.YesNoCancel);
            if(result==MessageBoxResult.Cancel)return false;
            if(result==MessageBoxResult.Yes)return await SaveWorkspace();
            _creatingModel=false;_floorEditor=null;_loadedOptions=null;ReloadWorkspaceModel();return true;
        }
        public async Task<bool> SaveWorkspace(bool allowInvalidGenerationSettings=false)
        {
            _workspace.EnsureDocument?.Invoke();
            if(_floorEditor==null)throw new InvalidOperationException("请先新建或选择模型。");
            UpdateFloorProperties();
            double thickness;if(!double.TryParse(_slabThickness.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out thickness)||double.IsNaN(thickness)||double.IsInfinity(thickness)||thickness<=.5)
            {
                if(!allowInvalidGenerationSettings)throw new InvalidDataException("楼板厚度请输入有效的毫米数值。");
                thickness=_registry?.ResolvedSlabThickness??100;
            }
            var mode=_generationMode.SelectedIndex;
            var floors=_floorEditor.CollectStoreys();
            string replacementBackup=null;
            if(_creatingModel)
            {
                var name=_floorEditor.ModelName;var fresh=BuildingModelProjectWorkflow.Empty(name);fresh.Storeys=floors;
                var target=BuildingModelJson.ModelFilePath(_workspace.ProjectFolder,name);
                if(File.Exists(target))
                {
                    var expected=BuildingModelProjectWorkflow.ModelFileHash(target);
                    var answer=AskWorkspace("替换已有模型","已存在同名模型“"+name+"”。是否备份并替换？\n\n替换后按当前楼层设置创建空模型，原模型、CAD 登记和图纸等整个模型目录会自动保留为备份，不与新模型混用。\n选择“否”或“取消”保留旧模型和当前草稿。",MessageBoxButton.YesNoCancel);
                    if(answer!=MessageBoxResult.Yes){_workspaceStatus.Text="已取消替换，旧模型未改动，当前草稿保留。";return false;}
                    replacementBackup=BuildingModelProjectWorkflow.ReplaceExisting(target,fresh,expected);
                }
                else BuildingModelProjectWorkflow.CreateNew(target,fresh);
                BuildingModelCatalog.Register(_workspace.ProjectFolder,target,_workspace.DrawingFingerprint,_workspace.DrawingName);
                _creatingModel=false;_floorEditor=null;_workspaceOriginalHash=null;LoadModel(target);ShowWorkspacePage(1);
            }
            else
            {
                CheckWorkspaceFiles();
                if(_floorEditor.HasChanges)
                {
                    var changed=_model.Storeys.Where(old=>!floors.Any(f=>f.Id==old.Id&&f.TemplateStoreyId==old.TemplateStoreyId)).Select(f=>f.Name).ToArray();
                    var confirmation=AskWorkspace("楼层设置","保存楼层设置；删除楼层或改为共用平面会同步处理本层构件。"+(changed.Length==0?"":"涉及："+string.Join("、",changed))+"\n继续吗？",MessageBoxButton.OKCancel);
                    if(confirmation!=MessageBoxResult.OK)return false;
                    if(_workspace.SaveFloors==null)throw new InvalidOperationException("楼层保存服务不可用。");
                    await _workspace.SaveFloors(_path,_workspaceOriginalHash,floors);
                    _floorEditor=null;_workspaceOriginalHash=null;LoadModel(_path);
                }
                else if(_workspace.Save!=null) {await _workspace.Save(_path);ReloadWorkspaceModel();}
            }
            _workspace.EnsureDocument?.Invoke();
            _slabThickness.Text=thickness.ToString(CultureInfo.InvariantCulture);_generationMode.SelectedIndex=mode;
            if(!File.Exists(CadFloorPlanRegistry.FilePath(_path))||_registry.ResolvedSlabThickness!=thickness||_registry.ReplaceRegisteredConstruction!=(mode==0))
            {
                var draft=_registry.Clone();draft.SlabThickness=thickness;draft.ReplaceRegisteredConstruction=mode==0;
                _registry.SaveUnderlays(draft,_registryStamp);_registryStamp=RegistryStamp();_registry=draft;
            }
            _loadedOptions=WorkspaceOptions();_workspaceStatus.Text="已保存："+_model.Name+(replacementBackup==null?"":" · 旧模型备份："+replacementBackup);RefreshModels();return true;
        }
        private async Task BeginNewModel()
        {
            if(!await ConfirmWorkspaceDraft())return;
            _model=BuildingModelProjectWorkflow.Empty("新建模型");_creatingModel=true;_workspaceFloorId=null;SetFloorEditor(true);ShowWorkspacePage(0);
            _workspaceRefreshing=true;try{_modelPicker.SelectedItem=null;}finally{_workspaceRefreshing=false;}
            _workspaceStatus.Text="新模型尚未创建";
        }
        private async Task OpenWorkspaceFile()
        {
            if(!await ConfirmWorkspaceDraft())return;
            var dialog=new Microsoft.Win32.OpenFileDialog {Title="打开建筑模型",Filter="建筑模型 (*.json)|*.json",CheckFileExists=true};
            if(dialog.ShowDialog(this)!=true)return;
            var candidate=BuildingModelJson.LoadModel(dialog.FileName);BuildingModelProjectWorkflow.ValidateStoreys(candidate.Storeys);
            LoadModel(dialog.FileName);BuildingModelCatalog.Register(_workspace.ProjectFolder,_path,_workspace.DrawingFingerprint,_workspace.DrawingName);RefreshModels();
        }
        private async Task ReloadWorkspace()
        {
            if(!await ConfirmWorkspaceDraft())return;ReloadWorkspaceModel();RefreshModels();
        }
        private void ReloadWorkspaceModel(){if(_path!=null){_floorEditor=null;_workspaceOriginalHash=null;LoadModel(_path);}}
        private async Task SaveWorkspaceAs()
        {
            if(!await SaveWorkspace())return;
            if(_workspace.SaveAs==null)throw new InvalidOperationException("另存服务不可用。");
            var next=await _workspace.SaveAs(this,_path);if(next!=null){_floorEditor=null;_workspaceOriginalHash=null;LoadModel(next);RefreshModels();}
        }
        private async Task OpenWorkspaceStudio()
        {
            if(!await SaveWorkspace())return;CheckWorkspaceFiles();_workspace.EnsureDocument?.Invoke();
            if(_workspace.Open==null)throw new InvalidOperationException("模型打开服务不可用。");
            await _workspace.Open(_path);RefreshModels();_workspaceStatus.Text="已打开三维："+_model.Name;
        }
        private async Task CaptureWorkspace(bool datum)
        {
            if(_creatingModel)throw new InvalidOperationException("请先创建模型，再登记楼层。");
            var selected=(_workFloors.SelectedItem as CadFloorRegistrationRow)?.Storey.Id;
            var x=_x.Text;var y=_y.Text;var units=_unitScale.Text;
            if(!await SaveWorkspace())return;CheckWorkspaceFiles();_workspace.EnsureDocument?.Invoke();
            _x.Text=x;_y.Text=y;_unitScale.Text=units;
            var row=_workFloors.SelectedItem as CadFloorRegistrationRow;if(row==null)throw new InvalidOperationException("请先选择楼层。");
            if(row.Storey.Id!=selected)throw new InvalidOperationException("楼层来源已改变，请重新选择登记楼层。");
            _floors.SelectedItem=row;
            await WorkspaceCad(()=>{CheckWorkspaceFiles();if(datum)PickDatum(row);else Capture(row);});
            _modelStamp=File.GetLastWriteTimeUtc(_path).Ticks;_registryStamp=RegistryStamp();_loadedOptions=WorkspaceOptions();RefreshWorkspace();
        }
        private async Task GenerateWorkspace()
        {
            var epoch=++_generationEpoch;_generationActive=true;_generationClock.Restart();_generationTimer.Start();_generationPanel.Visibility=Visibility.Visible;
            var succeeded=false;
            void Report(CadGenerationProgress step)
            {
                void Apply()
                {
                    if(!_generationActive||epoch!=_generationEpoch)return;
                    var count=Math.Max(0,Math.Min(step.Completed,step.Total));
                    _generationStage.Text=step.Stage+(step.Total>0?" · "+count+"/"+step.Total+"（当前阶段 "+(int)(count*100d/step.Total)+"%）":"");
                    _generationProgress.IsIndeterminate=step.Total<=0;_generationProgress.Value=step.Total>0?count*100d/step.Total:0;
                }
                if(Dispatcher.CheckAccess())Apply();else Dispatcher.BeginInvoke(new Action(Apply));
            }
            try
            {
            Report(new CadGenerationProgress {Stage="保存楼层与登记设置"});
            if(IsVisible)await Dispatcher.Yield(DispatcherPriority.Render);
            double thickness;
            var invalid=!double.TryParse(_slabThickness.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out thickness)||double.IsNaN(thickness)||double.IsInfinity(thickness)||thickness<=.5;
            if(!await SaveWorkspace(true))return;CheckWorkspaceFiles();_workspace.EnsureDocument?.Invoke();
            var registry=_registry.Clone();registry.ReplaceRegisteredConstruction=_generationMode.SelectedIndex==0;
            registry.GenerationWarning=invalid?"楼板厚度无效，本次保留已有模型，可进入模型后调整。":null;
            Report(new CadGenerationProgress {Stage="读取 CAD 登记平面（"+registry.Floors.Count+" 组楼层）"});
            if(IsVisible)await Dispatcher.Yield(DispatcherPriority.Render);
            await WorkspaceCad(()=>
            {
                try{_workspaceRead?.Invoke(registry);}catch(Exception ex){registry.GenerationWarning=(registry.GenerationWarning==null?"":registry.GenerationWarning+"；")+"CAD 重新读取未完成："+ex.Message;}
            },hide:false);
            _workspace.EnsureDocument?.Invoke();
            if(_workspace.GenerateWithProgress!=null)await _workspace.GenerateWithProgress(registry,Report);
            else if(_workspace.Generate!=null)await _workspace.Generate(registry);
            else
            {
                if(_workspaceGenerate==null)throw new InvalidOperationException("模型生成服务不可用。");
                _workspaceGenerate(registry);_workspaceStatus.Text="已提交生成请求，请在三维窗口查看结果。";return;
            }
            ReloadWorkspaceModel();_workspaceStatus.Text="生成请求已完成："+_model.Name;
            Report(new CadGenerationProgress {Stage="模型已生成并保存",Completed=1,Total=1,State="completed"});succeeded=true;
            }
            finally
            {
                _generationClock.Stop();_generationTimer.Stop();_generationElapsed.Text="已用时 "+_generationClock.Elapsed.ToString(@"mm\:ss");
                _generationActive=false;if(!succeeded)_generationPanel.Visibility=Visibility.Collapsed;
            }
        }
        private async Task EditWorkspaceOpenings(bool locations)
        {
            if(!await SaveWorkspace())return;CheckWorkspaceFiles();_workspace.EnsureDocument?.Invoke();
            await WorkspaceCad(()=>{CheckWorkspaceFiles();EditOpeningTable(locations);});ReloadWorkspaceModel();
        }
        private static bool SamePath(string a,string b)=>!string.IsNullOrWhiteSpace(a)&&!string.IsNullOrWhiteSpace(b)&&string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
        private MessageBoxResult AskWorkspace(string title,string text,MessageBoxButton buttons)=>_workspace.Confirm!=null?_workspace.Confirm(title,text,buttons):MessageBox.Show(this,text,title,buttons,MessageBoxImage.Question,MessageBoxResult.Cancel);
    }
}
