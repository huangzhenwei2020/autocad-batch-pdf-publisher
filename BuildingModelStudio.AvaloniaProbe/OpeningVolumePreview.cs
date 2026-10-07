using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using BatchPdfPublisher.BuildingModel;
using System.Collections.Concurrent;
using System.Text.Json;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class OpeningVolumePreview : Control
{
    private static readonly ConcurrentDictionary<string,Task<List<VolumeFace2D>>> Cache=new();
    private List<VolumeFace2D> _faces=new();
    private int _generation;
    private OpeningTypeModel? _pendingType;
    private bool _pending;
    internal int FaceCount=>_faces.Count;
    internal void SetType(OpeningTypeModel? type)
    {
        ++_generation;_faces=new();_pendingType=type==null?null:OpeningConstruction.Copy(type);_pending=type!=null;InvalidateVisual();
    }
    private async void LoadAsync(OpeningTypeModel type,int generation)
    {
        try {
            var copy=OpeningConstruction.Copy(type);var key=JsonSerializer.Serialize(copy);
            if(Cache.Count>128)Cache.Clear();
            var task=Cache.GetOrAdd(key,_=>Task.Run(()=>{
                var model=SampleModelFactory.CreateEmptyModel("preview");
                var wall=new WallModel {Id="preview",StoreyId=model.Storeys[0].Id,X2=copy.Width,Thickness=200};model.Walls.Add(wall);
                model.OpeningTypes.Add(copy);var opening=OpeningPlacementChoice.FromType(copy).CreateOpening(wall.Id,copy.Width/2);opening.Sill=0;model.Openings.Add(opening);
                return VolumeRenderer.Project(BuildingVolumeBuilder.BuildOpeningParts(model),new VolumeCamera {AzimuthDegrees=-12,ElevationDegrees=8},includeHiddenEdges:false);
            }));
            var faces=await task;
            await Dispatcher.UIThread.InvokeAsync(()=>{if(generation==_generation){_faces=faces;InvalidateVisual();}});
        } catch(Exception){if(generation==_generation){_faces=new();InvalidateVisual();}}
    }

    public override void Render(DrawingContext context)
    {
        if(_pending&&_pendingType!=null){_pending=false;LoadAsync(_pendingType,_generation);}
        if(_faces.Count==0)return;
        var points=_faces.SelectMany(f=>f.Points).ToList();if(points.Count==0)return;
        var x0=points.Min(p=>p.X);var x1=points.Max(p=>p.X);var y0=points.Min(p=>p.Y);var y1=points.Max(p=>p.Y);
        var scale=Math.Min(Math.Max(1,Bounds.Width-24)/Math.Max(1,x1-x0),Math.Max(1,Bounds.Height-20)/Math.Max(1,y1-y0));
        Point At(PointModel p)=>new(Bounds.Width/2+(p.X-(x0+x1)/2)*scale,Bounds.Height/2-(p.Y-(y0+y1)/2)*scale);
        foreach(var face in _faces.Where(f=>f.Visible&&f.Points.Count>=3)){
            var geometry=new StreamGeometry();using(var stream=geometry.Open()){
                stream.BeginFigure(At(face.Points[0]),true);foreach(var p in face.Points.Skip(1))stream.LineTo(At(p));stream.EndFigure(true);
            }
            var shade=Math.Clamp(face.Shade,.25,1);var glass=face.Kind=="glass";var color=glass?Color.FromRgb((byte)(75*shade),(byte)(115*shade),(byte)(136*shade)):Color.FromRgb((byte)(160*shade),(byte)(185*shade),(byte)(198*shade));
            context.DrawGeometry(new SolidColorBrush(color),null,geometry);
            foreach(var edge in face.Edges.Where(e=>e.Count>=2))context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#9DB6C6")),.65),At(edge[0]),At(edge[1]));
        }
    }
}
