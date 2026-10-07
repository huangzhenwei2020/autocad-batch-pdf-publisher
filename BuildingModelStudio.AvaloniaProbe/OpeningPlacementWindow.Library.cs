using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Data;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using BatchPdfPublisher.BuildingModel;
using System.IO;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class OpeningPlacementWindow
{
    internal readonly Button NewAsset=new(){Height=32};
    internal readonly Button GridMode=new(){Height=32},ListMode=new(){Height=32};
    internal readonly TabControl PreviewTabs=new();
    internal readonly OpeningVolumePreview VolumePreview=new();
    internal readonly ScrollViewer SettingsScroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    internal bool LibraryLoaded {get;private set;}
    private readonly TextBlock _selectedCode=new(){FontSize=22,FontWeight=FontWeight.SemiBold};
    private readonly TextBlock _selectedName=new(){Foreground=Brushes.LightGray,TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly TextBlock _resourceInfo=new(){TextWrapping=TextWrapping.Wrap,FontSize=12};
    private readonly Button _import=new(){Height=32},_export=new(){Height=32};
    private readonly ComponentAssetLibrary _library=new(Environment.GetEnvironmentVariable("WANLUO_COMPONENT_LIBRARY_ROOT")??ComponentAssetLibrary.DefaultRoot);
    private readonly CancellationTokenSource _libraryCancellation=new();
    private string? _subcategory;
    private bool _listMode,_libraryBusy;
    private double _tileWidth=228;
    private Grid? _libraryBody;
    private StackPanel? _categoryItems;

    private Grid BuildLibraryLayout()
    {
        var edge=new SolidColorBrush(Color.Parse("#38505F"));
        var root=new Grid {RowDefinitions=new("64,*,60"),ColumnDefinitions=new("*,420")};
        var toolbar=new Grid {ColumnDefinitions=new("*,140,36,36,100,100"),ColumnSpacing=8,Margin=new Thickness(16,12)};
        toolbar.Children.Add(Search);Grid.SetColumn(SourceFilter,1);toolbar.Children.Add(SourceFilter);
        GridMode.Content=IconLabel("grid-3x3","");ListMode.Content=IconLabel("rows-2","");
        ToolTip.SetTip(GridMode,"缩略图网格");ToolTip.SetTip(ListMode,"列表");
        GridMode.Click+=(_,_)=>SetListMode(false);ListMode.Click+=(_,_)=>SetListMode(true);
        Grid.SetColumn(GridMode,2);toolbar.Children.Add(GridMode);Grid.SetColumn(ListMode,3);toolbar.Children.Add(ListMode);
        _import.Content=IconLabel("folder-open","导入");_import.Click+=async (_,_)=>await ImportPackageAsync();ToolTip.SetTip(_import,"导入参数资源包 (.wlopkg)");
        NewAsset.Content=IconLabel("file-plus","新建");NewAsset.Click+=async (_,_)=>await SavePublicAssetAsync();ToolTip.SetTip(NewAsset,"将当前参数保存为公共库资源");
        Grid.SetColumn(_import,4);toolbar.Children.Add(_import);Grid.SetColumn(NewAsset,5);toolbar.Children.Add(NewAsset);root.Children.Add(toolbar);
        _libraryBody=new Grid {ColumnDefinitions=new("200,*"),ColumnSpacing=12,Margin=new Thickness(16,0,12,8)};
        Grid.SetRow(_libraryBody,1);root.Children.Add(_libraryBody);
        var categories=new DockPanel();
        var categoryHeader=new Grid {ColumnDefinitions=new("*,32"),Height=40,Margin=new Thickness(8,0)};
        var categoryTitle=new TextBlock {Text="门窗分类",VerticalAlignment=VerticalAlignment.Center};categoryHeader.Children.Add(categoryTitle);
        var collapse=new Button {Content=IconLabel("chevron-left",""),Width=32,Height=32,Padding=new Thickness(4)};
        ToolTip.SetTip(collapse,"收起/展开分类");Grid.SetColumn(collapse,1);categoryHeader.Children.Add(collapse);
        collapse.Click+=(_,_)=>{
            var visible=_categoryItems!.IsVisible;_categoryItems.IsVisible=!visible;categoryTitle.IsVisible=!visible;
            _libraryBody.ColumnDefinitions[0].Width=new GridLength(visible?44:200);
            collapse.Content=IconLabel(visible?"chevron-right":"chevron-left","");
        };
        DockPanel.SetDock(categoryHeader,Dock.Top);categories.Children.Add(categoryHeader);
        _categoryItems=new StackPanel {Spacing=4};
        void CategoryButton(string text,string icon,string? category=null,string? subcategory=null){
            var button=new Button {Content=IconLabel(icon,text),Height=40,HorizontalAlignment=HorizontalAlignment.Stretch,
                HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(subcategory==null?12:28,0),Background=Brushes.Transparent,BorderThickness=new Thickness(0)};
            button.Click+=(_,_)=>{
                _subcategory=subcategory;Category.SelectedItem=category??"全部";Filter();
                foreach(var b in _categoryItems.Children.OfType<Button>())b.Background=Brushes.Transparent;
                button.Background=new SolidColorBrush(Color.Parse("#155D96"));
            };
            if(text=="全部门窗")button.Background=new SolidColorBrush(Color.Parse("#155D96"));
            _categoryItems.Children.Add(button);
        }
        CategoryButton("全部门窗","grid-3x3");CategoryButton("门","door-open","门");
        CategoryButton("平开门","door-open","门","平开");CategoryButton("推拉门","columns-2","门","推拉");
        CategoryButton("子母门","columns-2","门","子母");CategoryButton("其他门","door-open","门","其他");
        CategoryButton("窗","app-window","窗");CategoryButton("固定窗","app-window","窗","固定");
        CategoryButton("平开窗","app-window","窗","平开");CategoryButton("推拉窗","columns-2","窗","推拉");
        CategoryButton("其他窗","app-window","窗","其他");
        var categoryScroll=new ScrollViewer {Content=_categoryItems,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};categories.Children.Add(categoryScroll);
        collapse.Click+=(_,_)=>{categoryScroll.IsVisible=_categoryItems.IsVisible;categoryHeader.Margin=new Thickness(_categoryItems.IsVisible?8:4,0);categoryHeader.ColumnDefinitions=new(_categoryItems.IsVisible?"*,32":"0,32");};
        _libraryBody.Children.Add(new Border {Background=new SolidColorBrush(Color.Parse("#1A2834")),BorderBrush=edge,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Child=categories});
        Choices.Background=Brushes.Transparent;Choices.BorderThickness=new Thickness(0);
        Choices.ItemContainerTheme=new ControlTheme(typeof(ListBoxItem)) {Setters={
            new Setter(ListBoxItem.MarginProperty,new Thickness(0,0,8,8)),
            new Setter(ListBoxItem.TemplateProperty,new FuncControlTemplate<ListBoxItem>((item,_)=>{
                var presenter=new ContentPresenter {Name="PART_ContentPresenter",HorizontalAlignment=HorizontalAlignment.Stretch};
                presenter.Bind(ContentPresenter.ContentProperty,new Binding("Content") {Source=item});
                presenter.Bind(ContentPresenter.ContentTemplateProperty,new Binding("ContentTemplate") {Source=item});
                var frame=new Border {Child=presenter,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4)};
                void Highlight(){frame.BorderBrush=new SolidColorBrush(Color.Parse(item.IsSelected?"#079CEC":"#38505F"));frame.Background=new SolidColorBrush(Color.Parse(item.IsSelected?"#123049":"#111A25"));}
                item.PropertyChanged+=(_,e)=>{if(e.Property==ListBoxItem.IsSelectedProperty)Highlight();};Highlight();return frame;
            }))}};
        Choices.ItemTemplate=new FuncDataTemplate<OpeningPlacementEntry>((entry,_)=>BuildAssetTile(entry));
        Choices.ItemsPanel=new FuncTemplate<Panel?>(()=>new WrapPanel {Orientation=Orientation.Horizontal});
        Choices.SizeChanged+=(_,_)=>{if(_listMode)return;var width=Choices.Bounds.Width;var columns=width>=620?3:width>=350?2:1;
            var next=Math.Max(140,Math.Floor((width-12)/columns)-12);if(Math.Abs(next-_tileWidth)<2)return;_tileWidth=next;
            foreach(var card in Choices.GetVisualDescendants().OfType<Border>().Where(b=>b.Name=="AssetTile"))card.Width=_tileWidth;
        };
        Grid.SetColumn(Choices,1);_libraryBody.Children.Add(Choices);
        var settings=new StackPanel {Spacing=12,Margin=new Thickness(18,12,18,12)};
        settings.Children.Add(_selectedCode);settings.Children.Add(_selectedName);
        PlanPreview.Height=240;ElevationPreview.Height=240;VolumePreview.Height=240;
        PreviewTabs.ItemsSource=new[]{new TabItem {Header="平面",Content=PlanPreview},new TabItem {Header="立面",Content=ElevationPreview},new TabItem {Header="三维",Content=VolumePreview}};
        PreviewTabs.Styles.Add(new Style(s=>s.OfType<TabItem>()){Setters={new Setter(TabItem.FontSizeProperty,13d),new Setter(TabItem.HeightProperty,36d),new Setter(TabItem.MinHeightProperty,36d),new Setter(TabItem.PaddingProperty,new Thickness(20,4)),new Setter(TabItem.MinWidthProperty,100d)}});
        PreviewTabs.SelectedIndex=0;settings.Children.Add(PreviewTabs);
        var fields=new StackPanel {Spacing=8};
        void Field(string label,Control input){
            input.Height=32;ToolTip.SetTip(input,label);var row=new Grid {ColumnDefinitions=new("132,*"),ColumnSpacing=10,Height=32};
            row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});Grid.SetColumn(input,1);row.Children.Add(input);fields.Children.Add(row);
        }
        Field("门窗编号",CodeInput);Field("洞口宽 mm",WidthInput);Field("洞口高 mm",HeightInput);
        Field("窗台高 mm",SillInput);Field("门槛高 mm",ThresholdInput);
        Field("平面角度 °",AngleInput);Field("角度预设",AnglePresets);
        Field("三维显示开启",OpenIn3DInput);OpenIn3DInput.Content=null;settings.Children.Add(fields);
        settings.Children.Add(new Border {Height=1,Background=edge});settings.Children.Add(_resourceInfo);
        var actions=new Grid {ColumnDefinitions=new("*,Auto"),ColumnSpacing=8};
        SaveTemplate.Content=IconLabel("save","保存项目模板");SaveTemplate.Click+=(_,_)=>SaveCurrentTemplate();actions.Children.Add(SaveTemplate);
        _export.Content=IconLabel("send","导出");_export.Click+=async (_,_)=>await ExportPackageAsync();Grid.SetColumn(_export,1);actions.Children.Add(_export);settings.Children.Add(actions);
        SettingsScroll.Content=settings;
        var inspector=new Border {BorderBrush=edge,BorderThickness=new Thickness(1,0,0,0),Child=SettingsScroll};Grid.SetColumn(inspector,1);Grid.SetRowSpan(inspector,2);root.Children.Add(inspector);
        var footer=new Grid {ColumnDefinitions=new("*,Auto,Auto"),ColumnSpacing=12,Margin=new Thickness(18,10)};footer.Children.Add(_status);
        _status.VerticalAlignment=VerticalAlignment.Center;
        Place.Content=IconLabel("move","放置");Place.Background=new SolidColorBrush(Color.Parse("#078CE0"));Place.MinWidth=140;
        var cancel=new Button {Content="取消",Height=32,MinWidth=110};cancel.Click+=(_,_)=>Close();Grid.SetColumn(cancel,1);footer.Children.Add(cancel);Grid.SetColumn(Place,2);footer.Children.Add(Place);
        var footerBorder=new Border {BorderBrush=edge,BorderThickness=new Thickness(0,1,0,0),Child=footer};Grid.SetRow(footerBorder,2);Grid.SetColumnSpan(footerBorder,2);root.Children.Add(footerBorder);
        SizeChanged+=(_,_)=>{var small=Width<1100;root.ColumnDefinitions[1].Width=new GridLength(small?320:420);
            foreach(var tab in PreviewTabs.Items.OfType<TabItem>()){tab.MinWidth=0;tab.Width=((small?320:420)-60)/3d;tab.Padding=new Thickness(4);tab.HorizontalContentAlignment=HorizontalAlignment.Center;}
            root.RowDefinitions[0].Height=new GridLength(small?96:64);toolbar.RowDefinitions=new(small?"32,32":"40");
            Grid.SetColumnSpan(Search,small?4:1);Grid.SetRow(SourceFilter,small?1:0);Grid.SetColumn(SourceFilter,small?0:1);Grid.SetColumnSpan(SourceFilter,small?2:1);
            Grid.SetRow(GridMode,small?1:0);Grid.SetRow(ListMode,small?1:0);
            toolbar.ColumnDefinitions[4].Width=new GridLength(Width<1100?72:100);toolbar.ColumnDefinitions[5].Width=new GridLength(Width<1100?72:100);
            if(small&&_categoryItems.IsVisible)collapse.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));};
        Closed+=(_,_)=>_libraryCancellation.Cancel();SetListMode(false);return root;
    }

    private Control BuildAssetTile(OpeningPlacementEntry? entry)
    {
        if(entry==null)return new Border();
        var panel=new StackPanel {Spacing=5,Margin=new Thickness(12)};
        if(!_listMode){var preview=new OpeningVolumePreview {Height=180};preview.SetType(entry.Type);panel.Children.Add(preview);}
        panel.Children.Add(new TextBlock {Text=entry.Type.Code,FontSize=15,FontWeight=FontWeight.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis});
        var detail=new Grid {ColumnDefinitions=new("*,Auto"),ColumnSpacing=4};
        detail.Children.Add(new TextBlock {Text=entry.Name,TextTrimming=TextTrimming.CharacterEllipsis,FontSize=12});
        var size=new TextBlock {Text=$"{entry.Type.Width:0.#} × {entry.Type.Height:0.#}",FontSize=12,Foreground=Brushes.LightGray};Grid.SetColumn(size,1);detail.Children.Add(size);panel.Children.Add(detail);
        if(_listMode)panel.Children.Add(new TextBlock {Text=entry.Source,FontSize=11,Foreground=Brushes.LightGray});
        var result=new Border {Name="AssetTile",Width=_listMode?double.NaN:_tileWidth,Child=panel};
        ToolTip.SetTip(result,$"{entry.Name} · {entry.Source} · {entry.Type.Code}");return result;
    }

    internal void SetListMode(bool list)
    {
        _listMode=list;GridMode.Background=list?Brushes.Transparent:new SolidColorBrush(Color.Parse("#097DBA"));ListMode.Background=list?new SolidColorBrush(Color.Parse("#097DBA")):Brushes.Transparent;
        Choices.ItemsPanel=new FuncTemplate<Panel?>(()=>list?new StackPanel():new WrapPanel {Orientation=Orientation.Horizontal});
        Choices.ItemTemplate=new FuncDataTemplate<OpeningPlacementEntry>((entry,_)=>BuildAssetTile(entry));
    }

    private bool MatchesSubcategory(OpeningPlacementEntry entry)
    {
        if(_subcategory==null)return true;
        var text=entry.Name+entry.Type.ElevationType+entry.Type.PlanStyle+entry.Type.CustomCellLayout;
        if(_subcategory=="其他")return !new[]{"平开","推拉","子母","固定"}.Any(s=>text.Contains(s,StringComparison.Ordinal));
        return text.Contains(_subcategory,StringComparison.Ordinal);
    }

    private void UpdateSelectionHeading()
    {
        var entry=Choices.SelectedItem as OpeningPlacementEntry;
        _selectedCode.Text=entry?.Type.Code??"未选择门窗";_selectedName.Text=entry?.Name??"";
        _export.IsEnabled=!_libraryBusy&&entry?.Asset!=null;
        _resourceInfo.Text=entry==null?"":entry.Asset==null?$"来源：{entry.Source}\n平面 / 立面 / 三维：参数生成":$"来源：公共库 · 修订 {entry.Asset.Manifest.Revision}\n平面 / 立面 / 三维：参数生成";
    }

    internal async Task ReloadLibraryAsync()
    {
        try {
            var result=await Task.Run(()=>_library.Load(_libraryCancellation.Token,includeExternal:false));
            if(_libraryCancellation.IsCancellationRequested)return;
            var selected=Choices.SelectedItem as OpeningPlacementEntry;
            var fields=new[]{CodeInput,WidthInput,HeightInput,SillInput,ThresholdInput,AngleInput};
            var texts=fields.Select(f=>f.Text).ToArray();var open=OpenIn3DInput.IsChecked;
            Entries.RemoveAll(e=>e.Source=="公共库");
            foreach(var asset in result.Assets.Where(a=>!a.IsExternal).OrderBy(a=>a.Manifest.Name).ThenByDescending(a=>a.Manifest.Revision))
                Entries.Add(new(OpeningConstruction.Copy(asset.Manifest.OpeningType),asset.Manifest.Name,"公共库",asset));
            Filter();
            var retained=selected?.Asset==null?selected:Entries.FirstOrDefault(e=>e.Asset?.Manifest.AssetId==selected.Asset.Manifest.AssetId&&e.Asset.Manifest.Revision==selected.Asset.Manifest.Revision);
            if(retained!=null&&Choices.Items.Contains(retained)){
                Choices.SelectedItem=retained;_loading=true;
                try{for(var i=0;i<fields.Length;i++)fields[i].Text=texts[i];OpenIn3DInput.IsChecked=open;}
                finally{_loading=false;}UpdateDraft();
            }
            if(result.Errors.Count>0)Status($"{result.Errors.Count} 个资源包未载入；"+result.Errors[0]);
        } catch(OperationCanceledException) { }
        catch(Exception ex){Status("公共库读取失败："+ex.Message);}
        finally {LibraryLoaded=true;}
    }

    internal async Task<ComponentAsset?> SavePublicDraftAsync(string name)
    {
        var draft=Draft();if(draft==null)return null;
        return await Task.Run(()=>_library.Save(name,draft.Type,_libraryCancellation.Token));
    }

    private void SetLibraryBusy(bool busy)
    {
        _libraryBusy=busy;_import.IsEnabled=!busy;NewAsset.IsEnabled=!busy&&Choices.SelectedItem!=null;UpdateSelectionHeading();
    }

    private async Task SavePublicAssetAsync()
    {
        var draft=Draft();if(draft==null)return;
        var input=new TextBox {Text=(Choices.SelectedItem as OpeningPlacementEntry)?.Name??draft.Type.Code,MaxLength=100};
        var ok=new Button {Content="保存",Height=32,HorizontalAlignment=HorizontalAlignment.Right};
        var panel=new StackPanel {Spacing=12,Margin=new Thickness(16)};panel.Children.Add(new TextBlock {Text="公共库资源名称"});panel.Children.Add(input);panel.Children.Add(ok);
        var dialog=new Window {Title="新建公共库资源",Width=400,Height=170,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=panel,Background=Background,Foreground=Foreground};
        ok.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(input.Text))dialog.Close(input.Text.Trim());};
        var name=await dialog.ShowDialog<string?>(this);if(name==null)return;
        SetLibraryBusy(true);
        try {var asset=await SavePublicDraftAsync(name);await ReloadLibraryAsync();SourceFilter.SelectedItem="公共库";Search.Text="";Category.SelectedIndex=0;_subcategory=null;Filter();Choices.SelectedItem=Entries.First(e=>e.Asset?.Manifest.AssetId==asset?.Manifest.AssetId);Status("已保存公共库资源 "+name);}
        catch(OperationCanceledException) { }
        catch(Exception ex){Status("保存失败："+ex.Message);}
        finally{SetLibraryBusy(false);}
    }

    private static readonly FilePickerFileType PackageType=new("万落参数资源包"){Patterns=new[]{"*.wlopkg"}};
    internal async Task<ComponentAsset> ImportPublicPackageAsync(string path)
    {
        var asset=await Task.Run(()=>_library.Import(path,_libraryCancellation.Token));
        if(asset.IsExternal){Status("外部资源已入库；请在管理 → 图库查看，正式放置尚未开放。");return asset;}
        await ReloadLibraryAsync();SourceFilter.SelectedItem="公共库";Search.Text="";Category.SelectedIndex=0;_subcategory=null;Filter();
        Choices.SelectedItem=Entries.First(e=>e.Asset?.Manifest.AssetId==asset.Manifest.AssetId&&e.Asset.Manifest.Revision==asset.Manifest.Revision);
        return asset;
    }
    private async Task ImportPackageAsync()
    {
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title="导入参数资源包",AllowMultiple=false,FileTypeFilter=new[]{PackageType}});
        var path=files.FirstOrDefault()?.TryGetLocalPath();if(path==null)return;
        SetLibraryBusy(true);
        try {var asset=await ImportPublicPackageAsync(path);Status(asset.IsExternal?"已导入外部资源；正式放置尚未开放":"已导入 "+asset.Manifest.Name);}
        catch(OperationCanceledException) { }
        catch(Exception ex){Status("导入失败："+ex.Message);}
        finally{SetLibraryBusy(false);}
    }

    private async Task ExportPackageAsync()
    {
        var asset=(Choices.SelectedItem as OpeningPlacementEntry)?.Asset;if(asset==null)return;
        var target=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {Title="导出参数资源包",SuggestedFileName=asset.Manifest.AssetId+".wlopkg",DefaultExtension="wlopkg",FileTypeChoices=new[]{PackageType}});
        var path=target?.TryGetLocalPath();if(path==null)return;SetLibraryBusy(true);
        try{await Task.Run(()=>_library.Export(asset,path,_libraryCancellation.Token));Status("已导出 "+asset.Manifest.Name);}
        catch(OperationCanceledException) { }
        catch(Exception ex){Status("导出失败："+ex.Message);}
        finally{SetLibraryBusy(false);}
    }
}
