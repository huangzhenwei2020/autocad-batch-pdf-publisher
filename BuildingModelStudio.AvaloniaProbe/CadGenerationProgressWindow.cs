using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class CadGenerationProgressWindow : Window
{
    internal readonly TextBlock Stage=new(){Text="读取生成请求",TextWrapping=TextWrapping.Wrap};
    internal readonly TextBlock Elapsed=new();
    internal readonly ProgressBar Bar=new(){IsIndeterminate=true,Minimum=0,Maximum=100,Height=6};
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private bool _finished;
    internal CadGenerationProgressWindow()
    {
        Title="生成建筑模型";Width=500;Height=210;MinWidth=420;MinHeight=200;CanResize=true;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=Brush.Parse("#1B2027");ComponentUi.Theme(this);
        var content=new StackPanel{Margin=new Thickness(22),Spacing=14};
        content.Children.Add(new TextBlock{Text="生成建筑模型",FontSize=20,FontWeight=FontWeight.SemiBold});
        content.Children.Add(Stage);content.Children.Add(Bar);content.Children.Add(Elapsed);Content=content;
        _timer.Tick+=(_,_)=>Elapsed.Text="已用时 "+_clock.Elapsed.ToString(@"mm\:ss");_timer.Start();
        Closing+=(_,e)=>{if(!_finished)e.Cancel=true;};Closed+=(_,_)=>_timer.Stop();
    }
    internal void Report(CadGenerationProgress step)
    {
        if(_finished)return;
        var definite=step.Total>0;
        var completed=Math.Clamp(step.Completed,0,Math.Max(0,step.Total));
        Stage.Text=step.Stage+(definite?$" · {completed}/{step.Total}（当前阶段 {completed*100/step.Total}%）":"");
        Bar.IsIndeterminate=!definite;Bar.Value=definite?completed*100d/step.Total:0;
    }
    internal void Finish(){_finished=true;_timer.Stop();Close();}
}

internal sealed class CadGenerationProgressPublisher : IDisposable
{
    private readonly string _path,_id;
    private readonly System.Threading.Timer _timer;
    private CadGenerationProgress _current=new(){Stage="读取生成请求"};
    private int _writing;
    internal CadGenerationProgressPublisher(string path,string id)
    {_path=path;_id=id;_timer=new(_=>Flush(),null,0,1000);}
    internal void Report(CadGenerationProgress progress)=>System.Threading.Volatile.Write(ref _current,progress);
    private void Flush()
    {
        if(System.Threading.Interlocked.Exchange(ref _writing,1)!=0)return;
        try
        {
            var step=System.Threading.Volatile.Read(ref _current);
            CadModelGenerationRequest.ReportProgress(_path,new(){RequestId=_id,Stage=step.Stage,State="running",Completed=step.Completed,Total=step.Total,UpdatedUtc=DateTime.UtcNow.ToString("O")});
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Runtime.Serialization.SerializationException){}
        finally{System.Threading.Volatile.Write(ref _writing,0);}
    }
    public void Dispose()=>_timer.Dispose();
}
