using System.Diagnostics;
using System.Text.Json;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ComponentProjectionSet
{
    public List<ViewDocument> Views {get;set;}=new();
    public ViewDocument Plan {get;set;}=new();
}
internal static class ComponentImportService
{
    internal sealed record Result(byte[] Glb,ComponentGlbInspection Mesh,ComponentProjectionSet Projections,string Notice);
    internal static async Task<Result> LoadAsync(string path,double unit,string up,double yaw,CancellationToken token,Action<string>? progress=null,double sx=1,double sy=1,double sz=1)
    {
        token.ThrowIfCancellationRequested();
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WanLuo","ComponentImports",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var glb=Path.Combine(root,"model.glb");var views=Path.Combine(root,"views.json");
        try
        {
            progress?.Invoke("读取并转换模型…");
            string Number(double n)=>n.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
            var notice=await RunAsync("--component-import-worker",new[]{path,glb,Number(unit),up,Number(yaw),Number(sx),Number(sy),Number(sz)},root,token);
            progress?.Invoke("生成四向 CAD 轮廓及消隐…");
            await RunAsync("--component-views-worker",new[]{glb,views,Path.GetFileNameWithoutExtension(path)},root,token);
            token.ThrowIfCancellationRequested();
            var bytes=await File.ReadAllBytesAsync(glb,token);
            var mesh=await Task.Run(()=>ComponentGlbInspector.Inspect(bytes,token),token);
            var set=JsonSerializer.Deserialize<ComponentProjectionSet>(await File.ReadAllTextAsync(views,token))??throw new IOException("没有返回立面。");
            if(set.Views.Count!=4)throw new IOException("四向立面生成未完成。");
            return new(bytes,mesh,set,notice);
        }
        finally
        {
            // Only the two known temporary outputs of this operation are removed.
            foreach(var file in new[]{glb,views})try{if(File.Exists(file))File.Delete(file);}catch(IOException){}
            try{Directory.Delete(root);}catch(IOException){}
        }
    }
    private static async Task<string> RunAsync(string mode,string[] args,string root,CancellationToken token)
    {
        var exe=Path.Combine(AppContext.BaseDirectory,"万落建筑模型.exe");
        if(!File.Exists(exe))throw new FileNotFoundException("缺少导入组件，请更新完整程序。",exe);
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true,RedirectStandardOutput=true,WorkingDirectory=root};
        start.ArgumentList.Add(mode);foreach(var a in args)start.ArgumentList.Add(a);
        start.Environment["OMP_NUM_THREADS"]="2";
        using var p=Process.Start(start)??throw new IOException("导入组件未启动。");
        using var guard=new BlenderProcessGuard(p,2L<<30);
        p.PriorityClass=ProcessPriorityClass.BelowNormal;
        var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await p.WaitForExitAsync(timeout.Token);var text=await output;var errors=await error;
            if(p.ExitCode!=0)throw new IOException(string.IsNullOrWhiteSpace(errors)?"导入组件未完成，请检查模型文件或先分件、简化后重试。":errors.Length>1500?errors[..1500]:errors);
            return text;
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested){throw new IOException("本阶段处理已超过 3 分钟，已停止导入组件；请先分件或简化后重试。");}
        finally{if(!p.HasExited){p.Kill(true);await p.WaitForExitAsync();}await Task.WhenAll(output,error);}
    }
}
