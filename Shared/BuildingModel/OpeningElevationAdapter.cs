using System;
using BatchPdfPublisher.Models;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// 把"模型里的洞口 + 类型库里的做法"翻译成插件已有门窗立面生成器的输入。
    ///
    /// 这一层是**适配器**，不是另一套画法：
    /// 尺寸以模型里的洞口为准（三维开洞用的是它），做法（分格/开启/框料/凸窗）以类型库为准，
    /// 然后整份交给 <see cref="DoorWindowElevationGeometryBuilder"/> —— CAD 里画门窗立面用的是同一份代码，
    /// 所以程序出的立面门窗与插件画的不会两样。类型库改了，重算视图就跟着变（不复制参数）。
    /// </summary>
    public static class OpeningElevationAdapter
    {
        public const string WindowLayerKey="DoorWindowWindow", DoorLayerKey="DoorWindowDoor", MarkLayerKey="DoorWindowOpening";
        public static bool UsesWindowAppearance(DoorWindowScheduleItem item)
        {
            var type=item?.ElevationType??"";
            if(type.Contains("窗"))return true;
            return type.Contains("门")&&DoorWindowElevationGeometryBuilder.ParseCellLayout(item.CustomCellLayout)
                .Any(c=>!c.IsDoor&&string.Equals(c.Material,"玻璃",StringComparison.Ordinal));
        }
        public static bool UsesWindowAppearance(OpeningModel opening,OpeningTypeModel type)
        {
            try{return UsesWindowAppearance(ToScheduleItem(opening,type,opening.Width,opening.Height));}
            catch{return (type?.ElevationType??opening.Kind??"").Contains("窗");}
        }
        public static string LayerKey(ViewLine line)=>line.OpeningRole==null?null:
            line.OpeningRole=="Hole"||line.OpeningRole=="Opening"?MarkLayerKey:
            line.OpeningUsesWindowAppearance==true?WindowLayerKey:DoorLayerKey;
        public static ViewLine ElevationLine(DoorWindowLineSegment line,bool window,double x=0,double y=0)
            =>new ViewLine {Layer=ViewLayers.Opening,OpeningRole=line.Role.ToString(),OpeningUsesWindowAppearance=window,
                OpeningArcId=line.OpeningPathId,
                LineType=line.Role==DoorWindowLineRole.Opening||line.Role==DoorWindowLineRole.Hole?"DASHED":null,
                X1=x+line.X1,Y1=y+line.Y1,X2=x+line.X2,Y2=y+line.Y2};
        public static int PaperWeight(string role,int? weight)=>weight.HasValue?DrawingLineWeights.Resolve(ViewLayers.Opening,weight):
            role=="Opening"||role=="Hole"?13:role!=null?25:DrawingLineWeights.Resolve(ViewLayers.Opening);
        public static List<DoorWindowLineSegment> CleanGeometryLines(IEnumerable<DoorWindowLineSegment> lines)
            =>CleanLines(lines.Select(l=>ElevationLine(l,false))).Select(l=>new DoorWindowLineSegment(l.X1,l.Y1,l.X2,l.Y2,
                (DoorWindowLineRole)Enum.Parse(typeof(DoorWindowLineRole),l.OpeningRole)) { OpeningPathId=l.OpeningArcId }).ToList();

        public static List<ViewLine> CleanLines(IEnumerable<ViewLine> source)
        {
            const double tolerance=.01;
            var result=new List<ViewLine>();
            var buckets=new Dictionary<string,List<ViewLine>>(StringComparer.Ordinal);
            foreach(var line in source.Where(l=>l!=null).OrderByDescending(Priority))
            {
                var dx=line.X2-line.X1;var dy=line.Y2-line.Y1;var length=Math.Sqrt(dx*dx+dy*dy);
                if(length<tolerance)continue;
                if(Math.Abs(line.Bulge)>1e-10){result.Add(line);continue;}
                var ux=dx/length;var uy=dy/length;
                if(ux< -1e-9||Math.Abs(ux)<=1e-9&&uy<0){ux=-ux;uy=-uy;}
                var nx=-uy;var ny=ux;var offset=nx*line.X1+ny*line.Y1;
                var direction=CoordinateKey(ux,8)+":"+CoordinateKey(uy,8);
                var row=(long)Math.Floor(offset/tolerance);
                var start=ux*line.X1+uy*line.Y1;var end=ux*line.X2+uy*line.Y2;
                var spans=new List<double[]> {new[]{Math.Min(start,end),Math.Max(start,end)}};
                // Adjacent offset buckets cover tolerance boundaries without moving model coordinates.
                for(var neighbor=row-1;neighbor<=row+1;neighbor++)
                {
                    if(!buckets.TryGetValue(direction+":"+neighbor,out var bucket))continue;
                    foreach(var kept in bucket)
                    {
                        if(!Compatible(kept,line)||Math.Abs(nx*kept.X1+ny*kept.Y1-offset)>tolerance
                            ||Math.Abs(nx*kept.X2+ny*kept.Y2-offset)>tolerance)continue;
                        var a=ux*kept.X1+uy*kept.Y1;var b=ux*kept.X2+uy*kept.Y2;var lo=Math.Min(a,b);var hi=Math.Max(a,b);
                        var next=new List<double[]>();
                        foreach(var span in spans)
                        {
                            if(hi<=span[0]+tolerance||lo>=span[1]-tolerance){next.Add(span);continue;}
                            if(lo>span[0]+tolerance)next.Add(new[]{span[0],Math.Min(lo,span[1])});
                            if(hi<span[1]-tolerance)next.Add(new[]{Math.Max(hi,span[0]),span[1]});
                        }
                        spans=next;if(spans.Count==0)break;
                    }
                }
                var key=direction+":"+row;
                if(!buckets.TryGetValue(key,out var own))buckets[key]=own=new List<ViewLine>();
                foreach(var span in spans)
                {
                    var a=(span[0]-start)/(end-start);var b=(span[1]-start)/(end-start);
                    if(a>b){var swap=a;a=b;b=swap;}
                    var trimmed=new ViewLine {Layer=line.Layer,LineType=line.LineType,LineWeight=line.LineWeight,
                        StrokeAreaId=line.StrokeAreaId,OpeningArcId=line.OpeningArcId,OpeningRole=line.OpeningRole,
                        OpeningUsesWindowAppearance=line.OpeningUsesWindowAppearance,
                        X1=a==0?line.X1:line.X1+a*dx,Y1=a==0?line.Y1:line.Y1+a*dy,
                        X2=b==1?line.X2:line.X1+b*dx,Y2=b==1?line.Y2:line.Y1+b*dy};
                    own.Add(trimmed);result.Add(trimmed);
                }
            }
            return MergePhysicalRuns(result);
        }
        private static List<ViewLine> MergePhysicalRuns(List<ViewLine> lines)
        {
            var result=lines.Where(l=>!Physical(l)||Math.Abs(l.Bulge)>1e-10).ToList();
            string Key(ViewLine l)
            {
                var dx=l.X2-l.X1;var dy=l.Y2-l.Y1;var length=Math.Sqrt(dx*dx+dy*dy);var ux=dx/length;var uy=dy/length;
                if(ux< -1e-9||Math.Abs(ux)<=1e-9&&uy<0){ux=-ux;uy=-uy;}
                return string.Join("\u001f",l.Layer,l.LineType,l.LineWeight,l.OpeningRole,l.OpeningUsesWindowAppearance,l.StrokeAreaId,l.OpeningArcId,
                    CoordinateKey(ux,8),CoordinateKey(uy,8),CoordinateKey(-uy*l.X1+ux*l.Y1,4));
            }
            foreach(var group in lines.Where(l=>Physical(l)&&Math.Abs(l.Bulge)<1e-10).GroupBy(Key))
            {
                var first=group.First();var dx=first.X2-first.X1;var dy=first.Y2-first.Y1;var length=Math.Sqrt(dx*dx+dy*dy);
                var ux=dx/length;var uy=dy/length;if(ux< -1e-9||Math.Abs(ux)<=1e-9&&uy<0){ux=-ux;uy=-uy;}
                ViewLine run=null;double runEnd=0;
                foreach(var l in group.OrderBy(l=>Math.Min(ux*l.X1+uy*l.Y1,ux*l.X2+uy*l.Y2)))
                {
                    var a=ux*l.X1+uy*l.Y1;var b=ux*l.X2+uy*l.Y2;
                    var lowX=a<=b?l.X1:l.X2;var lowY=a<=b?l.Y1:l.Y2;var highX=a<=b?l.X2:l.X1;var highY=a<=b?l.Y2:l.Y1;
                    var start=Math.Min(a,b);var end=Math.Max(a,b);
                    if(run!=null&&start<=runEnd+.01){if(end>runEnd){run.X2=highX;run.Y2=highY;runEnd=end;}continue;}
                    run=new ViewLine {Layer=l.Layer,LineType=l.LineType,LineWeight=l.LineWeight,StrokeAreaId=l.StrokeAreaId,
                        OpeningRole=l.OpeningRole,OpeningUsesWindowAppearance=l.OpeningUsesWindowAppearance,OpeningArcId=l.OpeningArcId,
                        X1=lowX,Y1=lowY,X2=highX,Y2=highY};runEnd=end;result.Add(run);
                }
            }
            return result;
        }
        private static string CoordinateKey(double value,int places)
        {var rounded=Math.Round(value,places);return (rounded==0?0:rounded).ToString("F"+places,CultureInfo.InvariantCulture);}
        private static int Priority(ViewLine line)=>line.OpeningRole=="Frame"?80:line.OpeningRole=="Mullion"?78:
            line.OpeningRole=="SashFrame"?72:line.OpeningRole=="Material"?30:line.OpeningRole=="Opening"?20:
            line.OpeningRole=="Hole"?10:60;
        private static bool Physical(ViewLine line)=>line.OpeningRole=="Frame"||line.OpeningRole=="Mullion"||line.OpeningRole=="SashFrame";
        private static bool Compatible(ViewLine kept,ViewLine line)
        {
            if(kept.Layer==line.Layer&&kept.LineType==line.LineType&&kept.LineWeight==line.LineWeight
                &&kept.OpeningArcId==line.OpeningArcId&&kept.OpeningRole==line.OpeningRole&&kept.OpeningUsesWindowAppearance==line.OpeningUsesWindowAppearance)return true;
            if(Physical(kept)&&Physical(line)&&kept.OpeningUsesWindowAppearance==line.OpeningUsesWindowAppearance)return true;
            return line.OpeningRole=="Hole"&&(Physical(kept)||kept.Layer==ViewLayers.Elevation&&kept.OpeningRole==null);
        }
        /// <summary>按编号查类型库；查不到就返回一个"单格"做法（只画洞口轮廓）。</summary>
        public static OpeningTypeModel Resolve(OpeningTypeLibraryDocument library, OpeningModel opening)
        {
            if (library == null || opening == null) return null;
            return library.FindType(opening.Code);
        }

        /// <summary>
        /// 组装生成器输入。视图里洞口的实际尺寸（宽/高）优先，做法其余取自类型库；
        /// 类型库缺失时退化成"普通窗/普通门 + 单格"。
        ///
        /// <paramref name="simplifiedDetail"/> = true 时按**1:100 及更小的立面图**简化：
        /// 不画 20mm 安装缝、不画 50mm 门扇内框、不画玻璃/百叶材料符号
        ///（这些在 1:50 的门窗立面详图里才看得清，1:100 上只有零点几毫米，纯属糊线）。
        /// 分格与开启线一律保留 —— 那正是立面图要表达的内容。
        /// </summary>
        public static DoorWindowScheduleItem ToScheduleItem(OpeningModel opening, OpeningTypeModel type,
            double width, double height, bool simplifiedDetail = false)
        {
            if (opening == null) throw new ArgumentNullException(nameof(opening));
            type=type ?? OpeningConstruction.Default(opening);
            var kind = string.IsNullOrWhiteSpace(opening.Kind) ? "窗" : opening.Kind.Trim();
            var isDoor = kind.IndexOf("门", StringComparison.Ordinal) >= 0;
            var item = new DoorWindowScheduleItem
            {
                Code = opening.Code,
                Width = width > 0.5d ? width : opening.Width,
                Height = height > 0.5d ? height : opening.Height,
                SillHeight = opening.Sill,
                ElevationType = type == null || string.IsNullOrWhiteSpace(type.ElevationType)
                    ? (isDoor ? "普通门" : "普通窗")
                    : type.ElevationType.Trim(),
                DivisionPreset = type == null ? null : type.DivisionPreset,
                OpeningMode = type == null ? null : type.OpeningMode,
                HasInstallationGap = type != null && type.HasInstallationGap && !simplifiedDetail,
                InstallationGap = type == null || simplifiedDetail ? 0d : type.InstallationGap,
                HasOuterFrame = type == null || type.HasOuterFrame,
                OuterFrameWidth = type == null ? 50d : type.OuterFrameWidth,
                HasMullion = type == null || type.HasMullion,
                MullionWidth = type == null ? 50d : type.MullionWidth,
                DoorFrameType = type == null || string.IsNullOrWhiteSpace(type.DoorFrameType) ? "N型" : type.DoorFrameType,
                DoorFrameWidth = type == null || simplifiedDetail ? 0d : Math.Max(0d, type.DoorFrameWidth > 0 ? type.DoorFrameWidth : type.SashWidth ?? 0d),
                CustomColumnRatios = type == null ? null : type.CustomColumnRatios,
                CustomRowRatios = type == null ? null : type.CustomRowRatios,
                CustomColumnWidths = type == null ? null : type.CustomColumnWidths,
                CustomRowHeights = type == null ? null : type.CustomRowHeights,
                CustomCellLayout = OpeningConstruction.ScaledLayout(type,width>0.5 ? width : opening.Width,height>0.5 ? height : opening.Height),
                CellOpeningModes = type == null ? null : type.CellOpeningModes,
                DoorPlacement = type == null || string.IsNullOrWhiteSpace(type.DoorPlacement) ? "靠左" : type.DoorPlacement,
                DoorEdgeDistance = type == null ? 0d : type.DoorEdgeDistance,
                BayLeftSide = type == null || string.IsNullOrWhiteSpace(type.BayLeftSide) ? "墙" : type.BayLeftSide,
                BayRightSide = type == null || string.IsNullOrWhiteSpace(type.BayRightSide) ? "墙" : type.BayRightSide,
                BayLeftDepth = type == null || type.BayLeftDepth <= 0d ? 600d : type.BayLeftDepth,
                BayRightDepth = type == null || type.BayRightDepth <= 0d ? 600d : type.BayRightDepth,
                BayLeftCellLayout = type == null ? null : type.BayLeftCellLayout,
                BayRightCellLayout = type == null ? null : type.BayRightCellLayout,
                Material = type == null || string.IsNullOrWhiteSpace(type.Material) || simplifiedDetail ? "无" : type.Material,
                AtlasName = type == null ? null : type.AtlasName
            };
            return item;
        }
    }
}
