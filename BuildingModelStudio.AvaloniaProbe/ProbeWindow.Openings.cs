using Avalonia.Controls;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private bool _openingEditorBusy;
    private int _openingPreviewGeneration;
    private async Task OpenOpeningEditorAsync(string? id,bool check=false)
    {
        if(_openingEditorBusy || id==null)return;
        if(_workspaces.SelectedIndex==1 && !id.Contains("@STD@")) {
            var floor=_session.Model.FindStorey((_storeyChooser.SelectedItem as StoreyItem)?.Id);
            if(floor?.TemplateStoreyId!=null)id+="@STD@"+floor.Id;
        }
        var physical=StandardStoreyLayout.Materialize(_session.Model);
        var opening=physical.Openings.FirstOrDefault(o=>o.Id==id);
        if(opening==null)return;
        CancelParameterPreview();_openingEditorBusy=true;
        var original=_session.Model;var restore=_viewport.CurrentScene;
        var count=physical.Openings.Count(o=>string.Equals(o.Code,opening.Code,StringComparison.OrdinalIgnoreCase));
        var window=new OpeningEditorWindow(opening,OpeningConstruction.Resolve(physical,opening),count,original.OpeningTemplates,original.OpeningEditorSnapStep??5);
        Exception? checkFailure=null;
        if(check)window.Opened+=async (_,_)=> {try{await window.RunUiCheckAsync();}catch(Exception ex){checkFailure=ex;window.Close();}};
        var fixedVolume=Task.Run(()=>BuildingVolumeBuilder.Build(original,includeOpeningParts:false));
        window.PreviewChanged+=(type,single)=>_ = PreviewAsync(type,single);
        try {
            var result=await window.ShowDialog<OpeningEditResult?>(this);
            ++_openingPreviewGeneration;_viewport.SetScene(restore);
            if(checkFailure!=null)throw checkFailure;
            if(result==null){_status.Text="已取消门窗分格修改。";return;}
            if(!_session.TrySetOpeningConstruction(id,result.Type,result.OnlyInstance,out var error,result.Templates,result.SnapStep)) {_status.Text=error;return;}
            await RefreshModelAsync("已应用门窗分格与三维构造 · 图纸需刷新或重新推到 CAD");
        } finally {_openingEditorBusy=false;++_openingPreviewGeneration;}
        async Task PreviewAsync(OpeningTypeModel type,bool single) {
            var generation=++_openingPreviewGeneration;
            try {
                var trial=new BuildingModelEditSession(original);
                if(!trial.TrySetOpeningConstruction(id,type,single,out var error)){_status.Text=error;return;}
                var basis=await fixedVolume;
                var timer=System.Diagnostics.Stopwatch.StartNew();
                var scene=await Task.Run(()=> {
                    var parts=BuildingVolumeBuilder.BuildOpeningParts(trial.Model);
                    var combined=new BuildingVolume {MinX=Math.Min(basis.MinX,parts.MinX),MinY=Math.Min(basis.MinY,parts.MinY),MinZ=Math.Min(basis.MinZ,parts.MinZ),
                        MaxX=Math.Max(basis.MaxX,parts.MaxX),MaxY=Math.Max(basis.MaxY,parts.MaxY),MaxZ=Math.Max(basis.MaxZ,parts.MaxZ),
                        Faces=basis.Faces.Concat(parts.Faces).ToList(),GuideLines=basis.GuideLines};
                    return ModelViewport.PrepareScene(combined);
                });
                if(generation==_openingPreviewGeneration && _openingEditorBusy){_viewport.SetScene(scene);if(check)Console.WriteLine("OPENING_SCENE_PREVIEW_MS "+timer.ElapsedMilliseconds);_status.Text="门窗草稿预览 · 应用后提交，取消恢复。";}
            } catch(Exception ex){if(generation==_openingPreviewGeneration)_status.Text=ex.Message;}
        }
    }
    private async Task OpenOpeningTypesAsync()
    {
        if(_openingEditorBusy)return;
        var result=await new OpeningTypeManagerWindow(_session.Model).ShowDialog<OpeningTypeManagerResult?>(this);
        if(result==null)return;
        if(result.EditId!=null){await OpenOpeningEditorAsync(result.EditId);return;}
        if(!_session.TrySetOpeningConstructions(result.Types,out var error)){_status.Text=error;return;}
        await RefreshModelAsync("已批量应用门窗三维构造");
    }
    private async Task<bool> RunOpeningEditorCheckAsync()
    {
        try {
            var physical=StandardStoreyLayout.Materialize(_session.Model);
            var selectionIds=physical.Openings.Take(2).Select(o=>o.Id).ToArray();
            if(selectionIds.Length==2){SelectById(selectionIds[0]);SelectById(selectionIds[1],true);await Task.Delay(300);
                if(_viewport.SelectedElementCount!=2 || _viewport.HighlightedElementCount!=2)throw new InvalidOperationException("浏览器加选未同步三维高亮。");
                SelectById(selectionIds[0],true);await Task.Delay(150);if(_viewport.SelectedElementCount!=1)throw new InvalidOperationException("浏览器减选失败。");
                Console.WriteLine("BROWSER_MULTI_SELECTION_OK add=2 remove=1 gpuHighlight=true");}
            var requested=Environment.GetEnvironmentVariable("WANLUO_OPENING_CHECK_CODE");
            var opening=physical.Openings.First(o=>string.IsNullOrWhiteSpace(requested) ? o.Kind=="窗" : o.Code==requested);
            // The check picks from the full physical model, not the current plan
            // floor. Open it from the 3D workspace so a plan floor cannot remap it.
            _workspaces.SelectedIndex=0;
            var before=BuildingModelJson.ToJson(_session.Model);
            await OpenOpeningEditorAsync(opening.Id,true);
            var result=OpeningConstruction.Library(_session.Model).FindType(opening.Code);
            if(result==null || result.GlassThickness!=12)throw new InvalidOperationException("门窗编辑提交失败。");
            var changed=BuildingModelJson.ToJson(_session.Model);
            if(!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=before||!_session.Redo()||BuildingModelJson.ToJson(_session.Model)!=changed)throw new InvalidOperationException("门窗编辑撤销未完整恢复。");
            Console.WriteLine("OPENING_EDITOR_UI_OK liveScene=true cells="+BatchPdfPublisher.Models.DoorWindowElevationGeometryBuilder.ParseCellLayout(result.CustomCellLayout).Count);
            _session.Undo();await RefreshModelAsync("门窗编辑检查完成");
            return true;
        }catch(Exception ex){Console.Error.WriteLine("OPENING_EDITOR_UI_FAILED "+ex);return false;}
    }

}
