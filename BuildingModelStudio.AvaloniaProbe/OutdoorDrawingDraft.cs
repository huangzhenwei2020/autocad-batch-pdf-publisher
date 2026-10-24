using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class OutdoorDrawingDraft
{
    internal bool Balcony {get;}
    internal string StoreyId {get;set;}
    internal WallDrawingChoice Wall {get;set;}
    internal double Thickness {get;set;}=100;
    internal double Offset {get;set;}=-100;
    internal bool Arc {get;set;}
    internal SlabModel? OriginalSlab {get;set;}
    internal bool Closed {get;private set;}
    internal List<PointModel> Points {get;}=new();
    internal PointModel? ArcEnd {get;private set;}
    private sealed record Snapshot(List<PointModel> Points,bool Closed);
    private readonly Stack<Snapshot> _history=new();
    private void Remember()=>_history.Push(new(Points.Select(Copy).ToList(),Closed));
    internal bool HasDraft=>Points.Count>0 || ArcEnd!=null;
    internal OutdoorDrawingDraft(bool balcony,string storey,WallDrawingChoice wall)
    {Balcony=balcony;StoreyId=storey;Wall=wall;Arc=wall.Curved&&!balcony;}
    internal double Elevation(BuildingModelDocument model)=>(model.FindStorey(StoreyId)?.Elevation??0)+(Balcony?Offset:Wall.BaseOffset);
    internal static PointModel Copy(PointModel p)=>new(p.X,p.Y){Bulge=p.Bulge};
    internal static double Distance(PointModel a,PointModel b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    internal string? Add(PointModel p)
    {
        if(Closed)return "轮廓已闭合，请完成或返回上一步。";
        if(Points.Count>=256)return "单条路径最多 256 个节点，请分段绘制。";
        if(!double.IsFinite(p.X)||!double.IsFinite(p.Y))return "绘制点无效。";
        if(Points.Count==0){Remember();Points.Add(Copy(p));return null;}
        if(ArcEnd!=null)
        {
            double bulge;
            try{bulge=CurveGeometry.ThroughBulge(Points[^1],ArcEnd,p);}
            catch(ArgumentException ex){return ex.Message;}
            Remember();Points[^1].Bulge=bulge;
            if(Points.Count>=2&&Distance(Points[0],ArcEnd)<.5)Closed=true;
            else Points.Add(ArcEnd);
            ArcEnd=null;return null;
        }
        if(Distance(Points[^1],p)<10)return "边长应至少 10 mm。";
        if(!Arc&&Points.Count>=2&&Distance(Points[0],p)<.5)return Close();
        if(Arc)ArcEnd=Copy(p);else {Remember();Points.Add(Copy(p));}return null;
    }
    internal string? Close()
    {
        if(Closed)return null;
        if(ArcEnd!=null)return "请先完成圆弧经过点。";
        if(Points.Count<2||Points.Count==2&&!Points.Any(p=>Math.Abs(p.Bulge)>1e-10))return "闭合轮廓至少需要三条直边，或有效的圆弧围合。";
        if(Distance(Points[0],Points[^1])<10)return "闭合边过短。";
        Remember();Closed=true;return null;
    }
    internal void Previous()
    {
        if(ArcEnd!=null){ArcEnd=null;return;}
        if(_history.Count==0)return;
        var snapshot=_history.Pop();Points.Clear();Points.AddRange(snapshot.Points.Select(Copy));Closed=snapshot.Closed;
    }
    internal void Clear(){Points.Clear();ArcEnd=null;Closed=false;_history.Clear();}
    internal string? TakeReference(IReadOnlyList<PointModel> source,bool closed)
    {
        if(Closed||ArcEnd!=null)return "请先返回上一步或取消当前路径，再取边。";
        if(source.Count<2||source.Count>256)return "取边轮廓需要 2～256 个节点。";
        var path=source.Select(Copy).ToList();
        try {
            for(var i=0;i<path.Count-(closed?0:1);i++)
                if(new CurveGeometry(path[i],path[(i+1)%path.Count],path[i].Bulge).Length<10)return "源边过短，不能作为绘制路径。";
            if(closed) {
                var error=SlabGeometry.Validate(new SlabModel {Outline=path});if(error!=null)return error;
            }
        }catch(ArgumentException ex){return ex.Message;}
        if(Points.Count==0){Remember();Points.AddRange(path);Closed=closed;return null;}
        if(closed)return "完整周边需要空草稿，请先取消当前路径。";
        if(Distance(Points[^1],path[0])>.5)
        {
            if(Distance(Points[^1],path[^1])>.5)return "所选边与路径末端不相接，请选择相邻边或反向当前路径。";
            path=OutdoorReferenceEdges.Reverse(path,false);
        }
        var closes=Distance(Points[0],path[^1])<.5;
        if(Points.Count+path.Count-1-(closes?1:0)>256)return "单条路径最多 256 个节点。";
        var candidate=Points.Select(Copy).ToList();candidate[^1].Bulge=path[0].Bulge;
        candidate.AddRange(path.Skip(1).Take(path.Count-1-(closes?1:0)));
        if(closes) {
            var error=SlabGeometry.Validate(new SlabModel {Outline=candidate});if(error!=null)return error;
        }
        Remember();Points.Clear();Points.AddRange(candidate);Closed=closes;return null;
    }
    internal string? Reverse()
    {
        if(ArcEnd!=null)return "请先完成当前圆弧，再反向。";
        if(Points.Count<2)return "请先绘制或拾取一段路径。";
        Remember();var reversed=OutdoorReferenceEdges.Reverse(Points,Closed);Points.Clear();Points.AddRange(reversed);return null;
    }
    internal List<PointModel> PreviewPoints(PointModel? cursor)
    {
        var list=Points.Select(Copy).ToList();
        if(Closed||cursor==null||list.Count==0)return list;
        if(ArcEnd!=null)
        {
            try{list[^1].Bulge=CurveGeometry.ThroughBulge(list[^1],ArcEnd,cursor);
                if(list.Count<2||Distance(list[0],ArcEnd)>=.5)list.Add(Copy(ArcEnd));}
            catch(ArgumentException){ }
        }
        else if(Distance(list[^1],cursor)>=10)list.Add(Copy(cursor));
        return list;
    }
    internal bool PreviewClosed(PointModel? cursor)=>Closed||cursor!=null&&ArcEnd!=null&&Points.Count>=2&&Distance(Points[0],ArcEnd)<.5;
    internal BuildingModelDocument Geometry(BuildingModelDocument model,PointModel? cursor=null)
    {
        var floor=model.FindStorey(StoreyId)??throw new ArgumentException("请选择所属楼层。");
        if(!string.IsNullOrWhiteSpace(floor.TemplateStoreyId))throw new ArgumentException("该层共用标准层，请选择标准层来源；单层绘制需先拆为独立层。");
        var result=new BuildingModelDocument();
        result.Storeys.Add(new StoreyModel {Id=floor.Id,Name=floor.Name,Elevation=floor.Elevation,Height=floor.Height});
        result.Storeys.AddRange(model.Storeys.Where(s=>s.TemplateStoreyId==floor.Id));
        var points=cursor==null?Points.Select(Copy).ToList():PreviewPoints(cursor);
        if(Balcony)
        {
            if(points.Count<2)return result;
            if(points.Count==2)
            {
                if(points.All(p=>Math.Abs(p.Bulge)<1e-10))return result;
                // Legacy slab consumers require three control points. Split one analytic arc, not its tessellation.
                var i=Math.Abs(points[0].Bulge)>1e-10?0:1;var curve=new CurveGeometry(points[i],points[1-i],points[i].Bulge);
                var half=Math.Tan(Math.Atan(points[i].Bulge)/2);points[i].Bulge=half;
                var mid=curve.At(.5);mid.Bulge=half;points.Insert(i+1,mid);
            }
            var slab=OriginalSlab==null?new SlabModel():BuildingModelJson.FromJson(BuildingModelJson.ToJson(
                new BuildingModelDocument {Slabs=new(){OriginalSlab}})).Slabs.Single();
            slab.Id="outdoor-preview-slab";slab.StoreyId=StoreyId;slab.Purpose=SlabPurpose.Balcony;
            var followsTop=OriginalSlab!=null&&(OriginalSlab.FollowsStoreyTop||!OriginalSlab.TopOffset.HasValue
                &&Math.Abs(OriginalSlab.TopElevation-floor.Elevation-floor.Height)<1);
            slab.Thickness=Thickness;slab.TopOffset=Offset-(followsTop?floor.Height:0);slab.FollowsStoreyTop=followsTop;slab.Outline=points;
            var error=SlabGeometry.Validate(slab);if(error!=null)throw new ArgumentException(error);
            result.Slabs.Add(slab);
        }
        else
        {
            var error=Wall.Validate();if(error!=null)throw new ArgumentException(error);
            var count=points.Count-1+(PreviewClosed(cursor)?1:0);
            for(var i=0;i<count;i++)
            {
                var wall=Wall.CreateWall(StoreyId,points[i],points[(i+1)%points.Count],points[i].Bulge);
                wall.Id="outdoor-preview-wall-"+i;
                error=PlanEditing.ValidateWall(wall);if(error!=null)throw new ArgumentException(error);
                result.Walls.Add(wall);
            }
        }
        return result;
    }
}
