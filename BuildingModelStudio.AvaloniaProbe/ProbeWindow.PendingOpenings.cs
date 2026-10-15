using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private bool _pendingOpeningSizeBusy;
    private Task _pendingOpeningApplyTask=Task.CompletedTask;
    private Func<PendingOpeningSizeWindow,Task>? _pendingOpeningSizeCheck;
    private async Task AdjustPendingOpeningAsync(CadPendingOpening pending,string wallId,double offset)
    {
        if(_pendingOpeningSizeBusy)return;_pendingOpeningSizeBusy=true;
        var original=_session.Model;
        try
        {
            var initial=_session.PlanPendingOpeningPlacement(pending.StoreyId,pending.SourceHandle,wallId,offset);
            var wall=original.Walls.FirstOrDefault(w=>w.Id==wallId);if(wall==null)return;
            var dialog=new PendingOpeningSizeWindow(pending,wall.Code??wall.Id,initial.Sill,
                (width,height,sill)=>_session.PlanPendingOpeningPlacement(pending.StoreyId,pending.SourceHandle,wallId,offset,width,height,sill));
            dialog.Commit=async plan=>{
                if(!ReferenceEquals(original,_session.Model))return "模型已变化，请重新选择墙和洞口。";
                var changed=plan.Width!=pending.Width||plan.Height!=pending.Height;
                var choice=!changed&&plan.CodePlan.CanMerge?OpeningSizeConflictChoice.MergeExisting:OpeningSizeConflictChoice.CreateNew;
                if(plan.CodePlan.HasConflict&&(changed||!plan.CodePlan.CanMerge)){
                    var conflict=new OpeningSizeConflictWindow(plan.CodePlan,plan.Width,plan.Height,scope:"当前待放置洞口（共用层同步）");
                    if(_openingSizeConflictCheck!=null)conflict.Opened+=async(_,_)=>await _openingSizeConflictCheck(conflict);
                    var answer=await conflict.ShowDialog<OpeningSizeConflictChoice?>(dialog);
                    if(answer==null)return "已取消编号选择，尺寸草稿保留。";choice=answer.Value;
                }
                if(!ReferenceEquals(original,_session.Model))return "模型已变化，请重新选择墙和洞口。";
                if(!_session.TryPlacePendingOpening(pending.StoreyId,pending.SourceHandle,wallId,offset,plan.Width,plan.Height,plan.Sill,choice,out var id,out var error))return error;
                _planCanvas.ClearPendingPlacement();_selectedId=id;
                await RefreshModelAsync("已调整洞口并放置 "+_session.Model.Openings.Single(o=>o.Id==id).Code+" · Ctrl+Z 可撤销");return null;
            };
            Exception? failure=null;
            if(_pendingOpeningSizeCheck!=null)dialog.Opened+=async(_,_)=>{try{await _pendingOpeningSizeCheck(dialog);}catch(Exception ex){failure=ex;dialog.Close();}};
            await dialog.ShowDialog(this);if(failure!=null)throw failure;
        }
        finally{_pendingOpeningSizeBusy=false;}
    }
}
