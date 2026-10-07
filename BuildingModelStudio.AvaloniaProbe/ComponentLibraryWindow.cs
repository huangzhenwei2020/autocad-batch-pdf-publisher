using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ComponentLibraryWindow : Window
{
    private sealed record Entry(string Name,string Code,string Category,OpeningTypeModel? Type,ComponentAsset? Asset,string Source,ComponentCatalogRecord? Record=null)
    {
        public override string ToString()=>Code+" · "+Name;
    }
    private readonly ComponentAssetLibrary _library;
    private readonly ComponentCatalog _catalog;
    private FileSystemWatcher? _watcher;
    private readonly Avalonia.Threading.DispatcherTimer _refreshTimer=new(){Interval=TimeSpan.FromMilliseconds(350)};
    private readonly SemaphoreSlim _reloadGate=new(1,1);
    private readonly BuildingModelDocument _model;
    private readonly string? _modelPath;
    private readonly List<Entry> _entries=new();
    internal readonly ListBox Assets=new();
    internal readonly TextBox Search=new(){PlaceholderText="搜索编号或名称",Height=36};
    private readonly ComboBox _source=new(){ItemsSource=new[]{"全部来源","内置 / 项目","公共库","项目资源"},SelectedIndex=0,Height=36};
    private readonly ComboBox _category=new(){ItemsSource=new[]{"全部","门","窗","家具"},SelectedIndex=0,Height=36};
    private readonly TextBlock _heading=new(){FontSize=20,FontWeight=FontWeight.SemiBold},_details=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _state=new(){TextWrapping=TextWrapping.Wrap};
    private readonly DrawingViewCanvas _plan=new();
    private readonly ModelViewport _volume=new(ModelViewport.PrepareScene(new BuildingVolume()));
    private readonly OpeningVolumePreview _parameterPreview=new(){Height=250};
    private readonly StackPanel _parts=new(){Orientation=Orientation.Horizontal,Spacing=8};
    internal readonly Button ProjectCopy,Export;
    private readonly Button _new,_import;
    private readonly TabControl _tabs=new();
    private readonly Grid _body;
    private readonly Button _furniture;
    private bool _listMode;
    private readonly CancellationTokenSource _lifetime=new();
    private bool _closed,_busy;
    private int _selectionGeneration;
    internal int ExternalCount=>_entries.Count(e=>e.Asset?.IsExternal==true);
    internal int CatalogCount=>_entries.Count(e=>e.Record!=null);
    internal void SelectCatalog(string id)=>Assets.SelectedItem=_entries.First(e=>e.Record?.AssetId==id);
    internal bool OpenParameterPlacement {get;private set;}
    internal ComponentLibraryWindow(BuildingModelDocument model,string? modelPath,ComponentAssetLibrary? library=null)
    {
        _model=model;_modelPath=modelPath;_library=library??new ComponentAssetLibrary(Environment.GetEnvironmentVariable("WANLUO_COMPONENT_LIBRARY_ROOT")??ComponentAssetLibrary.DefaultRoot);
        _catalog=new ComponentCatalog(_library.Root);
        Title="构件图库";Width=1500;Height=960;MinWidth=960;MinHeight=680;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#101923"));
        ComponentUi.Theme(this);
        var root=new Grid {RowDefinitions=new("56,*,64"),Margin=new Thickness(12),RowSpacing=8};
        var toolbar=new Grid {ColumnDefinitions=new("*,150,36,36,Auto,Auto"),ColumnSpacing=8};toolbar.Children.Add(Search);
        Grid.SetColumn(_source,1);toolbar.Children.Add(_source);
        var grid=ComponentUi.Tool("grid-3x3","",()=>SetListMode(false));ToolTip.SetTip(grid,"缩略图网格");Grid.SetColumn(grid,2);toolbar.Children.Add(grid);
        var list=ComponentUi.Tool("rows-2","",()=>SetListMode(true));ToolTip.SetTip(list,"列表");Grid.SetColumn(list,3);toolbar.Children.Add(list);
        _import=ComponentUi.Tool("rotate-ccw","刷新",()=>_=ReloadAsync());_new=ComponentUi.Tool("box","补充三维",()=>_=NewPairingAsync());
        Grid.SetColumn(_import,4);toolbar.Children.Add(_import);Grid.SetColumn(_new,5);toolbar.Children.Add(_new);root.Children.Add(toolbar);
        _body=new Grid {ColumnDefinitions=new("180,*,430"),ColumnSpacing=12};Grid.SetRow(_body,1);root.Children.Add(_body);
        var categories=new StackPanel {Spacing=8,Margin=new Thickness(8)};
        Button Category(string title,string icon,string value) {
            var button=ComponentUi.Tool(icon,title,()=>{_category.SelectedItem=value;});button.HorizontalContentAlignment=HorizontalAlignment.Left;return button;
        }
        categories.Children.Add(Category("全部构件","grid-3x3","全部"));categories.Children.Add(Category("门","door-open","门"));categories.Children.Add(Category("窗","app-window","窗"));
        _furniture=Category("家具","box","家具");_furniture.IsVisible=false;categories.Children.Add(_furniture);
        _body.Children.Add(ComponentUi.Region("分类",ComponentUi.Scroll(categories)));
        Assets.Background=Brushes.Transparent;Assets.BorderThickness=new Thickness(0);Assets.ItemsPanel=new FuncTemplate<Panel?>(()=>new WrapPanel {Orientation=Orientation.Horizontal});
        Assets.ItemTemplate=new FuncDataTemplate<Entry>((entry,_)=>AssetTile(entry));Grid.SetColumn(Assets,1);_body.Children.Add(Assets);
        var right=new StackPanel {Spacing=12,Margin=new Thickness(12)};right.Children.Add(_heading);right.Children.Add(_details);
        _plan.Height=270;_volume.Height=270;_volume.SetDisplayMode(ModelViewport.DisplayMode.Shaded);
        _tabs.ItemsSource=new[]{new TabItem {Header="平面",Content=_plan},new TabItem {Header="三维",Content=_volume}};right.Children.Add(_tabs);right.Children.Add(_parameterPreview);
        right.Children.Add(new Border {Height=1,Background=ComponentUi.Edge});right.Children.Add(new TextBlock {Text="部件",FontWeight=FontWeight.SemiBold});
        right.Children.Add(new ScrollViewer {Content=_parts,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled});
        Export=ComponentUi.Tool("send","导出资源包",()=>_=ExportAsync());right.Children.Add(Export);
        Export.IsVisible=false;
        var inspector=ComponentUi.Scroll(right);Grid.SetColumn(inspector,2);_body.Children.Add(inspector);
        var footer=new Grid {ColumnDefinitions=new("*,Auto,Auto,Auto"),ColumnSpacing=8};footer.Children.Add(_state);
        var close=new Button {Content="关闭",Height=36};close.Click+=(_,_)=>Close();Grid.SetColumn(close,1);footer.Children.Add(close);
        var parameters=ComponentUi.Tool("door-open","门窗选型 MM",()=>{OpenParameterPlacement=true;Close();});Grid.SetColumn(parameters,2);footer.Children.Add(parameters);
        ProjectCopy=ComponentUi.Tool("save","保存项目副本",()=>_=RetainFromUiAsync());Grid.SetColumn(ProjectCopy,3);footer.Children.Add(ProjectCopy);
        ProjectCopy.Background=new SolidColorBrush(Color.Parse("#078CE0"));Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
        Search.TextChanged+=(_,_)=>Filter();_source.SelectionChanged+=(_,_)=>Filter();_category.SelectionChanged+=(_,_)=>Filter();Assets.SelectionChanged+=(_,_)=>_=SelectionAsync();
        Closed+=(_,_)=>{_closed=true;_lifetime.Cancel();_watcher?.Dispose();_refreshTimer.Stop();};
        SizeChanged+=(_,_)=>{_body.ColumnDefinitions[0].Width=new GridLength(Bounds.Width<1200?140:180);_body.ColumnDefinitions[2].Width=new GridLength(Bounds.Width<1200?320:430);};
        Opened+=(_,_)=>_=ReloadAsync();
        Activated+=(_,_)=>{if(!_busy)_=ReloadAsync();};
        _refreshTimer.Tick+=(_,_)=>{_refreshTimer.Stop();if(!_closed&&!_busy)_=ReloadAsync();};
    }
    private Control AssetTile(Entry? entry)
    {
        if(entry==null)return new Border();
        var tile=new StackPanel {Spacing=6,Margin=new Thickness(10)};
        if(!_listMode&&entry.Asset?.ExternalContent is { } content){var shape=new ComponentShapePreview {Height=170};_=shape.SetVolumeAsync(content.Volume);tile.Children.Add(shape);}
        else if(!_listMode&&entry.Record!=null){var plan=new DrawingViewCanvas {Width=190,Height=170,IsHitTestVisible=false};plan.SizeChanged+=(_,_)=>plan.Fit();plan.SetView(ComponentPlanSymbols.Preview(entry.Record.Plan));tile.Children.Add(plan);}
        else if(!_listMode&&entry.Type!=null){var shape=new OpeningVolumePreview {Height=170};shape.SetType(entry.Type);tile.Children.Add(shape);}
        tile.Children.Add(new TextBlock {Text=entry.Code,FontSize=16,FontWeight=FontWeight.SemiBold});
        tile.Children.Add(new TextBlock {Text=entry.Name,TextWrapping=TextWrapping.Wrap});
        tile.Children.Add(new TextBlock {Text=entry.Record!=null?(entry.Record.IsModelCurrent&&entry.Asset!=null?"CAD / 三维已配对":entry.Record.ModelRevision>0?"三维待复核":"待补充三维"):entry.Asset?.IsExternal==true?"外部构件 · 固定规格 · 静态":"参数门窗",FontSize=12,Foreground=Brushes.LightGray});
        var size=entry.Asset?.Manifest.External;
        tile.Children.Add(new TextBlock {Text=entry.Record!=null?$"{entry.Record.Plan.Width:0.#} × {entry.Record.Plan.Height:0.#}":size!=null?$"{size.Width:0.#} × {size.Height:0.#}":$"{entry.Type?.Width:0.#} × {entry.Type?.Height:0.#}",FontSize=12});
        return new Border {Width=_listMode?double.NaN:212,Margin=new Thickness(0,0,8,8),BorderBrush=ComponentUi.Edge,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Child=tile};
    }
    private void SetListMode(bool list)
    {
        _listMode=list;Assets.ItemsPanel=new FuncTemplate<Panel?>(()=>list?new StackPanel():new WrapPanel {Orientation=Orientation.Horizontal});
        Assets.ItemTemplate=new FuncDataTemplate<Entry>((entry,_)=>AssetTile(entry));
    }
    internal async Task ReloadAsync()
    {
        try{await _reloadGate.WaitAsync(_lifetime.Token);}catch(OperationCanceledException){return;}
        var selectedId=(Assets.SelectedItem as Entry)?.Record?.AssetId;
        try {
            var loaded=await Task.Run(()=>{
                var common=_library.Load(_lifetime.Token);
                var catalog=_catalog.Load();
                // Migrate validated older paired resources once, preserving their stable identity.
                foreach(var asset in common.Assets.Where(a=>a.IsExternal).GroupBy(a=>a.Manifest.AssetId).Select(g=>g.OrderByDescending(a=>a.Manifest.Revision).First())) {
                    if(catalog.Records.Any(r=>r.AssetId==asset.Manifest.AssetId))continue;
                    try{_catalog.ImportPair(asset.ExternalContent.Plan,asset.Manifest.AssetId,asset.Manifest.Revision,asset.Sha256);}
                    catch(Exception ex) when(ex is IOException||ex is UnauthorizedAccessException){catalog.Errors.Add("旧资源关联："+ex.Message);}
                }
                var current=_catalog.Load();current.Errors.AddRange(catalog.Errors);
                var project=_modelPath!=null?new ComponentAssetLibrary(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_modelPath))!,"components")).Load(_lifetime.Token):new ComponentLibrarySnapshot();
                return (common,project,catalog:current);
            },_lifetime.Token);
            if(_closed)return;
            _entries.Clear();
            foreach(var entry in OpeningPlacementCatalog.Create(_model))_entries.Add(new(entry.Name,entry.Type.Code,entry.IsDoor?"门":"窗",entry.Type,null,entry.Source));
            void Add(ComponentLibrarySnapshot snapshot,string source) {
                foreach(var asset in snapshot.Assets.GroupBy(a=>a.Manifest.AssetId).Select(g=>g.OrderByDescending(a=>a.Manifest.Revision).First())) {
                    if(source=="公共库"&&loaded.catalog.Records.Any(r=>r.AssetId==asset.Manifest.AssetId))continue;
                    var type=asset.Manifest.OpeningType;var definition=asset.Manifest.External;
                    _entries.Add(new(asset.Manifest.Name,definition?.Code??type.Code,asset.Manifest.Category=="Furniture"?"家具":(type.Kind??"").Contains("门")?"门":"窗",type,asset,source));
                }
            }
            foreach(var record in loaded.catalog.Records) {
                var asset=record.IsModelCurrent?loaded.common.Assets.FirstOrDefault(a=>a.IsExternal&&a.Manifest.AssetId==record.AssetId&&a.Manifest.Revision==record.ModelRevision&&a.Sha256==record.ModelHash
                    &&ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(a.ExternalContent.Plan))==record.PlanHash):null;
                _entries.Add(new(record.Plan.Name,record.Plan.Code,record.Plan.Category=="Door"?"门":record.Plan.Category=="Window"?"窗":"家具",null,asset,"公共库",record));
            }
            Add(loaded.common,"公共库");Add(loaded.project,"项目资源");Filter();
            if(selectedId!=null&&Assets.ItemsSource is IEnumerable<Entry> visible)Assets.SelectedItem=visible.FirstOrDefault(e=>e.Record?.AssetId==selectedId)??Assets.SelectedItem;
            _furniture.IsVisible=_entries.Any(e=>e.Category=="家具");
            var errors=loaded.common.Errors.Concat(loaded.project.Errors).Concat(loaded.catalog.Errors).ToArray();_state.Text=errors.Length==0?$"{_entries.Count} 项资源 · 共享图库":$"{errors.Length} 个资源未载入：{errors[0]}";
            StartWatcher();
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closed)_state.Text="图库读取失败："+ex.Message;}
        finally{_reloadGate.Release();}
    }
    private void StartWatcher()
    {
        if(_watcher!=null)return;
        Directory.CreateDirectory(_catalog.RecordsPath);
        _watcher=new FileSystemWatcher(_library.Root,"*"){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite};
        void Changed(object? sender,FileSystemEventArgs e) {
            if(!e.FullPath.EndsWith(".json",StringComparison.OrdinalIgnoreCase)&&!e.FullPath.EndsWith(".wlopkg",StringComparison.OrdinalIgnoreCase))return;
            Avalonia.Threading.Dispatcher.UIThread.Post(()=>{if(!_closed){_refreshTimer.Stop();_refreshTimer.Start();}});
        }
        _watcher.Changed+=Changed;_watcher.Created+=Changed;_watcher.Deleted+=Changed;_watcher.Renamed+=(_,e)=>Changed(null,e);_watcher.EnableRaisingEvents=true;
    }
    private void Filter()
    {
        var selected=Assets.SelectedItem;var query=Search.Text?.Trim()??"";var category=_category.SelectedItem as string??"全部";var source=_source.SelectedItem as string??"全部来源";
        var filtered=_entries.Where(e=>(category=="全部"||e.Category==category)&&(source=="全部来源"||(source=="内置 / 项目"?e.Asset==null&&e.Record==null:e.Source==source))
            &&(e.Code+" "+e.Name).Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
        Assets.ItemsSource=filtered;Assets.SelectedItem=filtered.Contains(selected)?selected:filtered.FirstOrDefault();
    }
    private async Task SelectionAsync()
    {
        var generation=++_selectionGeneration;_parts.Children.Clear();
        if(Assets.SelectedItem is not Entry entry){_heading.Text="未选择资源";_details.Text="";ProjectCopy.IsEnabled=false;Export.IsEnabled=false;_tabs.IsVisible=false;_parameterPreview.IsVisible=false;return;}
        _heading.Text=entry.Code;ProjectCopy.IsEnabled=!_busy&&entry.Asset!=null&&_modelPath!=null;Export.IsEnabled=!_busy&&entry.Asset!=null;
        _new.IsEnabled=!_busy&&entry.Record!=null;
        _details.Text=entry.Name+"\n来源："+entry.Source+(entry.Asset!=null?$" · 修订 {entry.Asset.Manifest.Revision}":"");
        var content=entry.Asset?.ExternalContent;_tabs.IsVisible=content!=null;_parameterPreview.IsVisible=content==null;
        if(entry.Record!=null&&content==null) {
            _details.Text+="\n"+(entry.Record.ModelRevision>0?"平面已更新或模型缺失 · 三维待复核":"待补充三维模型");
            _tabs.IsVisible=true;_parameterPreview.IsVisible=false;_plan.SetView(ComponentPlanSymbols.Preview(entry.Record.Plan));
            _tabs.SelectedIndex=0;((TabItem)((IEnumerable<TabItem>)_tabs.ItemsSource!).Last()).IsEnabled=false;return;
        }
        ((TabItem)((IEnumerable<TabItem>)_tabs.ItemsSource!).Last()).IsEnabled=true;
        if(content==null){_parameterPreview.SetType(entry.Type);return;}
        _plan.SetView(ComponentPlanSymbols.Preview(content.Plan));
        var scene=await Task.Run(()=>ModelViewport.PrepareScene(content.Volume));if(_closed||generation!=_selectionGeneration)return;
        _volume.SetScene(scene);_volume.ResetView();
        foreach(var part in entry.Asset!.Manifest.External.Parts) {
            var panel=new StackPanel {Width=92,Spacing=4};var shape=new ComponentShapePreview {Height=90};panel.Children.Add(shape);
            panel.Children.Add(new TextBlock {Text=part.Name,TextWrapping=TextWrapping.Wrap,FontSize=12});
            var button=new Button {Content=panel,Padding=new Thickness(4)};ToolTip.SetTip(button,part.Name);
            button.Click+=(_,_)=>_volume.SelectElements(part.MeshNodes.Select(n=>"node-"+n));_parts.Children.Add(button);_=shape.SetVolumeAsync(content.PartVolume(part));
        }
    }
    private async Task NewPairingAsync()
    {
        if(Assets.SelectedItem is not Entry {Record:{ } record}){_state.Text="请先在 CAD 图库入库平面，再选择该记录补充三维。";return;}
        SetBusy(true);
        try {
            record=_catalog.Get(record.AssetId);
            var window=new ComponentPairingWindow(_library,record);
            var assets=await Task.Run(()=>_library.Load(_lifetime.Token).Assets.Where(a=>a.Manifest.AssetId==record.AssetId).ToArray());
            window.SetLatestRevision(assets.Select(a=>a.Manifest.Revision).DefaultIfEmpty(0).Max());
            var existing=assets.FirstOrDefault(a=>a.Manifest.Revision==record.ModelRevision&&a.Sha256==record.ModelHash);
            if(existing!=null)await window.LoadExistingAsync(existing);
            await window.ShowDialog(this);await ReloadAsync();
        }catch(Exception ex){_state.Text="未打开："+ex.Message;}finally{SetBusy(false);}
    }
    private static readonly FilePickerFileType PackageType=new("万落构件资源包"){Patterns=new[]{"*.wlopkg"}};
    private async Task ImportAsync()
    {
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title="导入构件资源包",AllowMultiple=false,FileTypeFilter=new[]{PackageType}});
        var path=files.FirstOrDefault()?.TryGetLocalPath();if(path==null)return;SetBusy(true);
        try{await Task.Run(()=>_library.Import(path,_lifetime.Token));await ReloadAsync();_state.Text="资源已导入图库";}
        catch(OperationCanceledException){}
        catch(Exception ex){if(!_closed)_state.Text="未导入："+ex.Message;}
        finally{SetBusy(false);}
    }
    private async Task ExportAsync()
    {
        if(Assets.SelectedItem is not Entry {Asset:{ } asset})return;
        var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {Title="导出构件资源包",SuggestedFileName=asset.Manifest.AssetId+".wlopkg",DefaultExtension="wlopkg",FileTypeChoices=new[]{PackageType}});
        var path=file?.TryGetLocalPath();if(path==null)return;SetBusy(true);
        try{await Task.Run(()=>_library.Export(asset,path,_lifetime.Token));_state.Text="资源包已导出";}
        catch(OperationCanceledException){}
        catch(Exception ex){if(!_closed)_state.Text="未导出："+ex.Message;}
        finally{SetBusy(false);}
    }
    internal async Task<ComponentAsset> RetainSelectedAsync()
    {
        if(Assets.SelectedItem is not Entry {Asset:{ } asset}||_modelPath==null)throw new InvalidOperationException("请先保存建筑模型并选择已入库资源。");
        return await Task.Run(()=>ComponentAssetLibrary.RetainInProject(asset,_modelPath,_lifetime.Token));
    }
    internal void SelectExternal(string code)=>Assets.SelectedItem=_entries.First(e=>e.Asset?.IsExternal==true&&e.Code==code);
    private async Task RetainFromUiAsync()
    {
        SetBusy(true);
        try{await RetainSelectedAsync();await ReloadAsync();_state.Text="已保存项目资源副本；未放置模型";}
        catch(OperationCanceledException){}
        catch(Exception ex){if(!_closed)_state.Text="未保存项目副本："+ex.Message;}
        finally{SetBusy(false);}
    }
    private void SetBusy(bool busy){_busy=busy;_new.IsEnabled=!busy&&Assets.SelectedItem is Entry {Record:not null};_import.IsEnabled=!busy;ProjectCopy.IsEnabled=!busy&&Assets.SelectedItem is Entry {Asset:not null}&&_modelPath!=null;Export.IsEnabled=!busy&&Assets.SelectedItem is Entry {Asset:not null};}
}
