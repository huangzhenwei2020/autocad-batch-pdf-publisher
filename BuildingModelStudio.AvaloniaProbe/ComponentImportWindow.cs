using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ComponentImportWindow : Window
{
    private readonly ComponentAssetLibrary _library;
    private readonly ComponentCatalog _catalog;
    private ComponentCatalogRecord? _record;
    private readonly ComboBox _mode=new();
    private readonly TextBox _name=new(){MaxLength=100},_width=new(),_depth=new(),_height=new(),_x=new(){Text="0"},_y=new(){Text="0"},_z=new(){Text="0"},_gap=new(){Text="1000"};
    private readonly TextBox _code=new(){MaxLength=64};
    private readonly ComboBox _unit=new(){ItemsSource=new[]{"毫米","厘米","米"},SelectedIndex=0},_up=new(){ItemsSource=new[]{"Z 向上","Y 向上"},SelectedIndex=0},_rotation=new(){ItemsSource=new[]{"0°","90°","180°","270°"},SelectedIndex=0},_direction=new(){ItemsSource=new[]{"四向一起","正立面","背立面","左立面","右立面"},SelectedIndex=0};
    private readonly TextBlock _status=new(){Text="选择 FBX 或 GLB，载入后先核对尺寸和方向。",TextWrapping=TextWrapping.Wrap},_size=new(){TextWrapping=TextWrapping.Wrap},_file=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TabControl _tabs=new();
    private readonly ModelViewport _viewport=new(ModelViewport.PrepareScene(new BuildingVolume()));
    private readonly ProgressBar _progress=new(){IsIndeterminate=true,Height=5,IsVisible=false};
    private readonly CheckBox _titles=new(){Content="标注立面名称",IsChecked=true};
    private readonly CheckBox _dimensions=new(){Content="标注总宽和总高",IsChecked=true};
    private readonly Button _publish,_cad,_apply;
    private CancellationTokenSource? _operation;
    private ComponentImportService.Result? _base,_result;
    private bool _busy,_closed,_changing,_published;
    private string? _path,_basePath;
    internal ComponentImportWindow(ComponentAssetLibrary library,ComponentCatalogRecord? record=null)
    {
        _library=library;_catalog=new ComponentCatalog(library.Root);_record=record;
        Title=record==null?"导入三维 · 家具 / 设备":"补充三维与四向立面 · "+record.Plan.Name;
        Width=1180;Height=820;MinWidth=820;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(Color.Parse("#101923"));ComponentUi.Theme(this);
        var root=new Grid {RowDefinitions=new("Auto,*,Auto"),Margin=new Thickness(14),RowSpacing=10};
        var top=new WrapPanel {Orientation=Orientation.Horizontal};top.Children.Add(ComponentUi.Tool("folder-open","选择 FBX / GLB",()=>_=Choose()));top.Children.Add(_file);root.Children.Add(top);
        var body=new Grid {ColumnDefinitions=new("*,310"),ColumnSpacing=12};Grid.SetRow(body,1);root.Children.Add(body);body.Children.Add(_tabs);
        _viewport.SetDisplayMode(ModelViewport.DisplayMode.Shaded);_tabs.ItemsSource=new[]{new TabItem {Header="三维",Content=_viewport}};
        var fields=new StackPanel {Spacing=8,Margin=new Thickness(8)};
        _name.Text=record?.Plan.Name;_name.IsReadOnly=record!=null;ComponentUi.Field(fields,"资源名称",_name);
        _code.Text=record?.Plan.Code??NextCode();_code.IsReadOnly=record!=null;ComponentUi.Field(fields,"资源编号",_code);
        _mode.ItemsSource=record==null?new[]{"新建家具 / 设备"}:new[]{"新建家具 / 设备","更新选中资源"};_mode.SelectedIndex=record==null?0:1;
        ComponentUi.Field(fields,"入库方式",_mode);
        _mode.SelectionChanged+=(_,_)=>{if(_changing)return;_record=_mode.SelectedIndex==1?record:null;_name.IsReadOnly=_code.IsReadOnly=_record!=null;_code.Text=_record?.Plan.Code??NextCode();_name.Text=_record?.Plan.Name??(_path==null?"":Path.GetFileNameWithoutExtension(_path));if(_publish!=null)_publish.Content=_record==null?"存入共用图库":"更新该资源的三维";};
        ComponentUi.Field(fields,"来源单位（FBX 请核对）",_unit);ComponentUi.Field(fields,"来源向上方向",_up);
        fields.Children.Add(ComponentUi.Tool("rotate-ccw","按来源设置重新读取",()=>_=ReadSource()));
        fields.Children.Add(new TextBlock {Text="模型轴向尺寸 · mm",FontWeight=FontWeight.SemiBold});
        ComponentUi.Field(fields,"宽 X",_width);ComponentUi.Field(fields,"深 Y",_depth);ComponentUi.Field(fields,"高 Z",_height);ComponentUi.Field(fields,"旋转模型 / 正面方向",_rotation);
        _apply=ComponentUi.Tool("check","应用尺寸并重新预览",()=>_=Apply());fields.Children.Add(_apply);fields.Children.Add(_size);
        fields.Children.Add(new TextBlock {Text="入库基点 · mm",FontWeight=FontWeight.SemiBold});ComponentUi.Field(fields,"X",_x);ComponentUi.Field(fields,"Y",_y);ComponentUi.Field(fields,"Z",_z);
        ComponentUi.Field(fields,"生成方向",_direction);ComponentUi.Field(fields,"CAD 排布间距 mm",_gap);fields.Children.Add(_titles);fields.Children.Add(_dimensions);
        fields.Children.Add(new TextBlock {Text="立面输出为可编辑 CAD 线，分别成块；默认消隐并去掉共面三角边。图库资源为静态几何，尚不放入建筑项目。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightSlateGray});
        var inspector=ComponentUi.Scroll(fields);Grid.SetColumn(inspector,1);body.Children.Add(inspector);
        var bottom=new StackPanel {Spacing=6};bottom.Children.Add(_progress);bottom.Children.Add(_status);
        var actions=new WrapPanel {Orientation=Orientation.Horizontal};
        _publish=ComponentUi.Tool("save",record==null?"存入共用图库":"更新该资源的三维",()=>_=Publish());actions.Children.Add(_publish);
        _cad=ComponentUi.Tool("send","生成 CAD 立面",()=>_=SendCad());actions.Children.Add(_cad);
        actions.Children.Add(ComponentUi.Tool("rotate-ccw","取消处理 / 关闭",()=>{if(_busy)_operation?.Cancel();else Close();}));bottom.Children.Add(actions);Grid.SetRow(bottom,2);root.Children.Add(bottom);Content=root;
        foreach(var input in new[]{_width,_depth,_height})input.TextChanged+=(_,_)=>{if(!_changing){_result=null;UpdateButtons();_status.Text="尺寸已修改，请应用后查看四向立面。";}};
        _rotation.SelectionChanged+=(_,_)=>{if(!_changing){_result=null;UpdateButtons();}};
        foreach(var combo in new[]{_unit,_up})combo.SelectionChanged+=(_,_)=>{if(!_changing){_result=null;_base=null;UpdateButtons();_status.Text="来源单位或方向已变化，请重新读取模型。";}};
        Closed+=(_,_)=>{_closed=true;_operation?.Cancel();if(_basePath!=null)try{File.Delete(_basePath);}catch(IOException){}};
        UpdateButtons();
    }
    private string NextCode(){var codes=_catalog.Load(true).Records.Select(r=>r.Plan.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);var n=1;while(codes.Contains("F"+n.ToString("D3")))n++;return "F"+n.ToString("D3");}
    private void UpdateButtons(){_publish.IsEnabled=_cad.IsEnabled=!_busy&&_result!=null;_apply.IsEnabled=!_busy&&_base!=null;_progress.IsVisible=_busy;foreach(var field in new Control[]{_mode,_unit,_up,_rotation,_width,_depth,_height,_x,_y,_z,_name,_code,_direction,_gap,_titles,_dimensions})field.IsEnabled=!_busy;_mode.IsEnabled=!_busy&&!_published;}
    private async Task Run(Func<CancellationToken,Task> action)
    {
        if(_busy)return;_busy=true;_operation=new();UpdateButtons();
        try{await action(_operation.Token);}catch(OperationCanceledException){_status.Text="已停止本次处理；已经完成的保存或落图以回执为准。";}catch(Exception ex){_status.Text=ex.Message;}
        finally{_busy=false;_operation.Dispose();_operation=null;if(!_closed)UpdateButtons();}
    }
    private async Task Choose()
    {
        if(_busy)return;
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title="选择静态三维模型",AllowMultiple=false,FileTypeFilter=new[]{new FilePickerFileType("FBX / GLB"){Patterns=new[]{"*.fbx","*.glb"}}}});
        var path=files.FirstOrDefault()?.TryGetLocalPath();if(path==null)return;_path=path;_file.Text=Path.GetFileName(path);
        if(_record==null)_name.Text=Path.GetFileNameWithoutExtension(path);
        _unit.SelectedIndex=Path.GetExtension(path).Equals(".glb",StringComparison.OrdinalIgnoreCase)?2:0;_up.SelectedIndex=_unit.SelectedIndex==2?1:0;
        await ReadSource();
    }
    private void Progress(string text)=>Dispatcher.UIThread.Post(()=>{if(!_closed)_status.Text=text;});
    private async Task ReadSource()
    {
        if(_path==null)return;
        await Run(async token=>
        {
            _result=null;_base=null;var result=await ComponentImportService.LoadAsync(_path,new[]{1d,10d,1000d}[_unit.SelectedIndex],_up.SelectedIndex==0?"Z":"Y",0,token,Progress);
            if(_closed)return;_base=result;
            if(_basePath!=null)File.Delete(_basePath);
            var session=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WanLuo","ComponentImports");Directory.CreateDirectory(session);
            _basePath=Path.Combine(session,Guid.NewGuid().ToString("N")+".glb");await File.WriteAllBytesAsync(_basePath,result.Glb,token);
            _changing=true;_width.Text=result.Mesh.Volume.Width.ToString("0.###",CultureInfo.InvariantCulture);_depth.Text=result.Mesh.Volume.Depth.ToString("0.###",CultureInfo.InvariantCulture);_height.Text=result.Mesh.Volume.Height.ToString("0.###",CultureInfo.InvariantCulture);_rotation.SelectedIndex=0;_changing=false;
            await Display(result);
        });
    }
    private static double Number(TextBox field){if(!double.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||!double.IsFinite(n))throw new ArgumentException("请输入有效的毫米尺寸或基点。");return n;}
    private async Task Apply()
    {
        if(_base==null||_basePath==null)return;
        await Run(async token=>{_result=null;var v=_base.Mesh.Volume;
            var result=await ComponentImportService.LoadAsync(_basePath,1000,"Y",_rotation.SelectedIndex*90,token,Progress,Number(_width)/v.Width,Number(_depth)/v.Depth,Number(_height)/v.Height);if(!_closed)await Display(result);});
    }
    private async Task Display(ComponentImportService.Result result)
    {
        var scene=await Task.Run(()=>ModelViewport.PrepareScene(result.Mesh.Volume));if(_closed)return;
        _result=result;_viewport.SetScene(scene);_viewport.ResetView();
        foreach(var tab in _tabs.Items.OfType<TabItem>())if(ReferenceEquals(tab.Content,_viewport))tab.Content=null;
        _tabs.ItemsSource=null;var tabs=new List<TabItem>{new() {Header="三维",Content=_viewport}};
        foreach(var view in result.Projections.Views){var canvas=new DrawingViewCanvas();canvas.SetView(view);tabs.Add(new TabItem {Header=view.Title.Split('·').Last().Trim(),Content=canvas});}
        _tabs.ItemsSource=tabs;var v=result.Mesh.Volume;_size.Text=$"实际范围：{v.Width:0.#} × {v.Depth:0.#} × {v.Height:0.#} mm";
        _x.Text=v.MinX.ToString("0.###",CultureInfo.InvariantCulture);_y.Text=v.MinY.ToString("0.###",CultureInfo.InvariantCulture);_z.Text=v.MinZ.ToString("0.###",CultureInfo.InvariantCulture);
        _status.Text="四向立面已生成，请检查正面和尺寸。FBX 首阶段仅静态几何与基础颜色，不导入外部纹理及动画。";
    }
    private async Task Publish()
    {
        var result=_result;if(result==null)return;
        await Run(async token=>
        {
            var name=_name.Text?.Trim();if(string.IsNullOrWhiteSpace(name))throw new ArgumentException("请输入资源名称。");
            var frame=new ComponentSourceFrame {X=Number(_x),Y=Number(_y),Z=Number(_z),AlongAxis="X"};
            var v=result.Mesh.Volume;var dx=v.MinX-frame.X;var dy=v.MinY-frame.Y;
            var plan=_record?.Plan??new ComponentPlanSymbol {SchemaVersion=2,Code=_code.Text?.Trim(),Name=name,Category="Furniture",Width=v.Width,Height=v.Height,Depth=v.Depth,
                Primitives=result.Projections.Plan.Lines.Select(l=>new ComponentPlanPrimitive {Kind="Line",X1=l.X1+dx,Y1=l.Y1+dy,X2=l.X2+dx,Y2=l.Y2+dy}).ToList()};
            if(Math.Abs(v.Width-plan.Width)>1||Math.Abs(v.Height-plan.Height)>1||(plan.Category=="Furniture"&&Math.Abs(v.Depth-plan.Depth)>1))throw new IOException("三维尺寸与该 CAD 图库记录不符，请修改尺寸后重新预览。不会修改原 CAD 平面。");
            var parts=result.Mesh.Nodes.Select(n=>new ComponentPartDefinition {Name=n.Name,Role="Fixed",MeshNodes=new(){n.Index}}).ToArray();
            OpeningTypeModel? fallback=plan.Category=="Furniture"?null:new OpeningTypeModel {Code=ComponentPlanSymbols.ReferenceCode(plan),Kind=plan.Category=="Door"?"门":"窗",Width=plan.Width,Height=plan.Height,Sill=plan.Category=="Door"?0:900};
            var existing=_record;
            if(existing!=null){var current=_catalog.Get(existing.AssetId);if(current.Version!=existing.Version||current.PlanHash!=existing.PlanHash)throw new IOException("该 CAD 图库记录已更新，请重新打开后补充三维。");}
            var revision=existing==null?1:_library.Load(token).Assets.Where(a=>a.Manifest.AssetId==existing.AssetId).Select(a=>a.Manifest.Revision).DefaultIfEmpty(existing.ModelRevision).Max()+1;
            var asset=await Task.Run(()=>_library.SaveExternal(name,plan,result.Glb,frame,parts,fallback,token,existing?.AssetId,revision),token);
            _record=existing==null?_catalog.ImportPair(plan,asset.Manifest.AssetId,revision,asset.Sha256):_catalog.AttachModel(existing,revision,asset.Sha256);
            _name.IsReadOnly=_code.IsReadOnly=true;_published=true;_publish.Content="更新该资源的三维";
            _changing=true;_mode.ItemsSource=new[]{"更新当前资源"};_mode.SelectedIndex=0;_changing=false;
            _status.Text="已保存到同一共用图库："+name+"。CAD 平面与三维使用相同资源身份。";
        });
    }
    private Task SendCad()=>Run(async token=>
    {
        var result=_result;if(result==null)return;
        var views=_direction.SelectedIndex==0?result.Projections.Views:new(){result.Projections.Views[_direction.SelectedIndex-1]};
        var job=new ComponentCadViews {CadProcessId=Program.CadProcessId,DrawingFingerprint=Program.CadFingerprint,DrawingPath=Program.CadDrawingPath,Name=_name.Text,Gap=Number(_gap),AddTitles=_titles.IsChecked==true,AddDimensions=_dimensions.IsChecked==true,Views=views};
        if(job.Gap<0)throw new ArgumentException("排布间距不能小于零。");
        foreach(var view in views)view.Title=(_name.Text??"构件")+" · "+view.Title.Split('·').Last().Trim();
        var file=job.Queue();_status.Text="请切回来源 CAD 图纸并指定插入点。";
        try{for(var i=0;i<600;i++){token.ThrowIfCancellationRequested();if(File.Exists(file+".result.txt")){_status.Text=await File.ReadAllTextAsync(file+".result.txt",token);return;}await Task.Delay(500,token);}}
        finally{if(!File.Exists(file+".result.txt"))await File.WriteAllTextAsync(file+".cancel","cancel");}
        _status.Text="尚未落图，请检查来源 CAD 图纸是否打开。";
    });
    internal async Task SmokeAsync(string path)
    {
        _path=path;_name.Text="合成导入家具";_unit.SelectedIndex=0;_up.SelectedIndex=0;await ReadSource();
        if(_result==null||_result.Projections.Views.Count!=4)throw new IOException("Import UI failed: "+_status.Text);
        _width.Text="2400";_height.Text="450";_rotation.SelectedIndex=1;await Apply();
        if(_result==null||Math.Abs(_result.Mesh.Volume.Depth-2400)>.1||Math.Abs(_result.Mesh.Volume.Height-450)>.1)throw new IOException("Resize/rotation UI failed: "+_status.Text);
        _x.Text=(_result.Mesh.Volume.MinX+300).ToString(CultureInfo.InvariantCulture);_y.Text=(_result.Mesh.Volume.MinY+200).ToString(CultureInfo.InvariantCulture);
        await Publish();var first=_catalog.Load().Records.SingleOrDefault();
        if(first==null||first.ModelRevision!=1||first.Plan.Category!="Furniture")throw new IOException("Shared catalog publication failed: "+_status.Text);
        var stored=_library.Load().Assets.Single().ExternalContent;
        if(Math.Abs(first.Plan.Primitives.Min(p=>Math.Min(p.X1,p.X2))-stored.Volume.MinX)>.01||Math.Abs(first.Plan.Primitives.Min(p=>Math.Min(p.Y1,p.Y2))-stored.Volume.MinY)>.01)throw new IOException("CAD/model insertion base differs.");
        await Publish();var second=_catalog.Get(first.AssetId);
        if(second.ModelRevision!=2||_catalog.Load().Records.Count!=1)throw new IOException("Shared revision update failed: "+_status.Text);
        for(var i=1;i<5;i++){_tabs.SelectedIndex=i;await Task.Delay(40);}
        _tabs.SelectedIndex=0;await Task.Delay(250);
        if(!_viewport.FrameRendered)throw new IOException("Import GPU preview missing.");
        Console.WriteLine("COMPONENT_IMPORT_UI_OK Fbx protectedWorker dimensions rotation fourPreviews GPU sharedCatalog sameId revisions sourceUnchanged");
    }
    internal async Task SmokeExistingAsync(string path)
    {
        var original=_record??throw new IOException("Existing catalog resource required.");var plan=original.Plan;var hash=original.PlanHash;
        if(_mode.SelectedIndex!=1||!_name.IsReadOnly||!_code.IsReadOnly)throw new IOException("Existing-resource mode unclear.");
        _path=path;await ReadSource();_width.Text=plan.Width.ToString(CultureInfo.InvariantCulture);_depth.Text=plan.Depth.ToString(CultureInfo.InvariantCulture);_height.Text=plan.Height.ToString(CultureInfo.InvariantCulture);await Apply();
        if(_result==null)throw new IOException("Existing resource import failed: "+_status.Text);
        _x.Text=(_result.Mesh.Volume.MinX-plan.Primitives.Min(p=>Math.Min(p.X1,p.X2))).ToString(CultureInfo.InvariantCulture);
        _y.Text=(_result.Mesh.Volume.MinY-plan.Primitives.Min(p=>Math.Min(p.Y1,p.Y2))).ToString(CultureInfo.InvariantCulture);
        await Publish();var current=_catalog.Get(original.AssetId);
        if(current.ModelRevision!=original.ModelRevision+1||current.PlanHash!=hash||_catalog.Load().Records.Count!=1)throw new IOException("Existing CAD plan/identity changed: "+_status.Text);
        Console.WriteLine("COMPONENT_IMPORT_EXISTING_OK sameAssetId originalCadPlanUnchanged modelRevisionUpdated");
    }
}
