using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace BuildingModelStudio.AvaloniaProbe;

internal static class ComponentUi
{
    private static readonly Dictionary<string,Bitmap> Icons=new();
    internal static readonly IBrush Edge=new SolidColorBrush(Color.Parse("#38505F"));
    internal static void Theme(Window window)
    {
        window.FontSize=14;window.Foreground=new SolidColorBrush(Color.Parse("#E7F1FA"));
        var field=new SolidColorBrush(Color.Parse("#20364A"));
        window.Styles.Add(new Style(s=>s.OfType<TextBox>()){Setters={new Setter(TextBox.BackgroundProperty,field),new Setter(TextBox.BorderBrushProperty,Edge),new Setter(TextBox.BorderThicknessProperty,new Thickness(1))}});
        window.Styles.Add(new Style(s=>s.OfType<ComboBox>()){Setters={new Setter(ComboBox.BackgroundProperty,field),new Setter(ComboBox.BorderBrushProperty,Edge)}});
        window.Styles.Add(new Style(s=>s.OfType<Button>()){Setters={new Setter(Button.BackgroundProperty,field),new Setter(Button.BorderBrushProperty,Edge),new Setter(Button.BorderThicknessProperty,new Thickness(1))}});
    }
    internal static Control Icon(string name)
    {
        if(!Icons.TryGetValue(name,out var bitmap)) {
            using var stream=AssetLoader.Open(new Uri("avares://万落建筑模型/Resources/Icons/"+name+".png"));
            Icons[name]=bitmap=new Bitmap(stream);
        }
        return new Image {Source=bitmap,Width=18,Height=18};
    }
    internal static Button Tool(string icon,string label,Action action)
    {
        var contents=new StackPanel {Orientation=Orientation.Horizontal,Spacing=6};contents.Children.Add(Icon(icon));
        if(label.Length>0)contents.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});
        var button=new Button {Content=contents,Height=36,MinWidth=36};ToolTip.SetTip(button,label.Length==0?icon:label);
        button.Click+=(_,_)=>action();return button;
    }
    internal static Control Region(string title,Control content)
    {
        var panel=new Grid {RowDefinitions=new("36,*")};
        panel.Children.Add(new Border {BorderBrush=Edge,BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(10,0),
            Child=new TextBlock {Text=title,FontSize=16,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center}});
        Grid.SetRow(content,1);panel.Children.Add(content);
        return new Border {BorderBrush=Edge,BorderThickness=new Thickness(1),Child=panel};
    }
    internal static ScrollViewer Scroll(Control content)
    {
        var scroll=new ScrollViewer {Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
        scroll.Classes.Add("slab-inspector");return scroll;
    }
    internal static void Field(StackPanel panel,string label,Control input)
    {
        input.Height=36;input.MinWidth=0;ToolTip.SetTip(input,label);
        var row=new Grid {ColumnDefinitions=new("112,*"),ColumnSpacing=8,MinHeight=42};
        row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});
        Grid.SetColumn(input,1);row.Children.Add(input);panel.Children.Add(row);
    }
}
