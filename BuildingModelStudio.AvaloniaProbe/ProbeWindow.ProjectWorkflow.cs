using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private bool _storeyRequestBusy, _storeyDialogOpen;
    private async Task NewModelAsync()
    {
        if(_cadImportBusy || _storeyRequestBusy||_modelCommandBusy||_loadingModel||_savingModel){_status.Text="模型更新正忙，请稍后新建。";return;}
        if(!await ConfirmSavedAsync())return;
        var dialog=new NewBuildingModelWindow(ModelProject);
        var path=await dialog.ShowDialog<string?>(this);
        if(path==null)return;
        _modelProjectFolder=dialog.ProjectFolder;
        await LoadModelAsync(path);
    }
    private async Task ReceiveCadStoreysAsync()
    {
        if(_cadImportBusy||_storeyRequestBusy||_storeyDialogOpen||_modelCommandBusy||_modelManagerOpen||_modelNameDialogOpen||_loadingModel||_savingModel||_filePath==null||!StudioLaunch.IsCurrentSession(_filePath))return;
        var path=_filePath;
        if(!File.Exists(BuildingModelProjectWorkflow.RequestPath(path)))return;
        _storeyRequestBusy=true;
        string? id=null;
        try
        {
            var request=BuildingModelProjectWorkflow.Load(path);id=request.Id;
            BuildingModelProjectWorkflow.ValidateRequest(request,_session.Model);
            var check=new BuildingModelEditSession(BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model)));
            if(!check.TryReplaceStoreys(request.Storeys,out var error,allowContentChanges:true))throw new InvalidDataException(error);
            // Apply through the live session so unsaved geometry and one-step undo are preserved.
            if(!_session.TryReplaceStoreys(request.Storeys,out error,allowContentChanges:true))throw new InvalidDataException(error);
            try
            {
                // Do not yield between commit and save: a user edit must not be undone on a failed write.
                BuildingModelJson.SaveModel(path,_session.Model);
                _savedJson=BuildingModelJson.ToJson(_session.Model);UpdateTitle();
            }
            catch { _session.Undo();throw; }
            await RefreshModelAsync("已从 CAD 更新并保存楼层 · Ctrl+Z 可撤销");
            BuildingModelProjectWorkflow.Complete(path,id,null);
        }
        catch(Exception ex)
        {
            _status.Text="CAD 楼层更新未完成："+ex.Message;
            if(id!=null)try {BuildingModelProjectWorkflow.Complete(path,id,ex.Message);}catch(Exception ack){_status.Text+=" · "+ack.Message;}
        }
        finally{_storeyRequestBusy=false;}
    }
}
