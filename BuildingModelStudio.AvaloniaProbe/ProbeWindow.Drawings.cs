using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private sealed class DrawingItem
    {
        public ViewDefinitionModel Definition = new();
        public override string ToString()=>Definition.Title;
    }
    private readonly DrawingViewCanvas _drawingCanvas = new();
    private readonly ComboBox _drawingChooser = new() { MinWidth=180,MaxWidth=310,Height=32 };
    private readonly TextBlock _drawingInfo = new() { Text="滚轮缩放 · 拖动平移",Margin=new Thickness(12,8),TextWrapping=TextWrapping.Wrap };
    private List<ViewDefinitionModel> _drawingCatalogue = new();
    private readonly Dictionary<string,TreeViewItem> _drawingBrowserNodes=new();
    private string? _drawingId;
    private ViewDefinitionModel? _drawingDraft;
    private TextBox? _drawingTitle,_drawingScale,_drawingCut,_drawingDepth,_drawingYaw,_drawingPitch;
    private ComboBox? _drawingFloor,_drawingDirection,_drawingCutAxis,_drawingSign;
    private CheckBox? _drawingPerspective;
    private bool _refreshingDrawings,_buildingDrawingProperties;
    private int _drawingGeneration;
    private Task _drawingPreviewTask=Task.CompletedTask;
    private string? _drawingInputKey;

    private Control BuildDrawingTab()
    {
        var root=new Grid { RowDefinitions=new RowDefinitions("Auto,*,Auto") };
        var toolbar=new WrapPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(8,4) };
        toolbar.Children.Add(_drawingChooser);
        var kinds=new[] { ViewKind.Plan,ViewKind.Elevation,ViewKind.Section,ViewKind.Axonometric,ViewKind.Schedule,ViewKind.OpeningElevation };
        var addKind=new ComboBox { ItemsSource=kinds.Select(DrawingViewCatalogue.KindName).ToArray(),SelectedIndex=0,Height=32,Width=100,Margin=new Thickness(6,0) };
        toolbar.Children.Add(addKind);
        Button Action(string label,Func<Task> action) {
            var button=InspectorButton(label);button.Margin=new Thickness(2,0);button.Click+=async(_,_)=>await action();toolbar.Children.Add(button);return button;
        }
        Action("新增图纸",()=>AddDrawingAsync(kinds[Math.Max(0,addKind.SelectedIndex)]));
        Action("复制",CopyDrawingAsync);Action("删除",RemoveDrawingAsync);
        Action("刷新",()=>RefreshDrawingViewAsync());
        Action("缩放适应",()=> { _drawingCanvas.Fit();return Task.CompletedTask; });
        _drawingChooser.SelectionChanged+=(_,_)=> {
            if(_refreshingDrawings || _drawingChooser.SelectedItem is not DrawingItem item)return;
            SelectDrawing(item.Definition.Id);
        };
        root.Children.Add(toolbar);Grid.SetRow(_drawingCanvas,1);root.Children.Add(_drawingCanvas);
        Grid.SetRow(_drawingInfo,2);root.Children.Add(_drawingInfo);
        return root;
    }

    private void AddDrawingBrowserNodes(TreeViewItem project)
    {
        _drawingBrowserNodes.Clear();
        _drawingCatalogue=DrawingViewCatalogue.Resolve(_session.Model);
        if(!_drawingCatalogue.Any(v=>v.Id==_drawingId))_drawingId=_drawingCatalogue.FirstOrDefault()?.Id;
        _refreshingDrawings=true;
        try {
            var items=_drawingCatalogue.Select(v=>new DrawingItem { Definition=v }).ToList();
            _drawingChooser.ItemsSource=items;_drawingChooser.SelectedItem=items.FirstOrDefault(i=>i.Definition.Id==_drawingId);
        } finally { _refreshingDrawings=false; }
        var drawings=new TreeViewItem { Header=BrowserHeader("图纸","file-axis-3d"),IsExpanded=true };
        foreach(var group in _drawingCatalogue.GroupBy(v=>v.Kind)) {
            var category=new TreeViewItem { Header=BrowserHeader(DrawingViewCatalogue.KindName(group.Key),"layers"),IsExpanded=true };
            foreach(var definition in group.Where(v=>_elementFilter.Length==0 || v.Title.Contains(_elementFilter,StringComparison.CurrentCultureIgnoreCase))) {
                var id=definition.Id;
                var leaf=new TreeViewItem { Header=new Border { Child=BrowserHeader(definition.Title,"file-axis-3d"),
                    BorderBrush=new SolidColorBrush(Color.Parse("#38D4FF")),
                    BorderThickness=new Thickness(_workspaces.SelectedIndex==2 && id==_drawingId ? 3 : 0,0,0,0),Padding=new Thickness(4,0) } };
                leaf.PointerPressed+=(_,e)=> { SelectDrawing(id);e.Handled=true; };
                category.Items.Add(leaf);
                _drawingBrowserNodes[id]=leaf;
            }
            if(category.Items.Count>0)drawings.Items.Add(category);
        }
        project.Items.Add(drawings);
        if(_workspaces.SelectedIndex==2)_drawingPreviewTask=RefreshDrawingViewAsync();
    }

    private void SelectDrawing(string id)
    {
        _drawingId=id;_workspaces.SelectedIndex=2;
        _refreshingDrawings=true;
        try { _drawingChooser.SelectedItem=(_drawingChooser.ItemsSource as IEnumerable<DrawingItem>)?.FirstOrDefault(i=>i.Definition.Id==id); }
        finally { _refreshingDrawings=false; }
        _showSlabProperties?.Invoke();RefreshProperties();
        foreach(var node in _drawingBrowserNodes)if(node.Value.Header is Border border)border.BorderThickness=new Thickness(node.Key==id ? 3 : 0,0,0,0);
        FocusDrawingBrowser();
        _drawingPreviewTask=RefreshDrawingViewAsync();
    }

    private void FocusDrawingBrowser()
    {
        if(_drawingId!=null && _drawingBrowserNodes.TryGetValue(_drawingId,out var selected))
        {
            _browserTree.SelectedItem=selected;
            selected.BringIntoView();
        }
    }

    private static ViewDefinitionModel CopyDrawing(ViewDefinitionModel view)=>BuildingModelJson.FromJson(BuildingModelJson.ToJson(
        new BuildingModelDocument { DrawingViews=new() { view } })).DrawingViews[0];
    private static string DrawingJson(ViewDefinitionModel view)=>BuildingModelJson.ToJson(new BuildingModelDocument { DrawingViews=new() { view } });

    private async Task AddDrawingAsync(ViewKind kind)
    {
        var list=DrawingViewCatalogue.Resolve(_session.Model);
        var view=list.FirstOrDefault(v=>v.Kind==kind);
        view=view==null ? new ViewDefinitionModel { Kind=kind } : CopyDrawing(view);
        if(_session.Model.DrawingScales!=null) view.Scale=_session.Model.DrawingScales.For(kind);
        view.Id="drawing-"+Guid.NewGuid().ToString("N");
        var number=1;var title=DrawingViewCatalogue.KindName(kind);
        while(list.Any(v=>v.Title==title+" "+number))number++;
        view.Title=title+" "+number;
        if(kind==ViewKind.Plan && view.StoreyIds.Count==0 && _session.Model.Storeys.Count>0)view.StoreyIds.Add(_session.Model.Storeys[0].Id);
        list.Add(view);_drawingId=view.Id;
        if(!_session.TryReplaceDrawingViews(list,out var error)) { _status.Text=error;return; }
        await RefreshModelAsync("已新增"+title);SelectDrawing(view.Id);await _drawingPreviewTask;
    }

    private async Task CopyDrawingAsync()
    {
        var list=DrawingViewCatalogue.Resolve(_session.Model);var view=list.FirstOrDefault(v=>v.Id==_drawingId);
        if(view==null)return;
        var copy=CopyDrawing(view);copy.Id="drawing-"+Guid.NewGuid().ToString("N");copy.Title+=" 副本";
        list.Add(copy);_drawingId=copy.Id;
        if(!_session.TryReplaceDrawingViews(list,out var error)) { _status.Text=error;return; }
        await RefreshModelAsync("已复制图纸");SelectDrawing(copy.Id);await _drawingPreviewTask;
    }

    private async Task RemoveDrawingAsync()
    {
        var list=DrawingViewCatalogue.Resolve(_session.Model);if(list.RemoveAll(v=>v.Id==_drawingId)==0)return;
        _drawingId=list.FirstOrDefault()?.Id;
        if(!_session.TryReplaceDrawingViews(list,out var error)) { _status.Text=error;return; }
        await RefreshModelAsync("已删除图纸（可撤销）");await _drawingPreviewTask;
    }

    private async Task RefreshDrawingViewAsync(ViewDefinitionModel? draft=null)
    {
        var definition=draft ?? _drawingCatalogue.FirstOrDefault(v=>v.Id==_drawingId);
        var generation=++_drawingGeneration;
        if(definition==null) { _drawingCanvas.SetView(null);_drawingInfo.Text="图纸目录为空，可新增图纸。";return; }
        var model=BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model));
        definition=CopyDrawing(definition);
        var path=_filePath==null ? null : Path.Combine(Path.GetDirectoryName(_filePath)!,"openings.json");
        _drawingInfo.Text="正在生成 "+definition.Title+"…";
        try {
            var view=await Task.Run(()=>OrthographicProjector.Project(model,definition,
                path!=null && File.Exists(path) ? BuildingModelJson.LoadOpeningLibrary(path) : null));
            if(generation!=_drawingGeneration)return;
            _drawingCanvas.SetView(view);
            _drawingInfo.Text=view.Title+" · 1:"+view.Scale+" · 滚轮缩放，拖动平移"
                +(view.Warnings.Count==0 ? "" : "\n"+string.Join("；",view.Warnings.Distinct().Take(2)));
        } catch(Exception ex) { if(generation==_drawingGeneration)_drawingInfo.Text="图纸生成失败："+ex.Message; }
    }

    private void BuildDrawingProperties()
    {
        _buildingDrawingProperties=true;
        _propertyHeading.Text="图纸属性";_slabDraft=null;
        var source=_drawingCatalogue.FirstOrDefault(v=>v.Id==_drawingId);
        if(source==null) { _properties.Children.Add(new TextBlock { Text="新增图纸或从项目浏览器选择图纸。" });_buildingDrawingProperties=false;return; }
        _drawingDraft=CopyDrawing(source);
        TextBox TextField(string name,string value) {
            var field=new TextBox { Text=value,MinHeight=34 };
            _properties.Children.Add(new TextBlock { Text=name });_properties.Children.Add(field);
            field.TextChanged+=(_,_)=> { if(_properties.Children.Contains(field))PreviewDrawingParameters(); };return field;
        }
        ComboBox Choice(string name,IEnumerable<object> values,int selected) {
            var field=new ComboBox { ItemsSource=values,SelectedIndex=selected,MinHeight=34,HorizontalAlignment=HorizontalAlignment.Stretch };
            _properties.Children.Add(new TextBlock { Text=name });_properties.Children.Add(field);
            field.SelectionChanged+=(_,_)=>PreviewDrawingParameters();return field;
        }
        _drawingTitle=TextField("图名",source.Title);_drawingScale=TextField("比例 1:",source.Scale.ToString());
        _properties.Children.Add(new TextBlock { Text=DrawingViewCatalogue.KindName(source.Kind),Foreground=Brushes.LightSkyBlue });
        _drawingFloor=_drawingDirection=_drawingCutAxis=_drawingSign=null;
        _drawingCut=_drawingDepth=_drawingYaw=_drawingPitch=null;_drawingPerspective=null;
        if(source.Kind==ViewKind.Plan) {
            var floors=_session.Model.Storeys.Select(s=>new StoreyItem { Id=s.Id,Name=s.Name }).ToArray();
            _drawingFloor=Choice("楼层",floors,Array.FindIndex(floors,s=>source.StoreyIds.Contains(s.Id)));
        }
        if(source.Kind==ViewKind.Elevation)_drawingDirection=Choice("方向",new[] { "南立面","北立面","东立面","西立面" },(int)source.Direction);
        if(source.Kind==ViewKind.Section) {
            _drawingCutAxis=Choice("剖切线",new[] { "X = 定值（沿 Y）","Y = 定值（沿 X）" },(int)source.CutAxis);
            _drawingCut=TextField("剖切位置 mm",Mm(source.CutPosition));
            _drawingSign=Choice("剖视方向",new[] { "坐标增大方向","坐标减小方向" },source.ViewSign==1 ? 0 : 1);
            _drawingDepth=TextField("剖视深度 mm（0 为不限）",Mm(source.ViewDepth));
        }
        if(source.Kind==ViewKind.Axonometric) {
            _drawingYaw=TextField("方位角 °",Mm(source.AzimuthDegrees));_drawingPitch=TextField("仰角 °",Mm(source.ElevationDegrees));
            _drawingPerspective=new CheckBox { Content="透视",IsChecked=source.Perspective };
            _drawingPerspective.IsCheckedChanged+=(_,_)=>PreviewDrawingParameters();_properties.Children.Add(_drawingPerspective);
        }
        _properties.Children.Add(new TextBlock { Text="参数变化会预览；应用后保存到模型，Ctrl+Z 可撤销。",TextWrapping=TextWrapping.Wrap });
        var apply=InspectorButton("应用图纸参数");
        apply.Click+=async(_,_)=> {
            if(!ReadDrawingDraft(out var draft)) { _status.Text="图名、比例或投影参数无效。";return; }
            var list=DrawingViewCatalogue.Resolve(_session.Model);var index=list.FindIndex(v=>v.Id==draft!.Id);
            if(index<0)return;list[index]=draft!;
            if(!_session.TryReplaceDrawingViews(list,out var error)) { _status.Text=error;return; }
            await RefreshModelAsync("已应用图纸参数");await _drawingPreviewTask;
        };
        _properties.Children.Add(apply);
        var cancel=InspectorButton("取消参数修改");cancel.Click+=(_,_)=> { RefreshProperties();_drawingPreviewTask=RefreshDrawingViewAsync(); };
        _properties.Children.Add(cancel);_drawingInputKey=DrawingJson(source);_buildingDrawingProperties=false;
    }

    private bool ReadDrawingDraft(out ViewDefinitionModel? draft)
    {
        draft=null;if(_drawingDraft==null || !int.TryParse(_drawingScale?.Text,out var scale) || scale<1 || scale>10000 || string.IsNullOrWhiteSpace(_drawingTitle?.Text))return false;
        var value=CopyDrawing(_drawingDraft);value.Title=_drawingTitle.Text.Trim();value.Scale=scale;
        if(value.Kind==ViewKind.Plan) {
            if(_drawingFloor?.SelectedItem is not StoreyItem floor)return false;value.StoreyIds=new() { floor.Id };
        }
        if(_drawingDirection!=null)value.Direction=(ElevationDirection)_drawingDirection.SelectedIndex;
        if(_drawingCut!=null) {
            if(!TryNumber(_drawingCut.Text,out var cut) || !TryNumber(_drawingDepth?.Text,out var depth) || depth<0)return false;
            value.CutPosition=cut;value.ViewDepth=depth;value.CutAxis=(SectionAxis)_drawingCutAxis!.SelectedIndex;value.ViewSign=_drawingSign!.SelectedIndex==0 ? 1 : -1;
        }
        if(_drawingYaw!=null) {
            if(!TryNumber(_drawingYaw.Text,out var yaw) || !TryNumber(_drawingPitch?.Text,out var pitch) || Math.Abs(pitch)>=89)return false;
            value.AzimuthDegrees=yaw;value.ElevationDegrees=pitch;value.Perspective=_drawingPerspective?.IsChecked==true;
        }
        draft=value;return true;
    }

    private void PreviewDrawingParameters()
    {
        if(!_buildingDrawingProperties && _workspaces.SelectedIndex==2 && ReadDrawingDraft(out var draft))
        {
            var key=DrawingJson(draft!);if(key==_drawingInputKey)return;
            _drawingInputKey=key;_drawingPreviewTask=RefreshDrawingViewAsync(draft);
        }
    }

    private async Task<bool> RunDrawingCheckAsync()
    {
        async Task Flush()
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);
            await _drawingPreviewTask;
        }
        try {
            var original=BuildingModelJson.ToJson(_session.Model);var count=_drawingCatalogue.Count;
            foreach(var kind in new[] { ViewKind.Plan,ViewKind.Elevation,ViewKind.Section,ViewKind.Axonometric,ViewKind.Schedule,ViewKind.OpeningElevation }) {
                SelectDrawing(_drawingCatalogue.First(v=>v.Kind==kind).Id);await Flush();
                if(_drawingCanvas.View?.Kind!=kind || _drawingCanvas.View.Lines.Count+_drawingCanvas.View.Texts.Count==0)
                    throw new InvalidOperationException("图纸预览缺失："+kind);
            }
            await AddDrawingAsync(ViewKind.Section);
            if(_drawingCatalogue.Count!=count+1)throw new InvalidOperationException("新增图纸没有进入目录");
            var added=BuildingModelJson.ToJson(_session.Model);var id=_drawingId;
            _drawingTitle!.Text="测试横剖面";_drawingCut!.Text="2500";_drawingScale!.Text="75";await Flush();
            if(_drawingCanvas.View?.Title!="测试横剖面" || _drawingCanvas.View.Scale!=75 || BuildingModelJson.ToJson(_session.Model)!=added)
                throw new InvalidOperationException("图纸参数预览没有更新，或提前写入模型");
            _drawingScale.Text="-";await Flush();
            if(_drawingCanvas.View!.Scale!=75 || BuildingModelJson.ToJson(_session.Model)!=added)throw new InvalidOperationException("非法比例改写了预览或模型");
            _drawingScale.Text="75";await Flush();
            var revision=_session.Revision;
            _properties.Children.OfType<Button>().First(b=>Equals(b.Content,"应用图纸参数"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Flush();
            if(_session.Revision!=revision+1 || _session.Model.DrawingViews.Single(v=>v.Id==id).CutPosition!=2500)
                throw new InvalidOperationException("应用图纸参数没有形成一次事务");
            await CopyDrawingAsync();if(_drawingCatalogue.Count!=count+2)throw new InvalidOperationException("图纸复制失败");
            await RemoveDrawingAsync();if(_drawingCatalogue.Count!=count+1)throw new InvalidOperationException("图纸删除失败");
            for(var i=0;i<4;i++)if(!_session.Undo())throw new InvalidOperationException("图纸修改不能撤销");
            if(BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("撤销没有恢复原图纸目录");
            if(!_session.Redo() || _session.Model.DrawingViews.Count!=count+1 || !_session.Undo())throw new InvalidOperationException("图纸修改不能重做");
            await RefreshModelAsync("图纸检查完成");
            SelectDrawing(_drawingCatalogue.First().Id);await Flush();
            if(_filePath!=null) {
                await PublishViewsAsync(true);
                var folder=Path.GetDirectoryName(_filePath)!;
                var entries=StudioLaunch.ListViews(folder);
                if(!_drawingCatalogue.All(v=>entries.Any(entry=>entry.Id==v.Id))
                    || !File.Exists(StudioLaunch.PendingFilePath(folder)))
                    throw new InvalidOperationException("编辑器图纸生成与 CAD 待落图清单不完整："+_status.Text);
                Console.WriteLine("DRAWING_CAD_PUBLISH_OK catalogue="+_drawingCatalogue.Count+" cached="+entries.Count);
            }
            Console.WriteLine("DRAWING_UI_CHECK_OK all-kinds default-catalogue add copy remove live-preview invalid-input apply undo redo");
            return true;
        } catch(Exception ex) { Console.Error.WriteLine("DRAWING_UI_CHECK_FAILED "+ex);return false; }
    }
}
