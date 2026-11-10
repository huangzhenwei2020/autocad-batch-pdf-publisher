using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class ComponentCadViews
    {
        public string Id {get;set;}=Guid.NewGuid().ToString("N");
        public int CadProcessId {get;set;}
        public string DrawingFingerprint {get;set;}
        public string DrawingPath {get;set;}
        public string Name {get;set;}
        public double Gap {get;set;}=1000;
        public bool AddTitles {get;set;}=true;
        public bool AddDimensions {get;set;}=true;
        public List<ViewDocument> Views {get;set;}=new List<ViewDocument>();
        public static string Root(int pid)=>Path.Combine(Environment.GetEnvironmentVariable("WANLUO_COMPONENT_CAD_ROOT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WanLuo","ComponentCadViews"),pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public string Queue()
        {
            Guid id;if(CadProcessId<=0||!Guid.TryParseExact(Id,"N",out id)||string.IsNullOrWhiteSpace(DrawingFingerprint))throw new InvalidDataException("请先从目标 CAD 图纸打开建筑模型，再生成立面。");
            var root=Root(CadProcessId);Directory.CreateDirectory(root);var file=Path.Combine(root,Id+".json");var temp=file+".tmp";
            try{using(var stream=new FileStream(temp,FileMode.CreateNew))new DataContractJsonSerializer(typeof(ComponentCadViews)).WriteObject(stream,this);File.Move(temp,file);return file;}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        public static ComponentCadViews Read(string file)
        {
            if(new FileInfo(file).Length>40*1024*1024)throw new InvalidDataException("立面数据过大，请分方向生成。");
            using(var stream=File.OpenRead(file))return (ComponentCadViews)new DataContractJsonSerializer(typeof(ComponentCadViews)).ReadObject(stream);
        }
    }
}
