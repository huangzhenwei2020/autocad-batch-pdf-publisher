using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private OutdoorDrawingDraft? _outdoor;
    private ModelViewport.PreparedScene? _outdoorBase;
    private OutdoorPathOverlay? _outdoorOverlay;
    private readonly WrapPanel _outdoorTools=new(){IsVisible=false,Orientation=Orientation.Horizontal,
        HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,
        Margin=new Thickness(10,48,10,0),MaxWidth=460};
    private readonly WrapPanel _outdoorPlanTools=new(){IsVisible=false,Orientation=Orientation.Horizontal,
        HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,
        Margin=new Thickness(8),MaxWidth=460};
    private PointModel? _outdoorCursor;
    private int _outdoorGeneration;
    private Task _outdoorPreviewTask=Task.CompletedTask;
    private bool _outdoorPreviewRunning;
    private OutdoorPreviewRequest? _outdoorPendingPreview;
    private sealed record OutdoorPreviewRequest(int Generation,OutdoorDrawingDraft Draft,BuildingModelEditSession Session,
        long Revision,BuildingVolume Source,BuildingVolume Preview,double Elevation,IReadOnlySet<string> ExcludedIds,
        IReadOnlySet<string> HiddenPreviewIds);
    private IReadOnlySet<string> _outdoorExcludedIds=new HashSet<string>();
    private Task _outdoorCommitTask=Task.CompletedTask;
    private Border? _outdoorInput;
    private string? _outdoorConflict;
    private Button? _outdoorConflictButton;
    private void ClearOutdoorConflict(){_outdoorConflict=null;if(_outdoorConflictButton!=null)_outdoorConflictButton.IsEnabled=false;}

    private void AttachOutdoorDrawing(Grid host,Border input)
    {
        _outdoorInput=input;
        _outdoorOverlay=new OutdoorPathOverlay(_viewport){IsHitTestVisible=false,ClipToBounds=true};
        host.Children.Add(_outdoorOverlay);host.Children.Add(_outdoorTools);
        void Button(string title,string icon,Action action)
        {
            var b=SlabButton(title,icon);b.HorizontalAlignment=HorizontalAlignment.Left;b.Margin=new Thickness(0,0,2,2);
            b.Click+=(_,_)=>{action();FocusOutdoor();};_outdoorTools.Children.Add(b);
            var plan=SlabButton(title,icon);plan.Margin=new Thickness(0,0,2,2);
            plan.Click+=(_,_)=>{action();FocusOutdoor();};_outdoorPlanTools.Children.Add(plan);
        }
        Button("完成","check",()=>_outdoorCommitTask=FinishOutdoorDrawingAsync());
        Button("闭合","pentagon",()=>{if(_outdoor!=null){var error=_outdoor.Close();if(error!=null)_status.Text=error;UpdateOutdoorPreview();}});
        Button("上一步","undo-2",OutdoorPrevious);
        Button("反向","rotate-ccw",ReverseOutdoorPath);
        Button("退出","square-x",()=>StopOutdoorDrawing());
        _planCanvas.OutdoorPressed=(pixel,modifiers,right)=>{
            if(right){OpenOutdoorMenu(_planCanvas);return;}
            if(_outdoorReferenceMode!=0)
            {var edge=PickOutdoorReference(pixel,plan:true);if(edge==null)_status.Text="当前位置没有可用的源边。";else TakeOutdoorReference(edge);}
            else if(_outdoor!=null)OutdoorAdd(_planCanvas.OutdoorPoint(pixel,_outdoorSnap,_outdoor.ArcEnd==null));
        };
        _planCanvas.OutdoorMoved=(pixel,modifiers)=>{
            if(_outdoor==null)return;
            if(_outdoorReferenceMode!=0){_outdoorCursor=null;HoverOutdoorReference(pixel,plan:true);}
            else {_outdoorCursor=_planCanvas.OutdoorPoint(pixel,_outdoorSnap,_outdoor.ArcEnd==null);UpdateOutdoorPreview();}
        };
    }

    private void FocusOutdoor(){if(_workspaces.SelectedIndex==1)_planCanvas.Focus();else _viewport.Focus();}
    private void OutdoorWorkspaceChanged()
    {
        _outdoorTools.IsVisible=_outdoor!=null&&_workspaces.SelectedIndex==0;
        _outdoorPlanTools.IsVisible=_outdoor!=null&&_workspaces.SelectedIndex==1;
        if(_outdoor==null)return;
        _outdoorCursor=null;ClearOutdoorReferenceHover();UpdateOutdoorPreview();
    }
    private void OpenOutdoorMenu(Control target)
    {
        var menu=new ContextMenu();var items=new List<MenuItem>();
        foreach(var (name,action) in new (string,Action)[]{("完成",()=>_outdoorCommitTask=FinishOutdoorDrawingAsync()),
            ("闭合",()=>{var error=_outdoor?.Close();if(error!=null)_status.Text=error;UpdateOutdoorPreview();}),
            ("上一步",OutdoorPrevious),("取消当前路径",()=>{ClearOutdoorConflict();_outdoor?.Clear();UpdateOutdoorPreview();})})
        {var item=new MenuItem {Header=name};item.Click+=(_,_)=>{action();FocusOutdoor();};items.Add(item);}
        menu.ItemsSource=items;menu.Open(target);
    }

    private void BeginOutdoorDrawing(bool balcony,WallDrawingChoice? choice=null,SlabModel? source=null)
    {
        if(source!=null&&_selectedVisualIds.Any(id=>id!=source.Id&&StandardStoreyLayout.SourceElementId(_session.Model,id)==source.Id))
        {_status.Text="当前为共用层实例。请到来源楼层选择阳台后编辑；仅修改这一层需先拆为独立层。";return;}
        if(source!=null&&(_viewport.IsElementHidden(source.Id)||_viewport.IsElementFrozen(source.Id)))
        {_status.Text="请先显示并解冻需要重绘的阳台。";return;}
        StopOutdoorDrawing();CancelParameterPreview();CancelMove();_gizmo.Cancel();
        if(!ReferenceEquals(_sceneModel,_session.Model)){_status.Text="模型视图正在更新，请稍后开始绘制。";return;}
        var floor=source?.StoreyId??(_storeyChooser.SelectedItem as StoreyItem)?.Id??_session.Model.Storeys.FirstOrDefault()?.Id;
        if(floor==null){_status.Text="请先新建楼层。";return;}
        if(!string.IsNullOrWhiteSpace(_session.Model.FindStorey(floor)?.TemplateStoreyId))
        {_status.Text="该层共用标准层，请选择来源楼层绘制；仅修改这一层需先拆为独立层。";return;}
        EndAxisEditing();
        _planCanvas.Tool=PlanTool.Select;_planCanvas.CancelDraft();
        _outdoor=new OutdoorDrawingDraft(balcony,floor,choice??new(Purpose:WallPurpose.Parapet,Height:1100));
        if(source!=null){_outdoor.OriginalSlab=CopySlab(source);_outdoor.Thickness=source.Thickness;
            _outdoor.Offset=_session.Model.TopElevationOf(source)-(_session.Model.FindStorey(floor)?.Elevation??0);}
        _outdoorReferenceMode=0;ResetOutdoorReferences();
        _outdoorBase=_viewport.CurrentScene;_selectedId=null;_selectedVisualIds.Clear();_gizmo.SetSelection(null,0);_viewport.SelectElement(null);
        _planCanvas.SetSelection(null);
        _outdoorExcludedIds=source==null?new HashSet<string>():_outdoorBase.Volume.Faces
            .Where(f=>StandardStoreyLayout.SourceElementId(_session.Model,f.ElementId)==source.Id).Select(f=>f.ElementId).ToHashSet();
        _planCanvas.OutdoorDraft=_outdoor;_storeyChooser.IsEnabled=false;
        _storeyChooser.SelectedItem=_storeyChooser.Items.OfType<StoreyItem>().FirstOrDefault(s=>s.Id==floor);
        _workspaces.SelectedIndex=1;OutdoorWorkspaceChanged();_showSlabProperties?.Invoke();
        BuildOutdoorProperties();UpdateOutdoorPreview();FocusOutdoor();
        if(source!=null&&_outdoorOverlay!=null){_outdoorOverlay.OriginalPoints=source.Outline.Select(OutdoorDrawingDraft.Copy).ToList();
            _outdoorOverlay.OriginalElevation=_session.Model.TopElevationOf(source);_outdoorOverlay.InvalidateVisual();}
        Avalonia.Threading.Dispatcher.UIThread.Post(()=>{
            var scroll=_properties.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            if(scroll!=null)scroll.Offset=default;
        });
        _status.Text=source!=null?"平面重绘阳台：原编号及板洞保留；可切三维检查，完成后应用。":
            balcony?"平面绘制阳台：指定轮廓点，闭合后完成；可捕捉 CAD 底图。":"平面绘制女儿墙：指定路径点，完成后整条写入；可切三维检查。";
    }

    private void BuildOutdoorProperties()
    {
        if(_outdoor is not {} draft)return;
        _properties.Children.Clear();_slabFooter.IsVisible=false;_propertyHeading.Text=draft.Balcony?"绘制阳台":"绘制女儿墙";
        _slabPropertyCode.IsVisible=false;if(_slabPropertyMark!=null)_slabPropertyMark.IsVisible=false;
        _properties.Margin=new Thickness(12);_properties.Spacing=10;
        var views=new Grid {ColumnDefinitions=new ColumnDefinitions("*,*")};
        var planView=SlabButton("平面绘制","panel-top");var modelView=SlabButton("三维检查","box");
        planView.Click+=(_,_)=>{_workspaces.SelectedIndex=1;FocusOutdoor();};
        modelView.Click+=(_,_)=>{_workspaces.SelectedIndex=0;FocusOutdoor();};
        views.Children.Add(planView);Grid.SetColumn(modelView,1);views.Children.Add(modelView);_properties.Children.Add(views);
        var floors=_session.Model.Storeys.Select(s=>new StoreyItem {Id=s.Id,Name=s.Name+"（"+Mm(s.Elevation)+" mm）"}).ToList();
        var floorInput=new ComboBox {ItemsSource=floors,SelectedItem=floors.FirstOrDefault(s=>s.Id==draft.StoreyId),HorizontalAlignment=HorizontalAlignment.Stretch};
        floorInput.IsEnabled=draft.OriginalSlab==null;
        _properties.Children.Add(new TextBlock {Text="所属楼层 / 水平工作平面"});_properties.Children.Add(floorInput);
        if(draft.OriginalSlab is {} original)
        {
            var count=1+_session.Model.Storeys.Count(s=>s.TemplateStoreyId==draft.StoreyId);
            _properties.Children.Add(new TextBlock {Text=BuildingElementNames.Slab(original)+(count>1?$" · 共用 {count} 层":" · 当前独立楼层"),TextWrapping=TextWrapping.Wrap});
            var restore=SlabButton("取原轮廓","contour-edit");restore.Click+=(_,_)=>{
                var error=draft.TakeReference(original.Outline,true);if(error!=null){_status.Text=error;return;}
                ClearOutdoorConflict();_outdoorCursor=null;UpdateOutdoorPreview();FocusOutdoor();};
            _properties.Children.Add(restore);
        }
        var level=new TextBlock();_properties.Children.Add(level);
        NumericUpDown Number(double value,string label){var n=SlabNumber(value,label);n.Text=Mm(value);return n;}
        var offset=Number(draft.Balcony?draft.Offset:draft.Wall.BaseOffset,"相对楼层偏移 mm");
        _outdoorOffsetInput=offset;
        var thick=Number(draft.Balcony?draft.Thickness:draft.Wall.Thickness,"板厚 / 墙厚 mm");
        var height=Number(draft.Wall.Height,"女儿墙高度 mm");
        _properties.Children.Add(SlabRow(draft.Balcony?"板顶偏移 mm":"墙底偏移 mm",offset));
        _properties.Children.Add(SlabRow(draft.Balcony?"板厚 mm":"墙厚 mm",thick));
        if(!draft.Balcony)_properties.Children.Add(SlabRow("墙高 mm",height));
        var side=new ComboBox {ItemsSource=new[]{"定位线居中","实体在路径右侧","实体在路径左侧","自定义中心偏移"},SelectedIndex=draft.Wall.AxisOffset.HasValue?3:(int)draft.Wall.AxisPlacement,HorizontalAlignment=HorizontalAlignment.Stretch};
        if(!draft.Balcony){_properties.Children.Add(new TextBlock {Text="沿路径前进方向"});_properties.Children.Add(side);}
        var centerOffset=Number(draft.Wall.AxisOffset??0,"中心向路径左侧偏移 mm");
        if(!draft.Balcony)_properties.Children.Add(SlabRow("中心偏移 mm",centerOffset));
        var mode=new ComboBox {ItemsSource=new[]{"直线","三点圆弧（起点、终点、经过点）"},SelectedIndex=draft.Arc?1:0,HorizontalAlignment=HorizontalAlignment.Stretch};
        AddOutdoorReferenceProperties(draft,mode);
        _properties.Children.Add(mode);
        var preview=new ComponentShapePreview {Height=130};_properties.Children.Add(preview);
        var snap=new CheckBox {Content="捕捉（平面含 CAD 底图）",IsChecked=_outdoorSnap};_properties.Children.Add(snap);
        snap.IsCheckedChanged+=(_,_)=>_outdoorSnap=snap.IsChecked==true;
        var conflict=SlabButton("选用重叠的已有楼板","layers");conflict.IsEnabled=false;
        _outdoorConflictButton=conflict;
        conflict.Click+=(_,_)=>{var id=_outdoorConflict;if(id==null)return;StopOutdoorDrawing();SelectById(id);};
        if(draft.Balcony)_properties.Children.Add(conflict);
        void Change()
        {
            if(!ReferenceEquals(_outdoor,draft))return;
            ClearOutdoorConflict();
            if(!TryNumber(offset.Text,out var o)||!TryNumber(thick.Text,out var t)||t<1||t>10000||Math.Abs(o)>100000
                ||(!draft.Balcony&&side.SelectedIndex==3&&(!TryNumber(centerOffset.Text,out var co)||Math.Abs(co)>t/2))
                ||(!draft.Balcony&&(!TryNumber(height.Text,out var h)||h<10||h>100000)))
            {_outdoorParametersValid=false;_status.Text="请填写有效偏移、厚度和高度。";ClearOutdoorPreview();return;}
            _outdoorParametersValid=true;draft.Offset=o;draft.Thickness=t;
            draft.Wall=draft.Wall with {Thickness=t,Height=TryNumber(height.Text,out var wh)?wh:1100,BaseOffset=o,
                AxisPlacement=side.SelectedIndex==3?WallAxisPlacement.Center:(WallAxisPlacement)side.SelectedIndex,
                AxisOffset=side.SelectedIndex==3&&TryNumber(centerOffset.Text,out var c)?c:null};
            centerOffset.IsEnabled=side.SelectedIndex==3;
            level.Text="工作标高 "+Mm(draft.Elevation(_session.Model))+" mm";
            try {
                var sample=new OutdoorDrawingDraft(draft.Balcony,draft.StoreyId,draft.Wall){Offset=o,Thickness=t};
                if(draft.OriginalSlab!=null){sample.OriginalSlab=draft.OriginalSlab;sample.TakeReference(draft.OriginalSlab.Outline,true);}
                else {sample.Add(new(0,0));sample.Add(new(3000,0));if(draft.Balcony){sample.Add(new(3000,1500));sample.Add(new(0,1500));sample.Close();}}
                _ = preview.SetVolumeAsync(BuildingVolumeBuilder.Build(sample.Geometry(_session.Model)));
            }catch(ArgumentException ex){_status.Text=ex.Message;}
            UpdateOutdoorPreview();
            conflict.IsEnabled=_outdoorConflict!=null;
        }
        floorInput.SelectionChanged+=(_,_)=>{if(floorInput.SelectedItem is StoreyItem f){
            if(!string.IsNullOrWhiteSpace(_session.Model.FindStorey(f.Id)?.TemplateStoreyId))
            {_status.Text="请到标准层来源楼层绘制。";floorInput.SelectedItem=floors.FirstOrDefault(s=>s.Id==draft.StoreyId);return;}
            draft.StoreyId=f.Id;_storeyChooser.SelectedItem=_storeyChooser.Items.OfType<StoreyItem>().FirstOrDefault(s=>s.Id==f.Id);
            ResetOutdoorReferences();Change();}};
        mode.SelectionChanged+=(_,_)=>{if(draft.ArcEnd!=null)draft.Previous();draft.Arc=mode.SelectedIndex==1;UpdateOutdoorPreview();};
        side.SelectionChanged+=(_,_)=>Change();
        foreach(var input in new[]{offset,thick,height,centerOffset}) input.PropertyChanged+=(_,e)=>{
            if(e.Property==NumericUpDown.TextProperty||e.Property==NumericUpDown.ValueProperty)Change();};
        Change();
    }
    private bool _outdoorParametersValid=true,_outdoorSnap=true;

    private PointModel? OutdoorPoint(Point pixel,KeyModifiers modifiers)
    {
        if(_outdoor is not {} d||!_viewport.TryScreenToPlan(pixel,d.Elevation(_session.Model),out var hit))return null;
        hit=new PointModel(Math.Round(hit.X),Math.Round(hit.Y));
        if(d.Points.Count>0&&d.ArcEnd==null&&(_planCanvas.OrthogonalEnabled||modifiers.HasFlag(KeyModifiers.Shift)))
        {
            var last=d.Points[^1];if(Math.Abs(hit.X-last.X)>Math.Abs(hit.Y-last.Y))hit.Y=last.Y;else hit.X=last.X;
        }
        else if(d.Points.Count>0&&d.ArcEnd==null&&_planCanvas.PolarEnabled)
        {
            var last=d.Points[^1];var length=OutdoorDrawingDraft.Distance(last,hit);var step=_planCanvas.PolarStepDegrees*Math.PI/180;
            var angle=Math.Round(Math.Atan2(hit.Y-last.Y,hit.X-last.X)/step)*step;
            hit=new(last.X+length*Math.Cos(angle),last.Y+length*Math.Sin(angle));
        }
        if(!_outdoorSnap)return hit;
        var candidates=new List<(PointModel P,string? Id)>();
        candidates.AddRange(d.Points.Select(p=>(p,(string?)null)));
        var physical=_outdoorPhysical??=StandardStoreyLayout.Materialize(_session.Model);var z=d.Elevation(_session.Model);
        foreach(var wall in physical.Walls.Where(w=>Math.Abs(physical.BaseElevationOf(w)-z)<.5||Math.Abs(physical.BaseElevationOf(w)+physical.HeightOf(w)-z)<.5))
        {candidates.Add((new(wall.X1,wall.Y1),wall.Id));candidates.Add((new(wall.X2,wall.Y2),wall.Id));}
        foreach(var slab in physical.Slabs.Where(s=>Math.Abs(physical.TopElevationOf(s)-z)<.5))candidates.AddRange(slab.Outline.Select(p=>(p,(string?)slab.Id)));
        var best=9d;
        foreach(var (p,id) in candidates)
        {
            if(id!=null&&!_viewport.IsElementVisible(id))continue;
            var screen=_viewport.ProjectModelPoint(p.X,p.Y,z);if(screen==null)continue;
            var delta=screen.Value-pixel;var distance=Math.Sqrt(delta.X*delta.X+delta.Y*delta.Y);if(distance>=best)continue;
            var obstruction=_viewport.PickAt(screen.Value);
            if(id!=null&&obstruction!=null&&obstruction!=id&&!obstruction.StartsWith("outdoor-preview-",StringComparison.Ordinal))continue;
            best=distance;hit=OutdoorDrawingDraft.Copy(p);hit.Bulge=0;
        }
        return hit;
    }
    private bool OutdoorPress(PointerPressedEventArgs e)
    {
        if(_outdoor==null)return false;
        var buttons=e.GetCurrentPoint(_viewport).Properties;
        if(buttons.IsRightButtonPressed)
        {
            OpenOutdoorMenu(_viewport);e.Handled=true;return true;
        }
        if(!buttons.IsLeftButtonPressed)return false;
        if(_outdoorReferenceMode!=0)
        {
            var edge=PickOutdoorReference(e.GetPosition(_viewport));
            if(edge==null)_status.Text="当前位置没有可用的源边。";else TakeOutdoorReference(edge);
            _viewport.Focus();e.Handled=true;return true;
        }
        var point=OutdoorPoint(e.GetPosition(_viewport),e.KeyModifiers);
        if(point==null){_status.Text="当前视角无法确定工作平面，请调整视角。";return true;}
        OutdoorAdd(point);_viewport.Focus();e.Handled=true;return true;
    }
    private void OutdoorAdd(PointModel point)
    {
        if(_outdoor==null)return;ClearOutdoorConflict();var error=_outdoor.Add(point);_outdoorCursor=null;UpdateOutdoorPreview();
        _status.Text=error??(_outdoor.Closed?"轮廓已闭合，点击完成写入模型。":_outdoor.ArcEnd!=null?"指定圆弧经过点。":"继续指定下一点，或点击完成。 ");
    }
    private void OutdoorPrevious(){ClearOutdoorConflict();_outdoor?.Previous();_outdoorCursor=null;UpdateOutdoorPreview();}
    private void ClearOutdoorPreview(bool clearPath=true,bool restoreOriginal=false)
    {
        ++_outdoorGeneration;_outdoorPendingPreview=null;
        if(_outdoorBase!=null)
        {
            var scene=_outdoorBase;
            if(!restoreOriginal&&scene.Volume.Faces.Count==0&&_viewport.CurrentScene.Volume.Diagonal>10)
            {
                var v=_viewport.CurrentScene.Volume;
                scene=ModelViewport.PrepareScene(new BuildingVolume {MinX=v.MinX,MinY=v.MinY,MinZ=v.MinZ,
                    MaxX=v.MaxX,MaxY=v.MaxY,MaxZ=v.MaxZ,GuideLines=v.GuideLines.Where(g=>g.IsWorkingPlane).ToList()});
            }
            _viewport.SetScene(scene);
        }
        if(clearPath&&_outdoorOverlay!=null){_outdoorOverlay.Points.Clear();_outdoorOverlay.InvalidateVisual();}
        _planCanvas.OutdoorGeometry=null;
        if(clearPath)_planCanvas.OutdoorPath.Clear();_planCanvas.InvalidateVisual();
    }
    private void UpdateOutdoorPreview()
    {
        if(_outdoor is not {} draft||_outdoorBase==null)return;
        _planCanvas.OutdoorPath=draft.PreviewPoints(_outdoorCursor);_planCanvas.OutdoorClosed=draft.PreviewClosed(_outdoorCursor);
        _planCanvas.InvalidateVisual();
        if(!_outdoorParametersValid)return;
        if(_outdoorOverlay!=null){_outdoorOverlay.Points=draft.PreviewPoints(_outdoorCursor);_outdoorOverlay.Closed=draft.PreviewClosed(_outdoorCursor);
            _outdoorOverlay.Elevation=draft.Elevation(_session.Model);_outdoorOverlay.InvalidateVisual();}
        BuildingVolume preview;
        try {var geometry=draft.Geometry(_session.Model,_outdoorCursor);_planCanvas.OutdoorGeometry=geometry;
            preview=BuildingVolumeBuilder.Build(geometry);}
        catch(ArgumentException ex){ClearOutdoorPreview(false);_status.Text=ex.Message;return;}
        var source=_outdoorBase.Volume;
        var hiddenFloors=source.Faces.GroupBy(f=>f.StoreyId)
            .Where(g=>g.All(f=>_viewport.IsElementHidden(f.ElementId))).Select(g=>g.Key).ToHashSet();
        var hiddenOriginalFloors=source.Faces.Where(f=>_outdoorExcludedIds.Contains(f.ElementId)&&_viewport.IsElementHidden(f.ElementId))
            .Select(f=>f.StoreyId).ToHashSet();
        var hiddenPreview=preview.Faces.Where(f=>hiddenFloors.Contains(f.StoreyId)||hiddenOriginalFloors.Contains(f.StoreyId))
            .Select(f=>f.ElementId).ToHashSet();
        _outdoorPendingPreview=new(++_outdoorGeneration,draft,_session,_session.Revision,source,preview,draft.Elevation(_session.Model),_outdoorExcludedIds,hiddenPreview);
        if(!_outdoorPreviewRunning)_outdoorPreviewTask=PrepareOutdoorPreviewAsync();
    }
    private static BuildingVolume MergeOutdoorPreview(OutdoorPreviewRequest request)
    {
        var source=request.Source;var preview=request.Preview;var empty=source.Faces.Count==0;
        var combined=new BuildingVolume {MinX=empty?-5000:source.MinX,MaxX=empty?5000:source.MaxX,
            MinY=empty?-5000:source.MinY,MaxY=empty?5000:source.MaxY,MinZ=empty?request.Elevation-100:source.MinZ,
            MaxZ=empty?request.Elevation+3000:source.MaxZ,
            Faces=source.Faces.Where(f=>!request.ExcludedIds.Contains(f.ElementId))
                .Concat(preview.Faces.Where(f=>!request.HiddenPreviewIds.Contains(f.ElementId))).ToList(),
            GuideLines=source.GuideLines.Where(g=>g.ElementId==null||!request.ExcludedIds.Contains(g.ElementId))
                .Concat(preview.GuideLines.Where(g=>!g.IsBuildingAxis&&(g.ElementId==null||!request.HiddenPreviewIds.Contains(g.ElementId)))).ToList()};
        if(empty)for(var i=-5000;i<=5000;i+=1000)
        {
            var z=request.Elevation;
            combined.GuideLines.Add(new(){Start=new(i,-5000,z),End=new(i,5000,z),IsWorkingPlane=true});
            combined.GuideLines.Add(new(){Start=new(-5000,i,z),End=new(5000,i,z),IsWorkingPlane=true});
        }
        return combined;
    }
    private async Task PrepareOutdoorPreviewAsync()
    {
        _outdoorPreviewRunning=true;
        try
        {
            // Keep one builder in flight and one latest request; mouse movement never queues full scenes.
            while(_outdoorPendingPreview!=null)
            {
                await Task.Delay(60);
                var request=_outdoorPendingPreview;_outdoorPendingPreview=null;if(request==null)continue;
                try {
                    var scene=await Task.Run(()=>ModelViewport.PrepareScene(MergeOutdoorPreview(request)));
                    if(request.Generation!=_outdoorGeneration||_outdoor!=request.Draft||_session!=request.Session||_session.Revision!=request.Revision)continue;
                    var initialEmpty=request.Source.Faces.Count==0&&_viewport.CurrentScene.Volume.Faces.Count==0&&_viewport.CurrentScene.Volume.Diagonal<10;
                    _viewport.SetScene(scene);if(initialEmpty){_viewport.ResetView();_viewport.Zoom(3);}
                    _viewport.SelectElements(request.Preview.Faces.Select(f=>f.ElementId).Distinct());_outdoorOverlay?.InvalidateVisual();
                }catch(Exception ex){if(request.Generation==_outdoorGeneration)_status.Text="草稿预览失败："+ex.Message;}
            }
        }
        finally{_outdoorPreviewRunning=false;}
    }

    private async Task FinishOutdoorDrawingAsync()
    {
        if(_outdoor is not {} draft||!_outdoorParametersValid)return;
        if(draft.ArcEnd!=null){_status.Text="请先指定圆弧经过点。";return;}
        if(draft.Balcony&&!draft.Closed){_status.Text="请先闭合阳台轮廓，再完成。";return;}
        try {
            var geometry=draft.Geometry(_session.Model);string id,error;
            if(draft.Balcony)
            {
                var slab=geometry.Slabs.SingleOrDefault();if(slab==null){_status.Text="请先绘制有效阳台轮廓。";return;}
                var top=geometry.TopElevationOf(slab);
                var physical=StandardStoreyLayout.Materialize(_session.Model);
                var overlap=physical.Slabs.FirstOrDefault(s=>
                    (draft.OriginalSlab==null||StandardStoreyLayout.SourceElementId(_session.Model,s.Id)!=draft.OriginalSlab.Id)&&
                    Math.Min(top,physical.TopElevationOf(s))-Math.Max(top-slab.Thickness,physical.TopElevationOf(s)-s.Thickness)>.5
                    &&SlabGeometry.InteriorOverlaps(slab,s));
                if(overlap!=null){_outdoorConflict=overlap.Id;_status.Text="阳台与已有楼板重叠，请调整轮廓或选用已有楼板。";
                    if(_outdoorConflictButton!=null)_outdoorConflictButton.IsEnabled=true;return;}
                slab.Id=draft.OriginalSlab?.Id!;if(!_session.TryUpsertSlab(slab,out id,out error)){_status.Text=error;return;}
            }
            else if(!_session.TryAddWallPath(geometry.Walls,out id,out error)){_status.Text=error;return;}
            StopOutdoorDrawing();_selectedId=id;
            await RefreshModelAsync(draft.OriginalSlab!=null?"已更新阳台轮廓及参数 · Ctrl+Z 可撤销":
                draft.Balcony?"已绘制阳台 · Ctrl+Z 可撤销":"已绘制女儿墙路径 · Ctrl+Z 整条撤销");
        }catch(ArgumentException ex){_status.Text=ex.Message;}
    }
    private void StopOutdoorDrawing()
    {
        if(_outdoor==null)return;
        ClearOutdoorPreview(restoreOriginal:true);ClearOutdoorConflict();_outdoorConflictButton=null;
        ResetOutdoorReferences();_outdoorOffsetInput=null;
        _outdoorExcludedIds=new HashSet<string>();if(_outdoorOverlay!=null)_outdoorOverlay.OriginalPoints.Clear();
        _outdoor=null;_outdoorBase=null;_outdoorPhysical=null;_outdoorCursor=null;_outdoorTools.IsVisible=false;
        _outdoorPlanTools.IsVisible=false;_planCanvas.OutdoorDraft=null;_planCanvas.OutdoorGeometry=null;
        _planCanvas.OutdoorPath.Clear();_planCanvas.OutdoorReference.Clear();_planCanvas.InvalidateVisual();_storeyChooser.IsEnabled=true;
        RefreshProperties();_status.Text="已退出绘制，已完成的构件保留。";
    }
    private void CancelOutdoorDraft()
    {
        if(_outdoor?.HasDraft==true){ClearOutdoorConflict();_outdoor.Clear();_outdoorCursor=null;UpdateOutdoorPreview();_status.Text="已取消当前路径，可指定新起点；再次 Esc 退出。";}
        else StopOutdoorDrawing();
    }
    private bool OutdoorCommand(string command)
    {
        if(_outdoor is not {} d)return false;
        if(command.Equals("C",StringComparison.OrdinalIgnoreCase)){var error=d.Close();if(error!=null)_status.Text=error;UpdateOutdoorPreview();return true;}
        if(ModelCommandCatalog.TryDisplacement(command,out var x,out var y))
        {
            if(command.StartsWith('@')){if(d.Points.Count==0){_status.Text="相对坐标需要先指定起点。";return true;}x+=d.Points[^1].X;y+=d.Points[^1].Y;}
            OutdoorAdd(new(x,y));return true;
        }
        if(d.Points.Count>0&&ModelCommandCatalog.TryPolarLength(command,out var length,out var angle))
        {var a=angle*Math.PI/180;OutdoorAdd(new(d.Points[^1].X+length*Math.Cos(a),d.Points[^1].Y+length*Math.Sin(a)));return true;}
        if(d.Points.Count>0&&_outdoorCursor!=null&&ModelCommandCatalog.TryLength(command,out length))
        {var p=d.Points[^1];var distance=OutdoorDrawingDraft.Distance(p,_outdoorCursor);if(distance>=1)OutdoorAdd(new(p.X+(_outdoorCursor.X-p.X)/distance*length,p.Y+(_outdoorCursor.Y-p.Y)/distance*length));return true;}
        _status.Text="当前路径未完成，请先完成或退出绘制。";return true;
    }
    private BuildingModelDocument? _outdoorPhysical;
}
