using System;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

internal static class DrawingAnnotationTests
{
    public static void Run()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        var spacing=SampleModelFactory.CreateTwoStoreyHouse();
        spacing.StoreyAxes["1F"]=new System.Collections.Generic.List<AxisModel> {
            new AxisModel {Id="a",Vertical=true,Position=0},
            new AxisModel {Id="b",Vertical=true,Position=1000,EndHidden=true},
            new AxisModel {Id="c",Vertical=true,Position=2020},
            new AxisModel {Id="hidden",Vertical=true,Position=2120,Hidden=true},
            new AxisModel {Id="d",Vertical=true,Position=3030}
        };
        var tight=BuildingAxisLayout.Layout(spacing,"1F",4500,500);
        var first=tight.Single(p=>p.Axis.Id=="a");var near=tight.Single(p=>p.Axis.Id=="b");
        Check(first.StartLabel.Y-near.StartLabel.Y>0&&first.StartLabel.Y-near.StartLabel.Y<150,
            "Barely overlapping bubbles were moved by a full fixed row");
        Check(first.EndLabel.Y==tight.Single(p=>p.Axis.Id=="c").EndLabel.Y,
            "Hidden endpoint reserved spacing on its side");
        Check(first.StartLabel.Y==tight.Single(p=>p.Axis.Id=="c").StartLabel.Y&&
            first.StartLabel.Y==tight.Single(p=>p.Axis.Id=="d").StartLabel.Y,
            "Non-overlapping or hidden neighbors unnecessarily staggered bubbles");
        var model=SampleModelFactory.CreateTwoStoreyHouse();
        model.Annotations=new DrawingAnnotationSettings {TextHeight=3.5,WidthFactor=.7,AxisDiameter=10};
        model.Axes=Enumerable.Range(0,12).Select(i=>new AxisModel {Id="dense"+i,Vertical=true,Position=2000+i*100,
            ExtentStart=1000,ExtentEnd=2500,AutomaticNumber=true}).ToList();
        var layout=BuildingAxisLayout.Layout(model,"1F",4500,500);
        foreach(var p in layout.Where(p=>p.Axis.Vertical)) {
            Check(p.StartLabel.Y<=-4500&&p.EndLabel.Y>=9900,"Explicit extents left labels inside the building");
            Check(p.StartLabel.X==p.Axis.Position&&p.EndLabel.X==p.Axis.Position,"Avoidance moved an axis coordinate");
        }
        var bubbles=layout.SelectMany(p=>new[] {p.StartLabel,p.EndLabel}).ToArray();
        for(var i=0;i<bubbles.Length;i++)for(var j=i+1;j<bubbles.Length;j++)
            Check(Math.Sqrt(Math.Pow(bubbles[i].X-bubbles[j].X,2)+Math.Pow(bubbles[i].Y-bubbles[j].Y,2))>=1000,
                "Dense axis bubbles overlap");
        var view=SampleModelFactory.CreatePlanView(model.Storeys[0]);
        view.Scale=100;
        var drawing=OrthographicProjector.Project(model,view);
        Check(drawing.Circles.All(c=>Math.Abs(c.Radius-500)<.001),"Axis diameter did not follow settings");
        Check(drawing.Texts.Where(t=>t.Layer==ViewLayers.Axis).All(t=>Math.Abs(t.Height-350)<.001),"Axis text height not multiplied by scale");
        Check(drawing.Texts.Where(t=>t.Layer==ViewLayers.Opening).All(t=>Math.Abs(t.Height-350)<.001&&t.WidthFactor==.7),"Opening labels ignore text settings");
        var dimensions=drawing.Dimensions.Select(d=>Math.Abs(d.To-d.From)).OrderBy(d=>d).ToArray();
        view.Scale=50;var half=OrthographicProjector.Project(model,view);
        Check(half.Circles.All(c=>Math.Abs(c.Radius-250)<.001),"Changed scale ignored axis diameter");
        var halfDimensions=half.Dimensions.Select(d=>Math.Abs(d.To-d.From)).OrderBy(d=>d).ToArray();
        Check(dimensions.Length==halfDimensions.Length&&dimensions.Zip(halfDimensions,(a,b)=>Math.Abs(a-b)<1e-6).All(equal=>equal),"Print scale changed measured geometry");
        view.Annotations=new DrawingAnnotationSettings {TextHeight=2,WidthFactor=.8,AxisDiameter=6};
        half=OrthographicProjector.Project(model,view);
        Check(half.Annotations.TextHeight==2&&half.Texts.Where(t=>t.Layer==ViewLayers.Axis).All(t=>t.Height==100),"Per-view override ignored");
        var document=new ViewDocument {Scale=100,Annotations=model.Annotations};
        foreach(var vertical in new[] {false,true})foreach(var span in new[] {100d,199.988,3000d})
            document.Dimensions.Add(new ViewDimension {Vertical=vertical,From=10,To=10+span,LinePosition=-1000,AnchorPosition=0});
        SheetComposer.LayoutDimensionText(document);
        foreach(var d in document.Dimensions) {
            var mid=(d.From+d.To)/2;
            Check((d.Vertical?d.TextY:d.TextX)==mid,"Dimension text escaped its measured interval");
            Check(Math.Abs((d.Vertical?d.TextX.Value:d.TextY.Value)-d.LinePosition)<=d.TextHeight,"Text ran far from dimension line");
            Check(!DrawingAnnotationSettings.DimensionText(d).Contains('.'),"Automatic dimensions display decimals");
            Check(d.TextHeight==350&&d.TextWidthFactor<=.7,"Dimension typography ignores settings");
            Check(DrawingAnnotationSettings.DimensionText(d).Length*d.TextHeight*.8*d.TextWidthFactor<=Math.Abs(d.To-d.From)*.91,"Short text is wider than interval");
        }
        Check(DrawingAnnotationSettings.DimensionText(document.Dimensions[1])=="200","Integer display did not round measurements");
        var session=new BuildingModelEditSession(model);var original=BuildingModelJson.ToJson(session.Model);
        Check(!session.TryReplaceAxes(model.Axes,out _,null,new DrawingAnnotationSettings {TextHeight=10,AxisDiameter=8}),"Invalid typography was committed");
        Check(BuildingModelJson.ToJson(session.Model)==original,"Validation mutated the model");
        Check(session.TryReplaceAxes(model.Axes,out _,null,new DrawingAnnotationSettings {TextHeight=3,WidthFactor=.6,AxisDiameter=8}),"Valid settings rejected");
        Check(BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model)).Annotations.WidthFactor==.6,"Typography did not survive save/load");
        Check(session.Undo()&&BuildingModelJson.ToJson(session.Model)==original,"Typography undo did not restore model");
        var pendingModel=SampleModelFactory.CreateEmptyModel("模型内定位");
        pendingModel.Walls.Add(new WallModel {Id="host",StoreyId="1F",X2=4000,Thickness=200});
        pendingModel.CadImport=new CadModelImportState {PendingOpenings=new System.Collections.Generic.List<CadPendingOpening> {
            new CadPendingOpening {StoreyId="1F",SourceHandle="p",Code="TLM1525",Kind="门",Width=1500,Height=2500,ReferencePosition=new PointModel(1500,0)}}};
        var placementSession=new BuildingModelEditSession(pendingModel);var pendingJson=BuildingModelJson.ToJson(placementSession.Model);
        Check(!placementSession.TryPlacePendingOpening("1F","p","missing",1500,out _,out _),"Missing host accepted");
        Check(!placementSession.TryPlacePendingOpening("1F","p","host",200,out _,out _),"Opening beyond wall accepted");
        Check(BuildingModelJson.ToJson(placementSession.Model)==pendingJson,"Failed placement mutated model");
        Check(placementSession.TryPlacePendingOpening("1F","p","host",2000,out var placedId,out _),"Model placement failed");
        Check(placementSession.Model.CadImport.PendingOpenings.Count==0&&placementSession.Model.Openings.Single().Width==1500
            &&placementSession.Model.CadImport.ResolvedOpenings.Single().OpeningId==placedId,"Placement lost dimensions or identity");
        Check(BuildingVolumeBuilder.Build(placementSession.Model).Faces.Any(f=>f.ElementId==placedId),"Placed opening missing from 3D");
        Check(BuildingModelJson.FromJson(BuildingModelJson.ToJson(placementSession.Model)).CadImport.ResolvedOpenings.Count==1,"Placement mapping not saved");
        Check(placementSession.Undo()&&BuildingModelJson.ToJson(placementSession.Model)==pendingJson,"Pending placement not atomic undo");
        Console.WriteLine("PASS 注释布局：轴号外置、密集避让、真实坐标不变、字高/字宽/直径与比例、整数尺寸、界线内居中、保存撤销及无效输入");
    }
}
