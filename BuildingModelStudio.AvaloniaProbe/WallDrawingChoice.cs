using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed record WallDrawingChoice(bool Curved=false,double Thickness=200,double Height=0,
    WallAxisPlacement AxisPlacement=WallAxisPlacement.Center,double? AxisOffset=null,string Material="")
{
    internal WallModel CreateWall(string storeyId,PointModel start,PointModel end,double bulge=0)=>new()
    {
        StoreyId=storeyId,X1=start.X,Y1=start.Y,X2=end.X,Y2=end.Y,Bulge=bulge,
        Thickness=Thickness,Height=Height,AxisPlacement=AxisPlacement,AxisOffset=AxisOffset,Material=Material.Trim()
    };

    internal string? Validate()
    {
        if(!double.IsFinite(Thickness)||Thickness<10||Thickness>10000)return "墙厚应为 10～10000 mm。";
        if(!double.IsFinite(Height)||Height<0||Height>100000)return "墙高应为 0～100000 mm；0 表示随楼层。";
        if(AxisOffset.HasValue&&(!double.IsFinite(AxisOffset.Value)||Math.Abs(AxisOffset.Value)>10000))
            return "定位偏移应为 -10000～10000 mm。";
        if(Material.Length>64||Material.Any(char.IsControl))return "材料名称最长 64 个字符，不能含控制字符。";
        return null;
    }

    internal BuildingModelDocument PreviewModel(double storeyHeight)
    {
        var model=new BuildingModelDocument {Name="墙体预览"};
        model.Storeys.Add(new StoreyModel {Id="preview",Height=storeyHeight});
        var length=Curved?Math.Max(3000,4*(Thickness+Math.Abs(AxisOffset??0))):3000;
        var wall=CreateWall("preview",new(0,0),new(length,0),Curved?-.414213562373095:0);
        wall.Id="preview-wall";model.Walls.Add(wall);return model;
    }
}
