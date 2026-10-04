using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private readonly TextBox _openingDistanceInput=new() {Height=32,MinHeight=32,Width=180,Text="0",
        VerticalContentAlignment=VerticalAlignment.Center};
    private readonly TextBlock _openingDistanceCaption=new() {FontSize=12};
    private readonly TextBlock _openingCenterHint=new() {Text="右键：沿墙居中",FontSize=12,
        Foreground=Brushes.Cyan,IsHitTestVisible=false};
    private readonly Border _openingDistanceHost=new() {IsVisible=false,Padding=new Thickness(8),Width=198,
        BorderThickness=new Thickness(1),BorderBrush=Brushes.Cyan,Background=new SolidColorBrush(Color.Parse("#172734"))};
    private bool _openingDistanceEditing,_openingDistanceSynchronizing;

    private Canvas BuildOpeningDistanceLayer()
    {
        var layer=new Canvas {ClipToBounds=true};
        var fields=new StackPanel {Spacing=4};fields.Children.Add(_openingDistanceCaption);fields.Children.Add(_openingDistanceInput);
        fields.Children.Add(_openingCenterHint);
        _openingDistanceHost.Child=fields;layer.Children.Add(_openingDistanceHost);
        _planCanvas.OpeningCentering+=()=>_openingDistanceEditing=false;
        _openingDistanceHost.AddHandler(PointerPressedEvent,(_,e)=>_planCanvas.HandleOpeningCenterPress(e),Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _openingDistanceHost.AddHandler(PointerReleasedEvent,(_,e)=>_planCanvas.HandleOpeningCenterRelease(e),Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _planCanvas.OpeningDistanceChanged+=(point,distance,first)=> {
            if(point==null) {
                var focused=_openingDistanceHost.IsKeyboardFocusWithin;
                _openingDistanceHost.IsVisible=false;_openingDistanceEditing=false;
                if(focused)_planCanvas.Focus();return;
            }
            var started=!_openingDistanceHost.IsVisible;_openingDistanceHost.IsVisible=true;
            if(!_openingDistanceEditing) {
                _openingDistanceCaption.Text=first?"洞口边 → 起点墙端  mm":"洞口边 → 终点墙端  mm";
                _openingDistanceSynchronizing=true;
                _openingDistanceInput.Text=distance.ToString("0.###",CultureInfo.CurrentCulture);
                _openingDistanceInput.SelectAll();_openingDistanceSynchronizing=false;
            }
            Canvas.SetLeft(_openingDistanceHost,Math.Clamp(point.Value.X+18,4,Math.Max(4,_planCanvas.Bounds.Width-202)));
            var height=Math.Max(96,_openingDistanceHost.Bounds.Height);
            Canvas.SetTop(_openingDistanceHost,Math.Clamp(point.Value.Y+22,4,Math.Max(4,_planCanvas.Bounds.Height-height-4)));
            if(started)_openingDistanceInput.Focus();
        };
        _openingDistanceInput.PropertyChanged+=(_,e)=> {
            if(e.Property!=TextBox.TextProperty||_openingDistanceSynchronizing||!_openingDistanceHost.IsVisible)return;
            _openingDistanceEditing=true;_planCanvas.LockOpeningDistance();
        };
        _openingDistanceInput.KeyDown+=(_,e)=> {
            if(e.Key==Key.Enter) {
                _planCanvas.CommitOpeningDistance(_openingDistanceInput.Text??"");e.Handled=true;
            } else if(e.Key==Key.Escape) {_planCanvas.CancelDraft();e.Handled=true;}
        };
        return layer;
    }
}
