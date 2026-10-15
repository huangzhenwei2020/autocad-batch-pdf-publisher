using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ModelManagerWindow : Window
{
    internal readonly ListBox Models = new();
    internal readonly TextBox Search = new() {PlaceholderText="搜索模型名称"};
    internal readonly ComboBox Scope = new() {ItemsSource=new[]{"全部模型","当前 CAD 图纸"},SelectedIndex=0};
    internal readonly TextBlock Status = new() {TextWrapping=TextWrapping.Wrap};
    internal readonly Button Open;
    internal readonly Button Save;
    internal readonly Button SaveAs;
    private readonly Grid _root;
    private readonly TextBlock _detail=new() {TextWrapping=TextWrapping.Wrap};
    private readonly string _project;
    private readonly string? _fingerprint;
    private readonly Func<string?> _current;
    private readonly Func<ModelManagerWindow,string,BuildingModelCatalogEntry?,Task> _action;
    internal IReadOnlyList<BuildingModelCatalogEntry> Entries {get;private set;}=Array.Empty<BuildingModelCatalogEntry>();
    internal bool Busy {get;private set;}
    internal BuildingModelCatalogEntry? Selected => Models.SelectedItem as BuildingModelCatalogEntry;
    public ModelManagerWindow(string project,string? fingerprint,Func<string?> current,
        Func<ModelManagerWindow,string,BuildingModelCatalogEntry?,Task> action)
    {
        _project=project;_fingerprint=fingerprint;_current=current;_action=action;
        Title="模型管理";Width=920;Height=600;MinWidth=620;MinHeight=420;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#101923"));ComponentUi.Theme(this);
        Models.Background=Background;
        _root=new Grid {Margin=new Thickness(18),RowDefinitions=new("Auto,Auto,Auto,*,Auto,Auto"),RowSpacing=10};
        _root.Children.Add(new TextBlock {Text="模型管理",FontSize=22,FontWeight=FontWeight.SemiBold});
        var toolbar=new WrapPanel {Orientation=Orientation.Horizontal};
        Button Tool(string icon,string label,string operation){var b=ComponentUi.Tool(icon,label,()=>_=Run(operation));b.Margin=new Thickness(0,0,8,4);toolbar.Children.Add(b);return b;}
        Tool("file-plus","新建","New");Tool("folder-open","添加已有","Add");Open=Tool("folder-open","打开 / 切换","Open");
        Save=Tool("save","保存当前","Save");SaveAs=Tool("copy","另存当前","SaveAs");Tool("rotate-ccw","刷新","Refresh");
        Grid.SetRow(toolbar,1);_root.Children.Add(toolbar);
        var filter=new Grid {ColumnDefinitions=new("*,180"),ColumnSpacing=10};filter.Children.Add(Search);
        Grid.SetColumn(Scope,1);filter.Children.Add(Scope);Scope.IsEnabled=!string.IsNullOrWhiteSpace(fingerprint);
        Grid.SetRow(filter,2);_root.Children.Add(filter);
        var table=new Grid {RowDefinitions=new("34,*")};
        Grid Row(BuildingModelCatalogEntry? entry){var row=new Grid {ColumnDefinitions=new("*,65,120,145"),Margin=new Thickness(10,0),MinHeight=42};
            var values=entry==null?new[]{"模型名称","楼层","状态","修改时间"}:new[]{entry.Name,entry.Storeys.ToString(),entry.Status,entry.ModifiedText};
            for(var i=0;i<values.Length;i++){var text=new TextBlock {Text=values[i],TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(text,i);row.Children.Add(text);}
            if(entry!=null)ToolTip.SetTip(row,entry.Error??entry.ModelPath);return row;}
        table.Children.Add(new Border {BorderBrush=ComponentUi.Edge,BorderThickness=new Thickness(0,0,0,1),Child=Row(null)});
        Models.ItemTemplate=new FuncDataTemplate<BuildingModelCatalogEntry>((entry,_)=>Row(entry));
        Grid.SetRow(Models,1);table.Children.Add(Models);Grid.SetRow(table,3);_root.Children.Add(table);
        Grid.SetRow(_detail,4);_root.Children.Add(_detail);
        var footer=new Grid {ColumnDefinitions=new("*,Auto"),ColumnSpacing=10};footer.Children.Add(Status);
        var close=new Button {Content="关闭"};close.Click+=(_,_)=>Close();Grid.SetColumn(close,1);footer.Children.Add(close);
        Grid.SetRow(footer,5);_root.Children.Add(footer);Content=_root;
        Search.TextChanged+=(_,_)=>Filter();Scope.SelectionChanged+=(_,_)=>Filter();Models.SelectionChanged+=(_,_)=>Selection();
        Models.DoubleTapped+=(_,_)=>_=Run("Open");
        Closing+=(_,e)=>{if(Busy)e.Cancel=true;};
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape&&!Busy){Close();e.Handled=true;}};
        Refresh();
    }
    internal async Task Run(string operation)
    {
        if(Busy)return;Busy=true;_root.IsEnabled=false;
        try {Status.Text=operation=="Save"?"正在保存当前模型…":operation=="SaveAs"?"正在另存当前模型…":"正在处理…";if(operation!="Refresh")await _action(this,operation,Selected);else Status.Text="";Refresh();}
        catch(Exception ex){Status.Text=ex.Message;}
        finally{Busy=false;_root.IsEnabled=true;}
    }
    internal void Refresh()
    {
        try{Entries=BuildingModelCatalog.List(_project,_current());Filter();}
        catch(Exception ex){Status.Text=ex.Message;}
    }
    private void Filter()
    {
        var selected=Selected?.ModelPath;var query=Search.Text?.Trim()??"";
        var entries=Entries.Where(e=>(query.Length==0||e.Name.Contains(query,StringComparison.CurrentCultureIgnoreCase))
            &&(Scope.SelectedIndex!=1||BuildingModelCatalog.LinkedTo(e,_fingerprint!))).ToList();
        Models.ItemsSource=entries;Models.SelectedItem=entries.FirstOrDefault(e=>e.ModelPath==selected)??entries.FirstOrDefault(e=>e.IsCurrent)??entries.FirstOrDefault();Selection();
    }
    private void Selection()
    {
        Open.IsEnabled=Selected?.Available==true;
        var current=Entries.FirstOrDefault(e=>e.IsCurrent);Save.IsEnabled=SaveAs.IsEnabled=current?.Available==true;
        _detail.Text=Selected==null?"未找到模型":Selected.ModelPath+(Selected.DrawingText.Length==0?"":"\n关联图纸："+Selected.DrawingText);
    }
}
