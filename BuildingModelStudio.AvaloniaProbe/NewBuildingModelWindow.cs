using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class NewBuildingModelWindow : Window
{
    internal readonly TextBox ModelName = new() { Text="建筑模型",MaxLength=80 };
    internal readonly Button Floors = new() { Content="楼层设置" };
    internal readonly Button Create = new() { Content="创建模型" };
    private readonly TextBlock _summary = new() { TextWrapping=TextWrapping.Wrap };
    private readonly TextBox _path = new() { IsReadOnly=true,TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground=Brushes.OrangeRed,TextWrapping=TextWrapping.Wrap };
    private string? _projectFolder;
    internal string? ProjectFolder => _projectFolder;
    internal BuildingModelDocument Draft {get;private set;} = BuildingModelProjectWorkflow.Empty("建筑模型");
    public NewBuildingModelWindow(string? projectFolder)
    {
        _projectFolder=projectFolder;
        Title="新建建筑模型";Width=520;Height=370;MinWidth=360;MinHeight=330;
        Background=new SolidColorBrush(Color.Parse("#151B23"));Foreground=new SolidColorBrush(Color.Parse("#E8F1F4"));
        FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=13;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new Grid {Margin=new Thickness(18),RowDefinitions=new RowDefinitions("*,Auto,Auto")};
        var body=new StackPanel {Spacing=10};body.Children.Add(new TextBlock {Text="模型名称"});body.Children.Add(ModelName);
        var floorRow=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto")};floorRow.Children.Add(_summary);Grid.SetColumn(Floors,1);floorRow.Children.Add(Floors);body.Children.Add(floorRow);
        body.Children.Add(new TextBlock {Text="模型路径"});
        var fileRow=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=8};fileRow.Children.Add(_path);
        var browse=ComponentUi.Tool("folder-open","",()=>_=ChooseFolderAsync());ToolTip.SetTip(browse,"选择项目目录");Grid.SetColumn(browse,1);fileRow.Children.Add(browse);body.Children.Add(fileRow);
        browse.IsEnabled=projectFolder==null;
        root.Children.Add(new ScrollViewer {Content=body});Grid.SetRow(_error,1);root.Children.Add(_error);
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=8,Margin=new Thickness(0,12,0,0)};
        var cancel=new Button {Content="取消"};cancel.Click+=(_,_)=>Close((string?)null);footer.Children.Add(cancel);footer.Children.Add(Create);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
        ModelName.TextChanged+=(_,_)=>Refresh();
        Floors.Click+=async(_,_)=>{
            var dialog=new StoreySettingsWindow(Draft);
            if(await dialog.ShowDialog<bool>(this)){Draft.Storeys=dialog.ResultStoreys;Refresh();}
        };
        Create.Click+=(_,_)=>{
            try
            {
                if(string.IsNullOrWhiteSpace(_projectFolder))throw new InvalidOperationException("请选择项目目录。");
                Draft.Name=ModelName.Text?.Trim()??"";
                var path=BuildingModelJson.ModelFilePath(_projectFolder,Draft.Name);
                BuildingModelProjectWorkflow.CreateNew(path,Draft);Close(path);
            }
            catch(Exception ex){_error.Text=ex.Message;}
        };
        Refresh();
    }
    private async Task ChooseFolderAsync()
    {
        var folders=await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {Title="选择模型所在项目目录",AllowMultiple=false});
        if(folders.Count==0)return;_projectFolder=folders[0].TryGetLocalPath();Refresh();
    }
    private void Refresh()
    {
        _summary.Text=$"楼层 {Draft.Storeys.Count} 层 · 首层 {Draft.Storeys.Min(s=>s.Elevation):0.##} mm";
        _path.Text=string.IsNullOrWhiteSpace(_projectFolder)?"尚未选择项目目录":BuildingModelJson.ModelFilePath(_projectFolder,ModelName.Text??"建筑模型");
    }
}
