using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Features.BuildingModel.Services
{
    internal static class CadModelProjectService
    {
        public static string Create(Window owner)
        {
            var project = new PublishPlanStore().GetActiveProject();
            if (string.IsNullOrWhiteSpace(project?.ProjectFolder)) throw new InvalidOperationException("请先在启动器中打开或新建项目。");
            var dialog = new CadModelProjectWindow(BuildingModelProjectWorkflow.Empty(project.Name ?? "建筑模型"), true) { Owner = owner };
            if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(dialog) != true) return null;
            var model = BuildingModelProjectWorkflow.Empty(dialog.ModelName); model.Storeys = dialog.ResultStoreys;
            var path = BuildingModelJson.ModelFilePath(project.ProjectFolder, model.Name);
            BuildingModelProjectWorkflow.CreateNew(path, model);
            StudioLaunch.RememberActiveModel(project.ProjectFolder, path); return path;
        }
        public static string Open(Window owner)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "打开建筑模型", Filter = "建筑模型 (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(owner) != true) return null;
            var model = BuildingModelJson.LoadModel(dialog.FileName); BuildingModelProjectWorkflow.ValidateStoreys(model.Storeys);
            var project = new PublishPlanStore().GetActiveProject(); StudioLaunch.RememberActiveModel(project?.ProjectFolder, dialog.FileName); return dialog.FileName;
        }
        public static async Task EditStoreys(Document document, Window owner, string path)
        {
            var original = BuildingModelJson.LoadModel(path);
            var dialog = new CadModelProjectWindow(original, false) { Owner = owner };
            if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(dialog) != true) return;
            var preview = BuildingModelJson.FromJson(BuildingModelJson.ToJson(original)); preview.Storeys = dialog.ResultStoreys;
            if (BuildingModelProjectWorkflow.StoreysHash(preview) == BuildingModelProjectWorkflow.StoreysHash(original)) return;
            if (MessageBox.Show(owner, "将应用并保存楼层设置。删除楼层或改为共用平面会同步处理本层构件；模型窗口中的其他修改也会一并保存。继续吗？", "楼层设置", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            if (!BuildingModelStudioLauncher.OpenCurrentModel(document, path)) throw new IOException("模型程序未启动，楼层尚未修改。");
            var request = BuildingModelProjectWorkflow.Queue(path, BuildingModelProjectWorkflow.StoreysHash(original), dialog.ResultStoreys);
            try
            {
                for (var i = 0; i < 120; i++)
                {
                    await Task.Delay(250);
                    var result = BuildingModelProjectWorkflow.Result(path);
                    if (result?.RequestId != request.Id) continue;
                    if (!result.Succeeded) throw new IOException(result.Error);
                    return;
                }
                throw new IOException("模型尚未确认楼层更新，请检查模型窗口后重试；未处理请求已取消。");
            }
            finally { BuildingModelProjectWorkflow.Cancel(path, request.Id); }
        }
    }
}
