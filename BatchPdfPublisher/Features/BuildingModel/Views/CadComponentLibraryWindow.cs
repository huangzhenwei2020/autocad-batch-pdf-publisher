using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadComponentLibraryWindow : Window
    {
        private readonly ComponentCatalog _catalog;
        private readonly ListBox _items=new ListBox(),_types=new ListBox();
        private readonly TextBox _search=new TextBox {Height=34,VerticalContentAlignment=VerticalAlignment.Center};
        private readonly ComboBox _kind=new ComboBox {ItemsSource=new[]{"装修门","窗"},SelectedIndex=0,Height=34};
        private readonly CheckBox _deleted=new CheckBox {Content="已删除资源",Margin=new Thickness(12,8,0,0)};
        private readonly TextBlock _status=new TextBlock {TextWrapping=TextWrapping.Wrap},_details=new TextBlock {TextWrapping=TextWrapping.Wrap};
        private readonly CadComponentPartPreview _preview=new CadComponentPartPreview {MinHeight=120};
        private readonly Button _delete;
        private readonly RadioButton _x=new RadioButton {Content="UCS X",IsChecked=true,GroupName="Axis"},_y=new RadioButton {Content="UCS Y",GroupName="Axis"};
        private readonly RadioButton _edge=new RadioButton {Content="靠边",IsChecked=true,GroupName="Position"},_center=new RadioButton {Content="居中",GroupName="Position"};
        private readonly CheckBox _wall=new CheckBox {Content="沿墙自动开门洞",IsChecked=true};
        private readonly TextBox _units=Input("1"),_width=Input("900"),_height=Input("2100"),_jamb=Input("100"),_wallThickness=Input("0"),_code=Input("");
        private readonly ComboBox _angle=new ComboBox {ItemsSource=new[]{"原图","0","30","45","60","90","120","180"},SelectedIndex=0,IsEditable=true,Height=32};
        private string _baseKey;private PointModel _rememberedBase;private bool _loading;
        public Action<string,ComponentCatalogRecord> StorePlan {get;set;}
        public Action EditPlacedDoor {get;set;}
        public Action EditRegion {get;set;}
        public Action SynchronizeWalls {get;set;}
        public Func<ComponentCatalogRecord,string,double,PointModel,bool> InsertPlan {get;set;}
        public Func<bool> ConfirmDelete {get;set;}
        public ComponentCatalogRecord SelectedRecord=>(_items.SelectedItem as ListBoxItem)?.Tag as ComponentCatalogRecord;
        public bool AutoWall=>_wall.IsChecked==true;
        public bool Centered=>_center.IsChecked==true;
        public double JambClearance=>Read(_jamb,0,100000,"门垛净距");
        public double? WallThickness {get{var value=Read(_wallThickness,0,1000,"墙厚");if(value>0&&value<50)throw new InvalidOperationException("墙厚应为 0（自动）或 50～1000 mm。");return value==0?(double?)null:value;}}
        public double DoorWidth=>Read(_width,200,10000,"洞口宽");
        public double DoorHeight=>Read(_height,200,10000,"洞口高");
        public string InstanceCode=>_code.Text.Trim();
        public double? OpeningAngle {get{if(_angle.Text=="原图"||string.IsNullOrWhiteSpace(_angle.Text))return null;double value;if(!double.TryParse(_angle.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)||!Finite(value)||value<0||value>180)throw new InvalidOperationException("开启角度应在 0～180° 之间。");return value;}}
        public PointModel InsertionBase=>_preview.BasePoint;
        public CadComponentLibraryWindow(ComponentCatalog catalog)
        {
            _catalog=catalog;Title="图库 · 门窗平面";Width=1180;Height=780;MinWidth=960;MinHeight=640;FontSize=14;FontFamily=new FontFamily("Microsoft YaHei UI");WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Background=Brush(16,25,35);Foreground=Brushes.WhiteSmoke;UseLayoutRounding=true;Resources.MergedDictionaries.Add(CadComponentPlanWindow.CreateTheme());
            Width=Math.Min(Width,Math.Max(760,SystemParameters.WorkArea.Width-32));Height=Math.Min(Height,Math.Max(560,SystemParameters.WorkArea.Height-32));MinWidth=Math.Min(MinWidth,Width);MinHeight=Math.Min(MinHeight,Height);
            _search.Background=Brush(14,24,34);_search.Foreground=Brushes.WhiteSmoke;_search.BorderBrush=Brush(52,76,96);_types.Background=Brush(14,24,34);_types.Foreground=Brushes.WhiteSmoke;_types.BorderBrush=Brush(52,76,96);foreach(var toggle in new Control[]{_deleted,_wall,_x,_y,_edge,_center})toggle.Foreground=Brushes.WhiteSmoke;
            var root=new Grid {Margin=new Thickness(16)};root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var header=new Grid {Margin=new Thickness(0,0,0,12)};header.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(105)});header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});header.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});header.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            header.Children.Add(_kind);Grid.SetColumn(_search,1);_search.Margin=new Thickness(12,0,12,0);_search.ToolTip="搜索资源名称或参考编号";header.Children.Add(_search);Grid.SetColumn(_deleted,2);header.Children.Add(_deleted);var add=Button("平面入库",()=>Run(()=>StorePlan?.Invoke(Category(),null)));Grid.SetColumn(add,3);header.Children.Add(add);var refresh=Button("刷新",()=>Reload());Grid.SetColumn(refresh,4);header.Children.Add(refresh);root.Children.Add(header);
            var body=new Grid();body.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(214)});body.ColumnDefinitions.Add(new ColumnDefinition());body.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(240)});
            var left=new Grid {Margin=new Thickness(0,0,12,0)};left.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});left.RowDefinitions.Add(new RowDefinition {Height=new GridLength(148)});left.RowDefinitions.Add(new RowDefinition());left.Children.Add(Label("门型分类"));Grid.SetRow(_types,1);left.Children.Add(_types);
            var fields=new StackPanel {Margin=new Thickness(0,10,0,0)};fields.Children.Add(Label("插入参数"));fields.Children.Add(_wall);
            var mode=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,4)};_edge.Margin=new Thickness(0,0,28,0);mode.Children.Add(_edge);mode.Children.Add(_center);fields.Children.Add(mode);
            Pair(fields,"洞口宽 mm",_width,"洞口高 mm",_height);Pair(fields,"门垛净距 mm",_jamb,"墙厚 mm",_wallThickness);fields.Children.Add(new TextBlock {Text="门垛：洞口边到较近墙端。\n墙厚：0 自动识别；填写值限定双线墙厚。",FontSize=11,Foreground=Brushes.LightSteelBlue,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,4)});
            Field(fields,"项目编号（可留空）",_code);Field(fields,"开启角度 · °",_angle);fields.Children.Add(new TextBlock {Text="先点墙上位置，再用鼠标确定开启方向。",FontSize=12,Foreground=Brushes.LightSteelBlue,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)});
            var axes=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,8)};_x.Margin=new Thickness(0,0,20,0);axes.Children.Add(_x);axes.Children.Add(_y);fields.Children.Add(axes);Field(fields,"每 CAD 单位对应毫米",_units);
            fields.Children.Add(Button("恢复默认基点",()=>{if(_preview.Symbol!=null)SetInsertionBase(ComponentPlanSymbols.SuggestedInsertionBase(_preview.Symbol));}));fields.Children.Add(new TextBlock {Text="绿色十字是插入基点，可点击右侧预览调整。沿墙插入时门套中心对齐墙中线。",FontSize=11,Foreground=Brushes.LightSteelBlue,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)});
            var scroll=new ScrollViewer {Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,2);left.Children.Add(scroll);body.Children.Add(left);
            _items.Background=Brush(14,24,34);_items.BorderBrush=Brush(52,76,96);ScrollViewer.SetHorizontalScrollBarVisibility(_items,ScrollBarVisibility.Disabled);ScrollViewer.SetVerticalScrollBarVisibility(_items,ScrollBarVisibility.Auto);var factory=new FrameworkElementFactory(typeof(WrapPanel));_items.ItemsPanel=new ItemsPanelTemplate(factory);Grid.SetColumn(_items,1);body.Children.Add(_items);
            var inspector=new Grid {Margin=new Thickness(12,0,0,0)};inspector.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});inspector.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});inspector.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});inspector.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            inspector.Children.Add(_preview);_details.Margin=new Thickness(0,12,0,8);Grid.SetRow(_details,1);inspector.Children.Add(_details);var update=Button("更新所选平面",()=>Run(()=>{if(SelectedRecord!=null&&!SelectedRecord.IsDeleted)StorePlan?.Invoke(SelectedRecord.Plan.Category,SelectedRecord);}));Grid.SetRow(update,2);inspector.Children.Add(update);_delete=Button("删除资源",()=>Run(DeleteSelected));Grid.SetRow(_delete,3);inspector.Children.Add(_delete);Grid.SetColumn(inspector,2);body.Children.Add(inspector);Grid.SetRow(body,1);root.Children.Add(body);
            var footer=new StackPanel {Margin=new Thickness(0,12,0,0)};var actions=new WrapPanel();actions.Children.Add(Button("插入 CAD",InsertSelected));actions.Children.Add(Button("框选区域 · 平面编辑",()=>Run(()=>EditRegion?.Invoke())));actions.Children.Add(Button("编辑已插入门",()=>Run(()=>EditPlacedDoor?.Invoke())));actions.Children.Add(Button("恢复已删除门的墙洞",()=>Run(()=>SynchronizeWalls?.Invoke())));actions.Children.Add(Button("关闭",Close));footer.Children.Add(actions);_status.Margin=new Thickness(0,8,0,0);footer.Children.Add(_status);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
            _preview.BasePointRequested=SetInsertionBase;_items.SelectionChanged+=(_,__)=>Selection();_search.TextChanged+=(_,__)=>Reload();_kind.SelectionChanged+=(_,__)=>{PopulateTypes();Reload();};_types.SelectionChanged+=(_,__)=>{if(!_loading)Reload();};_deleted.Checked+=(_,__)=>Reload();_deleted.Unchecked+=(_,__)=>Reload();Activated+=(_,__)=>Reload();PopulateTypes();Reload();
            _width.TextChanged+=(_,__)=>UpdateInsertionPreview();_height.TextChanged+=(_,__)=>UpdateInsertionPreview();_angle.AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler((_,__)=>UpdateInsertionPreview()));_angle.SelectionChanged+=(_,__)=>UpdateInsertionPreview();_center.Checked+=(_,__)=>_jamb.IsEnabled=false;_edge.Checked+=(_,__)=>_jamb.IsEnabled=true;
        }
        public void DeleteSelected()
        {
            var record=SelectedRecord;if(record==null)throw new InvalidOperationException("请选择资源。");
            if(!record.IsDeleted&&!(ConfirmDelete?.Invoke()??MessageBox.Show("从 CAD / 三维共享图库删除此资源？已插入图纸的门保留；可在“已删除资源”中恢复。","删除图库资源",MessageBoxButton.OKCancel)==MessageBoxResult.OK))return;
            _catalog.SetDeleted(record.AssetId,record.Version,!record.IsDeleted);Reload();_status.Text=record.IsDeleted?"资源已恢复到共享图库。":"资源已删除；可勾选“已删除资源”恢复。";
        }
        private void InsertSelected()=>Run(()=>{var record=SelectedRecord;if(record==null||record.IsDeleted)throw new InvalidOperationException("请选择有效图库资源。");var units=Read(_units,.000001,1000000,"毫米换算");if(record.Plan.Category=="Door"&&AutoWall){var check=DoorWidth+DoorHeight+JambClearance+(WallThickness??0)+(OpeningAngle??0);if(InstanceCode.Length>64||InstanceCode.Any(char.IsControl))throw new InvalidOperationException("编号应在 64 字以内。");}_status.Text=InsertPlan?.Invoke(record,_y.IsChecked==true?"Y":"X",units,_preview.BasePoint)==true?"插入完成":"已取消插入";});
        private string Category()=>_kind.SelectedIndex==0?"Door":"Window";
        private void PopulateTypes(){_loading=true;_types.Items.Clear();_types.Items.Add("全部");foreach(var name in Category()=="Door"?ComponentPlanSymbols.DoorAssemblyNames:new[]{"全部窗型"})_types.Items.Add(name);_types.SelectedIndex=0;_loading=false;}
        private void Selection()
        {
            var record=SelectedRecord;_preview.Symbol=record?.Plan;
            if(record!=null){var key=record.AssetId+"|"+record.PlanHash;if(_baseKey!=key){_rememberedBase=ComponentPlanSymbols.SuggestedInsertionBase(record.Plan);_baseKey=key;_width.Text=record.Plan.Width.ToString("0.###",CultureInfo.InvariantCulture);_height.Text=record.Plan.Height.ToString("0.###",CultureInfo.InvariantCulture);_code.Text=ComponentPlanSymbols.ReferenceCode(record.Plan);}_preview.BasePoint=_rememberedBase;}else _preview.BasePoint=null;
            foreach(ListBoxItem item in _items.Items){var card=(Border)item.Content;card.BorderBrush=item==_items.SelectedItem?Brushes.DeepSkyBlue:Brush(52,76,96);card.Background=item==_items.SelectedItem?Brush(28,55,76):Brush(16,25,35);}
            _delete.Content=record?.IsDeleted==true?"恢复资源":"删除资源";_details.Text=record==null?"选择构件查看平面":record.Plan.Name+"\n"+record.Plan.Width.ToString("0.###")+" × "+record.Plan.Height.ToString("0.###")+" mm\n"+(record.IsDeleted?"已删除 · 可恢复":record.IsModelCurrent?"三维模型已配对":record.ModelRevision>0?"三维待复核":"CAD 平面 · 待补充三维");_preview.InvalidateVisual();
            var door=record?.Plan.Category=="Door"&&!record.IsDeleted;foreach(var field in new Control[]{_width,_height,_code,_jamb,_wallThickness,_angle,_center,_edge,_wall})field.IsEnabled=door;_jamb.IsEnabled=door&&!Centered;UpdateInsertionPreview();
        }
        private void UpdateInsertionPreview(){var record=SelectedRecord;if(record==null||record.IsDeleted||record.Plan.Category!="Door")return;try{var plan=ComponentPlanSymbols.DoorPlanVariant(record.Plan,_rememberedBase,DoorWidth,DoorHeight);if(OpeningAngle.HasValue)plan=ComponentPlanSymbols.DoorOpeningVariant(plan,OpeningAngle.Value);_preview.Symbol=plan;_preview.InvalidateVisual();}catch(Exception ex){_preview.Symbol=record.Plan;_preview.InvalidateVisual();_status.Text=ex.Message;}}
        public void Reload(string selectedId=null)
        {
            if(_loading)return;Run(()=>{selectedId=selectedId??SelectedRecord?.AssetId;var snapshot=_catalog.Load(_deleted.IsChecked==true);var query=_search.Text??"";var type=_types.SelectedIndex-1;
                var rows=snapshot.Records.Where(r=>r.IsDeleted==(_deleted.IsChecked==true)&&r.Plan.Category==Category()&&(r.Plan.Code+" "+r.Plan.Name).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0&&(Category()!="Door"||type<0||(r.Plan.DoorAssembly??"Other")==ComponentPlanSymbols.DoorAssemblies[type])).OrderBy(r=>ComponentPlanSymbols.DisplayName(r.Plan)).ToArray();
                _items.Items.Clear();foreach(var record in rows){var stack=new StackPanel();stack.Children.Add(new CadComponentPartPreview {Symbol=record.Plan,ShowEmptyText=false,Height=120,IsHitTestVisible=false});stack.Children.Add(new TextBlock {Text=record.Plan.Name,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Brushes.WhiteSmoke,Margin=new Thickness(6,6,6,0)});stack.Children.Add(new TextBlock {Text=ComponentPlanSymbols.ReferenceCode(record.Plan)+"  "+record.Plan.Width.ToString("0.###")+" × "+record.Plan.Height.ToString("0.###"),FontSize=11,Foreground=Brushes.LightSteelBlue,Margin=new Thickness(6,3,6,6)});var card=new Border {BorderThickness=new Thickness(1),Child=stack,CornerRadius=new CornerRadius(3)};_items.Items.Add(new ListBoxItem {Tag=record,Content=card,Width=176,Padding=new Thickness(2),Margin=new Thickness(3),ToolTip=record.Plan.Name});}
                _items.SelectedItem=_items.Items.Cast<ListBoxItem>().FirstOrDefault(item=>((ComponentCatalogRecord)item.Tag).AssetId==selectedId)??_items.Items.Cast<ListBoxItem>().FirstOrDefault();Selection();_status.Text=snapshot.Errors.Count>0?snapshot.Errors[0]:rows.Length+" 项 · CAD / 三维共享图库";
            });
        }
        public void SetInsertionBase(PointModel point){if(point==null||!Finite(point.X)||!Finite(point.Y)||Math.Abs(point.X)>1000000||Math.Abs(point.Y)>1000000)throw new InvalidOperationException("插入基点无效。");_rememberedBase=_preview.BasePoint=new PointModel(point.X,point.Y);_preview.InvalidateVisual();}
        private void Run(Action action){try{action();}catch(Exception ex){_status.Text=ex.Message;}}
        private static SolidColorBrush Brush(byte r,byte g,byte b)=>new SolidColorBrush(Color.FromRgb(r,g,b));
        private static TextBox Input(string value)=>new TextBox {Text=value,Height=32,VerticalContentAlignment=VerticalAlignment.Center,Padding=new Thickness(6,0,6,0),Background=Brush(14,24,34),Foreground=Brushes.WhiteSmoke};
        private static TextBlock Label(string text)=>new TextBlock {Text=text,Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,0,0,8)};
        private static void Field(StackPanel panel,string label,FrameworkElement value){var title=Label(label);title.Margin=new Thickness(0,10,0,5);panel.Children.Add(title);panel.Children.Add(value);}
        private static void Pair(StackPanel panel,string a,FrameworkElement first,string b,FrameworkElement second){var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition());var one=new StackPanel();Field(one,a,first);grid.Children.Add(one);var two=new StackPanel {Margin=new Thickness(8,0,0,0)};Field(two,b,second);Grid.SetColumn(two,1);grid.Children.Add(two);panel.Children.Add(grid);}
        private static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
        private static double Read(TextBox field,double min,double max,string name){double value;if(!double.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)||!Finite(value)||value<min||value>max)throw new InvalidOperationException(name+"应为 "+min+"～"+max+" 的有效数值。");return value;}
        private static Button Button(string caption,Action action){var button=new Button {Content=caption,Height=36,Padding=new Thickness(10,0,10,0),Margin=new Thickness(0,4,6,0),Foreground=Brushes.WhiteSmoke,Background=Brush(38,60,78)};button.Click+=(_,__)=>action();return button;}
    }
}
