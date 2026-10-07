using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ComponentSourceInspectionWindow : Window
{
    private readonly DrawingViewCanvas _plan=new();
    private readonly ModelViewport _volume=new(ModelViewport.PrepareScene(new BuildingVolume()));
    private readonly ListBox _nodes=new(){MinWidth=130};
    private readonly TextBlock _planInfo=new(){Text="CAD 符号：未选择",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _meshInfo=new(){Text="GLB 模型：未选择",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _status=new(){Text="未配对",TextWrapping=TextWrapping.Wrap};
    private readonly Button _report=new(){Height=32,IsEnabled=false};
    private CancellationTokenSource? _planLoad,_meshLoad;
    private bool _closed;
    internal ComponentPlanSymbol? Plan {get;private set;}
    internal ComponentGlbInspection? Mesh {get;private set;}
    internal string? PlanPath {get;private set;}
    internal string? MeshPath {get;private set;}
    internal ModelViewport Volume=>_volume;

    public ComponentSourceInspectionWindow()
    {
        Title="构件资源校验";Width=1280;Height=800;MinWidth=800;MinHeight=600;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(Color.Parse("#101923"));
        Button Tool(string icon,string label,Action action) {
            var button=new Button {Height=32,Content=new StackPanel {Orientation=Orientation.Horizontal,Spacing=6,Children={CreateIcon(icon),new TextBlock {Text=label}}}};
            ToolTip.SetTip(button,label);button.Click+=(_,_)=>action();return button;
        }
        var root=new Grid {RowDefinitions=new("Auto,Auto,*,Auto"),Margin=new Thickness(12),RowSpacing=8};
        var toolbar=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
        toolbar.Children.Add(Tool("folder-open","CAD 符号",()=>_ = BrowseAsync(true)));
        toolbar.Children.Add(Tool("box","GLB 模型",()=>_ = BrowseAsync(false)));
        toolbar.Children.Add(Tool("maximize","适合视图",()=>{_plan.Fit();_volume.ResetView();}));
        _report.Content=new StackPanel {Orientation=Orientation.Horizontal,Spacing=6,Children={CreateIcon("send"),new TextBlock {Text="校验报告"}}};
        ToolTip.SetTip(_report,"导出校验报告");_report.Click+=async(_,_)=>await ExportReportAsync();toolbar.Children.Add(_report);root.Children.Add(toolbar);
        var info=new Grid {ColumnDefinitions=new("*,*"),ColumnSpacing=12};info.Children.Add(_planInfo);Grid.SetColumn(_meshInfo,1);info.Children.Add(_meshInfo);Grid.SetRow(info,1);root.Children.Add(info);
        var workspace=new Grid {ColumnDefinitions=new("160,*,*"),ColumnSpacing=8};
        Control Region(string name,Control control) {
            var grid=new Grid {RowDefinitions=new("32,*")};grid.Children.Add(new TextBlock {Text=name,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center});
            Grid.SetRow(control,1);grid.Children.Add(control);return new Border {BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Color.Parse("#354B5D")),Padding=new Thickness(8),Child=grid};
        }
        workspace.Children.Add(Region("模型节点",_nodes));var plan=Region("CAD 平面",_plan);Grid.SetColumn(plan,1);workspace.Children.Add(plan);
        var mesh=Region("三维模型",_volume);Grid.SetColumn(mesh,2);workspace.Children.Add(mesh);Grid.SetRow(workspace,2);root.Children.Add(workspace);
        var footer=new Grid {ColumnDefinitions=new("*,Auto")};footer.Children.Add(_status);var close=Tool("square-x","关闭",Close);Grid.SetColumn(close,1);footer.Children.Add(close);Grid.SetRow(footer,3);root.Children.Add(footer);Content=root;
        _nodes.SelectionChanged+=(_,_)=>{if(_nodes.SelectedItem is ComponentGlbNode node)_volume.SelectElement("node-"+node.Index);};
        _nodes.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<ComponentGlbNode>((node,_)=>new TextBlock {Text=node==null?"":$"{node.Index} · {node.Name}\n{node.Triangles:N0} 面",TextWrapping=TextWrapping.Wrap});
        _volume.ElementPicked+=id=>{if(id!=null)_nodes.SelectedItem=Mesh?.Nodes.FirstOrDefault(n=>"node-"+n.Index==id);};
        SizeChanged+=(_,_)=>workspace.ColumnDefinitions[0].Width=new GridLength(Width<1000?130:160);
        Closed+=(_,_)=>{_closed=true;_planLoad?.Cancel();_meshLoad?.Cancel();};
    }
    private static Control CreateIcon(string name)
    {
        using var stream=AssetLoader.Open(new Uri("avares://万落建筑模型/Resources/Icons/"+name+".png"));
        return new Image {Source=new Bitmap(stream),Width=16,Height=16};
    }
    private async Task BrowseAsync(bool plan)
    {
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title=plan?"选择 CAD 构件平面符号":"选择静态 GLB 模型",AllowMultiple=false,
            FileTypeFilter=new[]{new FilePickerFileType(plan?"CAD 构件平面":"GLB 模型") {Patterns=new[]{plan?"*.wlplan.json":"*.glb"}}}});
        var path=files.FirstOrDefault()?.TryGetLocalPath();if(path==null)return;
        try {if(plan)await LoadPlanAsync(path);else await LoadMeshAsync(path);}
        catch(OperationCanceledException){}
        catch(Exception ex){if(!_closed)_status.Text="校验失败："+ex.Message;}
    }
    internal async Task LoadPlanAsync(string path)
    {
        if(_closed)return;
        _planLoad?.Cancel();var operation=new CancellationTokenSource();_planLoad=operation;
        try {
            var loaded=await Task.Run(()=>{operation.Token.ThrowIfCancellationRequested();var source=ComponentPlanSymbols.Load(path);return (source,view:ComponentPlanSymbols.Preview(source));},operation.Token);
            if(_closed||operation.IsCancellationRequested)return;
            Plan=loaded.source;PlanPath=Path.GetFullPath(path);_plan.SetView(loaded.view);
            _planInfo.Text=$"{Path.GetFileName(path)}\n{Plan.Code} · 洞口 {Plan.Width:0.###} × {Plan.Height:0.###} mm · {Plan.Primitives.Count} 图元 · 文字候选 {Plan.TextCandidates.Count}";RefreshReport();
        }finally{if(ReferenceEquals(_planLoad,operation))_planLoad=null;operation.Dispose();}
    }
    internal async Task LoadMeshAsync(string path)
    {
        if(_closed)return;
        _meshLoad?.Cancel();var operation=new CancellationTokenSource();_meshLoad=operation;
        try {
            var loaded=await Task.Run(()=>{var mesh=ComponentGlbInspector.Inspect(path,operation.Token);return (mesh,scene:ModelViewport.PrepareScene(mesh.Volume));},operation.Token);
            if(_closed||operation.IsCancellationRequested)return;
            Mesh=loaded.mesh;MeshPath=Path.GetFullPath(path);_volume.SetScene(loaded.scene);_volume.SetDisplayMode(ModelViewport.DisplayMode.Shaded);_volume.ResetView();_nodes.ItemsSource=Mesh.Nodes;
            _meshInfo.Text=$"{Path.GetFileName(path)}\n{Mesh.Volume.Width:0.###} × {Mesh.Volume.Depth:0.###} × {Mesh.Volume.Height:0.###} mm · {Mesh.Nodes.Count} 节点 · {Mesh.Triangles:N0} 面 · 材质 {Mesh.MaterialCount}";RefreshReport();
        }finally{if(ReferenceEquals(_meshLoad,operation))_meshLoad=null;operation.Dispose();}
    }
    private void RefreshReport()
    {
        _report.IsEnabled=Plan!=null&&Mesh!=null;
        if(Plan==null||Mesh==null){_status.Text="未配对";return;}
        var dx=Mesh.Volume.Width-Plan.Width;var dz=Mesh.Volume.Height-Plan.Height;
        _status.Text=$"洞口与网格：宽差 {dx:0.###} mm，高差 {dz:0.###} mm；模型最小点 ({Mesh.Volume.MinX:0.###}, {Mesh.Volume.MinY:0.###}, {Mesh.Volume.MinZ:0.###}) mm";
    }
    private async Task ExportReportAsync()
    {
        if(Plan==null||Mesh==null)return;
        var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {Title="导出构件资源校验报告",SuggestedFileName=Plan.Code+"-sources.json",DefaultExtension="json"});
        var path=file?.TryGetLocalPath();if(path==null)return;
        var report=new {SchemaVersion=1,Status="SourceInspectionOnly",PlanPath,MeshPath,Plan.Code,Plan.Width,Plan.Height,PrimitiveCount=Plan.Primitives.Count,
            MeshWidth=Mesh.Volume.Width,MeshDepth=Mesh.Volume.Depth,MeshHeight=Mesh.Volume.Height,Mesh.Triangles,Nodes=Mesh.Nodes.Select(n=>new {n.Index,n.Name,n.Triangles,n.Mirrored}).ToArray()};
        var target=Path.GetFullPath(path);var temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            if(string.Equals(target,PlanPath,StringComparison.OrdinalIgnoreCase)||string.Equals(target,MeshPath,StringComparison.OrdinalIgnoreCase)||!target.EndsWith(".json",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("报告不能覆盖源文件，且须使用 .json 扩展名。");
            await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));File.Move(temp,target,true);_status.Text="已导出校验报告："+path;
        }
        catch(Exception ex){_status.Text="报告未导出："+ex.Message;}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
