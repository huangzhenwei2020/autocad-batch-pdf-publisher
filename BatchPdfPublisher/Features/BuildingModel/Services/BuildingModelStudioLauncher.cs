using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Features.BuildingModel.Services
{
    /// <summary>
    /// 从 CAD 里启动建模程序（万落建筑模型）：直接打开**当前项目的模型**，
    /// 画完在 CAD 里用 <c>LTTZ</c> 落图（那条命令会先列出本项目的图纸/视图让你挑序号）。
    ///
    /// 找程序的位置（按顺序）：
    /// 1. 发布目录：插件 DLL 在 <c>&lt;发布根&gt;\CadApi\R24\</c>，新版建模程序在 <c>&lt;发布根&gt;\建筑模型\万落建筑模型.exe</c>；
    /// 2. 发布目录不存在时才让用户手动选择。旧版记忆路径不能悄悄覆盖当前 CAD 插件。
    /// </summary>
    internal static class BuildingModelStudioLauncher
    {
        public static void Open(Document document)
        {
            if (document == null) return;
            var editor = document.Editor;

            string projectFolder;
            string modelName;
            try
            {
                var project = new PublishPlanStore().GetActiveProject();
                projectFolder = project == null ? null : project.ProjectFolder;
                modelName = project == null ? null : project.Name;
            }
            catch (Exception exception)
            {
                editor.WriteMessage("\n读取当前项目失败：" + exception.Message);
                return;
            }
            if (string.IsNullOrWhiteSpace(projectFolder))
            {
                editor.WriteMessage("\n还没有打开/激活项目：先在启动器里建一个项目（或指定项目文件夹），再执行本命令。");
                return;
            }

            var executable = ResolveExecutable(editor);
            if (string.IsNullOrWhiteSpace(executable)) return;

            // 模型目录已经存在就直接打开它；不存在就把"项目文件夹 + 模型名称"给建模程序，由它新建
            var modelFolder = StudioLaunch.FindModelFolder(projectFolder, modelName);
            var arguments = StudioLaunch.BuildArguments(projectFolder, modelName);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? projectFolder
                });
                editor.WriteMessage("\n已启动建模程序：" + executable);
                editor.WriteMessage("\n项目：" + projectFolder + "　模型：" + (modelName ?? "（默认）")
                    + (modelFolder == null ? "（还不存在，程序里会按这个名字新建）" : ""));
                editor.WriteMessage("\n在建模程序里修改并保存模型后，点「生成 CAD 视图」或「推到 CAD」；回到 CAD 执行 LTTZ 落图。\n");
            }
            catch (Exception exception)
            {
                editor.WriteMessage("\n启动建模程序失败：" + exception.Message
                    + "\n可以直接双击：" + executable);
            }
        }

        /// <summary>只自动启动与当前 CAD 插件同包的建模程序；其他版本需用户明确选择。</summary>
        private static string ResolveExecutable(Autodesk.AutoCAD.EditorInput.Editor editor)
        {
            var candidates = new List<string>();
            candidates.AddRange(StudioLaunch.DefaultCandidates(PluginFolder()));
            var found = StudioLaunch.FindExecutable(candidates);
            if (found != null) return found;

            editor.WriteMessage("\n当前插件发布目录缺少「" + StudioLaunch.ExecutableName + "」。请选择要启动的程序，本次选择不覆盖当前版本。");
            using (var dialog = new OpenFileDialog
            {
                Title = "选择万落建筑模型.exe",
                Filter = "建模程序 (万落建筑模型.exe)|万落建筑模型.exe|可执行文件 (*.exe)|*.exe",
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return null;
                return dialog.FileName;
            }
        }

        private static string PluginFolder()
        {
            try
            {
                var location = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrWhiteSpace(location) ? null : Path.GetDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }

    }
}
