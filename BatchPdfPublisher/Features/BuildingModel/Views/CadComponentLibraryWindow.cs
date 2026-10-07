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
        private readonly ListBox _items=new ListBox();
        private readonly TextBox _search=new TextBox {Height=34,VerticalContentAlignment=VerticalAlignment.Center};
        private readonly ComboBox _kind=new ComboBox {ItemsSource=new[]{"门","窗"},SelectedIndex=0,Height=34};
        private readonly TextBlock _status=new TextBlock {TextWrapping=TextWrapping.Wrap};
        private readonly TextBlock _details=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,10)};
        private readonly PlanPreview _preview=new PlanPreview {MinHeight=210};
        private readonly RadioButton _x=new RadioButton {Content="X",IsChecked=true,Margin=new Thickness(0,8,30,8)};
        private readonly RadioButton _y=new RadioButton {Content="Y",Margin=new Thickness(0,8,0,8)};
        private readonly TextBox _units=new TextBox {Text="1",Height=34,VerticalContentAlignment=VerticalAlignment.Center};
        public Action<string,ComponentCatalogRecord> StorePlan {get;set;}
        public Func<ComponentCatalogRecord,string,double,bool> InsertPlan {get;set;}
        public CadComponentLibraryWindow(ComponentCatalog catalog)
        {
            _catalog=catalog;Title="图库";Width=960;Height=680;MinWidth=760;MinHeight=600;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;FontSize=14;FontFamily=new FontFamily("Microsoft YaHei UI");
            Background=new SolidColorBrush(Color.FromRgb(244,247,251));UseLayoutRounding=true;
            var root=new Grid {Margin=new Thickness(18)};
            foreach(var height in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})root.RowDefinitions.Add(new RowDefinition {Height=height});
            var toolbar=new Grid {Margin=new Thickness(0,0,0,12)};
            toolbar.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(80)});toolbar.ColumnDefinitions.Add(new ColumnDefinition());
            toolbar.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});toolbar.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            toolbar.Children.Add(_kind);Grid.SetColumn(_search,1);_search.Margin=new Thickness(10,0,10,0);toolbar.Children.Add(_search);
            var add=Button("平面入库",()=>Run(()=>StorePlan?.Invoke(Category(),null)));Grid.SetColumn(add,2);toolbar.Children.Add(add);
            var refresh=Button("刷新",()=>Reload());Grid.SetColumn(refresh,3);toolbar.Children.Add(refresh);root.Children.Add(toolbar);
            var body=new Grid();body.ColumnDefinitions.Add(new ColumnDefinition());body.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(300)});
            body.Children.Add(_items);_items.DisplayMemberPath="";
            var inspector=new Grid {Margin=new Thickness(16,0,0,0)};
            inspector.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});inspector.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            inspector.Children.Add(_preview);
            var fields=new StackPanel();fields.Children.Add(_details);
            fields.Children.Add(new TextBlock {Text="插入方向（当前 UCS）"});var axes=new StackPanel {Orientation=Orientation.Horizontal};axes.Children.Add(_x);axes.Children.Add(_y);fields.Children.Add(axes);
            fields.Children.Add(new TextBlock {Text="每 CAD 单位对应毫米",Margin=new Thickness(0,8,0,6)});fields.Children.Add(_units);
            fields.Children.Add(Button("更新所选平面",()=>Run(()=>{
                if(_items.SelectedItem is ComponentCatalogRecord record)StorePlan?.Invoke(record.Plan.Category,record);
            })));
            fields.Children.Add(Button("插入 CAD",()=>Run(()=>{
                if(!(_items.SelectedItem is ComponentCatalogRecord record))throw new InvalidOperationException("请选择图库构件。");
                double units;if(!double.TryParse(_units.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out units)||double.IsNaN(units)||double.IsInfinity(units)||units<=0||units>1000000)
                    throw new InvalidOperationException("请输入有效的毫米换算。");
                _status.Text=InsertPlan?.Invoke(record,_y.IsChecked==true?"Y":"X",units)==true?"插入完成":"已取消插入";
            })));
            Grid.SetRow(fields,1);inspector.Children.Add(fields);Grid.SetColumn(inspector,1);body.Children.Add(inspector);Grid.SetRow(body,1);root.Children.Add(body);
            var footer=new DockPanel {Margin=new Thickness(0,12,0,0)};var close=Button("关闭",Close);DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);footer.Children.Add(_status);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
            _search.TextChanged+=(_,__)=>Reload();_kind.SelectionChanged+=(_,__)=>Reload();
            _items.SelectionChanged+=(_,__)=>{
                var record=_items.SelectedItem as ComponentCatalogRecord;_preview.Symbol=record?.Plan;_preview.InvalidateVisual();
                _details.Text=record==null?"未选择构件":record.Plan.Code+"\n"+record.Plan.Width.ToString("0.###")+" × "+record.Plan.Height.ToString("0.###")+" mm\n"+
                    (record.IsModelCurrent?"三维模型已配对":record.ModelRevision>0?"平面已更新 · 三维待复核":"CAD 平面已入库 · 待补充三维");
            };
            Activated+=(_,__)=>Reload();Reload();
        }
        private string Category()=>_kind.SelectedIndex==0?"Door":"Window";
        public void Reload(string selectedId=null)
        {
            Run(()=>{
                selectedId=selectedId??(_items.SelectedItem as ComponentCatalogRecord)?.AssetId;
                var snapshot=_catalog.Load();var query=_search.Text??"";
                var rows=snapshot.Records.Where(r=>r.Plan.Category==Category()&&(r.Plan.Code+" "+r.Plan.Name).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0).OrderBy(r=>r.Plan.Code).ToArray();
                _items.ItemsSource=rows;_items.SelectedItem=rows.FirstOrDefault(r=>r.AssetId==selectedId)??rows.FirstOrDefault();
                _status.Text=snapshot.Errors.Count>0?snapshot.Errors[0]:rows.Length+" 项 · 共享图库";
            });
        }
        private void Run(Action action){try{action();}catch(Exception ex){_status.Text=ex.Message;}}
        private static Button Button(string caption,Action action)
        {
            var button=new Button {Content=caption,Height=36,Padding=new Thickness(12,0,12,0),Margin=new Thickness(6,6,0,0)};
            button.Click+=(_,__)=>action();return button;
        }
        private sealed class PlanPreview : FrameworkElement
        {
            public ComponentPlanSymbol Symbol;
            protected override void OnRender(DrawingContext context)
            {
                base.OnRender(context);context.DrawRectangle(Brushes.White,new Pen(Brushes.LightGray,1),new Rect(RenderSize));if(Symbol==null)return;
                var points=Symbol.Primitives.SelectMany(p=>p.Kind=="Line"?new[]{new Point(p.X1,p.Y1),new Point(p.X2,p.Y2)}:
                    new[]{new Point(p.X1-p.Radius,p.Y1-p.Radius),new Point(p.X1+p.Radius,p.Y1+p.Radius)}).ToArray();
                var minX=points.Min(p=>p.X);var maxX=points.Max(p=>p.X);var minY=points.Min(p=>p.Y);var maxY=points.Max(p=>p.Y);
                var scale=Math.Max(.000001,Math.Min((ActualWidth-32)/Math.Max(1,maxX-minX),(ActualHeight-32)/Math.Max(1,maxY-minY)));
                Point Map(double x,double y)=>new Point(ActualWidth/2+(x-(minX+maxX)/2)*scale,ActualHeight/2-(y-(minY+maxY)/2)*scale);
                var pen=new Pen(new SolidColorBrush(Color.FromRgb(43,78,105)),1.2);
                foreach(var p in Symbol.Primitives) {
                    if(p.Kind=="Line")context.DrawLine(pen,Map(p.X1,p.Y1),Map(p.X2,p.Y2));
                    else if(p.Kind=="Circle")context.DrawEllipse(null,pen,Map(p.X1,p.Y1),p.Radius*scale,p.Radius*scale);
                    else {var count=Math.Max(8,(int)Math.Ceiling(Math.Abs(p.SweepDegrees)/5));for(var i=0;i<count;i++) {
                        var a=(p.StartDegrees+p.SweepDegrees*i/count)*Math.PI/180;var b=(p.StartDegrees+p.SweepDegrees*(i+1)/count)*Math.PI/180;
                        context.DrawLine(pen,Map(p.X1+p.Radius*Math.Cos(a),p.Y1+p.Radius*Math.Sin(a)),Map(p.X1+p.Radius*Math.Cos(b),p.Y1+p.Radius*Math.Sin(b)));
                    }}
                }
            }
        }
    }
}
