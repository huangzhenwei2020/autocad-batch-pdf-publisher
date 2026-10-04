using Avalonia.Controls;
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
        Func<BuildingModelEditSession,double[],bool> edit, string? openingId=null)
    {
        var selected=_selectedId;
        var lastInput=fields.Select(f=>f.Text).ToArray();
        foreach(var field in fields)field.TextChanged += (_,_) =>
        {
            if(selected!=_selectedId || field.Parent is not Control row || !_properties.Children.Contains(row))return;
            var input=fields.Select(f=>f.Text).ToArray();
            if(input.SequenceEqual(lastInput))return;
            lastInput=input;
            var generation=++_parameterInputGeneration;
            var values=new double[fields.Count];
            for(var i=0;i<fields.Count;i++)if(!TryNumber(fields[i].Text,out values[i]))return;
            _parameterPreviewTask=ScheduleParameterPreviewAsync(_session.Model,values,edit,generation,openingId);
        };
        var cancel=InspectorButton("取消参数修改");
        cancel.Click += (_,_) => { CancelParameterPreview();RefreshProperties(); };
        _properties.Children.Add(cancel);
    }

    private async Task ScheduleParameterPreviewAsync(BuildingModelDocument original,double[] values,
        Func<BuildingModelEditSession,double[],bool> edit,int generation,string? openingId)
    {
        await Task.Delay(180);
        await _parameterPreviewWorker.WaitAsync();
        try
        {
            if(generation!=_parameterInputGeneration || !ReferenceEquals(original,_session.Model))return;
            var model=await Task.Run(()=>
            {
                var trial=new BuildingModelEditSession(original);
                return edit(trial,values)?trial.Model:null;
            });
            if(model==null || generation!=_parameterInputGeneration || !ReferenceEquals(original,_session.Model))return;
            await PreviewParametersAsync(model,generation,openingId);
        }
        catch(Exception ex){if(generation==_parameterInputGeneration)_status.Text="参数预览失败："+ex.Message;}
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
}
