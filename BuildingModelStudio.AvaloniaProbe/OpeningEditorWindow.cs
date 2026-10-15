using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class OpeningEditResult
{
    internal OpeningTypeModel Type=null!;
    internal bool OnlyInstance;
    internal OpeningSizeConflictChoice SizeConflictChoice;
    internal double SnapStep;
    internal Dictionary<string,OpeningTypeModel> Templates=new();
}

internal sealed partial class OpeningEditorWindow : Window
{
    private readonly OpeningModel _opening;
    private OpeningTypeModel _draft;
    private readonly OpeningLayoutCanvas _canvas=new() { MinHeight=300 };
    private readonly ModelViewport _preview=new(new BuildingVolume());
    private readonly ComboBox _scope=new(){MinWidth=250};
    private bool OnlyInstance=>_scope.SelectedIndex==1;
    private readonly TextBlock _status=new() { TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock _openingSummary=new() {FontSize=13,Foreground=new SolidColorBrush(Color.Parse("#9CACBD"))};
    private readonly int _sameCodeCount;
    private readonly TextBlock _selectionSummary=new() {FontSize=13,FontWeight=FontWeight.SemiBold,Margin=new Thickness(12,10)};
    private readonly Dictionary<string,TextBox> _fields=new();
    private readonly Dictionary<Key,Button> _layoutShortcutButtons=new();
    private readonly NumericUpDown _divisionCount=new() {Name="OpeningDivisionCount",Value=2,Minimum=2,Maximum=20,Increment=1,
        FormatString="0",ParsingNumberStyle=NumberStyles.Integer,Width=54,Height=32,MinHeight=32,ShowButtonSpinner=false,ClipValueToMinMax=true};
    private readonly Dictionary<string,OpeningTypeModel> _templates=new();
    private readonly ComboBox _material=new() { ItemsSource=new[]{"玻璃","实板","百叶","无"},SelectedIndex=0 };
    private readonly ComboBox _openingMode=new() { ItemsSource=new[]{"固定","左平开","右平开","左推拉","右推拉","双向推拉","上悬","下悬","百叶","无"},SelectedIndex=0 };
    private readonly CheckBox _door=new() { Content="此格为门扇" },_deleted=new() {Content="删除此格"};
    private readonly CheckBox _outer=new() {Content="外框"},_mullion=new() {Content="分隔框"},_gap=new() {Content="安装缝"};
    private readonly CheckBox _sash=new() {Content="扇框 / 门套"};
    private readonly ComboBox _kind=new() {ItemsSource=new[]{"普通窗","高窗","带形窗","转角窗","拱形窗","普通门","推拉门","防火门","人防门","门联窗","百叶","百叶窗","百叶门","凸窗"}};
    private readonly ComboBox _planStyle=new() {ItemsSource=new[]{"按立面","折叠门","卷帘门","旋转门","矩形凸窗","梯形凸窗","转角窗"}};
    private readonly ComboBox _doorFrame=new() {ItemsSource=new[]{"N型","口型"}};
    private readonly ComboBox _doorPlacement=new() {ItemsSource=new[]{"靠左","居中","靠右"}};
    private readonly ComboBox _position=new() {ItemsSource=new[]{"居中","靠内","靠外"}};
    private readonly ComboBox _face=new() {ItemsSource=new[]{"主面","左转折面","右转折面"},SelectedIndex=0};
    private readonly ComboBox _left=new() {ItemsSource=new[]{"墙","窗"}},_right=new() {ItemsSource=new[]{"墙","窗"}};
    private readonly DispatcherTimer _timer=new() {Interval=TimeSpan.FromMilliseconds(100)};
    internal event Action<OpeningTypeModel,bool>? PreviewChanged;
    internal Func<OpeningTypeModel,bool,Task<(OpeningSizeConflictChoice? Choice,string? Error)>>? ConfirmSizeChange;
    internal Func<double,double,string>? SizeCodePreview;
    private bool _updating,_closed,_previewReady;
    private int _loadedFace;
    private ComboBox _presets=null!;
    private TabControl _inspectorTabs=null!;
    private ComboBox _templateChooser=null!;
    private int _generation;
    private Task _applyTask=Task.CompletedTask;
    private readonly double[]? _groupSizeLimits,_singleSizeLimits;
    private string? _sizeFitNotice;
    internal OpeningEditorWindow(OpeningModel opening,OpeningTypeModel type,int count,IEnumerable<OpeningTypeModel>? templates,double snapStep=5,
        double[]? groupSizeLimits=null,double[]? singleSizeLimits=null)
    {
        _groupSizeLimits=groupSizeLimits;_singleSizeLimits=singleSizeLimits;
        _opening=BuildingModelJson.FromJson(BuildingModelJson.ToJson(new BuildingModelDocument {Openings=new(){opening}})).Openings.Single();
        _opening.Width=NormalizeDimension("width",_opening.Width);_opening.Height=NormalizeDimension("height",_opening.Height);
        _sameCodeCount=count;
        _scope.ItemsSource=new[]{$"同编号全部（{count} 樘，含标准层）","仅当前樘（共用层同步）"};_scope.SelectedIndex=0;
        _draft=OpeningConstruction.Copy(type);OpeningConstruction.ResizeType(_draft,_opening.Width,_opening.Height);
        foreach(var template in templates??Enumerable.Empty<OpeningTypeModel>())_templates[template.Code]=OpeningConstruction.Copy(template);
        Background=new SolidColorBrush(Color.Parse("#101925"));FontSize=13;
        var controlBrush=new SolidColorBrush(Color.Parse("#243345"));var lineBrush=new SolidColorBrush(Color.Parse("#34465A"));
        Styles.Add(new Style(s=>s.OfType<Button>()){Setters={new Setter(Button.BackgroundProperty,controlBrush),new Setter(Button.BorderBrushProperty,lineBrush),new Setter(Button.CornerRadiusProperty,new CornerRadius(5)),new Setter(Button.FontSizeProperty,13d)}});
        Styles.Add(new Style(s=>s.OfType<TextBox>()){Setters={new Setter(TextBox.BackgroundProperty,new SolidColorBrush(Color.Parse("#142131"))),new Setter(TextBox.BorderBrushProperty,lineBrush),new Setter(TextBox.CornerRadiusProperty,new CornerRadius(4)),new Setter(TextBox.FontSizeProperty,13d),new Setter(TextBox.MinHeightProperty,32d)}});
        Styles.Add(new Style(s=>s.OfType<ComboBox>()){Setters={new Setter(ComboBox.BackgroundProperty,new SolidColorBrush(Color.Parse("#142131"))),new Setter(ComboBox.BorderBrushProperty,lineBrush),new Setter(ComboBox.FontSizeProperty,13d),new Setter(ComboBox.MinHeightProperty,34d)}});
        Title=$"门窗编辑 · {opening.Code}";Width=1440;Height=920;MinWidth=1120;MinHeight=720;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new Grid {RowDefinitions=new("Auto,*,Auto"),Margin=new Thickness(14)};
        var header=new StackPanel {Spacing=5};
        header.Children.Add(new TextBlock {Text=$"门窗分格设计  /  {opening.Code}",FontSize=22,FontWeight=FontWeight.Bold});
        _openingSummary.Text=$"{opening.Kind}   ·   洞口 {opening.Width:0.#} × {opening.Height:0.#} mm   ·   同编号 {count} 樘（含标准层）";header.Children.Add(_openingSummary);
        var scopePanel=new StackPanel {Spacing=5,VerticalAlignment=VerticalAlignment.Center};scopePanel.Children.Add(new TextBlock {Text="修改范围"});scopePanel.Children.Add(_scope);
        var topBar=new Grid {ColumnDefinitions=new("*,Auto"),Margin=new Thickness(0,0,0,14)};topBar.Children.Add(header);Grid.SetColumn(scopePanel,1);topBar.Children.Add(scopePanel);root.Children.Add(topBar);
        var body=new Grid {ColumnDefinitions=new("164,*,300"),ColumnSpacing=10,Margin=new Thickness(0,4,0,16)};Grid.SetRow(body,1);root.Children.Add(body);
        var leftArea=new Grid();Grid.SetColumn(leftArea,1);body.Children.Add(leftArea);
        var toolRows=new StackPanel {Spacing=5,Margin=new Thickness(8,10)};
        var toolbar=toolRows;
        var toolHost=new Grid {RowDefinitions=new("44,*")};toolHost.Children.Add(SectionHeader("工具"));
        var toolScroll=new ScrollViewer {Content=toolRows,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};Grid.SetRow(toolScroll,1);toolHost.Children.Add(toolScroll);
        var toolsBorder=new Border {Child=toolHost,CornerRadius=new CornerRadius(5),Background=new SolidColorBrush(Color.Parse("#1A2736")),BorderBrush=lineBrush,BorderThickness=new Thickness(1)};body.Children.Add(toolsBorder);
        _divisionCount.Background=new SolidColorBrush(Color.Parse("#142131"));_divisionCount.BorderBrush=lineBrush;
        ToolTip.SetTip(_divisionCount,"等分数量：2～20；每次增减 1。");
        void Tool(string text,Action action,Key? shortcut=null,string? hint=null)
        {
            var icon=text switch {"竖向等分"=>"columns-2","横向等分"=>"rows-2","合并选中"=>"combine","删除 / 恢复"=>"trash","等宽"=>"move-horizontal","等高"=>"move-vertical","居中"=>"maximize","撤销"=>"undo-2","重做"=>"redo-2",_=>"square-dashed"};
            var content=new Grid {ColumnDefinitions=new("*,Auto"),ColumnSpacing=8};content.Children.Add(IconLabel(icon,text));
            var label=hint??shortcut?.ToString();
            if(label!=null){var keyLabel=new TextBlock {Text=label,FontSize=11,Foreground=new SolidColorBrush(Color.Parse("#8DB7D5")),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(keyLabel,1);content.Children.Add(keyLabel);}
            var b=new Button {Content=content,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(8,10),MinHeight=40};
            if(text=="居中")b.Name="CenterOpeningLayout";
            ToolTip.SetTip(b,text+(label==null?"":" ("+label+")")+(text=="居中"?" · 所选矩形分格组整体水平居中；内部尺寸不变，两侧邻格随动。":""));
            b.Click+=(_,_)=>RunDraftCommand(action);
            if(shortcut.HasValue)_layoutShortcutButtons.Add(shortcut.Value,b);
            toolbar.Children.Add(b);
        }
        var countInput=new Grid {ColumnDefinitions=new("*,20"),Width=74,Height=32};countInput.Children.Add(_divisionCount);
        var countArrows=new Grid {RowDefinitions=new("*,*")};Grid.SetColumn(countArrows,1);countInput.Children.Add(countArrows);
        foreach(var increase in new[]{true,false}){
            using var stream=Avalonia.Platform.AssetLoader.Open(new Uri("avares://万落建筑模型/Resources/Icons/chevron-"+(increase?"up":"down")+".png"));
            var arrow=new RepeatButton {Name=increase?"DivisionCountIncrease":"DivisionCountDecrease",Padding=new Thickness(0),MinHeight=0,MinWidth=0,Width=20,Height=16,
                CornerRadius=new CornerRadius(0),Focusable=false,Content=new Image {Source=new Avalonia.Media.Imaging.Bitmap(stream),Width=12,Height=12}};
            ToolTip.SetTip(arrow,increase?"增加等分数量":"减少等分数量");Grid.SetRow(arrow,increase?0:1);countArrows.Children.Add(arrow);
            arrow.Click+=(_,_)=>{_divisionCount.Value=Math.Clamp(Math.Round(_divisionCount.Value??2)+(increase?1:-1),2,20);};
        }
        var countRow=new Grid {ColumnDefinitions=new("*,Auto"),Margin=new Thickness(4,0,0,6)};countRow.Children.Add(new TextBlock {Text="等分数量",VerticalAlignment=VerticalAlignment.Center});Grid.SetColumn(countInput,1);countRow.Children.Add(countInput);toolbar.Children.Add(countRow);
        Tool("竖向等分",()=>Split(true),Key.X);Tool("横向等分",()=>Split(false),Key.S);
        Tool("合并选中",()=> {if(_canvas.State.Merge(_canvas.Selected,out var error)){_canvas.Selected.Clear();_canvas.Notify();}else _status.Text=error;},Key.H);
        Tool("删除 / 恢复",()=>{var remove=_canvas.Selected.Any(i=>!_canvas.State.Cells[i].IsDeleted);if(_canvas.State.Edit(cells=>{foreach(var i in _canvas.Selected){cells[i].IsDeleted=remove;if(remove)cells[i].IsDoor=false;}},out var error))_canvas.Notify();else _status.Text=error;},Key.D);
        Tool("等宽",()=>{if(_canvas.State.Equalize(_canvas.Selected,true,out var error))_canvas.Notify();else _status.Text=error;},Key.F);
        Tool("等高",()=>{if(_canvas.State.Equalize(_canvas.Selected,false,out var error))_canvas.Notify();else _status.Text=error;},Key.G);
        Tool("居中",()=>{if(_canvas.State.Center(_canvas.Selected,out var error))_canvas.Notify();else _status.Text=error;},Key.Z);
        toolbar.Children.Add(new Border {Height=1,Background=lineBrush,Margin=new Thickness(8,6)});
        var stepGroup=new StackPanel {Spacing=5,Margin=new Thickness(8,4)};
        stepGroup.Children.Add(IconLabel("move","移动步长"));
        var stepRow=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
        var step=new TextBox {Text=snapStep.ToString("0.##",CultureInfo.InvariantCulture),Width=75};_fields["snap"]=step;stepRow.Children.Add(step);stepRow.Children.Add(new TextBlock {Text="mm",VerticalAlignment=VerticalAlignment.Center,Foreground=new SolidColorBrush(Color.Parse("#9CACBD"))});stepGroup.Children.Add(stepRow);toolbar.Children.Add(stepGroup);
        toolbar.Children.Add(new Border {Height=1,Background=lineBrush,Margin=new Thickness(8,6)});
        Tool("撤销",()=>UndoDraft(),hint:"Ctrl+Z");Tool("重做",()=>UndoDraft(true),hint:"Ctrl+Y");
        Tool("恢复单格",()=>{if(_face.SelectedIndex==1)_draft.BayLeftCellLayout=null;else if(_face.SelectedIndex==2)_draft.BayRightCellLayout=null;else {_draft.CustomCellLayout=null;_draft.DivisionPreset="单格";}LoadFace();StoreFace();QueuePreview();});
        ToolTip.SetTip(step,"拖动按此步长吸附，0 表示自由移动；毫米尺寸不受视图缩放影响。");step.TextChanged+=(_,_)=>{if(!_updating)QueuePreview("snap");};
        IntegerInput(step,"snap");
        var workArea=new Grid {ColumnDefinitions=new("1.3*,6,*"),ColumnSpacing=0};leftArea.Children.Add(workArea);
        var drawingArea=new Grid {RowDefinitions=new("44,*")};
        var drawingFrame=new Border {Child=drawingArea,CornerRadius=new CornerRadius(5),ClipToBounds=true,BorderBrush=lineBrush,BorderThickness=new Thickness(1)};workArea.Children.Add(drawingFrame);
        var drawingHeader=SectionHeader("二维分格");var headerGrid=(Grid)drawingHeader.Child!;Grid.SetColumn(_face,1);_face.MinWidth=102;_face.Margin=new Thickness(0,4);headerGrid.Children.Add(_face);drawingArea.Children.Add(drawingHeader);
        ToolTip.SetTip(drawingHeader,"空白处两点框选：左到右窗口，右到左交叉 · Shift 加选 / 减选 · 滚轮缩放 · 右键平移");
        Grid.SetRow(_canvas,1);drawingArea.Children.Add(_canvas);
        var divider=new GridSplitter {ResizeDirection=GridResizeDirection.Columns,HorizontalAlignment=HorizontalAlignment.Stretch,Background=Brushes.Transparent};Grid.SetColumn(divider,1);workArea.Children.Add(divider);
        var previewHost=new Grid();previewHost.Children.Add(_preview);var input=new Border {Background=Brushes.Transparent};previewHost.Children.Add(input);
        input.PointerPressed+=(_,e)=>{_preview.BeginInteraction(e.GetPosition(_preview),false,e.GetCurrentPoint(input).Properties.IsRightButtonPressed,true,true);e.Pointer.Capture(input);};
        input.PointerMoved+=(_,e)=>{var p=e.GetCurrentPoint(input).Properties;_preview.MoveInteraction(e.GetPosition(_preview),p.IsLeftButtonPressed,p.IsMiddleButtonPressed||p.IsRightButtonPressed);};
        input.PointerReleased+=(_,e)=>{_preview.EndInteraction(e.GetPosition(_preview));e.Pointer.Capture(null);};input.PointerWheelChanged+=(_,e)=>_preview.Zoom(e.Delta.Y);
        var threeArea=new Grid {RowDefinitions=new("44,*")};
        var previewBorder=new Border {Child=threeArea,CornerRadius=new CornerRadius(5),ClipToBounds=true,BorderBrush=lineBrush,BorderThickness=new Thickness(1)};Grid.SetColumn(previewBorder,2);workArea.Children.Add(previewBorder);
        var previewHeader=SectionHeader("三维预览");var fit=new Button {Content=IconLabel("box","适合视图"),FontSize=12,Padding=new Thickness(6,4)};fit.Click+=(_,_)=>FramePreview();Grid.SetColumn(fit,1);((Grid)previewHeader.Child!).Children.Add(fit);threeArea.Children.Add(previewHeader);ToolTip.SetTip(previewHeader,"拖动旋转 · 右键平移 · 滚轮缩放");Grid.SetRow(previewHost,1);threeArea.Children.Add(previewHost);
        var inspector=new Grid {RowDefinitions=new("44,*")};
        _selectionSummary.Margin=new Thickness(12,0);_selectionSummary.VerticalAlignment=VerticalAlignment.Center;
        inspector.Children.Add(new Border {Child=_selectionSummary,BorderBrush=lineBrush,BorderThickness=new Thickness(0,0,0,1)});
        var inspectorBorder=new Border {Child=inspector,CornerRadius=new CornerRadius(5),Background=new SolidColorBrush(Color.Parse("#1A2736")),BorderBrush=lineBrush,BorderThickness=new Thickness(1)};Grid.SetColumn(inspectorBorder,2);body.Children.Add(inspectorBorder);
        var tabs=_inspectorTabs=new TabControl {FontSize=13};Grid.SetRow(tabs,1);inspector.Children.Add(tabs);
        var tabItems=new List<TabItem>();var panel=new StackPanel();
        void Tab(string title){panel=new StackPanel {Spacing=9,Margin=new Thickness(12,14)};tabItems.Add(new TabItem {Header=IconLabel(title=="分格" ? "grid-3x3" : title=="构造" ? "box" : "layers",title),FontSize=14,Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto}});}
        Tab("分格");
        void Label(string text)=>panel.Children.Add(new TextBlock {Text=text,FontWeight=FontWeight.Bold,Margin=new Thickness(0,8,0,2)});
        void Field(string key,string label,double value){var row=new Grid {ColumnDefinitions=new("*,100")};row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});var input=new TextBox {Text=(key=="angle"?value:Millimetres(value,key is "width" or "height" or "leftDepth" or "rightDepth"?10:1)).ToString(key=="angle"?"0.###":"0",CultureInfo.InvariantCulture)};Grid.SetColumn(input,1);row.Children.Add(input);panel.Children.Add(row);_fields[key]=input;input.TextChanged+=(_,_)=>{if(!_updating)QueuePreview(key);};
            IntegerInput(input,key);}
        void Profile(CheckBox enabled,string widthKey,double width,string depthKey,double depth){
            var card=new StackPanel {Spacing=5};card.Children.Add(enabled);
            var row=new Grid {ColumnDefinitions=new("*,*"),ColumnSpacing=10};
            void Entry(int column,string key,string title,double value){var stack=new StackPanel {Spacing=3};stack.Children.Add(new TextBlock {Text=title,FontSize=12,Foreground=new SolidColorBrush(Color.Parse("#A6BACB"))});var input=new TextBox {Text=Millimetres(value).ToString("0",CultureInfo.InvariantCulture),MinHeight=32};stack.Children.Add(input);_fields[key]=input;input.TextChanged+=(_,_)=>{if(!_updating)QueuePreview(key);};IntegerInput(input,key);Grid.SetColumn(stack,column);row.Children.Add(stack);}
            Entry(0,widthKey,"正面宽 mm",width);Entry(1,depthKey,"进深 mm",depth);card.Children.Add(row);
            panel.Children.Add(new Border {Child=card,Padding=new Thickness(10),CornerRadius=new CornerRadius(6),Background=new SolidColorBrush(Color.Parse("#203342"))});
            enabled.IsCheckedChanged+=(_,_)=>row.IsEnabled=enabled.IsChecked==true;
        }
        Label("洞口尺寸");Field("width","洞口宽 mm",opening.Width);Field("height","洞口高 mm",opening.Height);
        var divisionPanel=panel;panel=new StackPanel {Spacing=7};divisionPanel.Children.Add(new Expander {Header="门窗类型与整体设置",IsExpanded=false,Content=panel,HorizontalAlignment=HorizontalAlignment.Stretch});
        Label("门窗类型");panel.Children.Add(_kind);
        ToolTip.SetTip(_scope,"同编号全部使用同一新类型或后缀；仅当前樘生成独立类型。标准层仍共用平面洞口，单层洞口修改须先解除共用。");
        Label("平面形式");panel.Children.Add(_planStyle);Field("planInset","凸窗斜边收进 · mm",type.PlanReturnInset??0);
        _planStyle.SelectionChanged+=(_,_)=>{if(_fields["planInset"].Parent is Control row)row.IsVisible=_planStyle.SelectedItem?.ToString()=="梯形凸窗";if(!_updating)QueuePreview();};
        Label("分格预设");
        _presets=new ComboBox {ItemsSource=new[]{"单格","双扇等分","三扇等分","四扇等分","五扇等分","上亮","侧亮","上亮+侧亮","拱形亮子","门联窗","自定义"},SelectedIndex=0};panel.Children.Add(_presets);
        _presets.SelectionChanged+=(_,_)=>{if(_updating||_presets.SelectedItem?.ToString()=="自定义")return;StoreFace();var preset=_presets.SelectedItem?.ToString();if(_face.SelectedIndex==0){_draft.DivisionPreset=preset;_draft.CustomCellLayout=null;}LoadFace(preset);StoreFace();QueuePreview();};
        var doorOptions=new StackPanel {Spacing=8};panel.Children.Add(doorOptions);
        doorOptions.Children.Add(new TextBlock {Text="门框形式 / 门扇靠位  [N]",FontWeight=FontWeight.Bold});doorOptions.Children.Add(_doorFrame);doorOptions.Children.Add(_doorPlacement);
        ToolTip.SetTip(_doorFrame,"N：N型 / 口型切换；保留门扇靠位。");
        Field("doorEdge","门边距离 · mm",type.DoorEdgeDistance);
        void DoorVisibility(){doorOptions.IsVisible=(_kind.SelectedItem?.ToString()??"").Contains("门");if(_fields["doorEdge"].Parent is Control row)row.IsVisible=doorOptions.IsVisible;}
        _kind.SelectionChanged+=(_,_)=>DoorVisibility();
        panel=divisionPanel;
        Label("面板参数");panel.Children.Add(new TextBlock {Text="面板材料"});panel.Children.Add(_material);panel.Children.Add(new TextBlock {Text="开启方式  [K]"});panel.Children.Add(_openingMode);
        ToolTip.SetTip(_openingMode,"K：依次切换所选分格的开启方式。");
        var paneFlags=new WrapPanel();_door.FontSize=_deleted.FontSize=12;_door.Margin=new Thickness(0,0,12,0);paneFlags.Children.Add(_door);paneFlags.Children.Add(_deleted);panel.Children.Add(paneFlags);
        Field("cellWidth","当前格宽 mm",opening.Width);Field("cellHeight","当前格高 mm",opening.Height);
        var size=new Button {Content="应用格宽 / 格高"};size.Click+=(_,_)=>{
            if(ApplyLiveCellSize(true))QueuePreview();
        };panel.Children.Add(size);
        panel.Children.Add(new TextBlock {Text="格宽移动右侧内部中梃，格高移动下侧内部中梃。实际尺寸优先；其余邻格随动，外边框固定。",TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.Parse("#A6BACB"))});
        Tab("构造");Label("框料尺寸");Profile(_outer,"outer",type.OuterFrameWidth,"frameDepth",type.FrameDepth??100);
        Profile(_mullion,"mullion",type.MullionWidth,"mullionDepth",type.MullionDepth??100);
        Profile(_sash,"sashWidth",type.SashWidth??type.DoorFrameWidth,"sashDepth",type.SashDepth??50);
        Label("面板与扇缝");
        Field("glass","玻璃厚度",type.GlassThickness??6);Field("panel","门扇 / 实板厚度",type.PanelThickness??40);
        Field("sashClearance","扇边缝（每边）",type.SashClearance??2);
        panel.Children.Add(_gap);Field("gap","安装缝",type.InstallationGap);Label("安装位置");panel.Children.Add(_position);Field("offset","墙厚方向偏移",type.InstallationOffset??0);
        _gap.IsCheckedChanged+=(_,_)=>{if(_fields["gap"].Parent is Control row)row.IsVisible=_gap.IsChecked==true;};
        Field("angle","三维打开角度 °",type.OpenAngle??0);
        var constructionPanel=panel;panel=new StackPanel {Spacing=7};constructionPanel.Children.Add(panel);var bayPanel=panel;
        Label("飘窗转折与封板");panel.Children.Add(_left);Field("leftDepth","左侧进深",type.BayLeftDepth);panel.Children.Add(_right);Field("rightDepth","右侧进深",type.BayRightDepth);
        Field("bayCap","飘窗上下封板厚度",type.BayCapThickness??100);
        _kind.SelectionChanged+=(_,_)=>bayPanel.IsVisible=_kind.SelectedItem?.ToString()=="凸窗";
        Tab("模板");Label("项目模板");var chooser=_templateChooser=new ComboBox {ItemsSource=_templates.Keys.ToArray()};panel.Children.Add(chooser);
        var use=new Button {Content="套用所选模板"};use.Click+=(_,_)=>{if(chooser.SelectedItem is not string name||!_templates.TryGetValue(name,out var template))return;var copy=OpeningConstruction.Copy(template);copy.Code=_draft.Code;ScaleTemplate(copy);_draft=copy;LoadControls();LoadFace();QueuePreview();};panel.Children.Add(use);
        var templateName=new TextBox {PlaceholderText="模板名称"};panel.Children.Add(templateName);var saveTemplate=new Button {Content="保存为项目模板（应用后保存）"};saveTemplate.Click+=(_,_)=>{if(!ReadDraft()||string.IsNullOrWhiteSpace(templateName.Text))return;StoreFace();_templates[templateName.Text.Trim()]=OpeningConstruction.Copy(_draft);chooser.ItemsSource=_templates.Keys.ToArray();_status.Text="模板已加入草稿，应用后保存。";};panel.Children.Add(saveTemplate);
        tabs.ItemsSource=tabItems;
        var footer=new Grid {ColumnDefinitions=new("*,Auto,Auto")};Grid.SetRow(footer,2);root.Children.Add(footer);footer.Children.Add(_status);_status.Foreground=new SolidColorBrush(Color.Parse("#9CACBD"));_status.VerticalAlignment=VerticalAlignment.Center;_status.FontSize=12;
        var apply=new Button {Name="ApplyOpeningEditor",Content="应用并关闭",Background=new SolidColorBrush(Color.Parse("#237CD4")),BorderBrush=new SolidColorBrush(Color.Parse("#399BED")),Margin=new Thickness(12,0),Padding=new Thickness(18,8)};Grid.SetColumn(apply,1);footer.Children.Add(apply);
        var cancel=new Button {Content="取消",Padding=new Thickness(18,8)};Grid.SetColumn(cancel,2);footer.Children.Add(cancel);
        async Task ApplyAsync() {if(!AcceptDraft())return;
            apply.IsEnabled=false;
            try {
                var draft=OpeningConstruction.Copy(_draft);
                var single=OnlyInstance;
                var decision=ConfirmSizeChange==null?(Choice:(OpeningSizeConflictChoice?)OpeningSizeConflictChoice.CreateNew,Error:(string?)null):await ConfirmSizeChange(draft,single);
                if(decision.Choice==null){_status.Text=decision.Error==null?"已取消应用，门窗草稿保留。":"未应用："+decision.Error;
                    _status.Foreground=new SolidColorBrush(Color.Parse("#FFB454"));return;}
                Close(new OpeningEditResult {Type=draft,OnlyInstance=single,Templates=_templates,SnapStep=_canvas.Snap,SizeConflictChoice=decision.Choice.Value});
            } finally {apply.IsEnabled=true;}
        }
        apply.Click+=(_,_)=>_applyTask=ApplyAsync();cancel.Click+=(_,_)=>Close();
        foreach(var combo in new[]{_kind,_planStyle,_presets,_material,_openingMode,_doorFrame,_doorPlacement,_position,_left,_right})combo.HorizontalAlignment=HorizontalAlignment.Stretch;
        Content=root;
        _canvas.Changed+=()=>{StoreFace();if(!_applyingCellSize)QueuePreview();};_canvas.SelectionChanged+=UpdateSelection;_canvas.Error+=message=>_status.Text=message;
        _material.SelectionChanged+=(_,_)=>ApplyCell();_openingMode.SelectionChanged+=(_,_)=>ApplyCell();_door.IsCheckedChanged+=(_,_)=>ApplyCell();_deleted.IsCheckedChanged+=(_,_)=>ApplyCell();
        foreach(var box in new[]{_outer,_mullion,_gap})box.IsCheckedChanged+=(_,_)=>{if(!_updating)QueuePreview();};
        _scope.SelectionChanged+=(_,_)=>{if(!_updating)QueuePreview();};
        _sash.IsCheckedChanged+=(_,_)=>{if(!_updating){if(_sash.IsChecked==true && (!Number("sashWidth",out var w)||w<=0))_fields["sashWidth"].Text="50";QueuePreview();}};
        foreach(var box in new[]{_kind,_position,_left,_right,_doorFrame,_doorPlacement})box.SelectionChanged+=(_,_)=>{if(!_updating)QueuePreview();};
        _face.SelectionChanged+=(_,_)=>{if(!_updating){StoreFace();LoadFace();QueuePreview();}};
        _doorPlacement.SelectionChanged+=(_,_)=> {if(!_updating && _canvas.State!=null && _kind.SelectedItem?.ToString()=="门联窗") { _canvas.State.Edit(cells=>{foreach(var c in cells)c.IsDoor=false;},out _);_canvas.Notify();} };
        _timer.Tick+=async (_,_)=>{_timer.Stop();await PreviewAsync();};Closed+=(_,_)=>{_closed=true;_timer.Stop();++_generation;};
        KeyDown+=(_,e)=>{
            if(e.Key==Key.Escape){if(!_canvas.CancelInteraction())Close();e.Handled=true;return;}
            var text=e.Source is TextBox or NumericUpDown or ComboBox||e.Source is Control control
                &&control.GetVisualAncestors().Any(parent=>parent is TextBox or NumericUpDown or ComboBox);
            if(text)return;
            if(e.KeyModifiers.HasFlag(KeyModifiers.Control)&&e.Key is Key.Z or Key.Y){UndoDraft(e.Key==Key.Y||e.KeyModifiers.HasFlag(KeyModifiers.Shift));e.Handled=true;return;}
            if((e.KeyModifiers&~KeyModifiers.Shift)!=KeyModifiers.None||_canvas.IsDraggingDivider||_canvas.HasSelectionBox)return;
            if(_layoutShortcutButtons.TryGetValue(e.Key,out var button)&&button.IsEnabled)
            {button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));e.Handled=true;}
            else if(e.Key==Key.K){RunDraftCommand(()=>{
                if(_canvas.Selected.Count==0){_status.Text="请先选择要切换开启方式的分格。";return;}
                var mode=_openingMode.Items[(_openingMode.SelectedIndex+1)%_openingMode.Items.Count]?.ToString()??"固定";
                if(_canvas.State.Edit(cells=>{foreach(var i in _canvas.Selected)if(!cells[i].IsDeleted)cells[i].Opening=mode;},out var error))
                {_canvas.Notify();UpdateSelection();}else _status.Text=error;
            });e.Handled=true;}
            else if(e.Key==Key.N&&(_kind.SelectedItem?.ToString()??"").Contains("门"))
            {RunDraftCommand(()=>_doorFrame.SelectedIndex=(_doorFrame.SelectedIndex+1)%_doorFrame.Items.Count);e.Handled=true;}
        };
        LoadControls();LoadFace();StoreFace();InitializeDraftHistory();Opened+=(_,_)=>QueuePreview();
        void Split(bool vertical){if(!int.TryParse(_divisionCount.Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)||n<2||n>20){_status.Text="等分数量须为 2～20 的整数。";return;}if(_canvas.Selected.Count==0)_canvas.Selected.Add(0);if(_canvas.State.Split(_canvas.Selected,vertical,n,out var error)){_canvas.Selected.Clear();_canvas.Notify();}else _status.Text=error;}
    }
    private void RunDraftCommand(Action action)
    {
        _timer.Stop();if(!AcceptDraft())return;
        var before=_acceptedDraft!.Signature;
        _pendingEditKey=null;_lastHistoryKey=null;action();
        _timer.Stop();if(AcceptDraft()&&_acceptedDraft!.Signature!=before)QueuePreview();
    }
    private bool Number(string key,out double value)=>double.TryParse(_fields[key].Text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)&&double.IsFinite(value);
    private static Control IconLabel(string icon,string text,double rotation=0)
    {
        using var stream=Avalonia.Platform.AssetLoader.Open(new Uri("avares://万落建筑模型/Resources/Icons/"+icon+".png"));
        var image=new Image {Source=new Avalonia.Media.Imaging.Bitmap(stream),Width=18,Height=18,Stretch=Stretch.Uniform,IsHitTestVisible=false};
        if(rotation!=0)image.RenderTransform=new RotateTransform(rotation);
        var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=10,VerticalAlignment=VerticalAlignment.Center};row.Children.Add(image);row.Children.Add(new TextBlock {Text=text,VerticalAlignment=VerticalAlignment.Center});return row;
    }
    private static Border SectionHeader(string title)
    {
        var row=new Grid {ColumnDefinitions=new("*,Auto"),Margin=new Thickness(12,0)};
        row.Children.Add(new TextBlock {Text=title,FontSize=14,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        return new Border {Child=row,Background=new SolidColorBrush(Color.Parse("#1A2736")),BorderBrush=new SolidColorBrush(Color.Parse("#34465A")),BorderThickness=new Thickness(0,0,0,1)};
    }
    private void LoadControls()
    {
        _updating=true;
        if(!_planStyle.Items.Cast<string>().Contains(_draft.PlanStyle??"按立面"))_planStyle.ItemsSource=_planStyle.Items.Cast<string>().Append(_draft.PlanStyle).ToArray();
        _planStyle.SelectedItem=_draft.PlanStyle??"按立面";
        _fields["planInset"].Text=(_draft.PlanReturnInset??0).ToString("0.###",CultureInfo.InvariantCulture);
        if(!string.IsNullOrWhiteSpace(_draft.ElevationType) && !_kind.Items.Cast<string>().Contains(_draft.ElevationType))_kind.ItemsSource=_kind.Items.Cast<string>().Append(_draft.ElevationType).ToArray();
        if(!string.IsNullOrWhiteSpace(_draft.DivisionPreset) && !_presets.Items.Cast<string>().Contains(_draft.DivisionPreset))_presets.ItemsSource=_presets.Items.Cast<string>().Append(_draft.DivisionPreset).ToArray();
        _updating=true;_face.IsEnabled=_face.IsVisible=_draft.ElevationType=="凸窗";_presets.SelectedItem=_draft.DivisionPreset??"单格";_kind.SelectedItem=_draft.ElevationType??"普通窗";_position.SelectedItem=_draft.InstallationPosition??"居中";_doorFrame.SelectedItem=_draft.DoorFrameType??"N型";_doorPlacement.SelectedItem=_draft.DoorPlacement??"靠左";
        _outer.IsChecked=_draft.HasOuterFrame;_mullion.IsChecked=_draft.HasMullion;_gap.IsChecked=_draft.HasInstallationGap;
        _sash.IsChecked=(_draft.SashWidth??_draft.DoorFrameWidth)>0;
        _left.SelectedItem=_draft.BayLeftSide??"墙";_right.SelectedItem=_draft.BayRightSide??"墙";
        var values=new Dictionary<string,double>{{"width",_opening.Width},{"height",_opening.Height},{"outer",_draft.OuterFrameWidth},{"frameDepth",_draft.FrameDepth??100},{"mullion",_draft.MullionWidth},{"mullionDepth",_draft.MullionDepth??100},
            {"sashWidth",_draft.SashWidth??_draft.DoorFrameWidth},{"sashDepth",_draft.SashDepth??50},{"glass",_draft.GlassThickness??6},{"panel",_draft.PanelThickness??40},{"gap",_draft.InstallationGap},{"offset",_draft.InstallationOffset??0},
            {"angle",_draft.OpenAngle??0},{"leftDepth",_draft.BayLeftDepth},{"rightDepth",_draft.BayRightDepth},{"doorEdge",_draft.DoorEdgeDistance},{"bayCap",_draft.BayCapThickness??100},{"sashClearance",_draft.SashClearance??2}};
        foreach(var entry in values)_fields[entry.Key].Text=(entry.Key=="angle"?entry.Value:NormalizeDimension(entry.Key,entry.Value)).ToString(entry.Key=="angle"?"0.###":"0",CultureInfo.InvariantCulture);_updating=false;
        if(_fields["gap"].Parent is Control gapRow)gapRow.IsVisible=_gap.IsChecked==true;
    }
    private bool ReadDraft()
    {
        var values=new Dictionary<string,double>();foreach(var key in _fields.Keys.Where(k=>k!="cellWidth"&&k!="cellHeight")) {
            if(!Number(key,out var value)){_status.Text="请输入完整有效的尺寸。";return false;}
            values[key]=key=="angle"?value:NormalizeDimension(key,value);
        }
        var limits=OnlyInstance?_singleSizeLimits:_groupSizeLimits;
        if(limits!=null&&(values["width"]>limits[0]+.5||values["height"]>limits[1]+.5))
        {
            _status.Text=$"未应用：洞口超出宿主墙范围；当前修改范围内宽最多 {limits[0]:0} mm，高最多 {limits[1]:0} mm。";
            _status.Foreground=new SolidColorBrush(Color.Parse("#FFB454"));return false;
        }
        var minimum=2*(_gap.IsChecked==true?values["gap"]:0)+Math.Max(2,2*(_outer.IsChecked==true?values["outer"]:0));
        if(values["width"]<=minimum||values["height"]<=minimum){_status.Text="洞口宽高不足以容纳外框和安装缝。";return false;}
        if(_loadedFace!=0&&(NormalizeDisplayed("width",_opening.Width,values["width"])!=_opening.Width
            ||NormalizeDisplayed("height",_opening.Height,values["height"])!=_opening.Height)) {
            _status.Text="请切换到主面再修改整体洞口尺寸。";return false;
        }
        _draft.PlanStyle=_planStyle.SelectedItem?.ToString()??"按立面";_draft.PlanReturnInset=values["planInset"];
        _draft.OuterFrameWidth=values["outer"];_draft.FrameDepth=values["frameDepth"];_draft.MullionWidth=values["mullion"];_draft.MullionDepth=values["mullionDepth"];
        _draft.SashWidth=_sash.IsChecked==true ? values["sashWidth"] : 0;_draft.DoorFrameWidth=_draft.SashWidth.Value;_draft.SashDepth=values["sashDepth"];
        _draft.GlassThickness=values["glass"];_draft.PanelThickness=values["panel"];_draft.InstallationGap=values["gap"];_draft.InstallationOffset=values["offset"];_draft.OpenAngle=values["angle"];
        _draft.HasOuterFrame=_outer.IsChecked==true;_draft.HasMullion=_mullion.IsChecked==true;_draft.HasInstallationGap=_gap.IsChecked==true;
        _draft.DoorFrameType=_doorFrame.SelectedItem?.ToString();_draft.DoorPlacement=_doorPlacement.SelectedItem?.ToString();_draft.DoorEdgeDistance=values["doorEdge"];
        _draft.ElevationType=_kind.SelectedItem?.ToString();_face.IsEnabled=_face.IsVisible=_draft.ElevationType=="凸窗";if(!_face.IsEnabled && _face.SelectedIndex!=0)_face.SelectedIndex=0;_draft.InstallationPosition=_position.SelectedItem?.ToString();
        _draft.BayLeftSide=_left.SelectedItem?.ToString();_draft.BayRightSide=_right.SelectedItem?.ToString();_draft.BayLeftDepth=values["leftDepth"];_draft.BayRightDepth=values["rightDepth"];
        _draft.BayCapThickness=values["bayCap"];
        _draft.SashClearance=values["sashClearance"];
        if(values["snap"]<0 || values["snap"]>1000){_status.Text="移动步长须在 0～1000 mm 之间，0 表示自由移动。";return false;}
        _canvas.Snap=values["snap"];
        _opening.Width=NormalizeDisplayed("width",_opening.Width,values["width"]);_opening.Height=NormalizeDisplayed("height",_opening.Height,values["height"]);
        foreach(var key in new[]{"width","height"})if(Number(key,out var entered)&&Millimetres(entered,10)!=values[key])
        {var updating=_updating;_updating=true;_fields[key].Text=values[key].ToString("0",CultureInfo.InvariantCulture);_updating=updating;}
        _draft.Width=_opening.Width;_draft.Height=_opening.Height;
        _openingSummary.Text=$"{_opening.Kind}   ·   洞口 {_opening.Width:0.#} × {_opening.Height:0.#} mm   ·   同编号 {_sameCodeCount} 樘（含标准层）";
        if(SizeCodePreview!=null)_openingSummary.Text+="   ·   "+SizeCodePreview(_opening.Width,_opening.Height);
        if(_canvas.State!=null) {
            var gap=_draft.HasInstallationGap ? _draft.InstallationGap : 0;
            var w=_face.SelectedIndex==1 ? _draft.BayLeftDepth : _face.SelectedIndex==2 ? _draft.BayRightDepth : _opening.Width-2*gap;
            var h=_opening.Height-2*gap;
            if(w<=0 || h<=0){_status.Text="安装缝大于门窗范围。";return false;}
            if(Math.Abs(w-_canvas.State.Width)>.001 || Math.Abs(h-_canvas.State.Height)>.001) {
                var sx=w/_canvas.State.Width;var sy=h/_canvas.State.Height;
                var cells=DoorWindowElevationGeometryBuilder.ParseCellLayout(DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells));
                foreach(var c in cells){c.Left*=sx;c.Right*=sx;c.Bottom*=sy;c.Top*=sy;}
                _canvas.State=new OpeningLayoutEditing(w,h,cells);
            }
            _canvas.Opening.Width=_face.SelectedIndex==0 ? _opening.Width : w;
            _canvas.Opening.Height=_face.SelectedIndex==0 ? _opening.Height : h;
        }
        return true;
    }
    private void LoadFace(string? preset=null)
    {
        var type=OpeningConstruction.Copy(_draft);var face=_face.SelectedIndex;_loadedFace=face;
        var width=face==1 ? type.BayLeftDepth : face==2 ? type.BayRightDepth : _opening.Width;
        if(face!=0){type.ElevationType="普通窗";type.CustomCellLayout=face==1 ? type.BayLeftCellLayout : type.BayRightCellLayout;type.DivisionPreset=preset??(string.IsNullOrWhiteSpace(type.CustomCellLayout) ? "单格" : "自定义");if(preset!=null)type.CustomCellLayout=null;}
        var opening=new OpeningModel {Code=_opening.Code,Kind=_opening.Kind,Width=width,Height=_opening.Height};
        // The elevation result contains unfolded side cells; only load the selected physical face.
        var item=OpeningElevationAdapter.ToScheduleItem(_opening,_draft,_opening.Width,_opening.Height);
        if(face!=0){item=DoorWindowElevationGeometryBuilder.CreateBayReturnItem(item,face==1);opening.Height=item.Height;}
        else if(type.ElevationType=="凸窗")item.ElevationType="普通窗";
        if(preset!=null){item.DivisionPreset=preset;item.CustomCellLayout=null;}
        var g=DoorWindowElevationGeometryBuilder.Build(item);
        var cells=g.Cells.Select(c=>new DoorWindowLayoutCell {Left=c.Left-g.FrameLeft,Right=c.Right-g.FrameLeft,Bottom=c.Bottom-g.FrameBottom,Top=c.Top-g.FrameBottom,Opening=c.Opening,Material=c.Material,IsDoor=c.IsDoor,IsDeleted=c.IsDeleted}).ToList();
        _canvas.State=new OpeningLayoutEditing(g.FrameRight-g.FrameLeft,g.FrameTop-g.FrameBottom,cells);_canvas.Opening=opening;_canvas.Type=type;_canvas.IsReturnFace=face!=0;
        _canvas.Selected.Clear();_canvas.Selected.Add(0);_canvas.IsEnabled=face==0 || (face==1 ? _draft.BayLeftSide : _draft.BayRightSide)=="窗";_canvas.Notify(false);
    }
    private void StoreFace()
    {
        if(_canvas.State==null)return;IntegerLayout();var layout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells);
        if(_loadedFace==1)_draft.BayLeftCellLayout=layout;else if(_loadedFace==2)_draft.BayRightCellLayout=layout;
        else {_draft.DivisionPreset="自定义";_draft.CustomCellLayout=layout;_draft.CellOpeningModes=null;var old=_updating;_updating=true;_face.IsEnabled=_draft.ElevationType=="凸窗";_presets.SelectedItem="自定义";_updating=old;}
    }
    private void UpdateSelection()
    {
        _selectionSummary.Text=_canvas.Selected.Count==0 ? "属性 · 未选择面板" : _canvas.Selected.Count==1 ? "属性 · 当前面板" : $"属性 · 已选择 {_canvas.Selected.Count} 个面板";
        _updating=true;var index=_canvas.Selected.FirstOrDefault(-1);
        if(index>=0 && index<_canvas.State.Cells.Count){var c=_canvas.State.Cells[index];_material.SelectedItem=c.Material;_openingMode.SelectedItem=c.Opening;_door.IsChecked=c.IsDoor;_deleted.IsChecked=c.IsDeleted;
            if(!_applyingCellSize||_pendingEditKey!="cellWidth")_fields["cellWidth"].Text=(c.Right-c.Left).ToString("0",CultureInfo.InvariantCulture);
            if(!_applyingCellSize||_pendingEditKey!="cellHeight")_fields["cellHeight"].Text=(c.Top-c.Bottom).ToString("0",CultureInfo.InvariantCulture);}
        _fields["cellWidth"].IsEnabled=_fields["cellHeight"].IsEnabled=_canvas.Selected.Count==1;
        if(!_applyingCellSize&&!_restoringDraft){_pendingEditKey=null;_lastHistoryKey=null;}
        _updating=false;
    }
    private void ApplyCell()
    {
        if(_updating || _canvas.State==null || _canvas.Selected.Count==0)return;
        if(_canvas.State.Edit(cells=>{foreach(var i in _canvas.Selected){cells[i].Material=_material.SelectedItem?.ToString()??"无";cells[i].Opening=_openingMode.SelectedItem?.ToString()??"固定";cells[i].IsDeleted=_deleted.IsChecked==true;cells[i].IsDoor=!cells[i].IsDeleted&&_door.IsChecked==true;}},out var error))_canvas.Notify();else _status.Text=error;
    }
    private void ScaleTemplate(OpeningTypeModel type)
    {
        var gap=type.HasInstallationGap ? type.InstallationGap : 0;var w=_opening.Width-2*gap;var h=_opening.Height-2*gap;
        var cells=DoorWindowElevationGeometryBuilder.ParseCellLayout(type.CustomCellLayout);
        if(cells.Count>0){var sx=w/Math.Max(1,type.Width-2*gap);var sy=h/Math.Max(1,type.Height-2*gap);foreach(var c in cells){c.Left*=sx;c.Right*=sx;c.Bottom*=sy;c.Top*=sy;}type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells);}
        type.Width=_opening.Width;type.Height=_opening.Height;
    }
    private void QueuePreview(string? key=null){if(_updating||_closed)return;_pendingEditKey=key;++_generation;_timer.Stop();_timer.Start();}
    private async Task PreviewAsync()
    {
        if(!AcceptDraft())return;
        var generation=++_generation;var type=OpeningConstruction.Copy(_draft);
        _canvas.Type=type;_canvas.InvalidateVisual();
        try {
            var scene=await Task.Run(()=> {
                var local=new BuildingModelDocument();local.Storeys.Add(new StoreyModel {Id="preview",Height=_opening.Height+_opening.Sill+200});
                local.Walls.Add(new WallModel {Id="host",StoreyId="preview",X2=_opening.Width,Thickness=200});
                local.Openings.Add(new OpeningModel {Id="opening",Code=type.Code,Kind=_opening.Kind,HostWallId="host",Offset=_opening.Width/2,Width=_opening.Width,Height=_opening.Height,Sill=0,
                    PlanFlipAlong=_opening.PlanFlipAlong,PlanFlipNormal=_opening.PlanFlipNormal,PlanOpenAngle=_opening.PlanOpenAngle,OpenIn3D=_opening.OpenIn3D});
                local.OpeningTypes.Add(type);return ModelViewport.PrepareScene(BuildingVolumeBuilder.BuildOpeningParts(local));
            });
            if(_closed||generation!=_generation)return;_preview.SetScene(scene);if(!_previewReady){FramePreview();_previewReady=true;}PreviewChanged?.Invoke(type,OnlyInstance);
            _status.Foreground=new SolidColorBrush(Color.Parse("#9CACBD"));
            _status.Text=(_sizeFitNotice==null?"":_sizeFitNotice+" · ")+"草稿未应用；应用并关闭后写入模型。";
        }catch(Exception ex){if(!_closed)_status.Text=ex.Message;}
    }
    private void FramePreview()
    {
        _preview.ResetView();_preview.FrameSelection(tight:true);
    }
    internal async Task RunUiCheckAsync(bool cancel=false)
    {
        await Task.Delay(500);
        string? error=null;
        var originalOpeningHeight=_opening.Height;
        _fields["height"].Text="2";await Task.Delay(180);
        if(_opening.Height!=originalOpeningHeight)throw new InvalidOperationException("不完整洞口尺寸改变了有效分格。");
        _fields["height"].Text=(originalOpeningHeight-100).ToString(CultureInfo.InvariantCulture);await Task.Delay(300);
        if(_opening.Height!=originalOpeningHeight-100||Math.Abs(_canvas.State.Height-(_opening.Height-(_gap.IsChecked==true?2*_draft.InstallationGap:0)))>.01)
            throw new InvalidOperationException("洞口高修改未同步整体分格。");
        _gap.IsChecked=false;
        if(!ReadDraft())throw new InvalidOperationException(_status.Text);
        StoreFace();
        if(_canvas.State.Cells.Max(c=>c.Right)<_canvas.State.Width-.01 || _canvas.State.Cells.Max(c=>c.Top)<_canvas.State.Height-.01)
            throw new InvalidOperationException("关闭安装缝后分格未布满洞口。");
        var bay=_draft.ElevationType=="凸窗";
        if(bay) {
            if(_canvas.State.Cells.Any(c=>c.Left<0 || c.Right>_canvas.State.Width+.01))throw new InvalidOperationException("飘窗正面包含展开侧格。");
            if(!_canvas.State.MoveDivider(true,600,750,out error,250,5))throw new InvalidOperationException(error);
            _canvas.Notify();_face.SelectedIndex=1;
            if(Math.Abs(_canvas.State.Width-_draft.BayLeftDepth)>.01)throw new InvalidOperationException("左侧面宽度不是实际进深。");
            if(!_canvas.State.MoveDivider(false,500,650,out error,250,5))throw new InvalidOperationException(error);
            _canvas.Notify();_face.SelectedIndex=2;
            if(_canvas.State.Cells.Any(c=>Math.Abs(c.Top-650)<.01))throw new InvalidOperationException("左侧编辑污染了右侧。");
            _face.SelectedIndex=1;
            if(!_canvas.State.Cells.Any(c=>Math.Abs(c.Top-650)<.01))throw new InvalidOperationException("切换面丢失编辑。");
            await Task.Delay(300);await SaveCheckImage("bay-editor-left.png");_face.SelectedIndex=0;
        }
        else if(!_opening.HasSwingLeaf() && !_canvas.State.Split(new[]{0},true,3,out error))throw new InvalidOperationException(error);
        _canvas.Selected.Clear();_canvas.Selected.Add(1);_canvas.Notify();
        _openingMode.SelectedItem="右平开";_fields["glass"].Text="12";_fields["frameDepth"].Text="100";
        await Task.Delay(2000);
        _scope.SelectedIndex=1;await Task.Delay(800);_scope.SelectedIndex=0;await Task.Delay(800);
        if(Environment.GetEnvironmentVariable("WANLUO_OPENING_CHECK_SLIDING")=="1"){
            _canvas.State.Edit(cells=>{foreach(var c in cells)if(DoorWindowElevationGeometryBuilder.IsOperable(c.Opening))c.Opening="双向推拉";},out _);_canvas.Notify();await Task.Delay(1200);
        }
        if(!_preview.FrameRendered)throw new InvalidOperationException("门窗局部三维未绘制。");
        var canvasTop=_canvas.TranslatePoint(default,this)?.Y;var previewTop=_preview.TranslatePoint(default,this)?.Y;
        if(canvasTop==null||previewTop==null||Math.Abs(canvasTop.Value-previewTop.Value)>1)throw new InvalidOperationException("二维与三维预览顶部未对齐。");
        await SaveCheckImage(bay ? "bay-editor-main.png" : "editor-ui.png");
        var originalWidth=Width;var originalHeight=Height;Width=1120;Height=720;await Task.Delay(300);FramePreview();await Task.Delay(300);
        await SaveCheckImage(bay ? "bay-editor-compact.png" : "editor-compact.png");
        Width=originalWidth;Height=originalHeight;await Task.Delay(300);FramePreview();await Task.Delay(300);
        if(Environment.GetEnvironmentVariable("WANLUO_OPENING_CHECK_SLIDING")=="1"){
            _inspectorTabs.SelectedIndex=1;await Task.Delay(300);await SaveCheckImage("sliding-construction-ui.png");
        }
        if(!ReadDraft())throw new InvalidOperationException(_status.Text);StoreFace();
        if(cancel)Close();else {
            this.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ApplyOpeningEditor")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await _applyTask;
            if(!_closed)throw new InvalidOperationException("分格编辑应用失败："+_status.Text);
        }
    }
    private async Task SaveCheckImage(string name)
    {
        var visual=Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(this);
        if(visual==null)throw new InvalidOperationException("编辑窗口没有渲染。");
        var snapshot=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
        snapshot.Save(Path.GetFullPath(".artifacts/opening-editor/"+name),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

}
