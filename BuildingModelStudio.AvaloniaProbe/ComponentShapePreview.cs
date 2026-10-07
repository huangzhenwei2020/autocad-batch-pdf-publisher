using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ComponentShapePreview : Control
{
    private List<VolumeFace2D> _faces=new();
    private List<VolumeFace2D[]> _planes=new();
    private int _generation;
    internal int FaceCount=>_faces.Count;
    internal async Task SetVolumeAsync(BuildingVolume volume)
    {
        var generation=++_generation;
        var faces=await Task.Run(()=>VolumeRenderer.Project(volume,new VolumeCamera {AzimuthDegrees=-18,ElevationDegrees=10},fillPreviewOnly:true));
        if(generation!=_generation)return;_faces=faces;
        _planes=faces.GroupBy(f=>(f.PlaneKey,shade:Math.Round(f.Shade,6))).Select(g=>g.ToArray())
            .OrderByDescending(g=>g.Average(f=>f.Depth)).ToList();InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        var faces=_faces.Where(f=>f.Visible&&f.Points.Count>=3).ToArray();if(faces.Length==0)return;
        var points=faces.SelectMany(f=>f.Points).ToArray();
        var x0=points.Min(p=>p.X);var x1=points.Max(p=>p.X);var y0=points.Min(p=>p.Y);var y1=points.Max(p=>p.Y);
        var scale=Math.Min(Math.Max(1,Bounds.Width-16)/Math.Max(1,x1-x0),Math.Max(1,Bounds.Height-16)/Math.Max(1,y1-y0));
        Point At(PointModel p)=>new(Bounds.Width/2+(p.X-(x0+x1)/2)*scale,Bounds.Height/2-(p.Y-(y0+y1)/2)*scale);
        foreach(var plane in _planes) {
            var face=plane[0];
            var geometry=new StreamGeometry();using(var path=geometry.Open()) {
                foreach(var patch in plane) {
                    path.BeginFigure(At(patch.Points[0]),true);foreach(var point in patch.Points.Skip(1))path.LineTo(At(point));path.EndFigure(true);
                }
            }
            var shade=Math.Clamp(face.Shade,.3,1);
            var fill=new SolidColorBrush(Color.FromRgb((byte)(150*shade),(byte)(178*shade),(byte)(194*shade)));
            // A single coplanar fill does not expose mesh subdivision as antialiasing seams.
            context.DrawGeometry(fill,null,geometry);
        }
    }
}
