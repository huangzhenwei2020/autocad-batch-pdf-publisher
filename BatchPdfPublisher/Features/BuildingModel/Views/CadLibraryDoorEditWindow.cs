using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadLibraryDoorEditWindow : Window
    {
        private readonly TextBox _width=new TextBox(),_height=new TextBox();
        private readonly CheckBox _along=new CheckBox {Content="左右换向"},_across=new CheckBox {Content="内外换向"};
        private readonly TextBlock _status=new TextBlock {TextWrapping=TextWrapping.Wrap};
        private readonly CadComponentPartPreview _preview=new CadComponentPartPreview {MinHeight=230};
        private readonly ComponentPlanSymbol _source;private readonly PointModel _base;
        public Func<Window,bool,bool> ApplyRequested {get;set;}
        public Action RemoveRequested {get;set;}
        public double DoorWidth=>Dimension(_width);public double DoorHeight=>Dimension(_height);
        public bool FlipAlong=>_along.IsChecked==true;public bool FlipAcross=>_across.IsChecked==true;
        public CadLibraryDoorEditWindow(ComponentPlanSymbol plan,PointModel localBase,double width,double height,bool along,bool across)
        {
            _source=plan;_base=localBase;Title="图库门 · 沿墙编辑";Width=740;Height=590;MinWidth=640;MinHeight=540;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=14;
            Background=new SolidColorBrush(Color.FromRgb(16,25,35));Foreground=Brushes.WhiteSmoke;
            Resources.MergedDictionaries.Add(CadComponentPlanWindow.CreateTheme());
            var root=new Grid {Margin=new Thickness(18),Background=Background,Resources=Resources};TextElement.SetFontSize(root,FontSize);root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            root.Children.Add(new TextBlock {Text="沿墙编辑 · "+ComponentPlanSymbols.DisplayName(plan),FontSize=21,Margin=new Thickness(0,0,0,16)});
            var body=new Grid();body.ColumnDefinitions.Add(new ColumnDefinition());body.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(215)});body.Children.Add(_preview);
            var fields=new StackPanel {Margin=new Thickness(16,0,0,0)};Field(fields,"洞口宽 mm",_width,width);Field(fields,"洞口高 mm",_height,height);
            _along.IsChecked=along;_across.IsChecked=across;_along.Foreground=_across.Foreground=Foreground;_along.Margin=_across.Margin=new Thickness(0,12,0,0);fields.Children.Add(_along);fields.Children.Add(_across);
            fields.Children.Add(Button("应用尺寸与方向",()=>Apply(false)));fields.Children.Add(Button("移动到墙上…",()=>Apply(true)));
            fields.Children.Add(Button("删除门并恢复墙",()=>Run(()=>{RemoveRequested?.Invoke();Close();})));
            fields.Children.Add(new TextBlock {Text="普通直线墙：图层名含 墙 / wall。移动和改宽度联动恢复旧洞、重开新洞。单开门按部件变尺，保持门框、门扇厚度和把手尺寸。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,0),Foreground=Brushes.LightSteelBlue});
            var propertyScroll=new ScrollViewer {Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(propertyScroll,1);body.Children.Add(propertyScroll);Grid.SetRow(body,1);root.Children.Add(body);
            var footer=new DockPanel {Margin=new Thickness(0,12,0,0)};var close=Button("关闭",Close);DockPanel.SetDock(close,Dock.Right);footer.Children.Add(close);footer.Children.Add(_status);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
            _width.TextChanged+=(_,__)=>Run(Preview);_height.TextChanged+=(_,__)=>Run(Preview);
            _along.Checked+=(_,__)=>Run(Preview);_along.Unchecked+=(_,__)=>Run(Preview);_across.Checked+=(_,__)=>Run(Preview);_across.Unchecked+=(_,__)=>Run(Preview);Preview();
        }
        private void Preview()
        {
            var plan=ComponentPlanSymbols.DoorPlanVariant(_source,_base,DoorWidth,DoorHeight);
            foreach(var primitive in plan.Primitives) {
                if(FlipAlong){primitive.X1=2*_base.X+DoorWidth-primitive.X1;if(primitive.Kind=="Line")primitive.X2=2*_base.X+DoorWidth-primitive.X2;else {primitive.StartDegrees=180-primitive.StartDegrees;primitive.SweepDegrees=-primitive.SweepDegrees;}}
                if(FlipAcross){primitive.Y1=2*_base.Y-primitive.Y1;if(primitive.Kind=="Line")primitive.Y2=2*_base.Y-primitive.Y2;else {primitive.StartDegrees=-primitive.StartDegrees;primitive.SweepDegrees=-primitive.SweepDegrees;}}
            }
            _preview.Symbol=plan;_preview.BasePoint=_base;_preview.InvalidateVisual();
            _status.Text="修改后应用；移动时右键或 Enter 可沿墙居中。";
        }
        private void Apply(bool move)=>Run(()=>{Preview();if(ApplyRequested?.Invoke(this,move)==true)_status.Text="已应用。关闭或 Esc 保留修改，Ctrl+Z 可撤销。";});
        private void Run(Action action){try{action();}catch(Exception ex){_status.Text=ex.Message;}}
        private static double Dimension(TextBox field){double value;if(!double.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)||double.IsNaN(value)||double.IsInfinity(value)||value<200||value>10000)throw new InvalidOperationException("洞口宽高应在 200～10000 mm 之间。");return value;}
        private static void Field(StackPanel parent,string label,TextBox field,double value){parent.Children.Add(new TextBlock {Text=label,Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,0,0,5)});field.Text=value.ToString("0.###",CultureInfo.InvariantCulture);field.Height=34;field.Background=new SolidColorBrush(Color.FromRgb(16,25,35));field.Foreground=Brushes.WhiteSmoke;field.BorderBrush=new SolidColorBrush(Color.FromRgb(52,76,96));field.Padding=new Thickness(8,0,8,0);field.Margin=new Thickness(0,0,0,12);field.VerticalContentAlignment=VerticalAlignment.Center;parent.Children.Add(field);}
        private static Button Button(string text,Action action){var button=new Button {Content=text,Height=36,Padding=new Thickness(12,0,12,0),Foreground=Brushes.WhiteSmoke,Background=new SolidColorBrush(Color.FromRgb(38,60,78)),BorderBrush=new SolidColorBrush(Color.FromRgb(52,76,96)),Margin=new Thickness(0,10,0,0)};button.Click+=(_,__)=>action();return button;}
    }
}
