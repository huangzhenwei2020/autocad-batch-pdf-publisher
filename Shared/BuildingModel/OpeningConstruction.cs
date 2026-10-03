using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class OpeningPart
    {
        public double Left,Bottom,Right,Top,Depth,NormalOffset;
        public string Kind;
        public DoorWindowCell Cell;
        public int Face; // 0 front, -1 left return, +1 right return
    }

    /// <summary>Same cells and net panel boundaries for elevation and physical construction.</summary>
    public static class OpeningConstruction
    {
        public static string AlphabeticSuffix(int index)
        {
            var value=index+1;var result="";
            while(value>0){value--;result=(char)('A'+value%26)+result;value/=26;}
            return result;
        }
        public static string SizeCode(string prefix,double width,double height)=>prefix
            + Math.Round(width/100d,0,MidpointRounding.AwayFromZero).ToString("00",System.Globalization.CultureInfo.InvariantCulture)
            + Math.Round(height/100d,0,MidpointRounding.AwayFromZero).ToString("00",System.Globalization.CultureInfo.InvariantCulture);
        public static string EffectiveCode(OpeningModel opening)=>string.IsNullOrWhiteSpace(opening.Code)
            ? SizeCode(opening.Kind=="门联窗" || opening.Kind=="门连窗" ? "MLC" : opening.HasSwingLeaf() ? "M" : "C",opening.Width,opening.Height) : opening.Code;
        public static OpeningTypeModel Copy(OpeningTypeModel type)
        {
            return BuildingModelJson.FromJson(BuildingModelJson.ToJson(new BuildingModelDocument {
                OpeningTypes=new List<OpeningTypeModel>{type} })).OpeningTypes[0];
        }
        public static OpeningTypeLibraryDocument Library(BuildingModelDocument model,OpeningTypeLibraryDocument external=null)
        {
            var types=(model.OpeningTypes ?? new List<OpeningTypeModel>()).Where(t=>t!=null).ToList();
            types.AddRange((external?.Types ?? new List<OpeningTypeModel>()).Where(t=>t!=null && !types.Any(x=>Same(x.Code,t.Code))));
            return new OpeningTypeLibraryDocument { ProjectName=model.Name,Types=types };
        }
        public static OpeningTypeModel Resolve(BuildingModelDocument model,OpeningModel opening)
        {
            var type=Library(model).FindType(EffectiveCode(opening));
            if(type!=null){
                if(type.ElevationType=="凸窗" && (type.BayLeftDepth!=DoorWindowElevationGeometryBuilder.NormalizeBayDepth(type.BayLeftDepth) || type.BayRightDepth!=DoorWindowElevationGeometryBuilder.NormalizeBayDepth(type.BayRightDepth))){
                    type=Copy(type);type.BayLeftDepth=DoorWindowElevationGeometryBuilder.NormalizeBayDepth(type.BayLeftDepth);type.BayRightDepth=DoorWindowElevationGeometryBuilder.NormalizeBayDepth(type.BayRightDepth);
                }
                return type;
            }
            return Default(opening);
        }
        public static OpeningTypeModel Default(OpeningModel opening)
        {
            var item=new DoorWindowScheduleItem {Code=EffectiveCode(opening),Width=opening.Width,Height=opening.Height,
                ElevationType=opening.HasSwingLeaf() ? "普通门" : opening.Kind=="门联窗" || opening.Kind=="门连窗" ? "门联窗" : opening.Kind=="百叶" ? "百叶" : "普通窗",
                SillHeight=opening.Sill,SillHeightFromCadRegistration=true,SourceNote="CAD 楼层登记"};
            BatchPdfPublisher.Services.DoorWindowElevationSuggestionService.Apply(item);
            var result=new OpeningTypeModel {Code=item.Code,Kind=opening.Kind,Width=opening.Width,Height=opening.Height,Sill=opening.Sill,
                FrameDepth=100,MullionDepth=100,SashDepth=50,GlassThickness=6,PanelThickness=40,OpenAngle=0};
            // Copy the original elevation defaults instead of maintaining another set of 2D rules.
            foreach(var property in typeof(DoorWindowScheduleItem).GetProperties()) {
                if(property.Name=="Width" || property.Name=="Height")continue;
                var target=typeof(OpeningTypeModel).GetProperty(property.Name);
                if(property.CanRead && target!=null && target.CanWrite && target.PropertyType==property.PropertyType)target.SetValue(result,property.GetValue(item,null),null);
            }
            result.SashWidth=item.DoorFrameWidth;
            result.HasInstallationGap=false; // Model openings fit the registered CAD hole by default.
            result.CustomCellLayout=ScaledLayout(result,opening.Width,opening.Height);
            return result;
        }
        public static string ScaledLayout(OpeningTypeModel type,double width,double height)
        {
            if(type==null || string.IsNullOrWhiteSpace(type.CustomCellLayout))return type?.CustomCellLayout;
            var gap=type.HasInstallationGap ? type.InstallationGap : 0;
            var cells=DoorWindowElevationGeometryBuilder.ParseCellLayout(type.CustomCellLayout);
            var sourceWidth=type.Width-2*gap;var sourceHeight=type.Height-2*gap;
            // Previous model defaults switched off the flag but retained the original
            // elevation's clear-area layout. Repair that exact legacy footprint only.
            var oldGap=type.InstallationGap;
            if(!type.HasInstallationGap && oldGap>0 && cells.Count>0
                && Math.Abs(cells.Min(c=>c.Left))<.01 && Math.Abs(cells.Min(c=>c.Bottom))<.01
                && Math.Abs(cells.Max(c=>c.Right)-(type.Width-2*oldGap))<.01
                && Math.Abs(cells.Max(c=>c.Top)-(type.Height-2*oldGap))<.01){
                sourceWidth=type.Width-2*oldGap;sourceHeight=type.Height-2*oldGap;
            }
            var sx=sourceWidth>0 ? (width-2*gap)/sourceWidth : 1;
            var sy=sourceHeight>0 ? (height-2*gap)/sourceHeight : 1;
            if(Math.Abs(sx-1)<.000001 && Math.Abs(sy-1)<.000001)return type.CustomCellLayout;
            foreach(var c in cells){c.Left*=sx;c.Right*=sx;c.Bottom*=sy;c.Top*=sy;}
            return DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells);
        }
        public static void ResizeType(OpeningTypeModel type,double width,double height)
        {
            type.CustomCellLayout=ScaledLayout(type,width,height);type.Width=width;type.Height=height;
        }
        public static BuildingModelDocument ApplyOverrides(BuildingModelDocument model)
        {
            if(model?.OpeningOverrides==null || model.OpeningOverrides.Count==0)return model;
            var clone=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));
            foreach(var o in clone.Openings) { var change=clone.OpeningOverrides.FirstOrDefault(x=>Same(x.OpeningId,o.Id)); if(change!=null)o.Code=change.TypeCode; }
            return clone;
        }
        public static List<OpeningPart> Build(OpeningModel opening,OpeningTypeModel type,double wallThickness)
        {
            var item=OpeningElevationAdapter.ToScheduleItem(opening,type,opening.Width,opening.Height);
            var mainItem=OpeningElevationAdapter.ToScheduleItem(opening,type,opening.Width,opening.Height);
            if(type.ElevationType=="凸窗")mainItem.ElevationType="普通窗";
            var geometry=DoorWindowElevationGeometryBuilder.Build(mainItem);
            var parts=new List<OpeningPart>();
            BuildFace(geometry,mainItem,0);
            if(type.ElevationType=="凸窗") {
                if(type.BayLeftSide=="窗")Return(-1,type.BayLeftDepth,type.BayLeftCellLayout);
                else parts.Add(new OpeningPart {Left=0,Right=type.BayLeftDepth,Bottom=0,Top=opening.Height,Depth=wallThickness,Kind="wall",Face=-1});
                if(type.BayRightSide=="窗")Return(1,type.BayRightDepth,type.BayRightCellLayout);
                else parts.Add(new OpeningPart {Left=0,Right=type.BayRightDepth,Bottom=0,Top=opening.Height,Depth=wallThickness,Kind="wall",Face=1});
                var cap=type.BayCapThickness??100;
                var half=parts.Where(p=>p.Kind=="frame"||p.Kind=="wall").Select(p=>p.Depth/2).DefaultIfEmpty((type.FrameDepth??100)/2).Max();
                var offset=parts.Where(p=>p.Kind=="frame").Select(p=>Math.Abs(p.NormalOffset)).DefaultIfEmpty(0).Max();
                var cover=half+offset;
                if(type.HasOuterFrame){
                    if(type.BayLeftSide=="窗")parts.Add(new OpeningPart {Left=-cover,Right=cover,Bottom=geometry.FrameBottom,Top=geometry.FrameTop,Depth=half*2,Kind="frame",Face=0});
                    if(type.BayRightSide=="窗")parts.Add(new OpeningPart {Left=opening.Width-cover,Right=opening.Width+cover,Bottom=geometry.FrameBottom,Top=geometry.FrameTop,Depth=half*2,Kind="frame",Face=0});
                }
                var depth=Math.Max(type.BayLeftDepth,type.BayRightDepth)+cover;
                parts.Add(new OpeningPart {Left=-cover,Right=opening.Width+cover,Bottom=-cap,Top=0,Depth=depth,Kind="bay-cap",Face=0});
                parts.Add(new OpeningPart {Left=-cover,Right=opening.Width+cover,Bottom=opening.Height,Top=opening.Height+cap,Depth=depth,Kind="bay-cap",Face=0});
            }
            return parts;
            void Return(int face,double depth,string layout) {
                var side=DoorWindowElevationGeometryBuilder.CreateBayReturnItem(item,face<0);
                var begin=parts.Count;
                BuildFace(DoorWindowElevationGeometryBuilder.Build(side),side,face);
                var gap=item.HasInstallationGap ? item.InstallationGap : 0;
                foreach(var part in parts.Skip(begin)){part.Bottom+=gap;part.Top+=gap;}
            }
            void BuildFace(DoorWindowElevationGeometry g,DoorWindowScheduleItem schedule,int face) {
                foreach(var cell in g.Cells) {
                    var net=DoorWindowElevationGeometryBuilder.PanelBounds(cell,g.Cells,schedule);
                    var l=net[0];var b=net[1];var r=net[2];var t=net[3];
                    Rail(cell.Left,cell.Bottom,cell.Right,b,false);
                    Rail(cell.Left,t,cell.Right,cell.Top,false);
                    Rail(cell.Left,b,l,t,true);Rail(r,b,cell.Right,t,true);
                    var swing=DoorWindowElevationGeometryBuilder.IsOperable(cell.Opening) && cell.Opening!="无";
                    // Moving leaves remain separate solids even when their fixed divider
                    // is omitted. Keep the clearance in the leaf, never in the opening.
                    if(swing){var clearance=type.SashClearance??2;l+=clearance;r-=clearance;b+=clearance;t-=clearance;}
                    if((cell.Opening??"").Contains("推拉")){
                        var lap=Math.Min(type.SashWidth??type.DoorFrameWidth,(cell.Right-cell.Left)*.2)/2+(type.SashClearance??2);
                        var neighbors=g.Cells.Where(c=>!ReferenceEquals(c,cell)&&(c.Opening??"").Contains("推拉")&&Math.Min(c.Top,cell.Top)-Math.Max(c.Bottom,cell.Bottom)>.05);
                        if(neighbors.Any(c=>Math.Abs(c.Right-cell.Left)<.05))l-=lap;
                        if(neighbors.Any(c=>Math.Abs(c.Left-cell.Right)<.05))r+=lap;
                    }
                    var sash=swing ? type.SashWidth ?? type.DoorFrameWidth : 0;
                    if(sash>0 && sash*2<Math.Min(r-l,t-b)) {
                        Add(l,b,r,b+sash,type.SashDepth??50,"sash",cell);
                        Add(l,t-sash,r,t,type.SashDepth??50,"sash",cell);
                        Add(l,b+sash,l+sash,t-sash,type.SashDepth??50,"sash",cell);
                        Add(r-sash,b+sash,r,t-sash,type.SashDepth??50,"sash",cell);
                        l+=sash;r-=sash;b+=sash;t-=sash;
                    }
                    var material=cell.Material;
                    if(material=="玻璃")Add(l,b,r,t,type.GlassThickness??6,"glass",cell);
                    else if(material=="实板" || cell.IsDoor && (material=="无" || string.IsNullOrWhiteSpace(material)))Add(l,b,r,t,type.PanelThickness??40,"door",cell);
                    else if(material=="百叶" || schedule.ElevationType=="百叶") {
                        var count=Math.Min(80,Math.Max(1,(int)((t-b)/80)));
                        for(var i=0;i<count;i++)Add(l,b+(t-b)*i/count,r,b+(t-b)*(i+.65)/count,type.PanelThickness??40,"louvre",cell);
                    }
                    void Rail(double x0,double y0,double x1,double y1,bool vertical) {
                        var outer=vertical ? Math.Abs(x0-g.FrameLeft)<.05 || Math.Abs(x1-g.FrameRight)<.05
                            : Math.Abs(y0-g.FrameBottom)<.05 || Math.Abs(y1-g.FrameTop)<.05;
                        Add(x0,y0,x1,y1,outer ? type.FrameDepth??100 : type.MullionDepth??100,"frame",null);
                    }
                }
                void Add(double x0,double y0,double x1,double y1,double depth,string kind,DoorWindowCell cell) {
                    if(x1-x0<.001 || y1-y0<.001)return;
                    var normal=type.InstallationOffset??0;
                    if(type.InstallationPosition=="靠外")normal+=(wallThickness-(type.FrameDepth??100))/2;
                    if(type.InstallationPosition=="靠内")normal-=(wallThickness-(type.FrameDepth??100))/2;
                    if(cell!=null && (cell.Opening??"").Contains("推拉")){
                        var leaves=g.Cells.Where(c=>(c.Opening??"").Contains("推拉") && Math.Min(c.Top,cell.Top)-Math.Max(c.Bottom,cell.Bottom)>.05).OrderBy(c=>c.Left).ToList();
                        var track=leaves.IndexOf(cell)%2;
                        var spacing=Math.Max(type.SashDepth??50,type.PanelThickness??40)+(type.SashClearance??2);
                        normal+=(track==0 ? -.5 : .5)*spacing;
                    }
                    parts.Add(new OpeningPart { Left=x0,Bottom=y0,Right=x1,Top=y1,Depth=depth,NormalOffset=normal,Kind=kind,Cell=cell,Face=face });
                }
            }
        }
        public static string Validate(OpeningModel opening,OpeningTypeModel type)
        {
            var numbers=new[] {type.FrameDepth??100,type.MullionDepth??100,type.SashDepth??50,type.GlassThickness??6,type.PanelThickness??40,type.BayCapThickness??100};
            if(numbers.Any(x=>double.IsNaN(x)||double.IsInfinity(x)||x<=0||x>2000))return "框料进深和面板厚度必须为 0～2000 mm 内的正数。";
            var angle=type.OpenAngle??0;
            var clearance=type.SashClearance??2;
            if(double.IsNaN(clearance)||double.IsInfinity(clearance)||clearance<0||clearance>20)return "扇边缝应为 0～20 mm。";
            if(clearance*2>=Math.Min(opening.Width,opening.Height))return "扇边缝超过门窗尺寸。";
            if(double.IsNaN(angle)||double.IsInfinity(angle)||angle<0||angle>120)return "开启角度应为 0～120°。";
            var sizes=new[] {type.OuterFrameWidth,type.MullionWidth,type.SashWidth??type.DoorFrameWidth,type.InstallationGap,type.InstallationOffset??0,type.BayLeftDepth,type.BayRightDepth};
            if(sizes.Any(x=>double.IsNaN(x)||double.IsInfinity(x)) || sizes.Take(4).Any(x=>x<0) || (type.SashWidth??type.DoorFrameWidth)>Math.Min(opening.Width,opening.Height)/2)return "框料宽度、安装缝或偏移无效。";
            if(type.ElevationType=="凸窗" && (type.BayLeftDepth<=0 || type.BayRightDepth<=0 || type.BayLeftDepth>5000 || type.BayRightDepth>5000))return "飘窗左右进深须为大于 0 且不超过 5000 mm 的尺寸。";
            try {
                var item=OpeningElevationAdapter.ToScheduleItem(opening,type,opening.Width,opening.Height);
                var g=DoorWindowElevationGeometryBuilder.Build(item);
                Build(opening,type,200);return null;
            }catch(Exception ex){return ex.Message;}
        }
        private static bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    }
}
