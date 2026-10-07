using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Globalization;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private bool _openingSizeConflictBusy;
    private bool _openingParameterDraftInvalid;
    private Func<OpeningSizeConflictWindow,Task>? _openingSizeConflictCheck;

    private void UpdateOpeningParameterFeedback(string id,double[]? values,string? error)
    {
        var opening=_session.Model.Openings.FirstOrDefault(o=>o.Id==id);
        if(opening==null||_selectedId!=id)return;
        _openingParameterDraftInvalid=error!=null;
        var fields=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).ToArray();
        var code=fields.FirstOrDefault(f=>f.Name=="OpeningCode");
        var info=_properties.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Name=="OpeningSizePreview");
        if(values!=null&&values.Length>=4) {
            double Original(double input,double original)=>input==double.Parse(original.ToString("0.##",CultureInfo.InvariantCulture),CultureInfo.InvariantCulture)?original:input;
            var width=Original(values[1],opening.Width);var height=Original(values[2],opening.Height);
            var sizeChanged=width!=opening.Width||height!=opening.Height;
            var plan=sizeChanged?_session.PlanOpeningSizeCode(id,width,height,out _):null;
            if(code!=null){code.IsReadOnly=sizeChanged;code.Text=plan?.NewCode??opening.Code;}
            var caption=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBlock>()).FirstOrDefault(t=>t.Name=="OpeningCodeCaption");
            if(caption!=null)caption.Text=sizeChanged?"计划编号":"门窗编号";
            if(info!=null) {
                info.IsVisible=sizeChanged;
                info.Text=plan==null?"尺寸草稿未应用。":$"计划编号：{plan.NewCode}（未应用）";
                if(plan?.HasConflict==true)info.Text+=$" · {plan.Code} 已存在，应用时选择合并或新建";
            }
            var wall=_session.Model.Walls.FirstOrDefault(w=>w.Id==opening.HostWallId);
            var wallHeight=wall==null?0:wall.Height>0?wall.Height:_session.Model.FindStorey(wall.StoreyId)?.Height??0;
            var heightField=fields.FirstOrDefault(f=>f.Name=="OpeningHeight");
            if(heightField!=null)ToolTip.SetTip(heightField,$"洞口高（mm）；墙高 {wallHeight:0.##}，当前窗台下可用高度 {Math.Max(0,wallHeight-values[3]):0.##} mm");
        }
        var feedback=_properties.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Name=="OpeningParameterFeedback");
        if(feedback!=null) {
            feedback.IsVisible=error!=null;
            feedback.Text=error==null?"":error+"\n未应用；当前模型仍为 "+opening.Code+"。";
        }
        var apply=_properties.Children.OfType<Button>().FirstOrDefault(b=>b.Name=="ApplyOpeningGeometry");
        if(apply!=null)apply.IsEnabled=error==null;
        var presentation=_properties.Children.OfType<Button>().FirstOrDefault(b=>b.Name=="ApplyOpeningPresentation");
        if(presentation!=null)presentation.IsEnabled=error==null&&code?.IsReadOnly!=true;
        if(error!=null)_status.Text=error;
    }

    private async Task<OpeningSizeConflictChoice?> ResolveOpeningSizeConflictAsync(Window owner,string id,double width,double height)
    {
        var sourceId=StandardStoreyLayout.SourceElementId(_session.Model,id);
        var source=_session.Model.Openings.FirstOrDefault(o=>o.Id==sourceId);
        if(source==null){_status.Text="未找到门窗。";return null;}
        if(source.Width==width&&source.Height==height)return OpeningSizeConflictChoice.CreateNew;
        var plan=_session.PlanOpeningSizeCode(id,width,height,out var error);
        if(plan==null){_status.Text=error;return null;}
        if(!plan.HasConflict)return OpeningSizeConflictChoice.CreateNew;
        var dialog=new OpeningSizeConflictWindow(plan,width,height);
        Exception? failure=null;
        if(_openingSizeConflictCheck!=null)dialog.Opened+=async(_,_)=> {
            try{await _openingSizeConflictCheck(dialog);}catch(Exception ex){failure=ex;dialog.Close();}
        };
        var result=await dialog.ShowDialog<OpeningSizeConflictChoice?>(owner);
        if(failure!=null)throw failure;
        return result;
    }

    private async Task ApplyOpeningGeometryAsync(string id,IReadOnlyList<TextBox> fields,bool onlyInstance,Func<double[],double[]> normalize)
    {
        if(_openingSizeConflictBusy)return;
        var values=new double[fields.Count];
        for(var i=0;i<fields.Count;i++)if(!TryNumber(fields[i].Text,out values[i])) {
            _status.Text=$"第 {i+1} 个参数不是有效的毫米数值。";return;
        }
        values=normalize(values);
        var original=_session.Model;
        var trial=new BuildingModelEditSession(original);
        if(!trial.TrySetOpeningParameters(id,values[0],values[1],values[2],values[3],values.Length>4?values[4]:0,onlyInstance,out var error)) {
            UpdateOpeningParameterFeedback(id,values,error);return;
        }
        _openingSizeConflictBusy=true;
        try {
            var choice=await ResolveOpeningSizeConflictAsync(this,id,values[1],values[2]);
            if(!ReferenceEquals(original,_session.Model)||_selectedId!=id)return;
            if(choice==null){_status.Text="已取消应用，洞口尺寸草稿保留。";return;}
            await ApplyGeometryAsync(fields,_=> {
                var success=_session.TrySetOpeningParameters(id,values[0],values[1],values[2],values[3],values.Length>4?values[4]:0,onlyInstance,out var message,choice.Value);
                return (success,message);
            },onlyInstance?id:null);
        } finally {_openingSizeConflictBusy=false;}
    }

    private async Task RunOpeningSizeConflictCheckAsync()
    {
        var saved=_session;
        try {
            var model=SampleModelFactory.CreateEmptyModel("Opening code conflict");
            model.Walls.Add(new WallModel {Id="conflict-host",StoreyId="1F",X2=8000,Thickness=200});
            model.Walls.Add(new WallModel {Id="existing-host",StoreyId="1F",Y1=3000,X2=8000,Y2=3000,Thickness=200});
            foreach(var id in new[]{"conflict-a","conflict-b"})model.Openings.Add(new OpeningModel {Id=id,HostWallId="conflict-host",Code="M1825",Kind="门",Width=1800,Height=2500,Offset=id=="conflict-a"?2000:6000});
            model.Openings.Add(new OpeningModel {Id="existing",HostWallId="existing-host",Code="M1821",Kind="门",Width=1800,Height=2100,Offset=4000});
            model.OpeningTypes.Add(OpeningConstruction.Default(model.Openings[0]));
            var existing=OpeningConstruction.Default(model.Openings[2]);existing.GlassThickness=12;model.OpeningTypes.Add(existing);
            _session=new BuildingModelEditSession(model);await RefreshModelAsync("Conflict check");
            _workspaces.SelectedIndex=1;SelectById("conflict-a");
            var original=BuildingModelJson.ToJson(_session.Model);var revision=_session.Revision;
            TextBox Height()=>_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).Single(f=>f.Name=="OpeningHeight");
            async Task Draft(){Height().Text="2100";await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);await _parameterPreviewTask;}
            async Task Apply(string action,bool snapshot=false) {
                _openingSizeConflictCheck=async dialog=> {
                    await Task.Delay(150);
                    var merge=dialog.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="MergeOpeningType");
                    if(!merge.IsEnabled)throw new InvalidOperationException("Compatible opening type merge was disabled.");
                    if(BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("Conflict prompt committed before choosing.");
                    if(snapshot) {
                        var visual=Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(dialog);
                        if(visual!=null) {
                            Directory.CreateDirectory(".artifacts/opening-editor");
                            var image=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
                            image.Save(Path.GetFullPath(".artifacts/opening-editor/size-conflict.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                        }
                    }
                    if(action=="Escape")dialog.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Escape});
                    else dialog.GetVisualDescendants().OfType<Button>().Single(b=>b.Name==action).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                _properties.Children.OfType<Button>().Single(b=>b.Name=="ApplyOpeningGeometry").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await _geometryApplyTask;
            }
            await Draft();await Apply("Escape",true);
            if(_session.Revision!=revision||BuildingModelJson.ToJson(_session.Model)!=original||Height().Text!="2100")
                throw new InvalidOperationException("Conflict cancellation lost the draft or changed history.");
            await Apply("MergeOpeningType");
            var merged=_session.Model.Openings.Single(o=>o.Id=="conflict-a");
            if(merged.Code!="M1821"||merged.Height!=2100||OpeningConstruction.Resolve(_session.Model,merged).GlassThickness!=12
                ||_session.Model.Openings.Single(o=>o.Id=="conflict-b").Code!="M1825")throw new InvalidOperationException("Merge did not adopt the existing construction for the selected opening.");
            if(!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("Merge was not one undo transaction.");
            await RefreshModelAsync("New-type check");SelectById("conflict-a");await Draft();
            _properties.Children.OfType<ComboBox>().Single(c=>c.Name=="OpeningSizeScope").SelectedIndex=1;
            await _parameterPreviewTask;await Apply("CreateOpeningType");
            if(!_session.Model.Openings.Where(o=>o.Id.StartsWith("conflict-")).All(o=>o.Code=="M1821A"&&o.Height==2100)
                ||_session.Model.Openings.Single(o=>o.Id=="existing").Code!="M1821"||_session.Revision!=revision+3)
                throw new InvalidOperationException("Group suffix creation changed the existing type or formed multiple transactions.");
            if(!_session.Undo()||BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("Group suffix creation did not undo atomically.");
            Console.WriteLine("OPENING_CODE_CONFLICT_UI_OK automaticCode promptBeforeCommit cancelDraftRetained mergeAdoptsExisting groupSuffix existingUnchanged undo");
        } finally {_openingSizeConflictCheck=null;CancelParameterPreview(false);_session=saved;await RefreshModelAsync("Conflict check restored");}
    }

    private async Task RunOpeningRejectedSizeCheckAsync()
    {
        var saved=_session;
        try {
            var model=SampleModelFactory.CreateEmptyModel("C0722 opening height validation");
            model.FindStorey("1F").Height=3000;
            model.Walls.Add(new WallModel {Id="height-limit-wall",Code="W-3",StoreyId="1F",X2=2000,Thickness=200});
            var opening=new OpeningModel {Id="height-limit-window",Code="C0722",HostWallId="height-limit-wall",Kind="窗",Width=700,Height=2200,Sill=800,Offset=747.89530825003};
            model.Openings.Add(opening);model.OpeningTypes.Add(OpeningConstruction.Default(opening));
            _session=new BuildingModelEditSession(model);await RefreshModelAsync("Height limit check");
            _workspaces.SelectedIndex=1;SelectById(opening.Id);_planCanvas.Fit();
            var toggle=this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="展开属性栏");
            toggle?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var original=BuildingModelJson.ToJson(_session.Model);
            TextBox Field(string name)=>_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).Single(f=>f.Name==name);
            TextBlock Feedback()=>_properties.Children.OfType<TextBlock>().Single(t=>t.Name=="OpeningParameterFeedback");
            Button Apply()=>_properties.Children.OfType<Button>().Single(b=>b.Name=="ApplyOpeningGeometry");
            async Task Flush(){await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);await _parameterPreviewTask;await Task.Delay(100);}
            Field("OpeningWidth").Text="900";Field("OpeningHeight").Text="2600";await Flush();
            if(_parameterPreview!=null||BuildingModelJson.ToJson(_session.Model)!=original||_session.CanUndo)
                throw new InvalidOperationException("Rejected C0722 height changed the model or history.");
            if(!Feedback().IsVisible||new[]{"800","2600","3400","3000","2200","C0722"}.Any(s=>Feedback().Text?.Contains(s)!=true)
                ||Apply().IsEnabled||Field("OpeningCode").Text!="C0926"||!Field("OpeningCode").IsReadOnly)
                throw new InvalidOperationException("Rejected opening did not show its dimensions, available height, draft code and uncommitted state.");
            await SaveOpeningCheck("size-over-height.png");
            Field("OpeningHeight").Text="2200";await Flush();
            var preview=_parameterPreview?.Openings.Single();
            if(preview?.Width!=900||preview.Code!="C0922"||Feedback().IsVisible||!Apply().IsEnabled||Field("OpeningCode").Text!="C0922")
                throw new InvalidOperationException("Corrected window size did not preview its new code or clear the validation error.");
            Apply().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await _geometryApplyTask;
            if(_session.Model.Openings.Single().Code!="C0922"||_session.Model.Openings.Single().Width!=900||_session.Revision!=1)
                throw new InvalidOperationException("Corrected C0722 window did not commit dimensions and code together.");
            var committed=BuildingModelJson.ToJson(_session.Model);var scene=_viewport.CurrentScene;
            Field("OpeningHeight").Text="2000";await Flush();
            if(_parameterPreview==null)throw new InvalidOperationException("Intermediate valid size did not preview.");
            Field("OpeningHeight").Text="2600";await Flush();
            if(_parameterPreview!=null||!ReferenceEquals(scene,_viewport.CurrentScene)||BuildingModelJson.ToJson(_session.Model)!=committed||!Feedback().IsVisible)
                throw new InvalidOperationException("Rejected later draft left a stale earlier scene or changed the committed window.");
            _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Escape});await _parameterPreviewTask;
            if(Field("OpeningHeight").Text!="2200"||Field("OpeningCode").Text!="C0922"||Feedback().IsVisible||_session.Revision!=1)
                throw new InvalidOperationException("Escape did not restore committed inspector values without undoing.");
            _planCanvas.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Z,KeyModifiers=KeyModifiers.Control});await Task.Delay(300);
            if(BuildingModelJson.ToJson(_session.Model)!=original)throw new InvalidOperationException("Corrected width and automatic code did not undo together.");
            Console.WriteLine("OPENING_REJECTED_SIZE_UI_OK C0722 C0926draft height3400Over3000 limit2200 inlineError noMutation C0922valid appliedTogether staleSceneCleared escape undo");
        } finally {CancelParameterPreview(false);_session=saved;await RefreshModelAsync("Height limit check restored");}
    }
}
