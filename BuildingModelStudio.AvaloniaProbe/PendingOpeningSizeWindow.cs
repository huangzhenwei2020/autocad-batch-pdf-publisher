using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class PendingOpeningSizeWindow : Window
{
    internal readonly TextBox WidthInput=new(){Name="PendingWidth"},HeightInput=new(){Name="PendingHeight"},SillInput=new(){Name="PendingSill"};
    internal readonly Button Fit=new(){Name="FitPendingSize",Content="适配可用尺寸"},Apply=new(){Name="ApplyPendingSize",Content="确认放置"};
    private readonly TextBlock _limits=new(){TextWrapping=TextWrapping.Wrap},_feedback=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _code=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Func<double,double,double,PendingOpeningPlacementPlan> _plan;
    private bool _updating;
    internal Task ApplyTask=Task.CompletedTask;
    internal PendingOpeningPlacementPlan? CurrentPlan;
    internal Func<PendingOpeningPlacementPlan,Task<string?>>? Commit;
    internal PendingOpeningSizeWindow(CadPendingOpening opening,string wall,double sill,
        Func<double,double,double,PendingOpeningPlacementPlan> plan)
    {
        _plan=plan;Title="调整洞口尺寸 · "+opening.Code;Width=520;MinWidth=460;CanResize=false;SizeToContent=SizeToContent.Height;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(Color.Parse("#101925"));Foreground=Brushes.White;FontSize=13;
        var root=new StackPanel {Spacing=14,Margin=new Thickness(20)};
        root.Children.Add(new TextBlock {Text="洞口尺寸调整 / "+opening.Code,FontSize=20,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});
        root.Children.Add(new TextBlock {Text="目标墙："+wall,TextWrapping=TextWrapping.Wrap});root.Children.Add(_limits);
        void Field(string label,TextBox input,double value){input.Text=value.ToString("0",CultureInfo.InvariantCulture);input.MinHeight=34;
            input.Background=new SolidColorBrush(Color.Parse("#142131"));input.BorderBrush=new SolidColorBrush(Color.Parse("#34465A"));
            var row=new Grid {ColumnDefinitions=new("*,130")};row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});
            Grid.SetColumn(input,1);row.Children.Add(input);root.Children.Add(row);input.TextChanged+=(_,_)=>{if(!_updating)Update();};}
        Field("洞口宽 mm",WidthInput,opening.Width);Field("洞口高 mm",HeightInput,opening.Height);Field("窗台高 mm",SillInput,sill);
        root.Children.Add(_code);root.Children.Add(_feedback);
        var actions=new Grid {ColumnDefinitions=new("*,Auto,Auto"),ColumnSpacing=8};actions.Children.Add(Fit);Grid.SetColumn(Apply,1);actions.Children.Add(Apply);
        var cancel=new Button {Content="取消"};Grid.SetColumn(cancel,2);actions.Children.Add(cancel);root.Children.Add(actions);
        foreach(var button in new[]{Fit,Apply,cancel}){button.MinHeight=36;button.Padding=new Thickness(12,6);button.CornerRadius=new CornerRadius(4);
            button.Background=new SolidColorBrush(Color.Parse("#20364A"));button.BorderBrush=new SolidColorBrush(Color.Parse("#38556E"));}
        ToolTip.SetTip(Fit,"保持定位中心和窗台高，按墙端、相邻洞口及共用层高度缩小至可用尺寸。");
        Fit.Click+=(_,_)=>{if(CurrentPlan is not {SuggestedWidth:>0,SuggestedHeight:>0} current)return;
            _updating=true;WidthInput.Text=current.SuggestedWidth.ToString("0",CultureInfo.InvariantCulture);HeightInput.Text=current.SuggestedHeight.ToString("0",CultureInfo.InvariantCulture);_updating=false;Update();};
        Apply.Click+=(_,_)=>ApplyTask=ApplyAsync();cancel.Click+=(_,_)=>Close();Content=root;
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};Update();
    }
    private void Update()
    {
        bool Number(TextBox input,out double value)=>double.TryParse(input.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)&&double.IsFinite(value);
        if(!Number(WidthInput,out var width)||!Number(HeightInput,out var height)||!Number(SillInput,out var sill))
        {CurrentPlan=null;Apply.IsEnabled=Fit.IsEnabled=false;_feedback.Text="请输入完整有效的毫米数值，当前模型未修改。";_code.Text="";return;}
        CurrentPlan=_plan(Math.Round(width/10,0,MidpointRounding.AwayFromZero)*10,Math.Round(height/10,0,MidpointRounding.AwayFromZero)*10,Math.Round(sill));
        _limits.Text=$"当前位置可用宽 ≤ {Math.Floor(CurrentPlan.MaximumWidth):0} mm；当前窗台下可用高 ≤ {Math.Floor(CurrentPlan.MaximumHeight):0} mm";
        _code.Text=CurrentPlan.CodePlan==null?"":$"计划编号：{CurrentPlan.CodePlan.Code} · 实际洞口 {CurrentPlan.Width:0} × {CurrentPlan.Height:0} mm";
        _feedback.Text=CurrentPlan.Error??"尺寸可用，确认后写入模型。";_feedback.Foreground=new SolidColorBrush(Color.Parse(CurrentPlan.Error==null?"#9CACBD":"#FFB454"));
        Apply.IsEnabled=CurrentPlan.Error==null;Fit.IsEnabled=CurrentPlan.SuggestedWidth>0&&CurrentPlan.SuggestedHeight>0;
    }
    private async Task ApplyAsync()
    {
        Update();if(CurrentPlan==null||CurrentPlan.Error!=null||Commit==null)return;
        Apply.IsEnabled=Fit.IsEnabled=false;
        try{var error=await Commit(CurrentPlan);if(error==null){Close();return;}_feedback.Text="未应用："+error;}
        finally{UpdateEnabled();}
    }
    private void UpdateEnabled(){Apply.IsEnabled=CurrentPlan?.Error==null;Fit.IsEnabled=CurrentPlan?.SuggestedWidth>0&&CurrentPlan?.SuggestedHeight>0;}
}
