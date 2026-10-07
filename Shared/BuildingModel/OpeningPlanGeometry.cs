using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.BuildingModel
{
    // Local coordinates: opening left jamb is (0,0), wall normal is positive Y.
    public static class OpeningPlanGeometry
    {
        public const int SwingArcSegments=64;
        public static List<ViewLine> Build(OpeningModel opening, OpeningTypeModel type, double thickness, List<ViewStrokeArea> strokeAreas=null)
        {
            type=type??OpeningConstruction.Default(opening);
            var lines=new List<ViewLine>();
            var width=opening.Width;var half=Math.Max(1,thickness/2);
            var style=type?.PlanStyle??"按立面";
            var item=OpeningElevationAdapter.ToScheduleItem(opening,type,width,opening.Height);
            void Line(double x,double y,double xx,double yy,string openingPath=null) {
                if(opening.PlanFlipAlong){x=width-x;xx=width-xx;}
                if(opening.PlanFlipNormal){y=-y;yy=-yy;}
                lines.Add(new ViewLine {Layer=ViewLayers.Opening,X1=x,Y1=y,X2=xx,Y2=yy,
                    OpeningArcId=openingPath,LineType=openingPath==null?null:"DASHED"});
            }
            void Polygon(params PointModel[] points) {
                var first=lines.Count;
                for(var i=0;i<points.Length;i++) {var a=points[i];var b=points[(i+1)%points.Length];Line(a.X,a.Y,b.X,b.Y);}
                if(strokeAreas==null)return;
                var id="opening-"+strokeAreas.Count;
                strokeAreas.Add(new ViewStrokeArea {Id=id});
                foreach(var line in lines.Skip(first))line.StrokeAreaId=id;
            }
            void Rect(double l,double b,double r,double t)=>Polygon(new PointModel(l,b),new PointModel(r,b),new PointModel(r,t),new PointModel(l,t));
            void Arrow(double from,double to,double y,string openingPath=null) {
                Line(from,y,to,y,openingPath==null?null:openingPath+"-shaft");var direction=to>from?1:-1;var size=Math.Min(60,Math.Abs(to-from)/3);
                if(openingPath==null){Line(to,y,to-direction*size,y-size/2);Line(to,y,to-direction*size,y+size/2);}
                else {Line(to-direction*size,y-size/2,to,y,openingPath+"-head");Line(to,y,to-direction*size,y+size/2,openingPath+"-head");}
            }
            void ArcArrow(double x,double y,double radius,double start,double end,double size) {
                for(var j=0;j<24;j++) {
                    var a=start+(end-start)*j/24;var b=start+(end-start)*(j+1)/24;
                    Line(x+radius*Math.Cos(a),y+radius*Math.Sin(a),x+radius*Math.Cos(b),y+radius*Math.Sin(b));
                }
                var ax=x+radius*Math.Cos(end);var ay=y+radius*Math.Sin(end);
                var sign=Math.Sign(end-start);var tx=-sign*Math.Sin(end);var ty=sign*Math.Cos(end);
                Line(ax,ay,ax-tx*size-ty*size*.45,ay-ty*size+tx*size*.45);
                Line(ax,ay,ax-tx*size+ty*size*.45,ay-ty*size-tx*size*.45);
            }
            var depth=type.BayLeftDepth>0?type.BayLeftDepth:600;
            var form=style=="按立面"?(item.ElevationType??""):style;
            if(!CutsWall(opening)){Rect(0,half,width,half+Math.Max(100,thickness));return lines;}
            foreach(var span in OpeningConstruction.ThresholdSpans(opening,type)) {
                Line(span[0],-half,span[1],-half);Line(span[0],half,span[1],half);
            }
            if(form=="旋转门") {
                var radius=width/2;
                foreach(var r in new[]{radius,Math.Max(radius*.8,radius-type.OuterFrameWidth),radius*.06})
                    for(var i=0;i<48;i++){var a=i*Math.PI/24;var b=(i+1)*Math.PI/24;
                        Line(radius+r*Math.Cos(a),r*Math.Sin(a),radius+r*Math.Cos(b),r*Math.Sin(b));}
                var panel=(type.PanelThickness??40)/2;
                Rect(0,-panel,width,panel);Rect(radius-panel,-radius,radius+panel,radius);
                ArcArrow(radius,0,radius*1.15,Math.PI*.18,Math.PI*.4,Math.Min(60,width*.04));return lines;
            }
            if(form=="卷帘门"){
                Rect(0,-half*.4,width,half*.4);Line(0,0,width,0);Line(0,-half*.2,width,-half*.2);Line(0,half*.2,width,half*.2);
                var radius=Math.Min(60,width*.04);var cx=width-radius*2;var cy=half+radius*1.8;
                for(var j=0;j<48;j++){var a=j*Math.PI/24;var b=(j+1)*Math.PI/24;
                    Line(cx+radius*.6*Math.Cos(a),cy+radius*.6*Math.Sin(a),cx+radius*.6*Math.Cos(b),cy+radius*.6*Math.Sin(b));}
                ArcArrow(cx,cy,radius,Math.PI*.15,Math.PI*1.65,radius*.4);return lines;
            }
            if(form=="折叠门") {
                var cut=Math.Max(0,Math.Min(opening.Height-.01,1200-opening.Sill));
                var panels=DoorWindowElevationGeometryBuilder.Build(item).Cells.Where(c=>!c.IsDeleted&&c.Bottom<=cut&&c.Top>cut).OrderBy(c=>c.Left).Take(12).ToList();
                var previous=new PointModel(0,0);var panelThickness=(type.PanelThickness??40)/2;
                for(var i=0;i<panels.Count;i++){
                    var step=(panels[i].Right-panels[i].Left)/Math.Sqrt(2);
                    var p=new PointModel(previous.X+step,previous.Y+(i%2==0?step:-step));
                    var normal=panelThickness/Math.Sqrt(2);var sign=i%2==0?-1:1;
                    Polygon(new PointModel(previous.X+sign*normal,previous.Y+normal),new PointModel(p.X+sign*normal,p.Y+normal),
                        new PointModel(p.X-sign*normal,p.Y-normal),new PointModel(previous.X-sign*normal,previous.Y-normal));previous=p;
                }
                Arrow(width*.25,width*.7,-half*.6);return lines;
            }
            if(form=="凸窗"||form=="矩形凸窗"||form=="梯形凸窗"||form=="转角窗") {
                var inset=form=="梯形凸窗"?Math.Min(width*.45,Math.Max(0,type?.PlanReturnInset??width*.15)):0;
                if(form=="转角窗") {Line(0,0,width,0);Line(width,0,width,depth);Line(0,35,width-35,35);Line(width-35,35,width-35,depth);}
                else {Line(0,0,inset,depth);Line(inset,depth,width-inset,depth);Line(width-inset,depth,width,0);
                    Line(35,0,inset+35,depth-35);Line(inset+35,depth-35,width-inset-35,depth-35);Line(width-inset-35,depth-35,width-35,0);}
                return lines;
            }
            var section=Math.Max(0,Math.Min(opening.Height-.01,1200-opening.Sill));
            var parts=OpeningConstruction.Build(opening,type,thickness)
                .Where(p=>p.Face==0&&p.Bottom<=section&&p.Top>section).ToList();
            void ClosedLeaf(DoorWindowCell cell,double l,double r) {
                var leaf=parts.Where(p=>SameCell(p.Cell,cell)).ToList();
                if(leaf.Count>0)foreach(var part in leaf) {
                    var left=Math.Max(l,part.Left);var right=Math.Min(r,part.Right);
                    if(right>left)Rect(left,part.NormalOffset-part.Depth/2,right,part.NormalOffset+part.Depth/2);
                }
                else {var inset=Math.Min(half*.35,60);Line(l,inset,r,inset);Line(l,-inset,r,-inset);}
            }
            foreach(var frame in parts.Where(p=>p.Kind=="frame"))
                Rect(frame.Left,frame.NormalOffset-frame.Depth/2,frame.Right,frame.NormalOffset+frame.Depth/2);
            var cells=DoorWindowElevationGeometryBuilder.Build(item).Cells
                .Where(c=>!c.IsDeleted&&c.Bottom<=section&&c.Top>section).OrderBy(c=>c.Left).ToList();
            for(var i=0;i<cells.Count;i++) {
                var c=cells[i];var l=Math.Max(0,c.Left);var r=Math.Min(width,c.Right);if(r-l<1)continue;
                var mode=c.Opening??item.OpeningMode??"固定";
                var projection=ProjectLeaf(c,parts,DoorWindowElevationGeometryBuilder.IsOperable(mode)?type.SashClearance??2:0);
                l=projection.Left;r=projection.Right;if(r-l<1)continue;
                if(mode.Contains("推拉")) {
                    var track=projection.Normal;var sashDepth=projection.Depth;
                    Rect(l,track-sashDepth/2,r,track+sashDepth/2);
                    Line(l,track,r,track);
                    var direction=mode.Contains("左推拉")?-1:mode.Contains("右推拉")?1:i%2==0?-1:1;
                    var center=(l+r)/2;Arrow(center-direction*(r-l)*.2,center+direction*(r-l)*.2,-half-Math.Max(60,half*.7),c.IsDoor?null:"window-slide-"+i);
                } else if(mode.Contains("平开")) {
                    var hinge=mode.Contains("右平开")?r:l;var direction=hinge==r?-1:1;var span=r-l;
                    var leafThickness=projection.Depth;var hingeY=SwingHingeNormal(projection,c,parts,type.SashClearance??2);
                    var angle=AngleRadians(opening);var dx=direction*Math.Cos(angle);var dy=Math.Sin(angle);
                    var nx=-dy*leafThickness/2;var ny=dx*leafThickness/2;
                    var ex=hinge+span*dx;var ey=hingeY+span*dy;
                    if(c.IsDoor)Polygon(new PointModel(hinge+nx,hingeY+ny),new PointModel(ex+nx,ey+ny),
                        new PointModel(ex-nx,ey-ny),new PointModel(hinge-nx,hingeY-ny));
                    else {
                        ClosedLeaf(c,l,r);
                        if(angle>0)Line(hinge,hingeY,ex,ey,"window-leaf-"+i);
                    }
                    if(angle>0) {
                        var radius=span*.78;var end=angle*.7;
                        for(var j=0;j<24;j++) {
                            var a=angle*(.25+.45*j/24);var b=angle*(.25+.45*(j+1)/24);
                            Line(hinge+direction*radius*Math.Cos(a),hingeY+radius*Math.Sin(a),
                                hinge+direction*radius*Math.Cos(b),hingeY+radius*Math.Sin(b),c.IsDoor?null:"window-arrow-"+i);
                        }
                        var ax=hinge+direction*radius*Math.Cos(end);var ay=hingeY+radius*Math.Sin(end);
                        var tx=-direction*Math.Sin(end);var ty=Math.Cos(end);
                        var size=Math.Min(55,radius*angle*.12);
                        if(c.IsDoor) {
                            Line(ax,ay,ax-tx*size-ty*size*.45,ay-ty*size+tx*size*.45);
                            Line(ax,ay,ax-tx*size+ty*size*.45,ay-ty*size-tx*size*.45);
                        } else {
                            Line(ax-tx*size-ty*size*.45,ay-ty*size+tx*size*.45,ax,ay,"window-arrow-head-"+i);
                            Line(ax,ay,ax-tx*size+ty*size*.45,ay-ty*size-tx*size*.45,"window-arrow-head-"+i);
                        }
                    }
                    if(angle>0)for(var j=0;j<SwingArcSegments;j++){var a=angle*(1-j/(double)SwingArcSegments);var b=angle*(1-(j+1)/(double)SwingArcSegments);
                        Line(hinge+direction*span*Math.Cos(a),hingeY+span*Math.Sin(a),hinge+direction*span*Math.Cos(b),hingeY+span*Math.Sin(b),"swing-"+i);}
                } else if(mode=="百叶"||item.ElevationType.Contains("百叶")) {
                    Rect(l,-half*.6,r,half*.6);for(var y=-half*.4;y<half*.5;y+=Math.Max(10,half*.2))Line(l,y,r,y);
                } else ClosedLeaf(c,l,r);
            }
            return lines;
        }

        public static PointModel DirectionHandle(OpeningModel opening, OpeningTypeModel type, double thickness)
        {
            type=type??OpeningConstruction.Default(opening);
            var item=OpeningElevationAdapter.ToScheduleItem(opening,type,opening.Width,opening.Height);
            var section=Math.Max(0,Math.Min(opening.Height-.01,1200-opening.Sill));
            var geometry=DoorWindowElevationGeometryBuilder.Build(item);
            var cells=geometry.Cells.Where(c=>!c.IsDeleted&&c.Bottom<=section&&c.Top>section).OrderBy(c=>c.Left).ToList();
            var swing=cells.FirstOrDefault(c=>(c.IsDoor||CutsWall(opening))&&(c.Opening??item.OpeningMode??"").Contains("平开"));
            var windowSwing=cells.FirstOrDefault(c=>!c.IsDoor&&(c.Opening??item.OpeningMode??"").Contains("平开"))
                ??geometry.Cells.FirstOrDefault(c=>!c.IsDeleted&&!c.IsDoor&&c.Left>=0&&c.Right<=opening.Width&&(c.Opening??item.OpeningMode??"").Contains("平开"));
            PointModel result=null;
            if((type?.PlanStyle??"按立面")=="按立面"&&swing!=null) {
                var parts=OpeningConstruction.Build(opening,type,thickness).Where(p=>p.Face==0&&p.Bottom<=section&&p.Top>section).ToList();
                var leaf=ProjectLeaf(swing,parts,type.SashClearance??2);
                var right=(swing.Opening??item.OpeningMode??"").Contains("右平开");var span=leaf.Right-leaf.Left;
                if(span<1)return null;
                result=new PointModel((right?leaf.Right:leaf.Left)+(right?-1:1)*span*Math.Cos(AngleRadians(opening)),
                    SwingHingeNormal(leaf,swing,parts,type.SashClearance??2)+span*Math.Sin(AngleRadians(opening)));
                result.X=Math.Round(result.X,9);result.Y=Math.Round(result.Y,9);
            }
            else if((type?.PlanStyle??"按立面")=="按立面"&&windowSwing!=null) {
                var leafSection=(windowSwing.Bottom+windowSwing.Top)/2;
                var parts=OpeningConstruction.Build(opening,type,thickness).Where(p=>p.Face==0&&p.Bottom<=leafSection&&p.Top>leafSection).ToList();
                var leaf=ProjectLeaf(windowSwing,parts,type.SashClearance??2);
                var right=(windowSwing.Opening??item.OpeningMode??"").Contains("右平开");
                var span=leaf.Right-leaf.Left;if(span<1)return null;
                result=new PointModel((right?leaf.Right:leaf.Left)+(right?-1:1)*span*Math.Cos(AngleRadians(opening)),
                    SwingHingeNormal(leaf,windowSwing,parts,type.SashClearance??2)+span*Math.Sin(AngleRadians(opening)));
                result.X=Math.Round(result.X,9);result.Y=Math.Round(result.Y,9);
            }
            else if((type?.PlanStyle??"按立面")=="按立面"&&cells.Any(c=>(c.Opening??item.OpeningMode??"").Contains("推拉")))
                result=new PointModel(opening.Width*.65,Math.Max(50,thickness*.65));
            else if(type?.PlanStyle=="折叠门")result=new PointModel(opening.Width*.25,opening.Width*.2);
            if(result!=null){if(opening.PlanFlipAlong)result.X=opening.Width-result.X;if(opening.PlanFlipNormal)result.Y=-result.Y;}
            return result;
        }

        public static bool CutsWall(OpeningModel opening,double cut=1200)=>opening.Sill<=cut&&opening.Sill+opening.Height>cut;

        private static double AngleRadians(OpeningModel opening)
            =>(double.IsNaN(opening.PlanOpenAngle)||double.IsInfinity(opening.PlanOpenAngle)?90:Math.Max(0,Math.Min(180,opening.PlanOpenAngle)))*Math.PI/180;

        public static bool HasSwingDoor(OpeningModel opening,OpeningTypeModel type)
        {
            if((type?.PlanStyle??"按立面")!="按立面")return false;
            var item=OpeningElevationAdapter.ToScheduleItem(opening,type,opening.Width,opening.Height);
            return DoorWindowElevationGeometryBuilder.Build(item).Cells.Any(c=>!c.IsDeleted&&c.IsDoor&&(c.Opening??item.OpeningMode??"").Contains("平开"));
        }

        public static bool HasPlanSwing(OpeningModel opening,OpeningTypeModel type)
        {
            if((type?.PlanStyle??"按立面")!="按立面")return false;
            var item=OpeningElevationAdapter.ToScheduleItem(opening,type,opening.Width,opening.Height);
            return DoorWindowElevationGeometryBuilder.Build(item).Cells.Any(c=>!c.IsDeleted&&(c.Opening??item.OpeningMode??"").Contains("平开"));
        }

        public static List<List<PointModel>> SelectionRegions(OpeningModel opening,OpeningTypeModel type,double thickness)
        {
            type=type??OpeningConstruction.Default(opening);
            var half=Math.Max(1,thickness/2);
            var regions=new List<List<PointModel>> {new List<PointModel> {new PointModel(0,-half),new PointModel(opening.Width,-half),
                new PointModel(opening.Width,half),new PointModel(0,half)}};
            var style=type.PlanStyle??"按立面";
            if(style=="按立面"&&CutsWall(opening)) {
                var item=OpeningElevationAdapter.ToScheduleItem(opening,type,opening.Width,opening.Height);
                var section=Math.Max(0,Math.Min(opening.Height-.01,1200-opening.Sill));
                var parts=OpeningConstruction.Build(opening,type,thickness).Where(p=>p.Face==0&&p.Bottom<=section&&p.Top>section).ToList();
                foreach(var cell in DoorWindowElevationGeometryBuilder.Build(item).Cells.Where(c=>!c.IsDeleted
                    &&c.Bottom<=section&&c.Top>section&&(c.Opening??item.OpeningMode??"").Contains("平开"))) {
                    var leaf=ProjectLeaf(cell,parts,type.SashClearance??2);
                    var right=(cell.Opening??item.OpeningMode??"").Contains("右平开");var hinge=right?leaf.Right:leaf.Left;
                    var sign=right?-1:1;var radius=leaf.Right-leaf.Left;var normal=SwingHingeNormal(leaf,cell,parts,type.SashClearance??2);
                    if(radius<1)continue;
                    var region=new List<PointModel> {new PointModel(hinge,normal)};
                    for(var i=0;i<=16;i++){var angle=AngleRadians(opening)*i/16;region.Add(new PointModel(hinge+sign*radius*Math.Cos(angle),normal+radius*Math.Sin(angle)));}
                    regions.Add(region);
                }
            }
            foreach(var region in regions)foreach(var point in region) {
                if(opening.PlanFlipAlong)point.X=opening.Width-point.X;
                if(opening.PlanFlipNormal)point.Y=-point.Y;
            }
            return regions;
        }

        private sealed class PlanLeaf
        {
            public double Left,Right,Normal,Depth;
        }

        private static bool SameCell(DoorWindowCell a,DoorWindowCell b)=>a!=null
            &&Math.Abs(a.Left-b.Left)<.01&&Math.Abs(a.Right-b.Right)<.01
            &&Math.Abs(a.Bottom-b.Bottom)<.01&&Math.Abs(a.Top-b.Top)<.01;

        private static PlanLeaf ProjectLeaf(DoorWindowCell cell,List<OpeningPart> parts,double clearance)
        {
            var leaf=parts.Where(p=>SameCell(p.Cell,cell)).ToList();
            var result=new PlanLeaf {Left=leaf.Count>0?leaf.Min(p=>p.Left):cell.Left,
                Right=leaf.Count>0?leaf.Max(p=>p.Right):cell.Right,
                Normal=leaf.Count>0?leaf[0].NormalOffset:parts.Select(p=>p.NormalOffset).FirstOrDefault(),
                Depth=leaf.Count>0?leaf.Max(p=>p.Depth):40};
            // Construction already includes sliding lap. Fixed posts still bound the plan's clear area.
            foreach(var frame in parts.Where(p=>p.Kind=="frame")) {
                if(frame.Left<=cell.Left+.01&&frame.Right>cell.Left+.01)
                    result.Left=Math.Max(result.Left,frame.Right+clearance);
                if(frame.Right>=cell.Right-.01&&frame.Left<cell.Right-.01)
                    result.Right=Math.Min(result.Right,frame.Left-clearance);
            }
            return result;
        }

        private static double SwingHingeNormal(PlanLeaf leaf,DoorWindowCell cell,List<OpeningPart> parts,double clearance)
        {
            var frames=parts.Where(p=>p.Kind=="frame"&&p.Right>=cell.Left-.01&&p.Left<=cell.Right+.01).ToList();
            // Keep the entire symbolic leaf clear of the frame for every supported opening angle.
            return frames.Count==0?leaf.Normal:Math.Max(leaf.Normal,
                frames.Max(p=>p.NormalOffset+p.Depth/2)+leaf.Depth/2+clearance);
        }

        public static double WallEndClearance(double length,OpeningModel opening,out bool fromStart)
        {
            var first=opening.Offset-opening.Width/2;var second=length-opening.Offset-opening.Width/2;
            fromStart=first<=second;return fromStart?first:second;
        }
        public static double OffsetFromWallEnd(double length,double width,double clearance,bool fromStart)
            =>fromStart?clearance+width/2:length-clearance-width/2;
    }
}
