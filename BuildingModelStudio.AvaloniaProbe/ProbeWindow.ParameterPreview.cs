using Avalonia.Controls;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private BuildingModelDocument? _parameterPreview;
    private bool _slabPreviewReady;
    private Task _parameterPreviewTask=Task.CompletedTask;

    private void BindParameterPreview(IReadOnlyList<TextBox> fields,
        Func<BuildingModelEditSession,double[],bool> edit)
    {
        var selected=_selectedId;
        var lastInput=fields.Select(f=>f.Text).ToArray();
        foreach(var field in fields)field.TextChanged += (_,_) =>
        {
            if(selected!=_selectedId || field.Parent is not Control row || !_properties.Children.Contains(row))return;
            var input=fields.Select(f=>f.Text).ToArray();
            if(input.SequenceEqual(lastInput))return;
            lastInput=input;
            var values=new double[fields.Count];
            for(var i=0;i<fields.Count;i++)if(!TryNumber(fields[i].Text,out values[i]))return;
            var trial=new BuildingModelEditSession(_session.Model);
            if(edit(trial,values))_parameterPreviewTask = PreviewParametersAsync(trial.Model);
        };
        var cancel=InspectorButton("取消参数修改");
        cancel.Click += (_,_) => { CancelParameterPreview();RefreshProperties(); };
        _properties.Children.Add(cancel);
    }

    private async Task PreviewParametersAsync(BuildingModelDocument model)
    {
        _parameterPreview=model;
        var generation=++_sceneGeneration;
        _planCanvas.SetModel(model,(_storeyChooser.SelectedItem as StoreyItem)?.Id ?? "1F");
        _status.Text="参数预览 · 应用后提交 · Esc 恢复";
        try {
            var scene=await Task.Run(()=>ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(model)));
            if(generation==_sceneGeneration && ReferenceEquals(model,_parameterPreview))_viewport.SetScene(scene);
        } catch(Exception ex) {
            if(generation==_sceneGeneration)_status.Text="参数预览失败："+ex.Message;
        }
    }

    private void CancelParameterPreview()
    {
        if(_parameterPreview==null)return;
        _parameterPreview=null;++_sceneGeneration;
        _planCanvas.SetModel(_session.Model,(_storeyChooser.SelectedItem as StoreyItem)?.Id ?? "1F");
        _viewport.SetScene(ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(_session.Model)));
        _status.Text="已恢复参数修改前的模型。";
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
        async Task FlushPreview()
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);
            await _parameterPreviewTask;
        }
        try {
            _workspaces.SelectedIndex=0;
            var original=BuildingModelJson.ToJson(_session.Model);var revision=_session.Revision;
            var opening=_session.Model.Openings.First();var width=opening.Width;
            SelectById(opening.Id);
            var fields=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).ToArray();
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
            fields=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).ToArray();
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
