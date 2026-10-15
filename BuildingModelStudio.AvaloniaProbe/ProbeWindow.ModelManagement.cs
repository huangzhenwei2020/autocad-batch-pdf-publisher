using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private string? _modelProjectFolder=Program.ProjectFolder;
    private string? _modelDrawingFingerprint=Program.CadFingerprint;
    private string? _modelDrawingPath=Program.CadDrawingPath;
    private bool _modelManagerOpen, _modelCommandBusy, _savingModel, _loadingModel, _modelNameDialogOpen;
    private ModelManagerWindow? _modelManagerWindow;
    private string? ModelProject => _modelProjectFolder ?? BuildingModelCatalog.InferProject(_filePath!);
    private string _modelManagementWarning="";
    private void RememberCurrentModel()
    {
        _modelManagementWarning="";
        if(_filePath==null||ModelProject==null)return;
        try
        {
            BuildingModelCatalog.Register(ModelProject,_filePath,_modelDrawingFingerprint,_modelDrawingPath);
            StudioLaunch.RememberActiveModel(ModelProject,_filePath,_modelDrawingFingerprint);
        }
        catch(Exception ex){_modelManagementWarning=" · 管理记录未更新："+ex.Message;}
    }
    private async Task ManageModelsAsync()
    {
        if(_modelManagerOpen||_modelCommandBusy||_savingModel||_loadingModel||_cadImportBusy||_storeyRequestBusy)return;
        var project=ModelProject;
        if(project==null)
        {
            var folders=await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {Title="选择模型项目目录",AllowMultiple=false});
            project=folders.FirstOrDefault()?.TryGetLocalPath();if(project==null)return;
        }
        _modelProjectFolder=project;_modelManagerOpen=true;
        try
        {
            RememberCurrentModel();
            var manager=new ModelManagerWindow(project,_modelDrawingFingerprint,()=>_filePath,async(owner,operation,entry)=>
            {
                if(operation=="Save"){if(!await SaveModelAsync(false))throw new IOException(_status.Text);owner.Status.Text=_status.Text;return;}
                if(operation=="SaveAs"){if(await SaveManagedCopyAsync(owner))owner.Status.Text="已另存并切换到 "+_session.Model.Name;return;}
                string? path=null;
                if(operation=="Open")
                {
                    if(entry?.Available!=true)return;path=entry.ModelPath;
                    if(string.Equals(path,_filePath,StringComparison.OrdinalIgnoreCase)){owner.Status.Text="已是当前模型";return;}
                }
                if(operation=="Add")
                {
                    var files=await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title="添加已有建筑模型",AllowMultiple=false,
                        FileTypeFilter=new[]{new FilePickerFileType("建筑模型 JSON"){Patterns=new[]{"*.json"}}}});
                    path=files.FirstOrDefault()?.TryGetLocalPath();if(path==null)return;
                    BuildingModelCatalog.Register(project,path,_modelDrawingFingerprint,_modelDrawingPath);
                }
                if(path!=null&&string.Equals(path,_filePath,StringComparison.OrdinalIgnoreCase))
                {RememberCurrentModel();owner.Status.Text="已是当前模型";return;}
                if(path!=null&&StudioLaunch.HasLiveSession(path))
                {
                    await ActivateOtherModelAsync(path,project);owner.Status.Text="已切换 CAD 当前模型，并激活已打开的模型窗口";return;
                }
                if(!await ConfirmSavedAsync(owner))return;
                if(operation=="New")
                {
                    var dialog=new NewBuildingModelWindow(project);path=await dialog.ShowDialog<string?>(owner);
                }
                if(path!=null&&!await LoadModelAsync(path))throw new IOException(_status.Text);
                owner.Status.Text=path==null?"": "当前模型："+_session.Model.Name;
            });
            _modelManagerWindow=manager;
            await manager.ShowDialog(this);
        }
        catch(Exception ex){_status.Text="模型管理："+ex.Message;}
        finally{_modelManagerOpen=false;_modelManagerWindow=null;}
    }
    private async Task ActivateOtherModelAsync(string path,string project)
    {
        var request=BuildingModelFileRequests.Queue(path,"Activate",project,_modelDrawingFingerprint,_modelDrawingPath);
        try
        {
            for(var i=0;i<120;i++)
            {
                await Task.Delay(250);var result=BuildingModelFileRequests.Result(path);
                if(result?.Id!=request.Id)continue;
                if(!result.Succeeded)throw new IOException(result.Error);return;
            }
            throw new IOException("模型窗口尚未响应，请关闭该窗口中的对话框后重试。");
        }
        finally{BuildingModelFileRequests.Cancel(path,request.Id);}
    }
    private async Task<bool> SaveManagedCopyAsync(Window owner)
    {
        var project=ModelProject;
        if(project==null)throw new InvalidOperationException("请先在模型管理中选择项目目录。");
        var dialog=new ModelNameWindow(project,_session.Model.Name);
        _modelNameDialogOpen=true;
        try
        {
            if(!await dialog.ShowDialog<bool>(owner))return false;
            return await CopyCurrentModelAsync(dialog.TargetPath,dialog.ModelName);
        }
        finally{_modelNameDialogOpen=false;}
    }
    private async Task<bool> CopyCurrentModelAsync(string target,string name)
    {
        // Copy the live draft, not its last saved disk version; the source stays untouched.
        var snapshot=BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model));
        BuildingModelProjectWorkflow.CreateCopy(_filePath,target,snapshot,name);
        if(!await LoadModelAsync(target))throw new IOException("副本已保存，但未切换："+_status.Text);
        _status.Text="已另存并切换到 "+name;return true;
    }
    private async Task ReceiveModelFileRequestAsync()
    {
        if(_modelCommandBusy||_modelManagerOpen||_modelNameDialogOpen||_loadingModel||_savingModel||_cadImportBusy||_storeyRequestBusy||_storeyDialogOpen||OwnedWindows.Any(w=>w.IsVisible)
            ||_filePath==null||!StudioLaunch.IsCurrentSession(_filePath))return;
        var source=_filePath;
        if(!File.Exists(BuildingModelFileRequests.RequestPath(source)))return;
        _modelCommandBusy=true;string? id=null;var content=Content as Control;var enabled=content?.IsEnabled??true;
        if(content!=null)content.IsEnabled=false;
        try
        {
            var request=BuildingModelFileRequests.Load(source);id=request.Id;BuildingModelFileRequests.Validate(source,request);
            if(string.IsNullOrWhiteSpace(request.ProjectFolder))throw new InvalidDataException("模型请求缺少项目目录。");
            var project=Path.GetFullPath(request.ProjectFolder);
            if(request.Operation=="SaveAs")
            {
                var canonical=BuildingModelJson.ModelFilePath(project,request.TargetName);
                if(!string.Equals(Path.GetFullPath(request.TargetPath),Path.GetFullPath(canonical),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("另存目标与项目目录不一致。");
                _modelProjectFolder=project;_modelDrawingFingerprint=request.DrawingFingerprint;_modelDrawingPath=request.DrawingPath;
                await CopyCurrentModelAsync(canonical,request.TargetName);
            }
            else
            {
                if(request.Operation=="Save"&&!await SaveModelAsync(false))throw new IOException(_status.Text);
                _modelProjectFolder=project;_modelDrawingFingerprint=request.DrawingFingerprint;_modelDrawingPath=request.DrawingPath;
                RememberCurrentModel();
            }
            if(_modelManagementWarning.Length>0)throw new IOException("模型内容已保存，但未切换 CAD 管理记录："+_modelManagementWarning);
            BuildingModelFileRequests.Complete(source,id,_filePath,null);
            if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
            Activate();
        }
        catch(Exception ex)
        {
            _status.Text="CAD 模型操作未完成："+ex.Message;
            if(id!=null)try{BuildingModelFileRequests.Complete(source,id,source,ex.Message);}catch(Exception ack){_status.Text+=" · "+ack.Message;}
        }
        finally{if(content!=null)content.IsEnabled=enabled;_modelCommandBusy=false;}
    }
}
