using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.BuildingModelStudio
{
    internal static class MainFormPersistenceSelfTest
    {
        public static void Run(Action<string> log)
        {
            var root = Path.Combine(Path.GetTempPath(), "WanluoModelPersistenceTest", Guid.NewGuid().ToString("N"));
            var previousFolder = Program.StartupProjectFolder;
            var previousName = Program.StartupModelName;
            try
            {
                Program.StartupProjectFolder = root;
                Program.StartupModelName = "state-test";
                using (var form = new MainForm())
                {
                    var canvas = Find<PlanCanvas>(form);
                    if (canvas == null || canvas.Model == null) throw new InvalidOperationException("没有找到模型画布。");
                    var model = canvas.Model;
                    model.Walls.Add(new WallModel
                    {
                        Id = "test-wall", StoreyId = model.Storeys[0].Id,
                        X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, Thickness = 200
                    });
                    canvas.History.Push(model);
                    canvas.Undo();
                    var path = BuildingModelJson.ModelFilePath(root, "state-test");
                    var saved = BuildingModelJson.LoadModel(path);
                    if (canvas.Model.Walls.Count != 0 || saved.Walls.Count != 0)
                        throw new InvalidOperationException("撤销后画布与保存文件不一致。");
                    log("PASS 撤销后保存：画布与磁盘模型均无已撤销的墙");

                    // Simulate a destination which cannot be replaced. An output
                    // must not be generated from a model that failed to persist.
                    File.Delete(path);
                    Directory.CreateDirectory(path);
                    var generate = typeof(MainForm).GetMethod("GenerateViewsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                    ((Task)generate.Invoke(form, new object[] { false })).GetAwaiter().GetResult();
                    var modelFolder = BuildingModelJson.ModelFolder(root, "state-test");
                    var views = Path.Combine(modelFolder, "views");
                    if (Directory.Exists(views) && Directory.GetFiles(views, "*.json").Length != 0)
                        throw new InvalidOperationException("模型保存失败后仍生成了视图文件。");
                    log("PASS 保存失败：未生成模型视图");
                }
            }
            finally
            {
                Program.StartupProjectFolder = previousFolder;
                Program.StartupModelName = previousName;
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        private static T Find<T>(Control parent) where T : Control
        {
            foreach (Control child in parent.Controls)
            {
                var match = child as T;
                if (match != null) return match;
                match = Find<T>(child);
                if (match != null) return match;
            }
            return null;
        }
    }
}
