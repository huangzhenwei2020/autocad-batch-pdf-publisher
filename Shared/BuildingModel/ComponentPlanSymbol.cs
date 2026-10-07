using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public sealed class ComponentPlanSymbol
    {
        public int SchemaVersion { get; set; } = 1;
        public string Units { get; set; } = "mm";
        public string Name { get; set; }
        public string Code { get; set; }
        public string Category { get; set; } = "Window";
        public double Width { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
        public List<ComponentPlanPrimitive> Primitives { get; set; } = new List<ComponentPlanPrimitive>();
        public List<ComponentPlanTextCandidate> TextCandidates { get; set; } = new List<ComponentPlanTextCandidate>();
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
        public const int MaxBytes=2*1024*1024;
        public const int MaxPrimitives=8192;
        internal static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
        public static void Validate(ComponentPlanSymbol symbol)
        {
            if(symbol==null||symbol.Units!="mm"||!((symbol.SchemaVersion==1&&(symbol.Category=="Door"||symbol.Category=="Window"))
                ||(symbol.SchemaVersion==2&&symbol.Category=="Furniture")))
                throw new InvalidDataException("不支持该符号版本、单位或类别。");
            if(symbol.SchemaVersion==2&&(!Finite(symbol.Depth)||symbol.Depth<=0||symbol.Depth>100000))
                throw new InvalidDataException("家具进深无效或超限。");
            if(string.IsNullOrWhiteSpace(symbol.Code)||symbol.Code.Length>64||symbol.Code.Any(char.IsControl)||string.IsNullOrWhiteSpace(symbol.Name)||symbol.Name.Length>100||symbol.Name.Any(char.IsControl))
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
