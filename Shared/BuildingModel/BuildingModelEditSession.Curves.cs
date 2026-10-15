using System;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed partial class BuildingModelEditSession
    {
        public bool TryFilletWalls(string firstId, PointModel firstPick, string secondId, PointModel secondPick,
            double radius, out string arcId, out string error, bool roundRelatedSlabs = false)
        {
            arcId = null; error = null;
            var candidate = Clone(Model);
            var a = candidate.Walls.FirstOrDefault(w => Same(w.Id, firstId));
            var b = candidate.Walls.FirstOrDefault(w => Same(w.Id, secondId));
            if (a == null || b == null || Same(firstId, secondId) || !Same(a.StoreyId, b.StoreyId))
            { error = "请选择同一楼层的两面不同直墙。"; return false; }
            if (Math.Abs(a.Bulge) > 1e-10 || Math.Abs(b.Bulge) > 1e-10)
            { error = "墙圆角目前选择两面直墙；弧墙可通过端点及弧度调整。"; return false; }
            if (Math.Abs(a.Thickness - b.Thickness) > .01 || Math.Abs(candidate.HeightOf(a) - candidate.HeightOf(b)) > .01)
            { error = "圆角两侧墙厚和墙高须一致，请先调整墙体参数。"; return false; }
            var ax = a.X2 - a.X1; var ay = a.Y2 - a.Y1;
            var bx = b.X2 - b.X1; var by = b.Y2 - b.Y1;
            var cross = ax * by - ay * bx;
            if (Math.Abs(cross) < 1e-8 * PlanEditing.WallLength(a) * PlanEditing.WallLength(b))
            { error = "平行墙不能圆角。"; return false; }
            var t = ((b.X1 - a.X1) * by - (b.Y1 - a.Y1) * bx) / cross;
            var corner = new PointModel(a.X1 + t * ax, a.Y1 + t * ay);
            int EndToKeep(WallModel w, PointModel pick)
            {
                var dx = w.X2 - w.X1; var dy = w.Y2 - w.Y1;
                var side = (pick.X - corner.X) * dx + (pick.Y - corner.Y) * dy;
                if (Math.Abs(side) < .01) side = ((w.X1 + w.X2) / 2 - corner.X) * dx + ((w.Y1 + w.Y2) / 2 - corner.Y) * dy;
                return side >= 0 ? 1 : 0;
            }
            var keepA = EndToKeep(a, firstPick); var keepB = EndToKeep(b, secondPick);
            var farA = new PointModel(keepA == 0 ? a.X1 : a.X2, keepA == 0 ? a.Y1 : a.Y2);
            var farB = new PointModel(keepB == 0 ? b.X1 : b.X2, keepB == 0 ? b.Y1 : b.Y2);
            if (!CurveGeometry.TryCorner(farA, corner, farB, radius, out var p, out var q, out var bulge, out error)) return false;
            var offsetA = WallReferenceGeometry.BodyOffset(a) * (keepA == 0 ? 1 : -1);
            var offsetB = WallReferenceGeometry.BodyOffset(b) * (keepB == 1 ? 1 : -1);
            if (Math.Abs(offsetA - offsetB) > .01)
            { error = "两面墙的定位轴线偏移不一致，无法连续圆角。"; return false; }
            bool Trim(WallModel w, int keep, PointModel point)
            {
                var start = new PointModel(w.X1, w.Y1); PlanEditing.WallDirection(w, out var ux, out var uy);
                if (keep == 0) { w.X2 = point.X; w.Y2 = point.Y; }
                else { w.X1 = point.X; w.Y1 = point.Y; }
                var delta = (w.X1 - start.X) * ux + (w.Y1 - start.Y) * uy;
                foreach (var opening in candidate.Openings.Where(o => Same(o.HostWallId, w.Id))) opening.Offset -= delta;
                return ValidateWallAndOpenings(candidate, w) == null;
            }
            if (!Trim(a, keepA, p) || !Trim(b, keepB, q))
            { error = "圆角会切到门窗洞口或使墙过短，请减小半径或先移动门窗。"; return false; }
            if (radius > 0)
            {
                var arc = new WallModel { Id = "W-" + Guid.NewGuid().ToString("N"), StoreyId = a.StoreyId,
                    X1 = p.X, Y1 = p.Y, X2 = q.X, Y2 = q.Y, Bulge = bulge,
                    Thickness = a.Thickness, Height = a.Height, Material = a.Material, AxisOffset = offsetA };
                error = PlanEditing.ValidateWall(arc); if (error != null) return false;
                candidate.Walls.Add(arc); arcId = arc.Id;
                if(roundRelatedSlabs) foreach(var slab in candidate.Slabs.Where(s=>Same(s.StoreyId,a.StoreyId)))
                {
                    var ca=new CurveGeometry(farA,corner,0);var cb=new CurveGeometry(corner,farB,0);
                    var ua=ca.Tangent(0);var ub=cb.Tangent(0);
                    var ring=slab.Outline;
                    for(var i=0;i<ring.Count;i++) {
                        var v=ring[i];var before=ring[(i+ring.Count-1)%ring.Count];var after=ring[(i+1)%ring.Count];
                        if(Math.Abs(before.Bulge)>1e-10||Math.Abs(v.Bulge)>1e-10)continue;
                        var oa=-(v.X-corner.X)*ua.Y+(v.Y-corner.Y)*ua.X;
                        var ob=-(v.X-corner.X)*ub.Y+(v.Y-corner.Y)*ub.X;
                        if(Math.Abs(oa-ob)>.01||Math.Abs(oa-offsetA)>a.Thickness/2+.01)continue;
                        var u=new CurveGeometry(v,before,0).Tangent(0);var w=new CurveGeometry(v,after,0).Tangent(0);
                        bool Align(PointModel d,PointModel direction,double sign)=>Math.Abs(d.X*direction.Y-d.Y*direction.X)<1e-6
                            &&(d.X*direction.X+d.Y*direction.Y)*sign>.999999;
                        if(!(Align(u,ua,-1)&&Align(w,ub,1)||Align(w,ua,-1)&&Align(u,ub,1)))continue;
                        var slabRadius=radius-Math.Sign(bulge)*oa;
                        if(!CurveGeometry.TryCorner(before,v,after,slabRadius,out var s1,out var s2,out var sb,out error))
                        {error="相接楼板圆角无法完成："+error;return false;}
                        s1.Bulge=sb;ring[i]=s1;ring.Insert(i+1,s2);i++;
                    }
                    error=SlabGeometry.Validate(slab);if(error!=null){error="相接楼板圆角无法完成："+error;return false;}
                }
            }
            else arcId = a.Id;
            Commit(candidate); return true;
        }

        public bool TrySetWallArcThrough(string id,PointModel through,out string error)
        {
            error=null;var candidate=Clone(Model);var wall=candidate.Walls.FirstOrDefault(w=>Same(w.Id,id));
            if(wall==null){error="未找到墙。";return false;}
            if(candidate.Openings.Any(o=>Same(o.HostWallId,id))){error="带洞口的直墙不能直接转为弧墙。";return false;}
            try {wall.Bulge=CurveGeometry.ThroughBulge(new PointModel(wall.X1,wall.Y1),new PointModel(wall.X2,wall.Y2),through);}
            catch(ArgumentException ex){error=ex.Message;return false;}
            error=PlanEditing.ValidateWall(wall);if(error!=null)return false;
            Commit(candidate);return true;
        }

        public bool TryFilletSlab(string id, int cornerIndex, double radius, out string error)
        {
            error = null; var candidate = Clone(Model);
            var slab = candidate.Slabs.FirstOrDefault(s => Same(s.Id, id));
            if (slab == null || cornerIndex < 0 || cornerIndex >= slab.Outline.Count)
            { error = "请选择楼板外轮廓的角点。"; return false; }
            var ring = slab.Outline; var previous = (cornerIndex + ring.Count - 1) % ring.Count;
            var next = (cornerIndex + 1) % ring.Count;
            if (Math.Abs(ring[previous].Bulge) > 1e-10 || Math.Abs(ring[cornerIndex].Bulge) > 1e-10)
            { error = "请选择两条直边相接的楼板角点。"; return false; }
            if (!CurveGeometry.Finite(radius) || radius <= 0) { error = "楼板圆角半径须大于 0 mm。"; return false; }
            if (!CurveGeometry.TryCorner(ring[previous], ring[cornerIndex], ring[next], radius,
                out var p, out var q, out var bulge, out error)) return false;
            p.Bulge = bulge; ring[cornerIndex] = p; ring.Insert(cornerIndex + 1, q);
            error = SlabGeometry.Validate(slab); if (error != null) return false;
            Commit(candidate); return true;
        }
    }
}
