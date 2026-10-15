using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Views;

internal static class ModelManagementUiTests
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    [STAThread] public static int Main()
    {
        try
        {
            var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var project=Path.GetFullPath(".artifacts/cad-model-manager/"+Guid.NewGuid().ToString("N"));
            var a=BuildingModelJson.ModelFilePath(project,"主楼");var b=BuildingModelJson.ModelFilePath(project,"副楼");
            BuildingModelProjectWorkflow.CreateNew(a,BuildingModelProjectWorkflow.Empty("主楼"));BuildingModelProjectWorkflow.CreateNew(b,BuildingModelProjectWorkflow.Empty("副楼"));
            BuildingModelCatalog.Register(project,a,"A","建筑平面.dwg");BuildingModelCatalog.Register(project,b,"B","另一图纸.dwg");
            string current=a;var count=0;
            foreach(var width in new[]{660d,1000d})
            {
                var window=new CadModelManagerWindow(project,"A",()=>current,async(owner,operation,entry)=>
                {await Task.Delay(20);count++;if(operation=="Open")current=entry.ModelPath;return current;}) {Width=width,Height=width==660?460:640};
                window.Show();Layout(window);Check(window.Models.Items.Count==2,"CAD manager list missing.");
                window.Scope.SelectedIndex=1;Layout(window);Check(window.Models.Items.Count==1,"CAD drawing filter failed.");
                window.Scope.SelectedIndex=0;window.Search.Text="副";Layout(window);Check(window.Models.Items.Count==1,"CAD search failed.");window.Search.Text="";
                window.Models.SelectedItem=window.Models.Items.Cast<BuildingModelCatalogEntry>().Single(e=>e.ModelPath==b);
                var task=window.Run("Open");Check(window.Busy,"Model operation can run twice.");awaitPump(task,window);
                Check(current==b&&window.SelectedModelPath==b,"CAD selection was not returned to registration.");
                Check(!window.Save.IsEnabled&&window.SaveAs.IsEnabled,"Disk-only save state misleading.");
                var root=(FrameworkElement)window.Content;Layout(window);Check(root.ActualWidth>600&&window.Models.ActualHeight>100,"CAD small window collapsed.");
                var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(project,"cad-manager-"+width+".png")))png.Save(output);
                window.Close();
            }
            Check(count==2,"Actions duplicated.");app.Shutdown();Console.WriteLine("MODEL_MANAGEMENT_CAD_UI_OK list filter search selection busy diskSave small large "+project);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static void Layout(Window window){window.UpdateLayout();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);}
    private static void awaitPump(Task task,Window window)
    {while(!task.IsCompleted){window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);System.Threading.Thread.Sleep(10);}task.GetAwaiter().GetResult();Layout(window);}
}
