using Avalonia.Controls;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunStructureCheckAsync()
    {
        void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        var old=BuildingModelJson.FromJson("{\"SchemaVersion\":1,\"Name\":\"旧模型\"}");
        Require(old.Beams!=null&&old.Beams.Count==0,"旧模型未初始化梁集合。");
        _session=new BuildingModelEditSession(new BuildingModelDocument {Name="梁柱验证",Storeys=new(){
            new StoreyModel {Id="1F",Name="一层",Height=3000},
            new StoreyModel {Id="2F",Name="二层",Elevation=3000,Height=3600,TemplateStoreyId="1F"}}});
        await RefreshModelAsync("梁柱检查");
        BeginStructureTool(PlanTool.Column);Require(_planCanvas.CreateColumn(new PointModel(1000,1000)),"柱工具未创建构件。");
        var column=_session.Model.Columns.Single();
        Require(PlanEditing.Snap(_session.Model,"1F",1001,1001,20,false,0,0).Kind=="柱中心","梁柱未支持柱中心捕捉。");
        BeginStructureTool(PlanTool.Beam);Require(_planCanvas.CreateBeam(new PointModel(1000,1000),new PointModel(5000,1000)),"梁工具未创建构件。");
        var beam=_session.Model.Beams.Single();
        var expanded=StandardStoreyLayout.Materialize(_session.Model);
        Require(expanded.Beams.Count==2&&expanded.Columns.Count==2,"标准层没有实例化梁柱。");
        var volume=BuildingVolumeBuilder.Build(_session.Model);
        Require(Math.Abs(volume.Faces.Where(f=>f.ElementId==beam.Id).SelectMany(f=>f.Points).Min(p=>p.Z)-2500)<.001,"梁底标高错误。");
        Require(Math.Abs(volume.Faces.Where(f=>f.StoreyId=="2F"&&f.Kind=="beam").SelectMany(f=>f.Points).Max(p=>p.Z)-6600)<.001,"标准层梁顶没有使用实际层高。");
        var roundtrip=BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model));
        Require(roundtrip.Beams.Single().Depth==500&&roundtrip.Columns.Single().Width==400,"保存/重读梁柱参数丢失。");
        Require(!_session.TryUpsertBeam(new BeamModel {StoreyId="1F",X1=0,Y1=0,X2=0,Y2=0},out _,out _),"零长度梁被接受。");
        var rect=new List<PointModel>{new(0,0),new(4000,0),new(4000,300),new(0,300)};
        var segments=Enumerable.Range(0,4).Select(i=>new CadProbeSegment {Start=new CadProbePoint {X=rect[i].X,Y=rect[i].Y},
            End=new CadProbePoint {X=rect[(i+1)%4].X,Y=rect[(i+1)%4].Y}}).Reverse().ToArray();
        Require(CadStructuralRegistration.FindRectangle(segments).Count==4,"CAD 无序边线未还原轮廓。");
        Require(!CadStructuralRegistration.TryRectangle(new List<PointModel>{new(0,0),new(400,300),new(0,300),new(400,0)},out _,out _),"交叉轮廓被接受。");
        var context=new CadFloorRegistrationContext {ModelPath=System.IO.Path.GetFullPath(".artifacts/structure-test-model.json"),Storey=_session.Model.Storeys[0],
            Alignment=new CadFloorRegistrationAlignment {StoreyId="1F",CadBase=new(0,0),ModelBase=new(100,200),MillimetresPerCadUnit=2},DirectionPoint=new(1,0),
            RegionMin=new(0,0),RegionMax=new(5000,5000),RegionPolygon=new(){new(0,0),new(5000,0),new(5000,5000),new(0,5000)}};
        var capture=new CadFloorPlanCapture {Floor=context,Probe=new CadBuildingProbeDocument {DrawingFingerprint="structure-test",Entities=new(){
            new CadBuildingProbeEntity {Handle="A",DxfName="TCH_BEAM",StructuralOutline=rect},
            new CadBuildingProbeEntity {Handle="B",DxfName="TCH_COLUMN",StructuralOutline=new(){new(0,0),new(400,0),new(400,600),new(0,600)}}}}};
        var registry=new CadFloorPlanRegistry {Floors=new(){capture}};
        var imported=CadFloorModelGeneration.Build(new BuildingModelDocument {Storeys=_session.Model.Storeys},registry);
        Require(imported.Beams.Single().Width==600&&imported.Columns.Single().Width==800,"CAD 单位换算错误。");
        Require(imported.Columns.Single().X==500&&imported.Columns.Single().Y==800,"CAD 定位基点未应用。");
        var importedAgain=CadFloorModelGeneration.Build(imported,registry);
        Require(importedAgain.Beams.Count==1&&importedAgain.Columns.Count==1,"重新登记重复梁柱。");
        imported.Columns[0].Width+=100;
        var protectedEdit=false;try{CadFloorModelGeneration.Build(imported,registry);}catch(System.IO.InvalidDataException){protectedEdit=true;}
        Require(protectedEdit,"重新登记覆盖了手动柱修改。");
        await RefreshModelAsync("梁柱已创建");
        SelectById(beam.Id);_showSlabProperties?.Invoke();
        var depthField=_properties.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).ToArray()[5];
        depthField.Text="650";
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.Background);
        await _parameterPreviewTask;
        Require(_session.Model.Beams.Single().Depth==500,"预览提前写入模型。");
        Require(Math.Abs(_viewport.CurrentScene.Volume.Faces.Where(f=>f.ElementId==beam.Id).SelectMany(f=>f.Points).Min(p=>p.Z)-2350)<.001,"梁参数没有动态预览。");
        _properties.Children.OfType<Button>().Single(b=>Equals(b.Content,"应用梁参数")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(600);
        Require(_session.Model.Beams.Single().Depth==650,"属性按钮没有应用梁截面高："+_session.Model.Beams.Single().Depth+" / "+_status.Text);
        Require(_session.Undo()&&_session.Model.Beams.Single().Depth==500,"梁编辑不能撤销。");
        Require(_session.Redo()&&_session.Model.Beams.Single().Depth==650,"梁编辑不能重做。");
        Require(_session.TryTransformStructure(beam.Id,100,200,true,out var copiedId,out _),"梁不能复制。");
        var copied=_session.Model.Beams.Single(b=>b.Id==copiedId);
        Require(copied.X1==1100&&copied.Y1==1200&&copied.Depth==650,"复制没有保留梁截面或位移错误。");
        _session.Undo();
        Require(_session.TryDeleteElement(beam.Id,out _)&&_session.Model.Beams.Count==0,"不能删除梁。");
        _session.Undo();await RefreshModelAsync("梁柱验证通过");
        _workspaces.SelectedIndex=0;_viewport.ResetView();SelectById(beam.Id);_viewport.FrameSelection();await Task.Delay(800);
        var visual=Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(this)!;
        var image=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
        image.Save(System.IO.Path.GetFullPath(".artifacts/opening-editor/structure-ui.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Console.WriteLine("STRUCTURE_OK creation snap dynamicPreview propertyApply undoRedo copy delete serialization oldModel standardElevation cadRectangle cadScale cadDatum repeatImport protectedManualEdit");
    }
    private double _structureWidth=400,_structureDepth=400,_structureHeight,_structureOffset;
    private bool _structureInputValid=true;
    private void BeginStructureTool(PlanTool tool)
    {
        _workspaces.SelectedIndex=1;_showSlabProperties?.Invoke();
        SetPlanTool(tool);_properties.Children.Clear();
        _structureWidth=tool==PlanTool.Beam?300:400;_structureDepth=tool==PlanTool.Beam?500:400;
        _structureHeight=_structureOffset=0;_structureInputValid=true;
        _properties.Children.Add(new TextBlock {Text=tool==PlanTool.Beam?"绘制梁 · 两点指定轴线":"绘制柱 · 点击中心",FontWeight=FontWeight.Bold});
        var width=AddNumberField(tool==PlanTool.Beam?"梁宽 mm":"柱宽 mm",_structureWidth);
        var depth=AddNumberField(tool==PlanTool.Beam?"截面高 mm":"柱深 mm",_structureDepth);
        var height=AddNumberField(tool==PlanTool.Beam?"梁顶相对层顶偏移 mm":"柱高 mm（0 随层高）",0);
        var offset=tool==PlanTool.Column?AddNumberField("柱底相对层底偏移 mm",0):null;
        void Update()
        {
            _structureInputValid=(TryNumber(width.Text,out var w)&TryNumber(depth.Text,out var d)&TryNumber(height.Text,out var h))
                &&w>.5&&d>.5&&(tool==PlanTool.Beam||h>=0);
            if(!_structureInputValid)return;
            _structureWidth=w;_structureDepth=d;
            if(tool==PlanTool.Beam)_structureOffset=h;else {_structureHeight=h;_structureInputValid=TryNumber(offset!.Text,out _structureOffset);}
        }
        width.TextChanged+=(_,_)=>Update();depth.TextChanged+=(_,_)=>Update();height.TextChanged+=(_,_)=>Update();
        if(offset!=null)offset.TextChanged+=(_,_)=>Update();
        _status.Text=tool==PlanTool.Beam?"绘制梁：指定起点、终点；梁顶默认随层顶，Esc 退出。":"绘制柱：点击放置中心，柱高默认随层高，Esc 退出。";
    }
    private void WireStructureTools()
    {
        _planCanvas.ColumnRequested+=p=>
        {
            if(!_structureInputValid){_status.Text="梁柱参数无效，请修改右侧参数。";return false;}
            if(!_session.TryUpsertColumn(new ColumnModel {StoreyId=(_storeyChooser.SelectedItem as StoreyItem)?.Id??"1F",X=p.X,Y=p.Y,
                Width=_structureWidth,Depth=_structureDepth,Height=_structureHeight,BaseOffset=_structureOffset},out var id,out var error))
            {_status.Text=error;return false;}
            _selectedId=id;_ = RefreshModelAsync("已创建柱（可撤销）");return true;
        };
        _planCanvas.BeamRequested+=(a,b)=>
        {
            if(!_structureInputValid){_status.Text="梁柱参数无效，请修改右侧参数。";return false;}
            if(!_session.TryUpsertBeam(new BeamModel {StoreyId=(_storeyChooser.SelectedItem as StoreyItem)?.Id??"1F",X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y,
                Width=_structureWidth,Depth=_structureDepth,TopOffset=_structureOffset},out var id,out var error))
            {_status.Text=error;return false;}
            _selectedId=id;_ = RefreshModelAsync("已创建梁（可撤销）");return true;
        };
    }
    private bool BuildStructureProperties()
    {
        var column=_session.Model.Columns.FirstOrDefault(c=>c.Id==_selectedId);
        var beam=_session.Model.Beams.FirstOrDefault(b=>b.Id==_selectedId);
        if(column==null&&beam==null)return false;
        _properties.Children.Add(new TextBlock {Text=column!=null?"柱 "+column.Code:"梁 "+beam!.Code,FontWeight=FontWeight.Bold});
        if(column!=null)
        {
            var fields=new[] {AddNumberField("中心 X mm",column.X),AddNumberField("中心 Y mm",column.Y),
                AddNumberField("柱宽 mm",column.Width),AddNumberField("柱深 mm",column.Depth),AddNumberField("柱高 mm（0 随层高）",column.Height),
                AddNumberField("旋转角度 °",column.RotationDegrees),AddNumberField("柱底偏移 mm",column.BaseOffset),AddNumberField("柱顶偏移 mm",column.TopOffset)};
            ColumnModel Draft(double[] v)=>new() {Id=column.Id,Code=column.Code,StoreyId=column.StoreyId,X=v[0],Y=v[1],Width=v[2],Depth=v[3],
                Height=v[4],RotationDegrees=v[5],BaseOffset=v[6],TopOffset=v[7]};
            var apply=InspectorButton("应用柱参数");_properties.Children.Add(apply);
            apply.Click+=async(_,_)=>await ApplyGeometryAsync(fields,v=>{var ok=_session.TryUpsertColumn(Draft(v),out _,out var error);return(ok,error);});
            BindParameterPreview(fields,(trial,v)=>trial.TryUpsertColumn(Draft(v),out _,out _));
        }
        else
        {
            var b=beam!;var fields=new[] {AddNumberField("起点 X mm",b.X1),AddNumberField("起点 Y mm",b.Y1),AddNumberField("终点 X mm",b.X2),
                AddNumberField("终点 Y mm",b.Y2),AddNumberField("梁宽 mm",b.Width),AddNumberField("截面高 mm",b.Depth),AddNumberField("梁顶相对层顶偏移 mm",b.TopOffset)};
            BeamModel Draft(double[] v)=>new() {Id=b.Id,Code=b.Code,StoreyId=b.StoreyId,X1=v[0],Y1=v[1],X2=v[2],Y2=v[3],Width=v[4],Depth=v[5],TopOffset=v[6]};
            var apply=InspectorButton("应用梁参数");_properties.Children.Add(apply);
            apply.Click+=async(_,_)=>await ApplyGeometryAsync(fields,v=>{var ok=_session.TryUpsertBeam(Draft(v),out _,out var error);return(ok,error);});
            BindParameterPreview(fields,(trial,v)=>trial.TryUpsertBeam(Draft(v),out _,out _));
        }
        return true;
    }
}
