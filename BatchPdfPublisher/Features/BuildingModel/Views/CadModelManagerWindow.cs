using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadModelManagerWindow : Window
    {
        public readonly DataGrid Models=new DataGrid {AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,
            SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow};
        public readonly TextBox Search=new TextBox {Height=34,VerticalContentAlignment=VerticalAlignment.Center};
        public readonly ComboBox Scope=new ComboBox {ItemsSource=new[]{"全部模型","当前 CAD 图纸"},SelectedIndex=0,MinWidth=160};
        public readonly TextBlock Status=new TextBlock {TextWrapping=TextWrapping.Wrap};
        public readonly Button Open,Save,SaveAs;
        private readonly Grid _root;
        private readonly TextBlock _detail=new TextBlock {TextWrapping=TextWrapping.Wrap};
        private readonly string _project,_fingerprint;
        private readonly Func<string> _current;
        private readonly Func<Window,string,BuildingModelCatalogEntry,Task<string>> _action;
        private List<BuildingModelCatalogEntry> _entries=new List<BuildingModelCatalogEntry>();
        public bool Busy {get;private set;}
        public string SelectedModelPath {get;private set;}
        public CadModelManagerWindow(string project,string fingerprint,Func<string> current,
            Func<Window,string,BuildingModelCatalogEntry,Task<string>> action)
        {
            _project=project;_fingerprint=fingerprint;_current=current;_action=action;
            Title="模型管理";Width=940;Height=600;MinWidth=640;MinHeight=420;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=14;UseLayoutRounding=true;
            Background=new SolidColorBrush(Color.FromRgb(16,25,35));Foreground=Brushes.WhiteSmoke;
            Resources.MergedDictionaries.Add(CadComponentPlanWindow.CreateTheme());
            _root=new Grid {Margin=new Thickness(18),Background=Background};
            foreach(var h in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})
                _root.RowDefinitions.Add(new RowDefinition {Height=h});
            _root.Children.Add(new TextBlock {Text="模型管理",FontSize=22,FontWeight=FontWeights.SemiBold});
            var toolbar=new WrapPanel {Margin=new Thickness(0,12,0,8)};
            Button Tool(string icon,string label,string op){var b=new Button {Content=ToolContent(icon,label),Height=36,Background=new SolidColorBrush(Color.FromRgb(32,54,74)),Foreground=Brushes.WhiteSmoke,
                Padding=new Thickness(10,0,10,0),Margin=new Thickness(0,0,8,4),ToolTip=label};
                b.Click+=async(s,e)=>await Run(op);toolbar.Children.Add(b);return b;}
            Tool("file-plus","新建","New");Tool("folder-open","添加已有","Add");Open=Tool("folder-open","打开 / 切换","Open");Save=Tool("save","保存当前","Save");SaveAs=Tool("copy","另存当前","SaveAs");Tool("rotate-ccw","刷新","Refresh");
            Grid.SetRow(toolbar,1);_root.Children.Add(toolbar);
            var filters=new Grid {Margin=new Thickness(0,0,0,10)};filters.ColumnDefinitions.Add(new ColumnDefinition());filters.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            Search.ToolTip="搜索模型名称";Search.Background=new SolidColorBrush(Color.FromRgb(32,54,74));Search.Foreground=Brushes.WhiteSmoke;
            filters.Children.Add(Search);Scope.Margin=new Thickness(10,0,0,0);Grid.SetColumn(Scope,1);filters.Children.Add(Scope);
            Scope.IsEnabled=!string.IsNullOrWhiteSpace(fingerprint);Grid.SetRow(filters,2);_root.Children.Add(filters);
            foreach(var c in new[]{new[]{"模型名称","Name"},new[]{"楼层","Storeys"},new[]{"状态","Status"},new[]{"修改时间","ModifiedText"}})
                Models.Columns.Add(new DataGridTextColumn {Header=c[0],Binding=new Binding(c[1]),Width=new DataGridLength(c[1]=="Name"?3:c[1]=="Storeys"?1:2,DataGridLengthUnitType.Star)});
            Models.Background=Background;Models.Foreground=Foreground;Models.BorderBrush=new SolidColorBrush(Color.FromRgb(56,80,95));
            Models.RowBackground=Background;Models.AlternatingRowBackground=new SolidColorBrush(Color.FromRgb(24,37,49));Models.RowHeight=42;
            Models.HeadersVisibility=DataGridHeadersVisibility.Column;Models.GridLinesVisibility=DataGridGridLinesVisibility.Horizontal;
            Models.HorizontalGridLinesBrush=new SolidColorBrush(Color.FromRgb(38,55,69));
            var headerStyle=new Style(typeof(DataGridColumnHeader));headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(32,54,74))));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,Foreground));headerStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,8,10,8)));Models.ColumnHeaderStyle=headerStyle;
            var cellStyle=new Style(typeof(DataGridCell));cellStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,8,10,8)));
            cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));
            var selected=new Trigger {Property=DataGridCell.IsSelectedProperty,Value=true};
            selected.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(22,86,124))));selected.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.WhiteSmoke));
            cellStyle.Triggers.Add(selected);Models.CellStyle=cellStyle;
            Grid.SetRow(Models,3);_root.Children.Add(Models);_detail.Margin=new Thickness(0,10,0,10);Grid.SetRow(_detail,4);_root.Children.Add(_detail);
            var footer=new DockPanel();var close=new Button {Content="关闭",Background=new SolidColorBrush(Color.FromRgb(32,54,74)),Foreground=Brushes.WhiteSmoke,Padding=new Thickness(15,7,15,7),IsCancel=true};close.Click+=(s,e)=>Close();DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);footer.Children.Add(Status);
            Grid.SetRow(footer,5);_root.Children.Add(footer);Content=_root;
            Search.TextChanged+=(s,e)=>Filter();Scope.SelectionChanged+=(s,e)=>Filter();Models.SelectionChanged+=(s,e)=>Selection();
            Models.MouseDoubleClick+=async(s,e)=>{if(Models.SelectedItem is BuildingModelCatalogEntry)await Run("Open");};
            Closing+=(s,e)=>{if(Busy)e.Cancel=true;};Refresh();
        }
        private static object ToolContent(string icon,string label)
        {
            var panel=new StackPanel {Orientation=Orientation.Horizontal};
            using(var stream=typeof(CadModelManagerWindow).Assembly.GetManifestResourceStream("WanLuo.ModelManager."+icon+".png"))
            {
                if(stream==null)throw new InvalidDataException("缺少模型管理图标："+icon);
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();
                panel.Children.Add(new Image {Source=image,Width=18,Height=18,Margin=new Thickness(0,0,6,0)});
            }
            panel.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});return panel;
        }
        public async Task Run(string operation)
        {
            if(Busy)return;Busy=true;_root.IsEnabled=false;
            try
            {
                Status.Text=operation=="Save"?"正在保存当前模型…":operation=="SaveAs"?"正在另存当前模型…":"正在处理…";
                if(operation!="Refresh")
                {
                    var next=await _action(this,operation,Models.SelectedItem as BuildingModelCatalogEntry);
                    if(next!=null){SelectedModelPath=next;Status.Text=(operation=="Save"?"已保存：":"当前模型：")+BuildingModelJson.LoadModel(next).Name;}
                }
                else Status.Text="";
                Refresh();
            }
            catch(Exception ex){Status.Text=ex.Message;}
            finally{Busy=false;_root.IsEnabled=true;}
        }
        public void Refresh()
        {
            try{_entries=BuildingModelCatalog.List(_project,_current());Filter();}
            catch(Exception ex){Status.Text=ex.Message;}
        }
        private void Filter()
        {
            var previous=(Models.SelectedItem as BuildingModelCatalogEntry)?.ModelPath;var query=Search.Text.Trim();
            var rows=_entries.Where(e=>(query.Length==0||e.Name.IndexOf(query,StringComparison.CurrentCultureIgnoreCase)>=0)
                &&(Scope.SelectedIndex!=1||BuildingModelCatalog.LinkedTo(e,_fingerprint))).ToList();
            Models.ItemsSource=rows;Models.SelectedItem=rows.FirstOrDefault(e=>e.ModelPath==previous)??rows.FirstOrDefault(e=>e.IsCurrent)??rows.FirstOrDefault();Selection();
        }
        private void Selection()
        {
            var entry=Models.SelectedItem as BuildingModelCatalogEntry;Open.IsEnabled=entry?.Available==true;
            var current=_entries.FirstOrDefault(e=>e.IsCurrent);Save.IsEnabled=current?.Available==true&&current.IsOpen;SaveAs.IsEnabled=current?.Available==true;
            Save.ToolTip=current?.IsOpen==true?"保存模型窗口里的最新修改":"模型未打开；磁盘文件已经保存";
            _detail.Text=entry==null?"未找到模型":entry.ModelPath+(string.IsNullOrWhiteSpace(entry.DrawingText)?"":"\n关联图纸："+entry.DrawingText)+(entry.Error==null?"":"\n"+entry.Error);
        }
    }
    public sealed class CadModelNameWindow : Window
    {
        private readonly TextBox _name=new TextBox();
        public string ModelName => _name.Text.Trim();
        public CadModelNameWindow(string project,string name)
        {
            Title="另存建筑模型";Width=520;Height=250;MinWidth=360;MinHeight=230;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=14;UseLayoutRounding=true;
            var root=new Grid {Margin=new Thickness(18)};root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var fields=new StackPanel();fields.Children.Add(new TextBlock {Text="新模型名称"});_name.Text=name+" 副本";_name.Margin=new Thickness(0,8,0,8);fields.Children.Add(_name);
            var path=new TextBlock {TextWrapping=TextWrapping.Wrap};fields.Children.Add(path);root.Children.Add(fields);
            var error=new TextBlock {Foreground=Brushes.Firebrick,TextWrapping=TextWrapping.Wrap};Grid.SetRow(error,1);root.Children.Add(error);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            var cancel=new Button {Content="取消",IsCancel=true,Padding=new Thickness(12,7,12,7)};cancel.Click+=(s,e)=>{DialogResult=false;};actions.Children.Add(cancel);
            var ok=new Button {Content="另存模型",Padding=new Thickness(12,7,12,7),Margin=new Thickness(8,0,0,0)};
            ok.Click+=(s,e)=>{try{BuildingModelProjectWorkflow.ValidateName(ModelName);if(Directory.Exists(Path.GetDirectoryName(BuildingModelJson.ModelFilePath(project,ModelName))))throw new IOException("此模型名称已存在，请使用其他名称。");DialogResult=true;}catch(Exception ex){error.Text=ex.Message;}};
            actions.Children.Add(ok);Grid.SetRow(actions,2);root.Children.Add(actions);Content=root;
            void Preview(){path.Text=BuildingModelJson.ModelFilePath(project,ModelName);}
            _name.TextChanged+=(s,e)=>Preview();Preview();
        }
    }
}
