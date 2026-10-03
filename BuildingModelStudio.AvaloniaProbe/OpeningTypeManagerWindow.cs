using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class OpeningTypeManagerResult
{
    internal string? EditId;
    internal Dictionary<string,OpeningTypeModel> Types=new();
}
internal sealed class OpeningTypeManagerWindow : Window
{
    internal OpeningTypeManagerWindow(BuildingModelDocument source)
    {
        Title="项目门窗表与构造";Width=1000;Height=720;MinWidth=850;MinHeight=550;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.Parse("#162431"));
        var model=StandardStoreyLayout.Materialize(source);
        var rows=model.Openings.GroupBy(OpeningConstruction.EffectiveCode,StringComparer.OrdinalIgnoreCase).ToArray();
        var root=new Grid {RowDefinitions=new("Auto,*,Auto"),Margin=new Thickness(16)};
        root.Children.Add(new TextBlock {Text="项目门窗表 · 编辑分格或勾选类型批量设置厚度",FontSize=20});
        var body=new Grid {ColumnDefinitions=new("*,260"),Margin=new Thickness(0,16)};Grid.SetRow(body,1);root.Children.Add(body);
        var list=new StackPanel {Spacing=6};var checks=new Dictionary<string,CheckBox>();
        var all=new Button {Content="全选 / 清空"};all.Click+=(_,_)=>{var select=checks.Values.Any(c=>c.IsChecked!=true);foreach(var c in checks.Values)c.IsChecked=select;};list.Children.Add(all);
        foreach(var group in rows) {
            var opening=group.First();var row=new Grid {ColumnDefinitions=new("28,100,*,60,90"),MinHeight=42};
            var check=new CheckBox();checks[group.Key]=check;row.Children.Add(check);
            var code=new TextBlock {Text=string.IsNullOrEmpty(group.Key) ? "未编号" : group.Key,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(code,1);row.Children.Add(code);
            var dims=new TextBlock {Text=$"{opening.Kind} · {opening.Width:0.#} × {opening.Height:0.#} mm",VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(dims,2);row.Children.Add(dims);
            var count=new TextBlock {Text=group.Count()+" 樘",VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(count,3);row.Children.Add(count);
            var edit=new Button {Content="编辑分格"};edit.Click+=(_,_)=>Close(new OpeningTypeManagerResult {EditId=opening.Id});Grid.SetColumn(edit,4);row.Children.Add(edit);list.Children.Add(row);
        }
        body.Children.Add(new ScrollViewer {Content=list});
        var parameters=new StackPanel {Spacing=10,Margin=new Thickness(16,0,0,0)};Grid.SetColumn(parameters,1);body.Children.Add(parameters);
        parameters.Children.Add(new TextBlock {Text="只修改勾选的参数 · mm",TextWrapping=TextWrapping.Wrap});
        var values=new List<(CheckBox apply,TextBox value,Action<OpeningTypeModel,double> set)>();
        void Parameter(string label,double initial,Action<OpeningTypeModel,double> set) {
            var check=new CheckBox {Content=label};var value=new TextBox {Text=initial.ToString(CultureInfo.InvariantCulture)};
            parameters.Children.Add(check);parameters.Children.Add(value);values.Add((check,value,set));
        }
        Parameter("外框进深",100,(t,v)=>t.FrameDepth=v);Parameter("分隔框进深",100,(t,v)=>t.MullionDepth=v);
        Parameter("扇框进深",50,(t,v)=>t.SashDepth=v);Parameter("玻璃厚度",6,(t,v)=>t.GlassThickness=v);
        Parameter("门扇 / 实板厚度",40,(t,v)=>t.PanelThickness=v);
        var status=new TextBlock {TextWrapping=TextWrapping.Wrap};parameters.Children.Add(status);
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=12};Grid.SetRow(footer,2);root.Children.Add(footer);
        var apply=new Button {Content="应用批量构造",Padding=new Thickness(16,8)};var cancel=new Button {Content="关闭",Padding=new Thickness(16,8)};footer.Children.Add(apply);footer.Children.Add(cancel);
        apply.Click+=(_,_)=> {
            var selected=rows.Where(g=>checks[g.Key].IsChecked==true).ToArray();var selectedValues=values.Where(v=>v.apply.IsChecked==true).ToArray();
            if(selected.Length==0||selectedValues.Length==0){status.Text="请勾选门窗类型和要修改的参数。";return;}
            var numbers=new List<double>();foreach(var v in selectedValues) {
                if(!double.TryParse(v.value.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||!double.IsFinite(n)||n<=0||n>2000){status.Text="厚度或进深应为有效正数。";return;}numbers.Add(n);
            }
            var result=new OpeningTypeManagerResult();foreach(var group in selected) {
                var type=OpeningConstruction.Copy(OpeningConstruction.Resolve(model,group.First()));
                for(var i=0;i<selectedValues.Length;i++)selectedValues[i].set(type,numbers[i]);result.Types[group.Key]=type;
            }Close(result);
        };cancel.Click+=(_,_)=>Close();Content=root;
    }
}
