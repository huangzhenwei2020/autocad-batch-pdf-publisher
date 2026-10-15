using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ModelNameWindow : Window
{
    internal readonly TextBox NameInput = new();
    internal readonly Button Commit = new() { Content="另存模型" };
    internal string ModelName => NameInput.Text?.Trim() ?? "";
    internal string TargetPath => BuildingModelJson.ModelFilePath(_project,ModelName);
    private readonly string _project;
    public ModelNameWindow(string project,string name)
    {
        _project=project;Title="另存建筑模型";Width=520;Height=250;MinWidth=360;MinHeight=230;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#151B23"));ComponentUi.Theme(this);
        var root=new Grid { Margin=new Thickness(18),RowDefinitions=new("*,Auto,Auto") };
        var fields=new StackPanel {Spacing=8};fields.Children.Add(new TextBlock {Text="新模型名称"});
        NameInput.Text=name+" 副本";fields.Children.Add(NameInput);
        var path=new TextBlock {TextWrapping=TextWrapping.Wrap};fields.Children.Add(path);
        root.Children.Add(fields);
        var error=new TextBlock {Foreground=Brushes.OrangeRed,TextWrapping=TextWrapping.Wrap};Grid.SetRow(error,1);root.Children.Add(error);
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=8};
        var cancel=new Button {Content="取消"};cancel.Click+=(_,_)=>Close(false);footer.Children.Add(cancel);footer.Children.Add(Commit);
        Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
        void Preview(){try{path.Text=TargetPath;}catch{path.Text="";}}
        NameInput.TextChanged+=(_,_)=>Preview();Preview();
        Commit.Click+=(_,_)=>{try{BuildingModelProjectWorkflow.ValidateName(ModelName);
            if(Directory.Exists(Path.GetDirectoryName(TargetPath)))throw new IOException("此模型名称已存在，请使用其他名称。");Close(true);}
            catch(Exception ex){error.Text=ex.Message;}};
    }
}
