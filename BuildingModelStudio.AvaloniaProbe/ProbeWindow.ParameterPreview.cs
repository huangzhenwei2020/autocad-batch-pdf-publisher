using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private BuildingModelDocument? _parameterPreview;
    private bool _slabPreviewReady;
    private Task _parameterPreviewTask=Task.CompletedTask;
    private Task _geometryApplyTask=Task.CompletedTask;
    private Task _openingApplyTask=Task.CompletedTask;
    private BuildingModelDocument? _sceneModel;
    private readonly SemaphoreSlim _parameterPreviewWorker=new(1,1);
    private int _parameterInputGeneration;
    private BuildingModelDocument? _parameterPreviewBaseModel;
    private ModelViewport.PreparedScene? _parameterPreviewBaseScene;
    private ModelViewport.PreparedScene? _parameterPreviewScene;

    private void BindParameterPreview(IReadOnlyList<TextBox> fields,
        Func<BuildingModelEditSession,double[],bool> edit, string? openingId=null,ComboBox? scope=null)
        =>BindParameterPreview(fields,(trial,values)=>(edit(trial,values),(string?)null),openingId,scope);

    private void BindParameterPreview(IReadOnlyList<TextBox> fields,
        Func<BuildingModelEditSession,double[],(bool Success,string? Error)> edit, string? openingId=null,ComboBox? scope=null)
    {
        var selected=_selectedId;
        var lastInput=fields.Select(f=>f.Text).ToArray();
        void Queue(bool force=false)
        {
            if(selected!=_selectedId || fields[0].Parent is not Control row || !_properties.Children.Contains(row))return;
            var input=fields.Select(f=>f.Text).ToArray();
            if(!force&&input.SequenceEqual(lastInput))return;
            lastInput=input;
            var generation=++_parameterInputGeneration;
            var values=new double[fields.Count];
            for(var i=0;i<fields.Count;i++)if(!TryNumber(fields[i].Text,out values[i])) {
                if(openingId!=null)UpdateOpeningParameterFeedback(openingId,null,"请输入完整的毫米数值，尚未应用。");
                return;
            }
            if(openingId!=null)UpdateOpeningParameterFeedback(openingId,values,null);
            _parameterPreviewTask=ScheduleParameterPreviewAsync(_session.Model,values,edit,generation,openingId);
        }
        foreach(var field in fields)field.TextChanged+=(_,_)=>Queue();
        if(scope!=null)scope.SelectionChanged+=(_,_)=>Queue(true);
        var cancel=InspectorButton("取消参数修改");
        cancel.Click += (_,_) => { CancelParameterPreview();RefreshProperties(); };
        _properties.Children.Add(cancel);
    }

    private async Task ScheduleParameterPreviewAsync(BuildingModelDocument original,double[] values,
        Func<BuildingModelEditSession,double[],(bool Success,string? Error)> edit,int generation,string? openingId)
    {
        await Task.Delay(180);
        await _parameterPreviewWorker.WaitAsync();
        try
        {
            if(generation!=_parameterInputGeneration || !ReferenceEquals(original,_session.Model))return;
            var outcome=await Task.Run(()=>
            {
                var trial=new BuildingModelEditSession(original);
                var result=edit(trial,values);
                return (Model:result.Success?trial.Model:null,result.Error);
            });
            if(generation!=_parameterInputGeneration || !ReferenceEquals(original,_session.Model))return;
            if(outcome.Model==null) {
                if(openingId!=null) {
                    // A rejected numeric draft must not leave an earlier valid preview on screen.
                    CancelParameterPreview();
                    UpdateOpeningParameterFeedback(openingId,values,outcome.Error??"洞口参数无效，尚未应用。");
                }
                return;
            }
            if(openingId!=null)UpdateOpeningParameterFeedback(openingId,values,null);
            await PreviewParametersAsync(outcome.Model,generation,openingId);
        }
        catch(Exception ex){if(generation==_parameterInputGeneration) {
            if(openingId!=null){CancelParameterPreview();UpdateOpeningParameterFeedback(openingId,values,"参数预览失败："+ex.Message);}
            else _status.Text="参数预览失败："+ex.Message;
        }}
        finally{_parameterPreviewWorker.Release();}
    }

    private async Task PreviewParametersAsync(BuildingModelDocument model,int? inputGeneration=null,string? openingId=null)
    {
        if(_parameterPreviewBaseModel==null)
        {
            _parameterPreviewBaseModel=_session.Model;
            _parameterPreviewBaseScene=ReferenceEquals(_sceneModel,_session.Model)?_viewport.CurrentScene:null;
        }
        _parameterPreview=model;
        _parameterPreviewScene=null;
        var generation=++_sceneGeneration;
        _planCanvas.SetModel(model,(_storeyChooser.SelectedItem as StoreyItem)?.Id ?? "1F");
        var beforeOpening=_session.Model.Openings.FirstOrDefault(o=>o.Id==openingId);
        var previewOpening=model.Openings.FirstOrDefault(o=>o.Id==openingId);
        var sizeInfo=_properties.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Name=="OpeningSizePreview");
        if(sizeInfo!=null&&beforeOpening!=null&&previewOpening!=null) {
            sizeInfo.IsVisible=beforeOpening.Width!=previewOpening.Width||beforeOpening.Height!=previewOpening.Height;
            sizeInfo.Text=$"尺寸预览：{previewOpening.Width:0.##} × {previewOpening.Height:0.##} mm · 编号 {previewOpening.Code}";
            var code=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).FirstOrDefault(t=>t.Name=="OpeningCode");
            if(code!=null){code.Text=sizeInfo.IsVisible?previewOpening.Code:beforeOpening.Code;code.IsReadOnly=sizeInfo.IsVisible;}
            var presentation=_properties.Children.OfType<Button>().FirstOrDefault(b=>b.Name=="ApplyOpeningPresentation");
            if(presentation!=null)presentation.IsEnabled=!sizeInfo.IsVisible;
            if(sizeInfo.IsVisible) {
                var plan=_session.PlanOpeningSizeCode(openingId!,previewOpening.Width,previewOpening.Height,out _);
                if(plan?.HasConflict==true)sizeInfo.Text+=$" · {plan.Code} 已存在，应用时选择合并或新建";
            }
        }
        _status.Text="参数预览 · 应用后提交 · Esc 恢复";
        var basis=_parameterPreviewBaseScene;
        var original=_parameterPreviewBaseModel;
        try {
            var scene=await Task.Run(()=>PrepareParameterScene(model,original,basis,openingId));
            if(generation==_sceneGeneration && ReferenceEquals(model,_parameterPreview)
                && (!inputGeneration.HasValue || inputGeneration==_parameterInputGeneration))
            { _parameterPreviewScene=scene;_viewport.SetScene(scene); }
        } catch(Exception ex) {
            if(generation==_sceneGeneration)_status.Text="参数预览失败："+ex.Message;
        }
    }

    private static ModelViewport.PreparedScene PrepareParameterScene(BuildingModelDocument model,
        BuildingModelDocument original,ModelViewport.PreparedScene? basis,string? openingId)
    {
        var before=original.Openings.FirstOrDefault(o=>o.Id==openingId);
        var after=model.Openings.FirstOrDefault(o=>o.Id==openingId);
        // Thresholds change the door parts, not the host opening or wall joins.
        if(basis!=null && before!=null && after!=null && before.Offset==after.Offset
            && before.Width==after.Width && before.Height==after.Height && before.Sill==after.Sill)
        {
            return PrepareOpeningEditScene(model,original,basis,openingId!);
        }
        return ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(model));
    }

    private void CancelParameterPreview(bool restoreScene=true)
    {
        ++_parameterInputGeneration;
        var hadPreview=_parameterPreview!=null;
        var restore=ReferenceEquals(_session.Model,_parameterPreviewBaseModel)?_parameterPreviewBaseScene:null;
        _parameterPreview=null;
        _parameterPreviewBaseModel=null;_parameterPreviewBaseScene=null;_parameterPreviewScene=null;
        if(hadPreview)++_sceneGeneration;
        if(!hadPreview || !restoreScene)return;
        _planCanvas.SetModel(_session.Model,(_storeyChooser.SelectedItem as StoreyItem)?.Id ?? "1F");
        if(restore!=null){_viewport.SetScene(restore);_sceneModel=_session.Model;}
        else _parameterPreviewTask=RestoreParameterSceneAsync(_session.Model,_sceneGeneration);
        _status.Text="已恢复参数修改前的模型。";
    }

    private async Task RestoreParameterSceneAsync(BuildingModelDocument model,int generation)
    {
        try
        {
            var scene=await Task.Run(()=>ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(model)));
            if(generation==_sceneGeneration && ReferenceEquals(model,_session.Model))
            {_viewport.SetScene(scene);_sceneModel=model;}
        }
        catch(Exception ex){if(generation==_sceneGeneration)_status.Text="恢复三维视图失败："+ex.Message;}
    }

    private static ModelViewport.PreparedScene PrepareOpeningEditScene(BuildingModelDocument model,
        BuildingModelDocument original,ModelViewport.PreparedScene basis,string openingId)
    {
        var before=original.Openings.Single(o=>o.Id==openingId);
        var after=model.Openings.Single(o=>o.Id==openingId);
        if(before.HostWallId==after.HostWallId && before.Code==after.Code && before.Kind==after.Kind
            && before.Offset==after.Offset && before.Width==after.Width && before.Height==after.Height
            && before.Sill==after.Sill && before.ThresholdHeight==after.ThresholdHeight
            && before.PlanFlipAlong==after.PlanFlipAlong && before.PlanFlipNormal==after.PlanFlipNormal
            && before.PlanOpenAngle==after.PlanOpenAngle && before.OpenIn3D==after.OpenIn3D)return basis;
        var parts=BuildingVolumeBuilder.BuildOpeningParts(model,openingId);
        var v=basis.Volume;
        var floors=new HashSet<string>();
        var wallFaces=new List<VolumeFace>();
        if(before.HostWallId!=after.HostWallId || before.Offset!=after.Offset
            || before.Width!=after.Width || before.Height!=after.Height || before.Sill!=after.Sill)
        {
            foreach(var document in new[]{original,model})
            {
                var host=document.Walls.Single(w=>w.Id==(ReferenceEquals(document,original)?before.HostWallId:after.HostWallId));
                floors.Add(host.StoreyId);
                foreach(var floor in document.Storeys.Where(s=>s.TemplateStoreyId==host.StoreyId))floors.Add(floor.Id);
            }
            foreach(var floor in floors)
                wallFaces.AddRange(BuildingVolumeBuilder.Build(model,floor,includeOpeningParts:false).Faces.Where(f=>f.Kind=="wall"));
        }
        var volume=new BuildingVolume
        {
            MinX=v.MinX,MinY=v.MinY,MinZ=v.MinZ,MaxX=v.MaxX,MaxY=v.MaxY,MaxZ=v.MaxZ,
            GuideLines=v.GuideLines,
            Faces=v.Faces.Where(f=>StandardStoreyLayout.SourceElementId(original,f.ElementId)!=openingId
                && !(f.Kind=="wall" && floors.Contains(f.StoreyId))).Concat(wallFaces).Concat(parts.Faces).ToList()
        };
        if(parts.Faces.Count>0)
        {
            volume.MinX=Math.Min(volume.MinX,parts.MinX);volume.MinY=Math.Min(volume.MinY,parts.MinY);volume.MinZ=Math.Min(volume.MinZ,parts.MinZ);
            volume.MaxX=Math.Max(volume.MaxX,parts.MaxX);volume.MaxY=Math.Max(volume.MaxY,parts.MaxY);volume.MaxZ=Math.Max(volume.MaxZ,parts.MaxZ);
        }
        return ModelViewport.PrepareScene(volume);
    }

    private void PreviewSlabParameters()
    {
        if(!_slabPreviewReady || _slabDraft==null || !ReadSlabDraft(out var draft))return;
        var original=_session.Model.Slabs.FirstOrDefault(s=>s.Id==draft!.Id);
        if(original!=null && _session.Model.TopElevationOf(original)==_session.Model.TopElevationOf(draft!))
        {
            // Initial control/template notifications and restoring the original values
            // must not restart a cancelled preview. A code rename has no geometry.
            var comparable=CopySlab(draft!);
            comparable.Code=original.Code;
            comparable.TopOffset=original.TopOffset;
            comparable.TopElevation=original.TopElevation;
            string Json(SlabModel slab)=>BuildingModelJson.ToJson(new BuildingModelDocument { Slabs=new() { slab } });
            if(Json(comparable)==Json(original)) { CancelParameterPreview();return; }
        }
        var trial=new BuildingModelEditSession(_session.Model);
        if(trial.TryUpsertSlab(draft!,out _,out _))_parameterPreviewTask = PreviewParametersAsync(trial.Model);
    }

    private async Task<bool> RunParameterPreviewCheckAsync()
    {
        TextBox[] OpeningFields()=>_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>())
            .Where(f=>f.Name is "OpeningOffset" or "OpeningWidth" or "OpeningHeight" or "OpeningSill" or "OpeningThresholdHeight").ToArray();
        async Task FlushPreview()
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);
            await _parameterPreviewTask;
        }
        try {
            await RunOpeningHeightInputCheckAsync();
            await RunOpeningApplyEscapeRaceCheckAsync();
            await RunOpeningRejectedSizeCheckAsync();
            await RunOpeningSizeScopeCheckAsync();
            await RunOpeningSizeConflictCheckAsync();
            _workspaces.SelectedIndex=0;
            _viewport.SetDisplayMode(ModelViewport.DisplayMode.Solid);
            var original=BuildingModelJson.ToJson(_session.Model);var revision=_session.Revision;
            var opening=_session.Model.Openings.First();var width=opening.Width;
            SelectById(opening.Id);
            var fields=OpeningFields();
            var frame=_viewport.RenderedFrameCount;
            foreach(var factor in new[] { .8,.7,.6 })fields[1].Text=(width*factor).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await FlushPreview();
            if(_parameterPreview?.Openings.First(o=>o.Id==opening.Id).Width!=width*.6
                || BuildingModelJson.ToJson(_session.Model)!=original || _session.Revision!=revision)
                throw new InvalidOperationException("门窗预览改写模型，或快速输入采用了旧值");
            await WaitForFrameAsync(ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(_parameterPreview)).VertexCount,frame);
            var valid=_parameterPreview;fields[1].Text="-";
            await FlushPreview();
            if(!ReferenceEquals(valid,_parameterPreview))throw new InvalidOperationException("不完整输入替换了有效预览");
            CancelActiveCommand();await _parameterPreviewTask;
            if(_parameterPreview!=null || BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("取消未恢复原模型");
            fields=OpeningFields();
            fields[1].Text=(width*.8).ToString(System.Globalization.CultureInfo.InvariantCulture);await FlushPreview();
            _properties.Children.OfType<Button>().First(b=>Equals(b.Content,"应用门窗参数"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if(_session.Revision!=revision+1 || _session.Model.Openings.First(o=>o.Id==opening.Id).Width!=width*.8)
                throw new InvalidOperationException("应用没有形成一次修改事务");
            if(!_session.Undo() || BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("预览应用不能一次撤销");
            await RefreshModelAsync("参数预览检查");
            var wall=_session.Model.Walls.First();SelectById(wall.Id);
            fields=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).ToArray();
            fields[1].Text=(wall.Thickness/2+WallReferenceGeometry.BodyOffset(wall)+20).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await FlushPreview();
            if(_parameterPreview?.Walls.First(w=>w.Id==wall.Id).Thickness!=wall.Thickness+20)throw new InvalidOperationException("墙厚未预览");
            CancelActiveCommand();
            SelectById(_session.Model.Slabs.First().Id);var thickness=_slabDraft!.Thickness;
            _showSlabProperties?.Invoke();
            await Task.Delay(100);
            _slabThickness!.Text=Mm(thickness+25);await FlushPreview();
            if(_parameterPreview?.Slabs.First(s=>s.Id==_selectedId).Thickness!=thickness+25)throw new InvalidOperationException("楼板厚度未预览："+_status.Text);
            CancelActiveCommand();
            await FlushPreview();
            if(_parameterPreview!=null || BuildingModelJson.ToJson(_session.Model)!=original)
                throw new InvalidOperationException("属性栏刷新重新触发了已取消的预览");
            Console.WriteLine("PARAMETER_PREVIEW_OK opening wall slab latest-input invalid-input cancel apply-single-undo GPU");
            return true;
        } catch(Exception ex) { Console.Error.WriteLine("PARAMETER_PREVIEW_FAILED "+ex);return false; }
    }

    private async Task RunOpeningApplyEscapeRaceCheckAsync()
    {
        var saved=_session;var workerHeld=false;
        try {
            var model=SampleModelFactory.CreateEmptyModel("Apply then immediate Escape");
            model.Walls.Add(new WallModel {Id="escape-host",StoreyId="1F",X2=5000,Thickness=200});
            var opening=new OpeningModel {Id="escape-door",HostWallId="escape-host",Kind="门",Code="M0921",Width=900,Height=2100,Offset=2000};
            model.Openings.Add(opening);model.OpeningTypes.Add(OpeningConstruction.Default(opening));
            _session=new BuildingModelEditSession(model);await RefreshModelAsync("Apply/Escape race check");
            _workspaces.SelectedIndex=1;SelectById(opening.Id);
            var original=BuildingModelJson.ToJson(_session.Model);
            await _parameterPreviewWorker.WaitAsync();workerHeld=true;
            var height=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).Single(f=>f.Name=="OpeningHeight");
            height.Text="2300";
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);
            await Task.Delay(200);
            if(_parameterPreviewTask.IsCompleted)throw new InvalidOperationException("Race fixture did not keep the parameter preview pending.");
            var pendingPreview=_parameterPreviewTask;
            _properties.Children.OfType<Button>().Single(b=>b.Name=="ApplyOpeningGeometry")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var apply=_geometryApplyTask;
            _planCanvas.RaiseEvent(new Avalonia.Input.KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Avalonia.Input.Key.Escape});
            _parameterPreviewWorker.Release();workerHeld=false;
            await apply;await pendingPreview;
            if(_session.Revision!=1||_session.Model.Openings.Single().Height!=2300||_session.Model.Openings.Single().Code!="M0923")
                throw new InvalidOperationException("Clicking Apply then Escape while preview was pending lost the confirmed dimensions/code.");
            if(_parameterPreview!=null||!ReferenceEquals(_sceneModel,_session.Model))throw new InvalidOperationException("Stale parameter preview replaced the confirmed scene.");
            var expected=ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(_session.Model));
            if(_viewport.CurrentScene.VertexCount!=expected.VertexCount)throw new InvalidOperationException("Immediate Escape left the previous mesh on screen.");
            for(var i=0;i<3;i++)_planCanvas.RaiseEvent(new Avalonia.Input.KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Avalonia.Input.Key.Escape});
            if(_session.Revision!=1||_session.Model.Openings.Single().Height!=2300)throw new InvalidOperationException("Repeated Escape undid the confirmed edit.");
            if(!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("Confirmed immediate-Escape edit did not undo exactly once.");
            Console.WriteLine("OPENING_APPLY_ESCAPE_RACE_OK pendingPreview applyImmediateEscape confirmedDimensionsCode latestScene repeatedEscape oneUndo");
        } finally {
            if(workerHeld)_parameterPreviewWorker.Release();
            CancelParameterPreview(false);_session=saved;await RefreshModelAsync("Apply/Escape race check restored");
        }
    }

    private async Task RunOpeningSizeScopeCheckAsync()
    {
        var saved=_session;
        try {
            var model=SampleModelFactory.CreateEmptyModel("Opening size scope");
            model.Walls.Add(new WallModel {Id="scope-host",StoreyId="1F",X2=8000,Thickness=200});
            foreach(var id in new[]{"scope-a","scope-b"})model.Openings.Add(new OpeningModel {Id=id,HostWallId="scope-host",Code="M1825",Kind="门",Width=1800,Height=2500,Offset=id=="scope-a"?2000:6000});
            model.OpeningTypes.Add(OpeningConstruction.Default(model.Openings[0]));
            _session=new BuildingModelEditSession(model);await RefreshModelAsync("Scope check");
            _workspaces.SelectedIndex=1;SelectById("scope-a");_planCanvas.Fit();
            var toggle=this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="展开属性栏");
            toggle?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var original=BuildingModelJson.ToJson(_session.Model);
            var scope=_properties.Children.OfType<ComboBox>().Single(c=>c.Name=="OpeningSizeScope");
            var height=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).Single(f=>f.Name=="OpeningHeight");
            async Task Flush() {await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);await _parameterPreviewTask;await Task.Delay(100);}
            scope.SelectedIndex=1;height.Text="2100";await Flush();
            if(_parameterPreview==null||!_parameterPreview.Openings.All(o=>o.Height==2100&&o.Code=="M1821"))throw new InvalidOperationException("Same-code scope did not preview all openings and update their codes.");
            scope.SelectedIndex=0;await Flush();
            if(_parameterPreview?.Openings.Single(o=>o.Id=="scope-a").Code!="M1821"||_parameterPreview.Openings.Single(o=>o.Id=="scope-b").Height!=2500)
                throw new InvalidOperationException("Scope switch did not replace the group preview with a newly numbered single opening.");
            if(BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("Scope preview changed the saved model.");
            var code=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).Single(f=>f.Name=="OpeningCode");
            if(code.Text!="M1821"||!code.IsReadOnly)throw new InvalidOperationException("Inspector code did not follow the dimension draft.");
            await SaveOpeningCheck("size-single-preview.png");
            scope.SelectedIndex=1;await Flush();
            _properties.Children.OfType<Button>().Single(b=>b.Name=="ApplyOpeningGeometry").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));await _geometryApplyTask;
            if(!_session.Model.Openings.All(o=>o.Height==2100&&o.Code=="M1821"))throw new InvalidOperationException("Group size apply failed.");
            var committed=BuildingModelJson.ToJson(_session.Model);var revision=_session.Revision;
            for(var i=0;i<2;i++)_planCanvas.RaiseEvent(new Avalonia.Input.KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Avalonia.Input.Key.Escape});
            if(_session.Revision!=revision||BuildingModelJson.ToJson(_session.Model)!=committed||!_session.CanUndo)
                throw new InvalidOperationException("Escape undid a committed dimension edit.");
            var expected=ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(_session.Model));
            if(_viewport.CurrentScene.VertexCount!=expected.VertexCount)throw new InvalidOperationException("Group size apply left other opening meshes stale.");
            var pendingHeight=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).Single(f=>f.Name=="OpeningHeight");
            // A second draft must cancel without discarding the previously committed edit.
            pendingHeight.Text="2000";await Flush();
            _planCanvas.RaiseEvent(new Avalonia.Input.KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Avalonia.Input.Key.Escape});await _parameterPreviewTask;
            if(_parameterPreview!=null||BuildingModelJson.ToJson(_session.Model)!=committed||_session.Revision!=revision)throw new InvalidOperationException("Escape draft cancellation changed the committed model.");
            _planCanvas.RaiseEvent(new Avalonia.Input.KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Avalonia.Input.Key.Z,KeyModifiers=Avalonia.Input.KeyModifiers.Control});
            await Task.Delay(300);
            if(BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("Ctrl+Z did not undo the committed size edit.");
            Console.WriteLine("OPENING_SIZE_SCOPE_UI_OK single group scopeSwitch automaticCode inspectorCode allMeshes oneUndo escapeNotUndo noMutation");
        } finally {CancelParameterPreview(false);_session=saved;await RefreshModelAsync("Scope check restored");}
    }
    private async Task RunOpeningHeightInputCheckAsync()
    {
        var savedSession=_session;
        try {
            var model=SampleModelFactory.CreateEmptyModel("Height input check");
            model.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=4000,Thickness=200});
            var opening=new OpeningModel {Id="height-door",HostWallId="host",Code="M1525",Kind="门",Width=1500,Height=2500,Offset=2000};
            var type=OpeningConstruction.Default(opening);type.DivisionPreset="自定义";
            type.CustomCellLayout=BatchPdfPublisher.Models.DoorWindowElevationGeometryBuilder.SerializeCellLayout(new[]{
                new BatchPdfPublisher.Models.DoorWindowLayoutCell {Right=750,Top=2200,IsDoor=true,Opening="左平开",Material="玻璃"},
                new BatchPdfPublisher.Models.DoorWindowLayoutCell {Left=750,Right=1500,Top=2200,IsDoor=true,Opening="右平开",Material="玻璃"},
                new BatchPdfPublisher.Models.DoorWindowLayoutCell {Bottom=2200,Right=1500,Top=2500,Opening="固定",Material="玻璃"}});
            model.Openings.Add(opening);model.OpeningTypes.Add(type);
            _session=new BuildingModelEditSession(model);
            await RefreshModelAsync("Height input regression");
            _workspaces.SelectedIndex=1;SelectById(opening.Id);_planCanvas.Fit();
            var original=BuildingModelJson.ToJson(_session.Model);var revision=_session.Revision;
            TextBox HeightField()=>_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).Single(f=>f.Name=="OpeningHeight");
            async Task Flush() {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);
                await _parameterPreviewTask;
                await Task.Delay(100);
            }
            var field=HeightField();
            foreach(var input in new[]{"","0","2","21"}) {
                field.Text=input;await Flush();
                if(_parameterPreview!=null||_session.Revision!=revision)throw new InvalidOperationException("Invalid height reached the canvas: "+input);
            }
            field.Text="2100";await Flush();
            if(_parameterPreview?.Openings.Single().Height!=2100||BuildingModelJson.ToJson(_session.Model)!=original)
                throw new InvalidOperationException("Valid height preview failed or changed the saved model.");
            _planCanvas.OpeningGripPoint(true);
            _properties.Children.OfType<Button>().Single(b=>b.Name=="ApplyOpeningGeometry")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await _geometryApplyTask;
            if(_session.Revision!=revision+1||!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=original)
                throw new InvalidOperationException("Height apply/undo was not atomic.");
            await RefreshModelAsync("Height cancel regression");SelectById(opening.Id);
            HeightField().Text="2200";await Flush();CancelActiveCommand();await _parameterPreviewTask;
            if(_parameterPreview!=null||BuildingModelJson.ToJson(_session.Model)!=original)
                throw new InvalidOperationException("Height cancel did not restore the model.");
            // Exercise the same invalid-grid exception recorded in the Windows crash log.
            var legacy=BuildingModelJson.FromJson(original);
            legacy.OpeningTypes.Single().Height=1000;
            _planCanvas.SetModel(legacy,"1F");_planCanvas.SetSelection(opening.Id);
            _planCanvas.OpeningGripPoint(true);
            await Task.Delay(200);
            await SaveOpeningCheck("height-invalid-grid.png");
            var pointer=new Avalonia.Input.Pointer(996,Avalonia.Input.PointerType.Mouse,true);
            var grip=_planCanvas.OpeningGripPoint(false);
            var root=_planCanvas.TranslatePoint(grip,this)!.Value;
            _planCanvas.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(_planCanvas,pointer,this,root,0,
                new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton,Avalonia.Input.PointerUpdateKind.LeftButtonPressed),Avalonia.Input.KeyModifiers.None,1));
            if(!_planCanvas.HasOpeningGrip)throw new InvalidOperationException("Invalid-grid movement grip did not activate.");
            var builds=_planCanvas.OpeningPreviewBuildCount;
            _planCanvas.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent,_planCanvas,pointer,this,root+new Avalonia.Vector(30,0),0,
                new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton,Avalonia.Input.PointerUpdateKind.Other),Avalonia.Input.KeyModifiers.None));
            await Task.Delay(100);await SaveOpeningCheck("height-invalid-grid-drag.png");
            if(_planCanvas.OpeningPreviewBuildCount<=builds)throw new InvalidOperationException("Invalid-grid drag did not exercise preview rendering.");
            _planCanvas.CancelDraft();pointer.Capture(null);
            Console.WriteLine("OPENING_HEIGHT_UI_OK slow-input invalid-grid render-grips preview apply undo cancel");
        }
        finally {
            CancelParameterPreview(false);_session=savedSession;
            await RefreshModelAsync("Height input check restored");
        }
    }
}
