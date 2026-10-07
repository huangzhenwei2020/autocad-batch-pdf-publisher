using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class OpeningSizeConflictWindow : Window
{
    internal OpeningSizeConflictWindow(OpeningSizeCodePlan plan,double width,double height)
    {
        Title="门窗编号已存在";Width=560;CanResize=false;SizeToContent=SizeToContent.Height;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#101925"));Foreground=Brushes.White;FontSize=13;
        var root=new StackPanel {Margin=new Thickness(20),Spacing=14};
        root.Children.Add(new TextBlock {Text=$"编号 {plan.Code} 已存在",FontSize=20,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});
        root.Children.Add(new TextBlock {Text=$"新洞口：{width:0.##} × {height:0.##} mm",TextWrapping=TextWrapping.Wrap});
        root.Children.Add(new TextBlock {Text=plan.CanMerge
            ?"并入已有类型将沿用该类型的分格、材料和构造；已有门窗不修改。"
            :plan.MergeError,TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.Parse("#A7B8C5"))});
        root.Children.Add(new TextBlock {Text="新建编号："+plan.NewCode,TextWrapping=TextWrapping.Wrap});
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Right};
        Add("并入已有类型","MergeOpeningType",plan.CanMerge,OpeningSizeConflictChoice.MergeExisting);
        Add("加后缀新建","CreateOpeningType",true,OpeningSizeConflictChoice.CreateNew);
        Add("取消","CancelOpeningType",true,null);
        root.Children.Add(buttons);Content=root;
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
        void Add(string label,string name,bool enabled,OpeningSizeConflictChoice? choice)
        {
            var button=new Button {Name=name,Content=label,IsEnabled=enabled,MinHeight=36,Padding=new Thickness(12,6),
                Background=new SolidColorBrush(Color.Parse("#20364A")),BorderBrush=new SolidColorBrush(Color.Parse("#38556E")),
                BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4)};
            button.Click+=(_,_)=>Close(choice);buttons.Children.Add(button);
        }
    }
}
