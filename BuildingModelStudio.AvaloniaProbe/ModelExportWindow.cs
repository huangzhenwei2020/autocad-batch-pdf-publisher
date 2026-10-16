using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ModelExportWindow : Window
{
    internal readonly ComboBox TargetSelector=new() {ItemsSource=new[]{"3ds Max 2024 (.fbx)","SketchUp 2026 (.skp)"},SelectedIndex=0};
    internal readonly ComboBox ScopeSelector=new() {ItemsSource=new[]{"整栋模型","指定楼层"},SelectedIndex=0};
    internal readonly ComboBox StoreySelector=new() {MaxDropDownHeight=240};
    internal readonly CheckBox NearOrigin=new() {IsChecked=true,Content="移至项目附近原点"};
    internal readonly TextBox OutputPath=new() {IsReadOnly=true,PlaceholderText="选择 .fbx 文件"};
    internal readonly TextBox SketchUpApiPath=new() {PlaceholderText="自动检测，或选择 SketchUp.exe"};
    internal readonly TextBlock Status=new() {Text="就绪",TextWrapping=TextWrapping.Wrap};
    internal readonly Button ButtonExport;
    internal readonly Button ButtonCancel;
    internal readonly Button ButtonOutputFolder;
    internal readonly Button ButtonLogFolder;
    internal Task OperationTask {get;private set;}=Task.CompletedTask;
    internal bool Busy {get;private set;}
    internal ModelExportResult? Result {get;private set;}

    private readonly BuildingModelDocument _model;
    private readonly StackPanel _settings=new() {Spacing=12};
    private readonly StackPanel _storeyField;
    private readonly StackPanel _apiField;
    private readonly TextBlock _apiStatus=new() {Text="正在检测 SketchUp 2026",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _originStatus=new() {Text="使用项目附近原点",TextWrapping=TextWrapping.Wrap};
    private readonly ProgressBar _progress=new() {IsIndeterminate=true,IsVisible=false,MinHeight=4};
    private CancellationTokenSource? _job;
    private string? _runDirectory;
    private bool _closeRequested,_closed,_cancelRequested;
    private int _exportGeneration;

    private sealed record StoreyChoice(string Id,string Label);
    private ModelExportFormat Format=>TargetSelector.SelectedIndex==1?ModelExportFormat.Skp:ModelExportFormat.Fbx;
    private string Extension=>Format==ModelExportFormat.Skp?"skp":"fbx";

    public ModelExportWindow(BuildingModelDocument model,string? currentStoreyId)
    {
        ArgumentNullException.ThrowIfNull(model);
        // The caller owns snapshot creation; this dialog never edits the supplied model.
        _model=model;
        Title="导出模型";Width=720;Height=600;MinWidth=560;MinHeight=500;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#101923"));ComponentUi.Theme(this);
        Identify(this,"ModelExportWindow","导出模型");
        Identify(TargetSelector,"ModelExportTarget","导出目标");
        Identify(ScopeSelector,"ModelExportScope","导出范围");
        Identify(StoreySelector,"ModelExportStorey","导出楼层");
        Identify(NearOrigin,"ModelExportNearOrigin","移至项目附近原点");
        Identify(OutputPath,"ModelExportOutputPath","导出文件路径");
        Identify(SketchUpApiPath,"ModelExportSketchUpApiPath","SketchUp API 路径");
        Identify(_apiStatus,"ModelExportApiStatus","SketchUp API 状态");
        Identify(Status,"ModelExportStatus","导出状态");
        Identify(_progress,"ModelExportProgress","导出进度");
        foreach(var selector in new[]{TargetSelector,ScopeSelector,StoreySelector}) {
            selector.HorizontalAlignment=HorizontalAlignment.Stretch;selector.MinWidth=0;selector.MinHeight=36;
        }
        OutputPath.MinWidth=SketchUpApiPath.MinWidth=0;
        OutputPath.MinHeight=SketchUpApiPath.MinHeight=36;
        ToolTip.SetTip(OutputPath,OutputPath.Text);
        var storeys=model.Storeys.Where(s=>s!=null&&!string.IsNullOrWhiteSpace(s.Id))
            .OrderBy(s=>s.Elevation).Select(s=>new StoreyChoice(s.Id,string.IsNullOrWhiteSpace(s.Name)?s.Id:s.Name+" · "+s.Id)).ToArray();
        StoreySelector.ItemsSource=storeys;
        StoreySelector.ItemTemplate=new FuncDataTemplate<StoreyChoice>((s,_)=>new TextBlock {
            Text=s?.Label??"",TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center});
        StoreySelector.SelectedItem=storeys.FirstOrDefault(s=>string.Equals(s.Id,currentStoreyId,StringComparison.OrdinalIgnoreCase))??storeys.FirstOrDefault();

        var root=new Grid {Margin=new Thickness(18),RowDefinitions=new("Auto,*,Auto,Auto"),RowSpacing=12};
        root.Children.Add(new TextBlock {Text="导出模型",FontSize=20,FontWeight=FontWeight.SemiBold});
        var choices=new Grid {ColumnDefinitions=new("*,*"),ColumnSpacing=12};
        choices.Children.Add(Field("目标格式",TargetSelector));var scope=Field("模型范围",ScopeSelector);
        Grid.SetColumn(scope,1);choices.Children.Add(scope);_settings.Children.Add(choices);
        _storeyField=Field("楼层",StoreySelector);_settings.Children.Add(_storeyField);
        _settings.Children.Add(NearOrigin);_settings.Children.Add(_originStatus);
        var pose=new TextBlock {Text="当前姿态 · 仅静态几何，不含动画",TextWrapping=TextWrapping.Wrap};
        Identify(pose,"ModelExportStaticPose","当前姿态，仅静态几何，不含动画");_settings.Children.Add(pose);
        var browseOutput=Tool("folder-open","选择导出文件","ModelExportBrowseOutput",()=>Start(async()=>{await BrowseOutputAsync();}));
        _settings.Children.Add(Field("输出文件",PathRow(OutputPath,browseOutput)));
        var browseApi=Tool("folder-open","选择 SketchUp 程序","ModelExportBrowseApi",()=>Start(BrowseApiAsync));
        _apiField=Field("SketchUp 2026 安装位置",PathRow(SketchUpApiPath,browseApi));
        _apiField.Children.Add(_apiStatus);_settings.Children.Add(_apiField);
        var scroll=new ScrollViewer {Content=_settings,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        Identify(scroll,"ModelExportSettingsScroll","导出设置");Grid.SetRow(scroll,1);root.Children.Add(scroll);

        var state=new StackPanel {Spacing=8};state.Children.Add(_progress);
        state.Children.Add(new ScrollViewer {Content=Status,MaxHeight=104,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        Grid.SetRow(state,2);root.Children.Add(state);
        var footer=new Grid {RowDefinitions=new("Auto,Auto"),RowSpacing=8};
        var folders=new WrapPanel {Orientation=Orientation.Horizontal};
        ButtonOutputFolder=Tool("folder-open","打开输出目录","ModelExportOpenOutputFolder",()=>Start(()=>OpenFolderAsync(Result==null?null:Path.GetDirectoryName(Result.Path))));
        ButtonLogFolder=Tool("folder-open","打开日志目录","ModelExportOpenLogFolder",()=>Start(()=>OpenFolderAsync(_runDirectory)));
        ButtonOutputFolder.Margin=new Thickness(0,0,8,0);folders.Children.Add(ButtonOutputFolder);folders.Children.Add(ButtonLogFolder);footer.Children.Add(folders);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Right};
        ButtonExport=Tool("save","导出","ModelExportStart",()=>Start(ExportCoreAsync));
        ButtonCancel=Tool("square-x","关闭","ModelExportCancel",CancelOrClose);
        AutomationProperties.SetName(ButtonCancel,"取消或关闭导出窗口");
        actions.Children.Add(ButtonExport);actions.Children.Add(ButtonCancel);Grid.SetRow(actions,1);footer.Children.Add(actions);
        Grid.SetRow(footer,3);root.Children.Add(footer);Content=root;

        TargetSelector.SelectionChanged+=(_,_)=>{
            if(Busy)return;
            OutputPath.Text=null;ToolTip.SetTip(OutputPath,null);OutputPath.PlaceholderText="选择 ."+Extension+" 文件";RefreshControls();
        };
        ScopeSelector.SelectionChanged+=(_,_)=>RefreshControls();
        NearOrigin.IsCheckedChanged+=(_,_)=>_originStatus.Text=NearOrigin.IsChecked==true?"使用项目附近原点":"保留 CAD 原始坐标";
        SketchUpApiPath.TextChanged+=(_,_)=>RefreshApiStatus();
        Opened+=(_,_)=>Start(DetectApiAsync);
        Closing+=(_,e)=>{
            if(!Busy)return;
            e.Cancel=true;_closeRequested=true;RequestCancellation();
        };
        Closed+=(_,_)=>_closed=true;
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;CancelOrClose();}};
        RefreshControls();
    }

    private static void Identify(Control control,string id,string name)
    {
        control.Name=id;AutomationProperties.SetAutomationId(control,id);AutomationProperties.SetName(control,name);
    }
    private static StackPanel Field(string label,Control control)
    {
        var panel=new StackPanel {Spacing=5};panel.Children.Add(new TextBlock {Text=label,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(control);return panel;
    }
    private static Grid PathRow(Control input,Button browse)
    {
        var row=new Grid {ColumnDefinitions=new("*,Auto"),ColumnSpacing=8};row.Children.Add(input);Grid.SetColumn(browse,1);row.Children.Add(browse);return row;
    }
    private static Button Tool(string icon,string label,string id,Action action)
    {
        var button=ComponentUi.Tool(icon,label,action);
        Identify(button,id,label);
        if(id is "ModelExportBrowseOutput" or "ModelExportBrowseApi") {
            button.Content=ComponentUi.Icon(icon);button.Width=36;
        }
        return button;
    }
    private void Start(Func<Task> action)
    {
        if(Busy||_closed||_closeRequested)return;
        OperationTask=RunAsync(action);
    }
    private async Task RunAsync(Func<Task> action)
    {
        Busy=true;_cancelRequested=false;
        try {RefreshControls();await action();}
        catch(OperationCanceledException) {Status.Text="导出已取消；已有文件保持不变。";}
        catch(Exception ex) {Status.Text="操作失败："+ex.Message;}
        finally {
            Busy=false;_progress.IsVisible=false;
            RefreshControls();
            if(_closeRequested&&!_closed)Close();
        }
    }
    private void RefreshControls()
    {
        _settings.IsEnabled=!Busy;
        _storeyField.IsVisible=ScopeSelector.SelectedIndex==1;
        _apiField.IsVisible=Format==ModelExportFormat.Skp;
        ButtonExport.IsEnabled=!Busy&&(ScopeSelector.SelectedIndex==0||StoreySelector.SelectedItem is StoreyChoice);
        ButtonOutputFolder.IsEnabled=!Busy&&Result!=null;
        ButtonLogFolder.IsEnabled=!Busy&&!string.IsNullOrWhiteSpace(_runDirectory);
        if(ButtonCancel.Content is StackPanel contents&&contents.Children.LastOrDefault() is TextBlock text)
            text.Text=Busy?"取消":"关闭";
        ButtonCancel.IsEnabled=!_closeRequested&&(!Busy||!_cancelRequested);
        ToolTip.SetTip(ButtonCancel,Busy?"取消本次操作":"关闭");
    }
    private void CancelOrClose()
    {
        if(Busy)RequestCancellation();else Close();
    }
    private void RequestCancellation()
    {
        _cancelRequested=true;
        if(_job!=null) {
            try {_job.Cancel();Status.Text="正在取消，等待本次导出结束；已有文件保持不变。";}
            catch(Exception ex) {Status.Text="取消请求失败："+ex.Message;}
        } else Status.Text=_closeRequested?"等待当前操作结束后关闭。":"已请求取消，等待当前操作结束。";
        RefreshControls();
    }
    private async Task DetectApiAsync()
    {
        var directory=await Task.Run(ModelExportService.FindSketchUpApiDirectory);
        if(_closeRequested||_cancelRequested)return;
        if(string.IsNullOrWhiteSpace(SketchUpApiPath.Text))SketchUpApiPath.Text=directory;
        RefreshApiStatus();
    }
    private string? ApiDirectory()
    {
        var value=SketchUpApiPath.Text?.Trim().Trim('"');
        if(string.IsNullOrWhiteSpace(value))return null;
        var path=Path.GetFullPath(value);
        return Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)
            ||Path.GetExtension(path).Equals(".dll",StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(path):path;
    }
    private void RefreshApiStatus()
    {
        try {
            var directory=ApiDirectory();var dll=directory==null?null:Path.Combine(directory,"SketchUpAPI.dll");
            if(dll==null||!File.Exists(dll)){_apiStatus.Text="未找到 SketchUp 2026 程序组件";return;}
            var version=FileVersionInfo.GetVersionInfo(dll);
            _apiStatus.Text=(ModelExportService.IsSketchUp2026(directory)?"已找到 SketchUp 2026":"程序版本或组件不符合 SketchUp 2026")
                +" · 版本 "+(version.FileVersion??version.ProductVersion??"未提供")+"\n"+dll;
        } catch(Exception ex) {_apiStatus.Text="API 检查失败："+ex.Message;}
    }
    private async Task BrowseApiAsync()
    {
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title="选择 SketchUp 2026 程序",AllowMultiple=false,
            FileTypeFilter=new[]{new FilePickerFileType("SketchUp 程序") {Patterns=new[]{"SketchUp.exe"}}}});
        if(_closeRequested||_cancelRequested||files.Count==0)return;
        var path=files[0].TryGetLocalPath()??throw new InvalidOperationException("请选择本机 SketchUp 文件。");
        if(!Path.GetFileName(path).Equals("SketchUp.exe",StringComparison.OrdinalIgnoreCase)
            &&!Path.GetFileName(path).Equals("SketchUpAPI.dll",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 SketchUp.exe 或 SketchUpAPI.dll。");
        SketchUpApiPath.Text=Path.GetDirectoryName(path);RefreshApiStatus();
    }
    private async Task<bool> BrowseOutputAsync()
    {
        var extension=Extension;
        var name=string.IsNullOrWhiteSpace(_model.Name)?"模型":_model.Name;
        foreach(var invalid in Path.GetInvalidFileNameChars())name=name.Replace(invalid,'_');
        var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title="保存 "+(Format==ModelExportFormat.Fbx?"3ds Max 2024 FBX":"SketchUp 2026 SKP"),
            SuggestedFileName=name+"."+extension,DefaultExtension=extension,ShowOverwritePrompt=true,
            FileTypeChoices=new[]{new FilePickerFileType(extension.ToUpperInvariant()+" 模型") {Patterns=new[]{"*."+extension}}}});
        if(_closeRequested||_cancelRequested||file==null)return false;
        var path=file.TryGetLocalPath()??throw new InvalidOperationException("请选择本机输出文件。");
        if(!Path.GetExtension(path).Equals("."+extension,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("输出文件扩展名必须是 ."+extension+"。");
        OutputPath.Text=Path.GetFullPath(path);ToolTip.SetTip(OutputPath,OutputPath.Text);
        Status.Text="就绪";
        return true;
    }
    private async Task ExportCoreAsync()
    {
        var format=Format;
        var storeyId=ScopeSelector.SelectedIndex==1?(StoreySelector.SelectedItem as StoreyChoice)?.Id:null;
        if(ScopeSelector.SelectedIndex==1&&storeyId==null)throw new InvalidOperationException("请选择要导出的模型楼层。");
        var nearOrigin=NearOrigin.IsChecked==true;
        var apiDirectory=format==ModelExportFormat.Skp?ApiDirectory():null;
        if(format==ModelExportFormat.Skp&&!ModelExportService.IsSketchUp2026(apiDirectory))
            throw new InvalidOperationException("未找到 SketchUp 2026 API 或其依赖，请选择 SketchUp 2026 程序或 API 文件。");
        if(string.IsNullOrWhiteSpace(OutputPath.Text)&&!await BrowseOutputAsync())return;
        var path=OutputPath.Text!;
        Result=null;_runDirectory=null;
        var generation=++_exportGeneration;
        using var job=new CancellationTokenSource();_job=job;
        try {
            _progress.IsVisible=true;Status.Text="正在导出当前静态姿态";RefreshControls();
            var progress=new Progress<ModelExportProgress>(p=>{
                // Ignore queued notifications from a completed or superseded operation.
                Dispatcher.UIThread.Post(()=>{
                    if(_closed||generation!=_exportGeneration)return;
                    if(!string.IsNullOrWhiteSpace(p.RunDirectory))_runDirectory=p.RunDirectory;
                    ButtonLogFolder.IsEnabled=!Busy&&!string.IsNullOrWhiteSpace(_runDirectory);
                    if(ReferenceEquals(_job,job)&&!job.IsCancellationRequested)Status.Text=p.Message;
                });
            });
            var result=await Task.Run(()=>ModelExportService.ExportAsync(_model,path,format,storeyId,nearOrigin,apiDirectory,progress,job.Token));
            Result=result;_runDirectory=result.RunDirectory;
            Status.Text=$"已保存 · {result.Storeys:N0} 层 · {result.Elements:N0} 构件 · {result.Meshes:N0} 网格 · {result.Polygons:N0} 多边形 · {result.Bytes:N0} 字节\n{result.Path}";
        } finally {_job=null;}
    }
    private Task OpenFolderAsync(string? directory)
    {
        if(string.IsNullOrWhiteSpace(directory)||!Directory.Exists(directory))throw new DirectoryNotFoundException("目录不存在。");
        using var process=Process.Start(new ProcessStartInfo {FileName=Path.GetFullPath(directory),UseShellExecute=true});
        if(process==null)throw new InvalidOperationException("无法打开目录。");
        return Task.CompletedTask;
    }
}
