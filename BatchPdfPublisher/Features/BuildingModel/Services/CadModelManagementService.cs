using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.Views;

namespace BatchPdfPublisher.Features.BuildingModel.Services
{
    internal static class CadModelManagementService
    {
        public static void CheckNative(Document document)
        {
            var root=Path.Combine(Path.GetTempPath(),"WanLuoModelsNative-"+Guid.NewGuid().ToString("N"));
            var a=BuildingModelJson.ModelFilePath(root,"A");var b=BuildingModelJson.ModelFilePath(root,"B");
            BuildingModelProjectWorkflow.CreateNew(a,BuildingModelProjectWorkflow.Empty("A"));
            BuildingModelProjectWorkflow.CreateNew(b,BuildingModelProjectWorkflow.Empty("B"));
            BuildingModelCatalog.Register(root,a,"native-drawing-A","A.dwg");BuildingModelCatalog.Register(root,b,"native-drawing-B","B.dwg");
            StudioLaunch.RememberActiveModel(root,a,"native-drawing-A");StudioLaunch.RememberActiveModel(root,b,"native-drawing-B");
            if(BuildingModelCatalog.List(root,a).Count!=2||StudioLaunch.ActiveModelPath(root,"B","native-drawing-A")!=a
                ||StudioLaunch.ActiveModelPath(root,"A","native-drawing-B")!=b)throw new InvalidDataException("原生模型清单或图纸来源串用。");
            StudioLaunch.RegisterSession(a);
            try
            {
                var request=BuildingModelFileRequests.Queue(a,"Save",root,"native-drawing-A");BuildingModelFileRequests.Validate(a,request);
                BuildingModelFileRequests.Complete(a,request.Id,a,null);
                if(!BuildingModelFileRequests.Result(a).Succeeded||File.Exists(BuildingModelFileRequests.RequestPath(a)))throw new InvalidDataException("原生会话确认失败。");
            }
            finally{StudioLaunch.RemoveSession(a);}
            var c=BuildingModelJson.ModelFilePath(root,"C");BuildingModelProjectWorkflow.CreateCopy(a,c,BuildingModelJson.LoadModel(a),"C");
            foreach(var icon in new[]{"file-plus","folder-open","save","copy","rotate-ccw"})
                using(var stream=typeof(CadModelManagerWindow).Assembly.GetManifestResourceStream("WanLuo.ModelManager."+icon+".png"))
                    if(stream==null)throw new InvalidDataException("管理图标未打包："+icon);
            document.Editor.WriteMessage("\nMODEL_MANAGEMENT_NATIVE_OK catalog twoDrawings sessionAck safeCopy icons "+root);
        }
        public static void Remember(Document document,string path)
        {
            var project=new PublishPlanStore().GetActiveProject();
            if(string.IsNullOrWhiteSpace(project?.ProjectFolder))throw new InvalidOperationException("请先在启动器中打开项目。");
            BuildingModelCatalog.Register(project.ProjectFolder,path,document.Database.FingerprintGuid.ToString(),document.Name);
            StudioLaunch.RememberActiveModel(project.ProjectFolder,path,document.Database.FingerprintGuid.ToString());
        }
        public static string Show(Document document,Window owner=null)
        {
            if(document==null)return null;
            var project=new PublishPlanStore().GetActiveProject();
            if(string.IsNullOrWhiteSpace(project?.ProjectFolder))throw new InvalidOperationException("请先在启动器中打开或新建项目。");
            var root=project.ProjectFolder;var fingerprint=document.Database.FingerprintGuid.ToString();
            string Current(){try{return StudioLaunch.ActiveModelPath(root,project.Name,fingerprint);}catch(FileNotFoundException){return null;}}
            var current=Current();if(current!=null)BuildingModelCatalog.Register(root,current,fingerprint,document.Name);
            var window=new CadModelManagerWindow(root,fingerprint,Current,async(dialog,operation,entry)=>
            {
                string path=null;
                if(operation=="New")path=CadModelProjectService.Create(dialog);
                if(operation=="Add")path=CadModelProjectService.Open(dialog);
                if(operation=="Open")path=entry?.Available==true?entry.ModelPath:null;
                if(path!=null)
                {
                    if(StudioLaunch.HasLiveSession(path))
                    {var activated=await Send(document,path,"Activate");Remember(document,activated);return activated;}
                    if(!BuildingModelStudioLauncher.OpenCurrentModel(document,path))throw new IOException("建筑模型窗口未启动，当前模型未切换。");
                    Remember(document,path);return path;
                }
                if(operation=="Save"||operation=="SaveAs")
                {
                    path=Current();if(path==null)throw new IOException("请先选择当前模型。");
                    string target=null,name=null;
                    if(operation=="SaveAs")
                    {
                        var naming=new CadModelNameWindow(root,BuildingModelJson.LoadModel(path).Name){Owner=dialog};
                        if(Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(naming)!=true)return null;
                        name=naming.ModelName;target=BuildingModelJson.ModelFilePath(root,name);
                    }
                    if(StudioLaunch.HasLiveSession(path))
                    {
                        var result=await Send(document,path,operation,target,name);
                        Remember(document,result);return result;
                    }
                    if(operation=="Save")throw new IOException("模型窗口未打开，磁盘文件已经保存。");
                    BuildingModelProjectWorkflow.CreateCopy(path,target,BuildingModelJson.LoadModel(path),name);
                    if(!BuildingModelStudioLauncher.OpenCurrentModel(document,target))throw new IOException("副本已保存，但模型窗口未启动。");
                    Remember(document,target);return target;
                }
                return null;
            }) {Owner=owner};
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(window);
            return window.SelectedModelPath;
        }
        public static async Task<string> Send(Document document,string path,string operation,string target=null,string name=null)
        {
            var project=new PublishPlanStore().GetActiveProject();
            var request=BuildingModelFileRequests.Queue(path,operation,project.ProjectFolder,document.Database.FingerprintGuid.ToString(),document.Name,target,name);
            try
            {
                for(var i=0;i<120;i++)
                {
                    await Task.Delay(250);var result=BuildingModelFileRequests.Result(path);
                    if(result?.Id!=request.Id)continue;
                    if(!result.Succeeded)throw new IOException(result.Error);return result.ModelPath;
                }
                throw new IOException("模型窗口尚未响应，请关闭模型窗口中的对话框后重试；未处理请求已取消。");
            }
            finally{BuildingModelFileRequests.Cancel(path,request.Id);}
        }
    }
}
