using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class ComponentPlanPrimitive
    {
        public string Kind { get; set; }
        public string SourcePath { get; set; }
        public string SourceLayer { get; set; }
        public string SourceLineType { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public double Radius { get; set; }
        public double StartDegrees { get; set; }
        public double SweepDegrees { get; set; }
    }
    public sealed class ComponentPlanTextCandidate
    {
        public string Text { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }
    public sealed class ComponentPlanPart
    {
        public string PartId { get; set; } = Guid.NewGuid().ToString("D");
        public string Name { get; set; }
        public string Role { get; set; }
        public List<int> Primitives { get; set; } = new List<int>();
        public override string ToString() => Name;
    }
    [DataContract]
    public sealed class ComponentPlanSymbol
    {
        [DataMember]
        public int SchemaVersion { get; set; } = 1;
        [DataMember]
        public string Units { get; set; } = "mm";
        [DataMember]
        public string Name { get; set; }
        [DataMember]
        public string Code { get; set; }
        [DataMember]
        public string Category { get; set; } = "Window";
        [DataMember]
        public double Width { get; set; }
        [DataMember]
        public double Height { get; set; }
        [DataMember]
        public double Depth { get; set; }
        [DataMember]
        public List<ComponentPlanPrimitive> Primitives { get; set; } = new List<ComponentPlanPrimitive>();
        [DataMember]
        public List<ComponentPlanTextCandidate> TextCandidates { get; set; } = new List<ComponentPlanTextCandidate>();
        // Omitted for legacy symbols: their serialized bytes and catalog hashes stay unchanged.
        [DataMember(EmitDefaultValue = false)]
        public string DoorAssembly { get; set; }
        [DataMember(EmitDefaultValue = false)]
        public List<ComponentPlanPart> Parts { get; set; }
    }
    public sealed class ComponentPlanFrame
    {
        private readonly double x,y,z,ux,uy,scale;
        public ComponentPlanFrame(double originX,double originY,double elevation,double alongX,double alongY,double mmPerUnit)
        {
            var length=Math.Sqrt(alongX*alongX+alongY*alongY);
            if(!new[]{originX,originY,elevation,length,mmPerUnit}.All(ComponentPlanSymbols.Finite)||length<.000001||mmPerUnit<=0||mmPerUnit>1000000)
                throw new InvalidDataException("基点、墙方向或毫米换算无效。");
            x=originX;y=originY;z=elevation;ux=alongX/length;uy=alongY/length;scale=mmPerUnit;
        }
        public PointModel Point(double px,double py,double pz)
        {
            if(!new[]{px,py,pz}.All(ComponentPlanSymbols.Finite)||Math.Abs(pz-z)*scale>.1)
                throw new InvalidDataException("符号含非有限坐标或不在同一水平平面的实体。");
            var dx=px-x;var dy=py-y;return new PointModel((dx*ux+dy*uy)*scale,(-dx*uy+dy*ux)*scale);
        }
        public ComponentPlanPrimitive Arc(PointModel center,PointModel start,PointModel tangent,double radians,bool circle)
        {
            if(center==null||start==null||tangent==null||!new[]{center.X,center.Y,start.X,start.Y,tangent.X,tangent.Y,radians}.All(ComponentPlanSymbols.Finite)||radians<=0||radians>2*Math.PI+.000001)
                throw new InvalidDataException("圆弧坐标或角度无效。");
            var ax=start.X-center.X;var ay=start.Y-center.Y;var bx=tangent.X-center.X;var by=tangent.Y-center.Y;
            var radius=Math.Sqrt(ax*ax+ay*ay);var other=Math.Sqrt(bx*bx+by*by);
            if(radius<.001||Math.Abs(radius-other)>Math.Max(.001,radius*.000001)||Math.Abs(ax*bx+ay*by)>radius*other*.000001)
                throw new InvalidDataException("圆弧被非等比缩放或剪切，当前不支持椭圆；请先在 CAD 中修正。");
            return new ComponentPlanPrimitive {Kind=circle?"Circle":"Arc",X1=center.X,Y1=center.Y,Radius=radius,
                StartDegrees=Math.Atan2(ay,ax)*180/Math.PI,SweepDegrees=(ax*by-ay*bx<0?-1:1)*radians*180/Math.PI};
        }
    }
    public static class ComponentPlanSymbols
    {
        public static readonly string[] DoorAssemblies = { "SingleSwing", "DoubleSwing", "UnequalSwing", "Sliding", "Folding", "Pivot", "Other" };
        public static readonly string[] DoorAssemblyNames = { "单开门", "双开门", "子母门", "推拉门", "折叠门", "转轴门", "其他门型" };
        public static readonly string[] PartRoles = { "Frame", "PrimaryLeaf", "SecondaryLeaf", "Handle", "Hardware", "FixedPanel", "OpeningSymbol", "Other", "Casing" };
        public static readonly string[] PartRoleNames = { "门框", "主门扇", "副门扇", "把手 / 拉手", "合页 / 轨道五金", "固定板 / 亮子", "开启示意", "其他部件", "门套" };
        public static string PrimitiveRole(ComponentPlanSymbol symbol,int index,IList<ComponentPlanPart> parts=null)
        {
            var role=(parts??symbol.Parts)?.FirstOrDefault(part=>part.Primitives.Contains(index))?.Role;
            var p=symbol.Primitives[index];
            // Large leaf arcs are opening symbols even in older leaf groups; small handle arcs remain hardware.
            if(symbol.Category=="Door"&&p.Kind=="Arc"&&(role==null||role=="PrimaryLeaf"||role=="SecondaryLeaf")
                &&p.Radius>=Math.Max(100,symbol.Width*.25)&&Math.Abs(p.SweepDegrees)>=15)return "OpeningSymbol";
            return role??"Other";
        }
        public static short RoleColor(string role)=>role=="Casing"?(short)30:role=="Frame"?(short)1:
            role=="PrimaryLeaf"||role=="SecondaryLeaf"?(short)2:role=="Handle"||role=="OpeningSymbol"?(short)8:
            role=="Hardware"?(short)9:role=="FixedPanel"?(short)4:(short)7;
        public static string RoleLineType(string role)=>role=="OpeningSymbol"?"DASHED":"Continuous";
        // CAD plan variants keep profiles and hardware rigid; they never resize the shared GLB payload.
        public static ComponentPlanSymbol DoorPlanVariant(ComponentPlanSymbol source,PointModel localBase,double width,double height)
        {
            Validate(source);if(source.Category!="Door"||!Finite(width)||!Finite(height)||width<200||width>10000||height<200||height>10000)
                throw new InvalidDataException("门洞尺寸应在 200～10000 mm 之间。");
            var result=Load(Bytes(source));var delta=width-source.Width;
            if(Math.Abs(delta)>.001) {
                if(!IsDecorationDoor(source)||source.DoorAssembly!="SingleSwing"||source.Parts.Any(p=>(p.Role=="SecondaryLeaf"||p.Role=="FixedPanel")&&p.Primitives.Count>0))
                    throw new InvalidDataException("该门型尚无部件变尺规则，不能整体拉伸。当前支持已标注的单开门。");
                var swings=source.Primitives.Select((p,i)=>new{P=p,I=i}).Where(item=>item.P.Kind=="Arc"&&PrimitiveRole(source,item.I)=="OpeningSymbol").ToArray();
                if(swings.Length!=1||!source.Parts.Any(p=>p.Role=="PrimaryLeaf"&&p.Primitives.Count>0))throw new InvalidDataException("请先标注主门扇，并保留一条开启弧以确定合页与门扇方向。");
                var swing=swings[0].P;var radius=swing.Radius+delta;if(radius<100)throw new InvalidDataException("门扇净宽过小。");
                var angle=(swing.StartDegrees+swing.SweepDegrees)*Math.PI/180;var ux=Math.Cos(angle);var uy=Math.Sin(angle);
                var start=swing.StartDegrees*Math.PI/180;
                var leafPoints=source.Parts.Where(p=>p.Role=="PrimaryLeaf").SelectMany(p=>p.Primitives).SelectMany(i=>new[]{new PointModel(source.Primitives[i].X1,source.Primitives[i].Y1),new PointModel(source.Primitives[i].X2,source.Primitives[i].Y2)}).ToArray();
                Func<double,double,double> extent=(dx,dy)=>leafPoints.Max(point=>(point.X-swing.X1)*dx+(point.Y-swing.Y1)*dy);
                if(extent(Math.Cos(start),Math.Sin(start))>extent(ux,uy)){ux=Math.Cos(start);uy=Math.Sin(start);}
                var hingeShift=swing.X1-localBase.X>source.Width/2?delta:0;
                for(var i=0;i<result.Primitives.Count;i++) {
                    var primitive=result.Primitives[i];var role=PrimitiveRole(source,i);
                    if(role=="Other")throw new InvalidDataException("还有未标注的线条，请先分配门框、门扇、把手或五金部件，再修改门宽。");
                    Func<double,double,PointModel> map=(x,y)=>{
                        if(role=="Frame"||role=="Casing")return new PointModel(x+(x-localBase.X>source.Width/2?delta:0),y);
                        var projection=(x-swing.X1)*ux+(y-swing.Y1)*uy;
                        var shift=role=="Handle"?delta:projection>swing.Radius/2?delta:0;
                        return new PointModel(x+hingeShift+ux*shift,y+uy*shift);
                    };
                    if(role=="OpeningSymbol") {
                        if(primitive.Kind!="Arc")throw new InvalidDataException("开启示意包含未知变尺线条，请先核对分组。");
                        primitive.X1+=hingeShift;primitive.Radius=radius;continue;
                    }
                    var first=map(primitive.X1,primitive.Y1);primitive.X1=first.X;primitive.Y1=first.Y;
                    if(primitive.Kind=="Line"){var last=map(primitive.X2,primitive.Y2);primitive.X2=last.X;primitive.Y2=last.Y;}
                }
            }
            result.Width=width;result.Height=height;
            if(Math.Abs(delta)>.001||Math.Abs(height-source.Height)>.001) {
                result.Code=OpeningConstruction.SizeCode("M",width,height);result.TextCandidates.Clear();
            }
            Validate(result);return result;
        }
        public static ComponentPlanSymbol DoorOpeningVariant(ComponentPlanSymbol source,double degrees)
        {
            if(!Finite(degrees)||degrees<0||degrees>180)throw new InvalidDataException("开启角度应在 0～180° 之间。");
            var plan=Load(Bytes(source));var swings=plan.Primitives.Select((p,i)=>new{P=p,I=i}).Where(v=>v.P.Kind=="Arc"&&PrimitiveRole(plan,v.I)=="OpeningSymbol").ToArray();
            if(!IsDecorationDoor(plan)||plan.DoorAssembly!="SingleSwing"||swings.Length!=1)throw new InvalidDataException("开启角度首批支持完整标注的单开门。");
            var arc=swings[0].P;var sign=Math.Sign(arc.SweepDegrees);var start=arc.StartDegrees*Math.PI/180;var end=(arc.StartDegrees+arc.SweepDegrees)*Math.PI/180;
            var leaf=plan.Parts.Where(p=>p.Role=="PrimaryLeaf").SelectMany(p=>p.Primitives).ToArray();if(leaf.Length==0)throw new InvalidDataException("请先标注主门扇。");
            Func<double,double> extent=a=>leaf.SelectMany(i=>new[]{new PointModel(plan.Primitives[i].X1,plan.Primitives[i].Y1),new PointModel(plan.Primitives[i].X2,plan.Primitives[i].Y2)}).Max(p=>(p.X-arc.X1)*Math.Cos(a)+(p.Y-arc.Y1)*Math.Sin(a));
            var current=extent(start)>extent(end)?start:end;var target=start+sign*degrees*Math.PI/180;var rotation=target-current;var cos=Math.Cos(rotation);var sin=Math.Sin(rotation);
            foreach(var i in plan.Parts.Where(p=>p.Role=="PrimaryLeaf"||p.Role=="Handle").SelectMany(p=>p.Primitives)){if(i==swings[0].I)continue;var p=plan.Primitives[i];Func<double,double,PointModel> rotate=(x,y)=>new PointModel(arc.X1+(x-arc.X1)*cos-(y-arc.Y1)*sin,arc.Y1+(x-arc.X1)*sin+(y-arc.Y1)*cos);var a=rotate(p.X1,p.Y1);p.X1=a.X;p.Y1=a.Y;if(p.Kind=="Line"){var b=rotate(p.X2,p.Y2);p.X2=b.X;p.Y2=b.Y;}else p.StartDegrees+=rotation*180/Math.PI;}
            arc.SweepDegrees=sign*Math.Max(.000001,degrees);Validate(plan);return plan;
        }
        public static PointModel DirectionHandle(ComponentPlanSymbol symbol)
        {
            if(symbol==null||symbol.Primitives.Count==0)return null;
            var arc=symbol.Primitives.FirstOrDefault(p=>p.Kind=="Arc");
            var handles=symbol.Primitives.Select((p,i)=>new {P=p,Role=PrimitiveRole(symbol,i)}).Where(v=>v.Role=="Handle").SelectMany(v=>v.P.Kind=="Line"?new[]{new PointModel(v.P.X1,v.P.Y1),new PointModel(v.P.X2,v.P.Y2)}:new[]{new PointModel(v.P.X1,v.P.Y1)}).ToArray();
            if(handles.Length>0){var point=new PointModel((handles.Min(p=>p.X)+handles.Max(p=>p.X))/2,(handles.Min(p=>p.Y)+handles.Max(p=>p.Y))/2);if(arc==null)return point;var dx=point.X-arc.X1;var dy=point.Y-arc.Y1;var length=Math.Sqrt(dx*dx+dy*dy);if(length>1e-8){var gap=arc.Radius*.035;point.X+=-dy/length*gap;point.Y+=dx/length*gap;}return point;}
            if(arc==null)return null;
            var leaves=symbol.Primitives.Select((p,i)=>new {P=p,Role=PrimitiveRole(symbol,i)}).Where(v=>v.Role=="PrimaryLeaf"&&v.P.Kind=="Line").SelectMany(v=>new[]{new PointModel(v.P.X1,v.P.Y1),new PointModel(v.P.X2,v.P.Y2)}).OrderByDescending(p=>Math.Pow(p.X-arc.X1,2)+Math.Pow(p.Y-arc.Y1,2)).ToArray();
            if(leaves.Length==0)return null;return new PointModel(arc.X1+(leaves[0].X-arc.X1)*.9,arc.Y1+(leaves[0].Y-arc.Y1)*.9);
        }
        public static PointModel SuggestedInsertionBase(ComponentPlanSymbol symbol)
        {
            Validate(symbol);
            var points=symbol.Primitives.SelectMany(p=>p.Kind=="Line"?new[]{new PointModel(p.X1,p.Y1),new PointModel(p.X2,p.Y2)}:
                new[]{new PointModel(p.X1-p.Radius,p.Y1-p.Radius),new PointModel(p.X1+p.Radius,p.Y1+p.Radius)}).ToArray();
            var margin=Math.Max(100,symbol.Width*.1);
            if(points.Min(p=>p.X)<=margin&&points.Max(p=>p.X)>=-margin&&points.Min(p=>p.Y)<=margin&&points.Max(p=>p.Y)>=-margin)
                return new PointModel(0,0);
            // A historical symbol can retain drawing offsets. Choose a visible local base without rewriting its shared data.
            var frame=symbol.Parts?.Where(p=>p.Role=="Frame"||p.Role=="Casing").SelectMany(p=>p.Primitives).Distinct().ToArray();
            var outlines=frame!=null&&frame.Length>0?frame.Select(i=>symbol.Primitives[i]).ToArray():symbol.Primitives.ToArray();
            var vertices=outlines.SelectMany(p=>p.Kind=="Line"?new[]{new PointModel(p.X1,p.Y1),new PointModel(p.X2,p.Y2)}:new[]{new PointModel(p.X1,p.Y1)}).ToArray();
            return new PointModel(vertices.Min(p=>p.X),vertices.Min(p=>p.Y));
        }
        public static bool IsDecorationDoor(ComponentPlanSymbol symbol) => symbol != null && symbol.SchemaVersion == 3 && symbol.Category == "Door";
        public static string DisplayName(ComponentPlanSymbol symbol) => string.IsNullOrWhiteSpace(symbol.Code) ? symbol.Name : symbol.Code + " · " + symbol.Name;
        // Reference-size output only; selecting a project type may override this code later.
        public static string ReferenceCode(ComponentPlanSymbol symbol) => string.IsNullOrWhiteSpace(symbol.Code)
            ? OpeningConstruction.SizeCode(symbol.Category == "Door" ? "M" : "C", symbol.Width, symbol.Height) : symbol.Code;
        public static string PartRoleName(string role) { var i=Array.IndexOf(PartRoles,role); return i < 0 ? "其他部件" : PartRoleNames[i]; }
        public static string GeometryHash(ComponentPlanSymbol symbol) => ComponentCatalog.Hash(Bytes(new ComponentPlanSymbol {
            Name="geometry",Code="geometry",Width=1,Height=1,Primitives=symbol.Primitives,TextCandidates=symbol.TextCandidates }));
        public const int MaxBytes=2*1024*1024;
        public const int MaxPrimitives=8192;
        internal static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
        public static void Validate(ComponentPlanSymbol symbol)
        {
            if(symbol==null||symbol.Units!="mm"||!((symbol.SchemaVersion==1&&(symbol.Category=="Door"||symbol.Category=="Window"))
                ||(symbol.SchemaVersion==2&&symbol.Category=="Furniture") || IsDecorationDoor(symbol)))
                throw new InvalidDataException("不支持该符号版本、单位或类别。");
            if(symbol.SchemaVersion==2&&(!Finite(symbol.Depth)||symbol.Depth<=0||symbol.Depth>100000))
                throw new InvalidDataException("家具进深无效或超限。");
            if((!IsDecorationDoor(symbol)&&string.IsNullOrWhiteSpace(symbol.Code))||symbol.Code?.Length>64||(symbol.Code?.Any(char.IsControl)??false)||string.IsNullOrWhiteSpace(symbol.Name)||symbol.Name.Length>100||symbol.Name.Any(char.IsControl))
                throw new InvalidDataException("符号名称或编号无效。");
            if(!Finite(symbol.Width)||!Finite(symbol.Height)||symbol.Width<=0||symbol.Height<=0||symbol.Width>100000||symbol.Height>100000)
                throw new InvalidDataException("洞口宽高无效或超限。");
            if(symbol.Primitives==null||symbol.Primitives.Count==0||symbol.Primitives.Count>MaxPrimitives||symbol.TextCandidates==null||symbol.TextCandidates.Count>256)
                throw new InvalidDataException("符号图元或文字候选数量无效或超限。");
            foreach(var p in symbol.Primitives) {
                if(p==null||!new[]{p.X1,p.Y1,p.X2,p.Y2,p.Radius,p.StartDegrees,p.SweepDegrees}.All(Finite)
                    ||new[]{p.X1,p.Y1,p.X2,p.Y2,p.Radius}.Any(n=>Math.Abs(n)>1000000)
                    ||(p.Kind!="Line"&&p.Kind!="Arc"&&p.Kind!="Circle")||p.SourcePath?.Length>1024||p.SourceLayer?.Length>255||p.SourceLineType?.Length>255)
                    throw new InvalidDataException("符号包含不支持的图元、坐标或来源信息。");
                if(p.Kind=="Line"&&Math.Abs(p.X2-p.X1)+Math.Abs(p.Y2-p.Y1)<.000001)
                    throw new InvalidDataException("符号含零长度线段。");
                if(p.Kind!="Line"&&(p.Radius<=0||Math.Abs(p.SweepDegrees)>360.000001||(p.Kind=="Arc"&&Math.Abs(p.SweepDegrees)<.000001)))
                    throw new InvalidDataException("圆或圆弧参数无效。");
            }
            foreach(var text in symbol.TextCandidates)
                if(text==null||text.Text==null||text.Text.Length>256||!Finite(text.X)||!Finite(text.Y)||Math.Abs(text.X)>1000000||Math.Abs(text.Y)>1000000)
                    throw new InvalidDataException("编号候选无效。");
            if(IsDecorationDoor(symbol)) {
                if(!DoorAssemblies.Contains(symbol.DoorAssembly)||symbol.Parts==null||symbol.Parts.Count>256)
                    throw new InvalidDataException("装修门类型或部件列表无效。");
                var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var assigned=new HashSet<int>();
                foreach(var part in symbol.Parts) {
                    Guid id;
                    if(part==null||!Guid.TryParseExact(part.PartId,"D",out id)||!ids.Add(part.PartId)||!PartRoles.Contains(part.Role)
                        ||string.IsNullOrWhiteSpace(part.Name)||part.Name.Length>100||part.Name.Any(char.IsControl)||part.Primitives==null||part.Primitives.Count>MaxPrimitives)
                        throw new InvalidDataException("装修门部件的名称、角色或身份无效。");
                    foreach(var index in part.Primitives)if(index<0||index>=symbol.Primitives.Count||!assigned.Add(index))
                        throw new InvalidDataException("同一平面图元不能重复归属部件，且必须存在。");
                }
            }else if(symbol.Parts!=null||symbol.DoorAssembly!=null)throw new InvalidDataException("部件分组须使用装修门符号版本。");
        }
        public static ComponentPlanSymbol Load(string path)
        {
            using(var file=File.OpenRead(path)) {
                if(file.Length>MaxBytes)throw new InvalidDataException("符号文件超过 2 MiB。");
                return Read(file);
            }
        }
        public static ComponentPlanSymbol Load(byte[] bytes)
        {
            if(bytes==null||bytes.Length>MaxBytes)throw new InvalidDataException("符号文件超过 2 MiB 或内容为空。");
            using(var stream=new MemoryStream(bytes,false)) {
                return Read(stream);
            }
        }
        public static byte[] Bytes(ComponentPlanSymbol symbol)
        {
            Validate(symbol);using(var stream=new MemoryStream()) {
                Serializer().WriteObject(stream,symbol);if(stream.Length>MaxBytes)throw new InvalidDataException("符号文件超过 2 MiB。");
                return stream.ToArray();
            }
        }
        public static int Pick(ComponentPlanSymbol symbol,PointModel point,double tolerance)
        {
            Validate(symbol);if(point==null||!Finite(point.X)||!Finite(point.Y)||!Finite(tolerance)||tolerance<=0)throw new InvalidDataException("二维拾取参数无效。");
            var best=tolerance;var result=-1;
            for(var i=0;i<symbol.Primitives.Count;i++) {
                var p=symbol.Primitives[i];double distance;
                if(p.Kind=="Line") {
                    var dx=p.X2-p.X1;var dy=p.Y2-p.Y1;var t=Math.Max(0,Math.Min(1,((point.X-p.X1)*dx+(point.Y-p.Y1)*dy)/(dx*dx+dy*dy)));
                    distance=Math.Sqrt(Math.Pow(point.X-p.X1-dx*t,2)+Math.Pow(point.Y-p.Y1-dy*t,2));
                }else {
                    var angle=Math.Atan2(point.Y-p.Y1,point.X-p.X1)*180/Math.PI;
                    var span=p.SweepDegrees>=0?(angle-p.StartDegrees)%360:(p.StartDegrees-angle)%360;if(span<0)span+=360;
                    if(p.Kind=="Circle"||span<=Math.Abs(p.SweepDegrees)+.000001)
                        distance=Math.Abs(Math.Sqrt(Math.Pow(point.X-p.X1,2)+Math.Pow(point.Y-p.Y1,2))-p.Radius);
                    else {
                        double End(double degrees)=>Math.Sqrt(Math.Pow(point.X-p.X1-p.Radius*Math.Cos(degrees*Math.PI/180),2)+Math.Pow(point.Y-p.Y1-p.Radius*Math.Sin(degrees*Math.PI/180),2));
                        distance=Math.Min(End(p.StartDegrees),End(p.StartDegrees+p.SweepDegrees));
                    }
                }
                if(distance<best){best=distance;result=i;}
            }
            return result;
        }
        public static void Save(string path,ComponentPlanSymbol symbol)
        {
            Validate(symbol);using(var memory=new MemoryStream()) {
                Serializer().WriteObject(memory,symbol);if(memory.Length>MaxBytes)throw new InvalidDataException("符号文件超过 2 MiB。");
                path=Path.GetFullPath(path);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try {using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write)){memory.Position=0;memory.CopyTo(file);file.Flush(true);}
                    if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
                finally {if(File.Exists(temp))File.Delete(temp);}
            }
        }
        private static DataContractJsonSerializer Serializer()=>new DataContractJsonSerializer(typeof(ComponentPlanSymbol),new DataContractJsonSerializerSettings {MaxItemsInObjectGraph=200000});
        private static ComponentPlanSymbol Read(Stream stream)
        {
            try {var symbol=(ComponentPlanSymbol)Serializer().ReadObject(stream);Validate(symbol);return symbol;}
            catch(System.Runtime.Serialization.SerializationException ex){throw new InvalidDataException("平面符号 JSON 无效。",ex);}
        }
        public static ViewDocument Preview(ComponentPlanSymbol symbol)
        {
            Validate(symbol);var view=new ViewDocument {Scale=1,Title=symbol.Code};
            foreach(var p in symbol.Primitives) {
                if(p.Kind=="Line")view.Lines.Add(new ViewLine {Layer=ViewLayers.Opening,LineWeight=0,X1=p.X1,Y1=p.Y1,X2=p.X2,Y2=p.Y2});
                else if(p.Kind=="Circle")view.Circles.Add(new ViewCircle {Layer=ViewLayers.Opening,LineWeight=0,X=p.X1,Y=p.Y1,Radius=p.Radius});
                else {
                    // Only the preview is tessellated; the exchange document retains native arcs.
                    var count=Math.Min(4096,Math.Max(8,(int)Math.Ceiling(Math.Abs(p.SweepDegrees)*Math.PI/180*p.Radius/4)));
                    for(var i=0;i<count;i++) {
                        var a=(p.StartDegrees+p.SweepDegrees*i/count)*Math.PI/180;var b=(p.StartDegrees+p.SweepDegrees*(i+1)/count)*Math.PI/180;
                        view.Lines.Add(new ViewLine {Layer=ViewLayers.Opening,LineWeight=0,X1=p.X1+p.Radius*Math.Cos(a),Y1=p.Y1+p.Radius*Math.Sin(a),X2=p.X1+p.Radius*Math.Cos(b),Y2=p.Y1+p.Radius*Math.Sin(b)});
                        if(view.Lines.Count>65536)throw new InvalidDataException("符号预览复杂度超过限制。");
                    }
                }
            }
            return view;
        }
    }
}
