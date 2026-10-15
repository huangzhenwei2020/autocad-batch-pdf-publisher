using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task CheckPendingOpeningSizesAsync()
    {
        var saved=_session;var selected=_selectedId;var conflict=_openingSizeConflictCheck;
        var model=SampleModelFactory.CreateEmptyModel("Pending aperture correction");model.Storeys.RemoveAll(s=>s.Id!="1F");model.Storeys[0].Height=3000;
        model.Storeys.Add(new(){Id="2F",TemplateStoreyId="1F",Elevation=3000,Height=2800});
        model.Walls.Add(new(){Id="target",StoreyId="1F",X2=6000,Thickness=200});
        model.Walls.Add(new(){Id="existing-host",StoreyId="1F",X2=10000,Y1=4000,Y2=4000,Thickness=200});
        var existing=new OpeningModel {Id="existing",Code="MLC6028",Kind="门联窗",HostWallId="existing-host",Offset=5000,Width=6000,Height=2800};
        model.Openings.Add(existing);var type=OpeningConstruction.Default(existing);type.GlassThickness=12;model.OpeningTypes.Add(type);
        model.CadImport=new(){PendingOpenings=new(){new(){StoreyId="1F",SourceHandle="pending",Code="MLC8035",Kind="门联窗",Width=8000,Height=3500,ReferencePosition=new(3000,1000)}}};
        try
        {
            _session=new(model);await RefreshModelAsync("Pending aperture correction");_workspaces.SelectedIndex=1;
            var before=BuildingModelJson.ToJson(_session.Model);var pending=_session.Model.CadImport.PendingOpenings.Single();
            void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
            void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            async Task Flush(){await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);await Task.Delay(50);}
            async Task Snapshot(Window window,string name){var visual=Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(window);
                if(visual==null)throw new InvalidOperationException("待放置尺寸窗口未绘制。");
                var image=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);Directory.CreateDirectory(".artifacts/opening-editor");
                image.Save(Path.GetFullPath(".artifacts/opening-editor/"+name),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);}
            _pendingOpeningSizeCheck=async dialog=>{
                await Task.Delay(150);Check(!dialog.Apply.IsEnabled&&dialog.Fit.IsEnabled,"超大洞口没有提示和适配入口。");
                Check(dialog.CurrentPlan!.SuggestedWidth==6000&&dialog.CurrentPlan.SuggestedHeight==2800,"待放置窗口没有显示实际可用限值。");
                await Snapshot(dialog,"pending-size-invalid.png");
                dialog.WidthInput.Text="abc";await Flush();Check(!dialog.Apply.IsEnabled&&!dialog.Fit.IsEnabled,"无效数字仍可放置。");
                dialog.WidthInput.Text="2600";dialog.HeightInput.Text="2400";await Flush();Check(dialog.Apply.IsEnabled,"手动修改有效洞口仍不能放置。");
                dialog.RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Escape});
            };
            await AdjustPendingOpeningAsync(pending,"target",3000);Check(BuildingModelJson.ToJson(_session.Model)==before,"取消尺寸草稿仍修改模型。");
            _pendingOpeningSizeCheck=async dialog=>{
                await Task.Delay(150);Click(dialog.Fit);await Flush();Check(dialog.WidthInput.Text=="6000"&&dialog.HeightInput.Text=="2800"&&dialog.Apply.IsEnabled,"自动适配没有更新字段或校验。");
                await Snapshot(dialog,"pending-size-fit.png");
                _openingSizeConflictCheck=d=>{d.Close();return Task.CompletedTask;};Click(dialog.Apply);await dialog.ApplyTask;
                Check(BuildingModelJson.ToJson(_session.Model)==before&&dialog.Apply.IsEnabled,"取消编号冲突丢失草稿或修改模型。");
                _openingSizeConflictCheck=d=>{Click(d.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="MergeOpeningType"));return Task.CompletedTask;};
                Click(dialog.Apply);await dialog.ApplyTask;
            };
            await AdjustPendingOpeningAsync(pending,"target",3000);
            var placed=_session.Model.Openings.Single(o=>o.HostWallId=="target");
            Check(placed.Width==6000&&placed.Height==2800&&placed.Offset==3000&&placed.Code=="MLC6028"&&_session.Model.CadImport.PendingOpenings.Count==0,"适配并入类型未实际放置。");
            Check(OpeningConstruction.Resolve(_session.Model,placed).GlassThickness==12&&_session.Model.Openings.Single(o=>o.Id=="existing").Width==6000,"并入未沿用做法或误改旧门窗。");
            var after=BuildingModelJson.ToJson(_session.Model);var folder=Path.GetFullPath(".artifacts/pending-opening-sizes");Directory.CreateDirectory(folder);
            var path=Path.Combine(folder,"model.json");BuildingModelJson.SaveModel(path,_session.Model);
            Check(BuildingModelJson.ToJson(BuildingModelJson.LoadModel(path))==after,"适配放置保存重开丢失。");
            Check(_session.Undo()&&BuildingModelJson.ToJson(_session.Model)==before&&_session.Redo()&&BuildingModelJson.ToJson(_session.Model)==after,"尺寸适配／编号／参考消失不是一步撤销。");
            _session.Undo();await RefreshModelAsync("Manual pending size");pending=_session.Model.CadImport.PendingOpenings.Single();
            _pendingOpeningSizeCheck=async dialog=>{dialog.WidthInput.Text="2600";dialog.HeightInput.Text="2400";await Flush();Click(dialog.Apply);await dialog.ApplyTask;};
            await AdjustPendingOpeningAsync(pending,"target",3000);
            placed=_session.Model.Openings.Single(o=>o.HostWallId=="target");Check(placed.Width==2600&&placed.Height==2400&&placed.Code=="MLC2624","手动输入未提交尺寸与编号。");
            Console.WriteLine("PENDING_SIZE_UI_OK oversized manual fit actualLimits standardHeight conflictCancel draftRetained mergeExisting unchangedOthers cancelledNoMutation atomicUndoRedo saveReload");
        }
        finally{_pendingOpeningSizeCheck=null;_openingSizeConflictCheck=conflict;_session=saved;await RefreshModelAsync("Pending size check restored");SelectById(selected);}
    }
}
