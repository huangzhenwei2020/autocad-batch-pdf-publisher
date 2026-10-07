using Avalonia.Controls;
using BatchPdfPublisher.BuildingModel;
using Avalonia.VisualTree;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private bool _openingEditorBusy;
    private int _openingPreviewGeneration;
    private async Task OpenOpeningEditorAsync(string? id,bool check=false,bool cancelCheck=false)
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
        var original=_session.Model;var restore=_viewport.CurrentScene;var planPreviewActive=false;
        var count=physical.Openings.Count(o=>string.Equals(o.Code,opening.Code,StringComparison.OrdinalIgnoreCase));
        var window=new OpeningEditorWindow(opening,OpeningConstruction.Resolve(physical,opening),count,original.OpeningTemplates,original.OpeningEditorSnapStep??5);
        window.ConfirmSizeChange=async(type,single)=> {
            var trial=new BuildingModelEditSession(original);
            if(!trial.TrySetOpeningDefinition(id,type,single,out var error))return (null,error);
            return (await ResolveOpeningSizeConflictAsync(window,id,type.Width,type.Height),null);
        };
        window.SizeCodePreview=(width,height)=> {
            if(width==opening.Width&&height==opening.Height)return "编号 "+opening.Code;
            var plan=_session.PlanOpeningSizeCode(id,width,height,out _);
            return plan==null?"":plan.HasConflict?$"编号 {plan.Code} 已存在（应用时选择）":"编号 "+plan.NewCode;
        };
        Exception? checkFailure=null;
        var previousConflictCheck=_openingSizeConflictCheck;
        if(check)_openingSizeConflictCheck=dialog=> {
            dialog.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="CreateOpeningType")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        };
        if(check)window.Opened+=async (_,_)=> {try{await window.RunUiCheckAsync(cancelCheck);}catch(Exception ex){checkFailure=ex;window.Close();}};
        var fixedVolume=Task.Run(()=>BuildingVolumeBuilder.Build(original,includeOpeningParts:false));
        window.PreviewChanged+=(type,single)=>_ = PreviewAsync(type,single);
        try {
            var result=await window.ShowDialog<OpeningEditResult?>(this);
            ++_openingPreviewGeneration;_viewport.SetScene(restore);
            if(checkFailure!=null)throw checkFailure;
            if(result==null){_status.Text="已取消门窗分格修改。";return;}
            if(!ReferenceEquals(original,_session.Model)){_status.Text="模型已变化，请重新打开门窗编辑。";return;}
            if(!_session.TrySetOpeningDefinition(id,result.Type,result.OnlyInstance,out var error,templates:result.Templates,snapStep:result.SnapStep,conflictChoice:result.SizeConflictChoice)) {_status.Text=error;return;}
            await RefreshModelAsync("已应用门窗分格与三维构造 · 图纸需刷新或重新推到 CAD");
        } finally {
            _openingSizeConflictCheck=previousConflictCheck;
            _openingEditorBusy=false;++_openingPreviewGeneration;
            if(planPreviewActive)_planCanvas.SetModel(_session.Model,(_storeyChooser.SelectedItem as StoreyItem)?.Id??"1F");
        }
        async Task PreviewAsync(OpeningTypeModel type,bool single) {
            var generation=++_openingPreviewGeneration;
            try {
                var trial=new BuildingModelEditSession(original);
                if(!trial.TrySetOpeningDefinition(id,type,single,out var error)){_status.Text=error;return;}
                if(generation!=_openingPreviewGeneration||!_openingEditorBusy)return;
                _planCanvas.SetModel(trial.Model,(_storeyChooser.SelectedItem as StoreyItem)?.Id??"1F");planPreviewActive=true;
                if(check) {
                    if(BuildingModelJson.ToJson(_session.Model)!=BuildingModelJson.ToJson(original))
                        throw new InvalidOperationException("门窗草稿预览修改了正式模型。");
                    var sourceId=StandardStoreyLayout.SourceElementId(trial.Model,id)??id;
                    var instance=trial.Model.Openings.FirstOrDefault(o=>o.Id==sourceId);
                    var host=trial.Model.Walls.FirstOrDefault(w=>w.Id==instance?.HostWallId);
                    if(instance!=null&&host!=null) {
                        var previewModel=new BuildingModelDocument {
                            Walls=new(){host},Openings=new(){instance},OpeningTypes=trial.Model.OpeningTypes,
                            OpeningOverrides=trial.Model.OpeningOverrides};
                        var expected=OrthographicProjector.CreatePlanDetailSymbols(OpeningConstruction.ApplyOverrides(previewModel),host.StoreyId);
                        if(!_planCanvas.OpeningPreviewMatches(instance.Id,expected))
                            throw new InvalidOperationException("门窗草稿未同步主窗口平面。");
                        Console.WriteLine("OPENING_PLAN_DRAFT_PREVIEW_OK "+instance.Code);
                        if(_planCanvas.IsEffectivelyVisible&&_planCanvas.Bounds.Width>20) {
                            await Task.Delay(100);
                            var visual=Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(_planCanvas);
                            if(visual!=null) {
                                Directory.CreateDirectory(".artifacts/opening-editor");
                                var image=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
                                image.Save(Path.GetFullPath(".artifacts/opening-editor/owner-plan-draft.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                            }
                        }
                    }
                }
                var basis=type.Width==opening.Width&&type.Height==opening.Height?await fixedVolume
                    :await Task.Run(()=>BuildingVolumeBuilder.Build(trial.Model,includeOpeningParts:false));
                var timer=System.Diagnostics.Stopwatch.StartNew();
                var scene=await Task.Run(()=> {
                    var parts=BuildingVolumeBuilder.BuildOpeningParts(trial.Model);
                    var combined=new BuildingVolume {MinX=Math.Min(basis.MinX,parts.MinX),MinY=Math.Min(basis.MinY,parts.MinY),MinZ=Math.Min(basis.MinZ,parts.MinZ),
                        MaxX=Math.Max(basis.MaxX,parts.MaxX),MaxY=Math.Max(basis.MaxY,parts.MaxY),MaxZ=Math.Max(basis.MaxZ,parts.MaxZ),
                        Faces=basis.Faces.Concat(parts.Faces).ToList(),GuideLines=basis.GuideLines};
                    return ModelViewport.PrepareScene(combined);
                });
                if(generation==_openingPreviewGeneration && _openingEditorBusy){_viewport.SetScene(scene);if(check)Console.WriteLine("OPENING_SCENE_PREVIEW_MS "+timer.ElapsedMilliseconds);_status.Text="门窗草稿预览 · 应用后提交，取消恢复。";}
            } catch(Exception ex){if(check)checkFailure=ex;if(generation==_openingPreviewGeneration)_status.Text=ex.Message;}
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
            var floorId=physical.Walls.First(w=>w.Id==opening.HostWallId).StoreyId;
            _storeyChooser.SelectedItem=(_storeyChooser.ItemsSource as IEnumerable<StoreyItem>)?.FirstOrDefault(s=>s.Id==floorId);
            _workspaces.SelectedIndex=1;_planCanvas.Fit();
            var before=BuildingModelJson.ToJson(_session.Model);
            await OpenOpeningEditorAsync(opening.Id,true);
            var sourceId=StandardStoreyLayout.SourceElementId(_session.Model,opening.Id);
            var newCode=_session.Model.Openings.Single(o=>o.Id==sourceId).Code;
            var result=OpeningConstruction.Library(_session.Model).FindType(newCode);
            if(result==null || result.GlassThickness!=12)throw new InvalidOperationException("门窗编辑提交失败。");
            if(result.Height!=opening.Height-100||StandardStoreyLayout.Materialize(_session.Model).Openings
                .Where(o=>o.Code==newCode).Any(o=>o.Width!=result.Width||o.Height!=result.Height))
                throw new InvalidOperationException("分格编辑的整体洞口尺寸未提交给全部同编号门窗。");
            var changed=BuildingModelJson.ToJson(_session.Model);
            if(!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=before||!_session.Redo()||BuildingModelJson.ToJson(_session.Model)!=changed)throw new InvalidOperationException("门窗编辑撤销未完整恢复。");
            Console.WriteLine("OPENING_EDITOR_UI_OK liveScene=true cells="+BatchPdfPublisher.Models.DoorWindowElevationGeometryBuilder.ParseCellLayout(result.CustomCellLayout).Count);
            if(opening.Kind=="窗"&&(result.OpenAngle??0)>0) {
                var expected=BuildingVolumeBuilder.BuildOpeningParts(_session.Model,sourceId);
                var actual=_viewport.CurrentScene.Volume;
                string Points(BuildingVolume volume)=>string.Join(";",volume.Faces.Where(f=>f.Kind=="glass"&&StandardStoreyLayout.SourceElementId(_session.Model,f.ElementId)==sourceId)
                    .SelectMany(f=>f.Points).Select(p=>$"{p.X:R},{p.Y:R},{p.Z:R}"));
                if(Points(expected)!=Points(actual))throw new InvalidOperationException("分格应用后的主场景未采用开启窗的真实顶点。");
                var hostWall=_session.Model.Walls.Single(w=>w.Id==opening.HostWallId);
                var length=Math.Sqrt(Math.Pow(hostWall.X2-hostWall.X1,2)+Math.Pow(hostWall.Y2-hostWall.Y1,2));
                var ux=(hostWall.X2-hostWall.X1)/length;var uy=(hostWall.Y2-hostWall.Y1)/length;
                var points=expected.Faces.Where(f=>f.Kind=="glass"&&f.ElementId==sourceId).SelectMany(f=>f.Points).ToArray();
                if(points.Max(p=>-uy*p.X+ux*p.Y)-points.Min(p=>-uy*p.X+ux*p.Y)<100)throw new InvalidOperationException("应用后的窗仍停在墙厚平面内。");
                _workspaces.SelectedIndex=0;SelectById(sourceId);_viewport.SetDisplayMode(ModelViewport.DisplayMode.Solid);
                _viewport.RotateForBenchmark(MathF.PI);FrameActiveSelection();_viewport.SelectElements(Array.Empty<string>());
                var frame=_viewport.RenderedFrameCount;await WaitForFrameAsync(_viewport.CurrentScene.VertexCount,frame);
                await SaveOpeningCheck("window-scene-open.png");
                _workspaces.SelectedIndex=1;
                Console.WriteLine("WINDOW_SCENE_OPEN_UI_OK constructionAngle previewApplied mainSceneExactPoints GPU outsideWallDepth");
            }
            _session.Undo();await RefreshModelAsync("门窗编辑取消检查");
            await OpenOpeningEditorAsync(opening.Id,true,true);
            if(BuildingModelJson.ToJson(_session.Model)!=before)throw new InvalidOperationException("取消分格草稿改变正式模型。");
            var host=_session.Model.Walls.First(w=>w.Id==opening.HostWallId);
            var originalSymbols=OrthographicProjector.CreatePlanDetailSymbols(OpeningConstruction.ApplyOverrides(new BuildingModelDocument {
                Walls=new(){host},Openings=new(){opening},OpeningTypes=_session.Model.OpeningTypes,OpeningOverrides=_session.Model.OpeningOverrides}),host.StoreyId);
            if(!_planCanvas.OpeningPreviewMatches(opening.Id,originalSymbols))throw new InvalidOperationException("取消分格草稿未恢复平面。");
            Console.WriteLine("OPENING_PLAN_DRAFT_CANCEL_OK modelAndSymbolsRestored");
            await RefreshModelAsync("门窗编辑检查完成");
            _workspaces.SelectedIndex=0;_viewport.SetDisplayMode(ModelViewport.DisplayMode.Solid);_viewport.ResetView();
            var finalFrame=_viewport.RenderedFrameCount;await WaitForFrameAsync(_viewport.CurrentScene.VertexCount,finalFrame);
            return true;
        }catch(Exception ex){Console.Error.WriteLine("OPENING_EDITOR_UI_FAILED "+ex);return false;}
    }

}
