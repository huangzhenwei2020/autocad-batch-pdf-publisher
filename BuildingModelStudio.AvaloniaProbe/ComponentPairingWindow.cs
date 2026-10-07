using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ComponentPairingWindow : Window
{
    private static readonly string[] Roles={"Fixed","Frame","Panel","Hardware","Body"};
    internal readonly TextBox ResourceName=new(){MaxLength=100};
    internal readonly TextBox PartName=new(){MaxLength=100};
    internal readonly TextBox OriginX=new(){Text="0"},OriginY=new(){Text="0"},OriginZ=new(){Text="0"};
    internal readonly RadioButton AlongX=new(){Content="X",IsChecked=true},AlongY=new(){Content="Y"};
    internal readonly ComboBox PartRole=new(){ItemsSource=new[]{"固定部件","外框","面板 / 门扇","五金","柜体"},SelectedIndex=0};
    internal readonly ListBox Parts=new();
    internal readonly ModelViewport Volume=new(ModelViewport.PrepareScene(new BuildingVolume()));
    internal readonly DrawingViewCanvas PlanView=new();
    internal readonly ComponentShapePreview Isolated=new(){Height=190};
    internal readonly Button Publish;
    internal ComponentPlanSymbol? Plan {get;private set;}
    internal ComponentGlbInspection? Mesh {get;private set;}
    internal ComponentAsset? Published {get;private set;}
    private readonly ComponentAssetLibrary _library;
    private readonly List<ComponentPartDefinition> _parts=new();
    private byte[]? _meshBytes;
    private readonly TextBlock _dimensions=new(),_state=new(){Text="未载入来源",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _partSize=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _code=new(),_binding=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Button _cad,_glb;
    private readonly Grid _body,_paired;
    private readonly Control _planRegion,_meshRegion;
    private readonly ComboBox _viewMode=new(){ItemsSource=new[]{"并排","二维","三维"},SelectedIndex=0,Width=96};
    private readonly CheckBox _bindPlan=new(){Content="绑定二维"};
    private readonly ComponentPlanOverlay _planOverlay;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _source;
    private bool _loading,_busy,_closed,_dirty,_closeAllowed,_confirming;
    private string? _assetId;
    private int _revision;
    private string? _publishedSignature;
    private int _planGeneration,_alignGeneration;
    private ComponentCatalogRecord? _record;
    private readonly ComponentCatalog _catalog;

    internal ComponentPairingWindow(ComponentAssetLibrary library,ComponentCatalogRecord? record=null)
    {
        _catalog=new ComponentCatalog(library.Root);_record=record;
        _library=library;Title="配对编辑 · 新构件";Width=1500;Height=960;MinWidth=960;MinHeight=680;
        Background=new SolidColorBrush(Color.Parse("#101923"));WindowStartupLocation=WindowStartupLocation.CenterOwner;
        ComponentUi.Theme(this);
        var root=new Grid {RowDefinitions=new("52,52,*,64"),Margin=new Thickness(10),RowSpacing=8};
        var top=new Grid {ColumnDefinitions=new("*,Auto")};
        top.Children.Add(new TextBlock {Text="部件配对",FontSize=20,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        Grid.SetColumn(_dimensions,1);top.Children.Add(_dimensions);_dimensions.VerticalAlignment=VerticalAlignment.Center;root.Children.Add(top);
        var toolbar=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
        _cad=ComponentUi.Tool("folder-open","导入 CAD 符号",()=>_=BrowseAsync(true));
        _glb=ComponentUi.Tool("box","载入 GLB",()=>_=BrowseAsync(false));toolbar.Children.Add(_cad);toolbar.Children.Add(_glb);
        toolbar.Children.Add(ComponentUi.Tool("maximize","适合视图",()=>{PlanView.Fit();Volume.ResetView();}));toolbar.Children.Add(_viewMode);
        toolbar.Children.Add(ComponentUi.Tool("folder-open","打开草稿",()=>_=OpenDraftUiAsync()));
        toolbar.Children.Add(_bindPlan);ToolTip.SetTip(_bindPlan,"点选关联的 CAD 线；再点取消关联");
        _planOverlay=new ComponentPlanOverlay(PlanView);_bindPlan.IsCheckedChanged+=(_,_)=>{PlanView.PickRequested=_bindPlan.IsChecked==true?BindPlanAt:null;};
        Grid.SetRow(toolbar,1);root.Children.Add(toolbar);
        _body=new Grid {ColumnDefinitions=new("220,*,350"),ColumnSpacing=8};Grid.SetRow(_body,2);root.Children.Add(_body);
        var left=new Grid {RowDefinitions=new("*,Auto")};left.Children.Add(Parts);
        var single=new StackPanel {Spacing=6,Margin=new Thickness(10)};single.Children.Add(new TextBlock {Text="选中部件预览",FontWeight=FontWeight.SemiBold});single.Children.Add(Isolated);single.Children.Add(_partSize);
        Grid.SetRow(single,1);left.Children.Add(single);_body.Children.Add(ComponentUi.Region("部件",left));
        _paired=new Grid {ColumnDefinitions=new("*,*"),ColumnSpacing=8};
        _planRegion=ComponentUi.Region("CAD 平面",new Grid {Children={PlanView,_planOverlay}});_meshRegion=ComponentUi.Region("三维配对",Volume);
        _paired.Children.Add(_planRegion);Grid.SetColumn(_meshRegion,1);_paired.Children.Add(_meshRegion);Grid.SetColumn(_paired,1);_body.Children.Add(_paired);
        var fields=new StackPanel {Spacing=8,Margin=new Thickness(12)};
        ComponentUi.Field(fields,"资源名称",ResourceName);ComponentUi.Field(fields,"编号",_code);
        fields.Children.Add(new Border {Height=1,Background=ComponentUi.Edge});
        fields.Children.Add(new TextBlock {Text="三维基点 · mm",FontWeight=FontWeight.SemiBold});
        ComponentUi.Field(fields,"X",OriginX);ComponentUi.Field(fields,"Y",OriginY);ComponentUi.Field(fields,"Z",OriginZ);
        var axis=new StackPanel {Orientation=Orientation.Horizontal,Spacing=20,Children={AlongX,AlongY}};
        ComponentUi.Field(fields,"沿墙方向",axis);
        fields.Children.Add(ComponentUi.Tool("check","应用基点",()=>_=RefreshAlignedAsync()));
        fields.Children.Add(new Border {Height=1,Background=ComponentUi.Edge});
        ComponentUi.Field(fields,"部件名称",PartName);ComponentUi.Field(fields,"部件角色",PartRole);
        fields.Children.Add(_binding);fields.Children.Add(ComponentUi.Tool("check","应用部件",ApplyPart));
        fields.Children.Add(new Border {Height=1,Background=ComponentUi.Edge});
        fields.Children.Add(new TextBlock {Text="尺寸：固定规格\n运动：静态",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray});
        var inspector=ComponentUi.Region("属性 · 当前部件",ComponentUi.Scroll(fields));Grid.SetColumn(inspector,2);_body.Children.Add(inspector);
        var footer=new Grid {ColumnDefinitions=new("*,Auto,Auto,Auto"),ColumnSpacing=10};footer.Children.Add(_state);
        var cancel=new Button {Content="取消",Height=36,MinWidth=96};cancel.Click+=(_,_)=>Close();Grid.SetColumn(cancel,1);footer.Children.Add(cancel);
        var saveDraft=ComponentUi.Tool("save","保存草稿",()=>_=SaveDraftUiAsync());Grid.SetColumn(saveDraft,2);footer.Children.Add(saveDraft);
        Publish=ComponentUi.Tool("send","发布到图库",()=>_=PublishFromUiAsync());Publish.Background=new SolidColorBrush(Color.Parse("#078CE0"));Publish.IsEnabled=false;
        Grid.SetColumn(Publish,3);footer.Children.Add(Publish);Grid.SetRow(footer,3);root.Children.Add(footer);Content=root;
        Parts.ItemTemplate=new FuncDataTemplate<ComponentPartDefinition>((part,_)=>BuildPartRow(part));
        Parts.SelectionChanged+=(_,_)=>_=SelectPartAsync();
        Volume.SetDisplayMode(ModelViewport.DisplayMode.Shaded);
        Volume.ElementPicked+=id=>{if(id!=null)Parts.SelectedItem=_parts.FirstOrDefault(p=>p.MeshNodes.Any(n=>id=="node-"+n));};
        foreach(var input in new[]{ResourceName,PartName,OriginX,OriginY,OriginZ})input.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty&&!_loading)_dirty=true;};
        PartName.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty&&!_loading&&Parts.SelectedItem is ComponentPartDefinition part)part.Name=PartName.Text??"";};
        AlongX.IsCheckedChanged+=(_,_)=>{if(!_loading)_dirty=true;};AlongY.IsCheckedChanged+=(_,_)=>{if(!_loading)_dirty=true;};
        PartRole.PropertyChanged+=(_,e)=>{if(e.Property==ComboBox.SelectedIndexProperty&&!_loading){_dirty=true;if(Parts.SelectedItem is ComponentPartDefinition part)part.Role=Roles[Math.Max(0,PartRole.SelectedIndex)];}};
        _viewMode.SelectionChanged+=(_,_)=>ArrangeViews();SizeChanged+=(_,_)=>ArrangeViews();
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        Closing+=(_,e)=>{if(_busy){e.Cancel=true;_state.Text="正在保存资源";}else if(_dirty&&!_closeAllowed){e.Cancel=true;_=ConfirmCloseAsync();}};
        Closed+=(_,_)=>{_closed=true;_lifetime.Cancel();_source?.Cancel();};
        if(record!=null) {
            Title="补充三维 · "+record.Plan.Code;_cad.IsVisible=false;ResourceName.IsReadOnly=true;
            _assetId=record.AssetId;
            _revision=record.ModelRevision;
            SetPlan(ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(record.Plan)));_dirty=false;
            _state.Text="CAD 平面已入库 · 待补充三维模型";
        }
    }
    internal void SetLatestRevision(int revision)=>_revision=Math.Max(_revision,revision);
    private void SetPlan(ComponentPlanSymbol plan)
    {
        Plan=plan;_planOverlay.Plan=plan;_planOverlay.Indices=Array.Empty<int>();_planOverlay.InvalidateVisual();
        PlanView.SetView(ComponentPlanSymbols.Preview(plan));_code.Text=plan.Code;
        _loading=true;ResourceName.Text=plan.Name;_loading=false;
        _dimensions.Text=$"{plan.Width:0.###} × {plan.Height:0.###} mm";
    }
    internal async Task LoadExistingAsync(ComponentAsset asset)
    {
        if(_record==null||asset.Manifest.AssetId!=_record.AssetId||!asset.IsExternal)throw new InvalidDataException("模型不属于所选资源。");
        SetLatestRevision(asset.Manifest.Revision);
        var bytes=await Task.Run(()=>{
            var verified=ComponentAssetLibrary.Read(asset.PackagePath,_lifetime.Token);
            if(verified.Sha256!=asset.Sha256)throw new InvalidDataException("模型资源已变化，请刷新。");
            using var zip=System.IO.Compression.ZipFile.OpenRead(asset.PackagePath);
            using var source=zip.GetEntry("model.glb")!.Open();using var memory=new MemoryStream();source.CopyTo(memory);return memory.ToArray();
        });
        Mesh=await Task.Run(()=>ComponentGlbInspector.Inspect(bytes,_lifetime.Token));_meshBytes=bytes;
        _parts.Clear();_parts.AddRange(asset.Manifest.External.Parts.Select(p=>new ComponentPartDefinition {PartId=p.PartId,Name=p.Name,Role=p.Role,MeshNodes=p.MeshNodes.ToList(),
            PlanPrimitives=_record.PlanHash==_record.ModelPlanHash?p.PlanPrimitives.ToList():new()}));
        var frame=asset.Manifest.External.ModelFrame;
        _loading=true;OriginX.Text=frame.X.ToString("G17",CultureInfo.InvariantCulture);OriginY.Text=frame.Y.ToString("G17",CultureInfo.InvariantCulture);OriginZ.Text=frame.Z.ToString("G17",CultureInfo.InvariantCulture);
        AlongX.IsChecked=frame.AlongAxis=="X";AlongY.IsChecked=frame.AlongAxis=="Y";_loading=false;
        Parts.ItemsSource=_parts.ToArray();Parts.SelectedIndex=0;await RefreshAlignedAsync();UpdateState();_dirty=false;
    }
    private Control BuildPartRow(ComponentPartDefinition? part)
    {
        if(part==null)return new Border();
        var row=new Grid {ColumnDefinitions=new("60,*"),Height=76,Margin=new Thickness(4)};
        var preview=new ComponentShapePreview();row.Children.Add(preview);
        if(Mesh!=null){var ids=new HashSet<string>(part.MeshNodes.Select(n=>"node-"+n));_=preview.SetVolumeAsync(new BuildingVolume {Faces=Mesh.Volume.Faces.Where(f=>ids.Contains(f.ElementId)).ToList()});}
        var label=new TextBlock {Text=part.Name,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap};Grid.SetColumn(label,1);row.Children.Add(label);return row;
    }
    private void ArrangeViews()
    {
        var compact=Bounds.Width<1250;
        _body.ColumnDefinitions[0].Width=new GridLength(compact?170:220);_body.ColumnDefinitions[2].Width=new GridLength(compact?300:350);
        if(compact&&_viewMode.SelectedIndex==0)_viewMode.SelectedIndex=2;
        var mode=_viewMode.SelectedIndex;
        _planRegion.IsVisible=mode!=2;_meshRegion.IsVisible=mode!=1;
        _paired.ColumnDefinitions[0].Width=new GridLength(mode==2?0:1,mode==2?GridUnitType.Pixel:GridUnitType.Star);
        _paired.ColumnDefinitions[1].Width=new GridLength(mode==1?0:1,mode==1?GridUnitType.Pixel:GridUnitType.Star);
    }
    private async Task BrowseAsync(bool plan)
    {
        if(_busy)return;
        if((plan?Plan!=null:Mesh!=null)&&!await ConfirmReplaceAsync())return;
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title=plan?"选择 CAD 平面符号":"选择关闭姿态 GLB",AllowMultiple=false,
            FileTypeFilter=new[]{new FilePickerFileType(plan?"CAD 平面":"GLB 模型"){Patterns=new[]{plan?"*.wlplan.json":"*.glb"}}}});
        var path=files.FirstOrDefault()?.TryGetLocalPath();if(path==null)return;
        try{if(plan)await LoadPlanAsync(path);else await LoadMeshAsync(path);}
        catch(OperationCanceledException){}
        catch(Exception ex){if(!_closed)_state.Text="来源未载入："+ex.Message;}
    }
    internal async Task LoadPlanAsync(string path)
    {
        if(_record!=null)throw new InvalidOperationException("共享平面由 CAD 图库维护，无需再次导入。");
        if(_busy)return;
        SetBusy(true);
        try {
        var generation=++_planGeneration;
        var plan=await Task.Run(()=>ComponentPlanSymbols.Load(path),_lifetime.Token);if(_closed||generation!=_planGeneration)return;
        Plan=plan;_planOverlay.Plan=plan;_planOverlay.Indices=Array.Empty<int>();_planOverlay.InvalidateVisual();
        foreach(var part in _parts)part.PlanPrimitives.Clear();PlanView.SetView(ComponentPlanSymbols.Preview(plan));_code.Text=plan.Code;
        _loading=true;ResourceName.Text=plan.Name;_loading=false;_dirty=true;
        _dimensions.Text=$"{plan.Width:0.###} × {plan.Height:0.###} mm";UpdateState();
        } finally{SetBusy(false);}
    }
    internal async Task LoadMeshAsync(string path)
    {
        if(_busy)return;
        SetBusy(true);
        _source?.Cancel();var operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_source=operation;
        try {
            var loaded=await Task.Run(()=>{
                using var file=File.OpenRead(path);if(file.Length>ComponentGlbInspector.MaxBytes)throw new InvalidDataException("GLB 超过 32 MiB。");
                var bytes=new byte[checked((int)file.Length)];file.ReadExactly(bytes);return (bytes,mesh:ComponentGlbInspector.Inspect(bytes,operation.Token));
            },operation.Token);
            if(_closed||operation.IsCancellationRequested)return;
            Mesh=loaded.mesh;_meshBytes=loaded.bytes;_parts.Clear();
            foreach(var node in Mesh.Nodes)_parts.Add(new ComponentPartDefinition {Name=node.Name,MeshNodes=new(){node.Index}});
            _loading=true;OriginX.Text=Mesh.Volume.MinX.ToString("0.###",CultureInfo.InvariantCulture);
            OriginY.Text=((Mesh.Volume.MinY+Mesh.Volume.MaxY)/2).ToString("0.###",CultureInfo.InvariantCulture);OriginZ.Text=Mesh.Volume.MinZ.ToString("0.###",CultureInfo.InvariantCulture);
            AlongX.IsChecked=true;_loading=false;_dirty=true;Parts.ItemsSource=_parts.ToArray();Parts.SelectedIndex=0;
            await RefreshAlignedAsync();UpdateState();
        } finally{if(ReferenceEquals(_source,operation))_source=null;operation.Dispose();SetBusy(false);}
    }
    private ComponentSourceFrame Frame()
    {
        double Number(TextBox field){if(!double.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))throw new InvalidDataException("基点须为有限毫米数值。");return value;}
        return new ComponentSourceFrame {X=Number(OriginX),Y=Number(OriginY),Z=Number(OriginZ),AlongAxis=AlongY.IsChecked==true?"Y":"X"};
    }
    internal async Task RefreshAlignedAsync()
    {
        if(Mesh==null)return;
        var generation=++_alignGeneration;
        try {
            var frame=Frame();var mesh=Mesh;var scene=await Task.Run(()=>ModelViewport.PrepareScene(ComponentAssetLibrary.Align(mesh.Volume,frame)),_lifetime.Token);
            if(_closed||generation!=_alignGeneration||!ReferenceEquals(mesh,Mesh))return;Volume.SetScene(scene);Volume.ResetView();await SelectPartAsync();UpdateState();
        }catch(OperationCanceledException){}
        catch(Exception ex){if(!_closed)_state.Text="基点未应用："+ex.Message;}
    }
    private void ApplyPart()
    {
        if(Parts.SelectedItem is not ComponentPartDefinition part)return;
        if(string.IsNullOrWhiteSpace(PartName.Text)){_state.Text="部件名称不能为空。";return;}
        part.Name=PartName.Text.Trim();part.Role=Roles[Math.Max(0,PartRole.SelectedIndex)];_dirty=true;
        var index=Parts.SelectedIndex;Parts.ItemsSource=_parts.ToArray();Parts.SelectedIndex=index;UpdateState();
    }
    private async Task SelectPartAsync()
    {
        if(Parts.SelectedItem is not ComponentPartDefinition part||Mesh==null)return;
        _loading=true;PartName.Text=part.Name;PartRole.SelectedIndex=Math.Max(0,Array.IndexOf(Roles,part.Role));_loading=false;
        _binding.Text="三维节点："+string.Join("、",Mesh.Nodes.Where(n=>part.MeshNodes.Contains(n.Index)).Select(n=>n.Name))+"\n二维图元："+part.PlanPrimitives.Count;
        _planOverlay.Indices=part.PlanPrimitives;_planOverlay.InvalidateVisual();
        Volume.SelectElements(part.MeshNodes.Select(n=>"node-"+n));
        var ids=new HashSet<string>(part.MeshNodes.Select(n=>"node-"+n));
        try {
            var volume=ComponentAssetLibrary.Align(new BuildingVolume {Faces=Mesh.Volume.Faces.Where(f=>ids.Contains(f.ElementId)).ToList()},Frame());
            _partSize.Text=$"{volume.Width:0.###} × {volume.Depth:0.###} × {volume.Height:0.###} mm";await Isolated.SetVolumeAsync(volume);
        }catch(InvalidDataException ex){_state.Text=ex.Message;}
    }
    internal void BindPlanAt(PointModel point,double tolerance)
    {
        if(_busy||Plan==null||Parts.SelectedItem is not ComponentPartDefinition part)return;
        var index=ComponentPlanSymbols.Pick(Plan,point,tolerance);if(index<0)return;
        var owner=_parts.FirstOrDefault(p=>p.PlanPrimitives.Contains(index));
        if(owner!=null&&!ReferenceEquals(owner,part)){_state.Text="该图元已关联："+owner.Name;return;}
        if(!part.PlanPrimitives.Remove(index))part.PlanPrimitives.Add(index);_dirty=true;_planOverlay.Indices=part.PlanPrimitives;_planOverlay.InvalidateVisual();_=SelectPartAsync();
    }
    private void UpdateState()
    {
        Publish.IsEnabled=!_busy&&_source==null&&Plan!=null&&Mesh!=null;
        _state.Text=Plan==null||Mesh==null?"未载入完整来源":$"{_parts.Count} 个部件 · 固定规格 · 静态 · 未发布";
    }
    internal async Task<ComponentAsset> PublishAsync()
    {
        if(_busy||_source!=null||Plan==null||Mesh==null||_meshBytes==null)throw new InvalidOperationException("请先完成 CAD 与 GLB 来源载入。");
        if(string.IsNullOrWhiteSpace(PartName.Text))throw new InvalidDataException("部件名称不能为空。");
        ApplyPart();var frame=Frame();var plan=ComponentPlanSymbols.Load(ComponentPlanSymbols.Bytes(Plan));
        var fallback=plan.Category=="Furniture"?null:new OpeningTypeModel {Code=plan.Code,Kind=plan.Category=="Door"?"门":"窗",Width=plan.Width,Height=plan.Height,
            Sill=plan.Category=="Door"?0:900,ElevationType=plan.Category=="Door"?"普通门":"普通窗",HasInstallationGap=false,
            CustomCellLayout=BatchPdfPublisher.Models.DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{new BatchPdfPublisher.Models.DoorWindowLayoutCell {Right=plan.Width,Top=plan.Height,Opening="固定",Material="玻璃",IsDoor=plan.Category=="Door"}})};
        var parts=_parts.Select(p=>new ComponentPartDefinition {PartId=p.PartId,Name=p.Name,Role=p.Role,MeshNodes=p.MeshNodes.ToList(),PlanPrimitives=p.PlanPrimitives.ToList()}).ToArray();
        var name=ResourceName.Text??"";var bytes=_meshBytes;
        var signature=JsonSerializer.Serialize(new {name,plan,frame,parts,hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))});
        if(Published!=null&&signature==_publishedSignature){_dirty=false;return Published;}
        SetBusy(true);
        try {
            var identity=_assetId;var revision=_revision+1;
            if(_record!=null) {
                var current=_catalog.Get(_record.AssetId);
                if(current.Version!=_record.Version||current.PlanHash!=_record.PlanHash)throw new IOException("CAD 平面已更新，请重新打开图库记录。");
                if(ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(plan))!=_record.PlanHash)throw new InvalidDataException("平面与共享记录不一致。");
                name=_record.Plan.Name;
            }
            var asset=await Task.Run(()=>_library.SaveExternal(name,plan,bytes,frame,parts,fallback,_lifetime.Token,identity,revision));
            _revision=asset.Manifest.Revision;
            if(_record!=null)_record=await Task.Run(()=>_catalog.AttachModel(_record,asset.Manifest.Revision,asset.Sha256));
            if(_closed)throw new OperationCanceledException();Published=asset;_publishedSignature=signature;_assetId=asset.Manifest.AssetId;_revision=asset.Manifest.Revision;_dirty=false;_state.Text="已发布到图库："+asset.Manifest.Name;return asset;
        }finally{SetBusy(false);}
    }
    private async Task PublishFromUiAsync()
    {
        try{await PublishAsync();}
        catch(OperationCanceledException){}
        catch(Exception ex){if(!_closed)_state.Text="未发布："+ex.Message;}
    }
    private void SetBusy(bool busy)
    {
        _busy=busy;foreach(var control in new Control[]{_cad,_glb,ResourceName,Parts,PartName,PartRole,OriginX,OriginY,OriginZ,AlongX,AlongY})control.IsEnabled=!busy;
        Publish.IsEnabled=!busy&&_source==null&&Plan!=null&&Mesh!=null;
    }
    internal async Task SaveDraftAsync(string path)
    {
        if(_busy)throw new InvalidOperationException("正在处理资源，请稍后保存草稿。");
        if(Parts.SelectedItem is ComponentPartDefinition selected){selected.Name=PartName.Text??"";selected.Role=Roles[Math.Max(0,PartRole.SelectedIndex)];}
        var draft=new ComponentAuthorDraft {Name=ResourceName.Text??"",Plan=Plan,ModelBytes=_meshBytes,OriginX=OriginX.Text??"",OriginY=OriginY.Text??"",OriginZ=OriginZ.Text??"",
            AlongAxis=AlongY.IsChecked==true?"Y":"X",BasedOnAssetId=_assetId,BasedOnRevision=_revision,Parts=_parts};
        SetBusy(true);
        try{await Task.Run(()=>ComponentAssetLibrary.SaveDraft(path,draft,_lifetime.Token));_dirty=false;_state.Text="草稿已保存";}
        finally{SetBusy(false);}
    }
    internal async Task LoadDraftAsync(string path)
    {
        if(_busy)return;SetBusy(true);
        try {
            var loaded=await Task.Run(()=>{var d=ComponentAssetLibrary.LoadDraft(path,_lifetime.Token);return (d,mesh:d.ModelBytes==null?null:ComponentGlbInspector.Inspect(d.ModelBytes,_lifetime.Token));});
            if(_record!=null&&(loaded.d.BasedOnAssetId!=_record.AssetId||loaded.d.Plan==null||ComponentCatalog.Hash(ComponentPlanSymbols.Bytes(loaded.d.Plan))!=_record.PlanHash))
                throw new InvalidDataException("草稿不属于此资源或 CAD 平面已更新。");
            if(_closed)return;_source?.Cancel();_planGeneration++;_loading=true;
            try {
                Plan=loaded.d.Plan;Mesh=loaded.mesh;_meshBytes=loaded.d.ModelBytes;_parts.Clear();_parts.AddRange(loaded.d.Parts);
                _assetId=loaded.d.BasedOnAssetId;_revision=Math.Max(_revision,loaded.d.BasedOnRevision);Published=null;
                ResourceName.Text=loaded.d.Name;OriginX.Text=loaded.d.OriginX;OriginY.Text=loaded.d.OriginY;OriginZ.Text=loaded.d.OriginZ;
                AlongX.IsChecked=loaded.d.AlongAxis=="X";AlongY.IsChecked=loaded.d.AlongAxis=="Y";
                _code.Text=Plan?.Code??"";_dimensions.Text=Plan==null?"":$"{Plan.Width:0.###} × {Plan.Height:0.###} mm";
                _planOverlay.Plan=Plan;PlanView.SetView(Plan==null?null:ComponentPlanSymbols.Preview(Plan));Parts.ItemsSource=_parts.ToArray();Parts.SelectedIndex=_parts.Count>0?0:-1;
                if(Mesh==null){Volume.SetScene(ModelViewport.PrepareScene(new BuildingVolume()));await Isolated.SetVolumeAsync(new BuildingVolume());}
                else await RefreshAlignedAsync();
            } finally{_loading=false;}
            _dirty=false;_state.Text="草稿已载入";
        }finally{SetBusy(false);}
    }
    private async Task<bool> SaveDraftUiAsync()
    {
        var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {Title="保存配对草稿",SuggestedFileName=(Plan?.Code??"构件")+".wlodraft",DefaultExtension="wlodraft"});
        var path=file?.TryGetLocalPath();if(path==null)return false;
        try{await SaveDraftAsync(path);return true;}catch(OperationCanceledException){return false;}catch(Exception ex){_state.Text="草稿未保存："+ex.Message;return false;}
    }
    private async Task OpenDraftUiAsync()
    {
        if(_busy||!await ConfirmReplaceAsync())return;
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title="打开配对草稿",AllowMultiple=false,FileTypeFilter=new[]{new FilePickerFileType("配对草稿"){Patterns=new[]{"*.wlodraft"}}}});
        var path=files.FirstOrDefault()?.TryGetLocalPath();if(path==null)return;
        try{await LoadDraftAsync(path);}catch(OperationCanceledException){}catch(Exception ex){_state.Text="草稿未载入："+ex.Message;}
    }
    private async Task ConfirmCloseAsync()
    {
        if(_confirming)return;
        if(await ConfirmReplaceAsync()){_closeAllowed=true;Close();}
    }
    private async Task<bool> ConfirmReplaceAsync()
    {
        if(!_dirty)return true;
        if(_confirming)return false;
        _confirming=true;
        try {
        var dialog=new Window {Title="未发布的修改",Width=420,Height=160,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Background};
        ComponentUi.Theme(dialog);
        var panel=new StackPanel {Margin=new Thickness(16),Spacing=16};panel.Children.Add(new TextBlock {Text="放弃未发布的修改？"});
        var actions=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12};var back=new Button {Content="返回",Height=36};var discard=new Button {Content="放弃",Height=36};var save=new Button {Content="保存草稿",Height=36};
        back.Click+=(_,_)=>dialog.Close(0);discard.Click+=(_,_)=>dialog.Close(1);save.Click+=(_,_)=>dialog.Close(2);
        actions.Children.Add(back);actions.Children.Add(discard);actions.Children.Add(save);panel.Children.Add(actions);dialog.Content=panel;
        var choice=await dialog.ShowDialog<int>(this);
        return choice==1||(choice==2&&await SaveDraftUiAsync());
        }finally{_confirming=false;}
    }
    internal void CloseForCheck(){_closeAllowed=true;Close();}
}
