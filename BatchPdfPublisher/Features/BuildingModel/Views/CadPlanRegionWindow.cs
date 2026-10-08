using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadPlanDoorDraft
    {
        public string Key,Name,Code,AssetId;
        public ComponentPlanSymbol Source,Preview;
        public double X,Y,Z,Angle,Units=1,BaseX,BaseY,Width,Height,LabelHeight=90;
        public bool FlipAlong,FlipAcross,Deleted,Changed;
        public double? OpeningAngle;
        public double WallStartX,WallStartY,WallEndX,WallEndY,WallThickness,LabelAlong,LabelNormal;
        public CadPlanDoorDraft Copy()=>(CadPlanDoorDraft)MemberwiseClone();
    }
    public sealed class CadPlanRegionWindow:Window
    {
        public IList<CadPlanDoorDraft> Doors {get;private set;}
        public IList<ComponentPlanPrimitive> BackgroundLines {get;set;}=new List<ComponentPlanPrimitive>();
        public Rect? RegionBounds {get;set;}
        public Func<IList<CadPlanDoorDraft>,IList<ComponentPlanPrimitive>> PreviewRequested;
        public Action<IList<CadPlanDoorDraft>> SubmitRequested;
        public readonly CadPlanRegionCanvas Scene;
        private readonly TextBox _width=new TextBox(),_height=new TextBox(),_code=new TextBox(),_opening=new TextBox();
        private readonly TextBlock _title=new TextBlock(),_status=new TextBlock {TextWrapping=TextWrapping.Wrap};
        private readonly Border _distanceHost=new Border {Width=198,Padding=new Thickness(8),Background=new SolidColorBrush(Color.FromRgb(30,48,65)),BorderBrush=Brushes.DeepSkyBlue,BorderThickness=new Thickness(1),Visibility=Visibility.Collapsed};
        private readonly TextBlock _distanceCaption=new TextBlock {FontSize=12,Foreground=Brushes.LightSteelBlue};
        private readonly TextBox _distance=new TextBox {Height=32,Padding=new Thickness(6),Foreground=Brushes.WhiteSmoke,Background=new SolidColorBrush(Color.FromRgb(16,25,35))};
        private bool _distanceSync,_distanceLocked,_distanceFromStart;
        private readonly Stack<IList<CadPlanDoorDraft>> _undo=new Stack<IList<CadPlanDoorDraft>>(),_redo=new Stack<IList<CadPlanDoorDraft>>();
        public CadPlanDoorDraft Selected=>Doors.FirstOrDefault(d=>d.Key==Scene.SelectedKey);
        public CadPlanRegionWindow(IList<CadPlanDoorDraft> doors)
        {
            Doors=doors;Title="CAD 区域平面编辑";Width=1180;Height=780;MinWidth=900;MinHeight=600;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(Color.FromRgb(16,25,35));Foreground=Brushes.WhiteSmoke;FontSize=14;FontFamily=new FontFamily("Microsoft YaHei UI");
            Resources.MergedDictionaries.Add(CadComponentPlanWindow.CreateTheme());
            var root=new Grid {Margin=new Thickness(16),Background=Background,Resources=Resources};
            root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            root.Children.Add(new TextBlock {Text="区域平面编辑  /  CAD",FontSize=23,Margin=new Thickness(0,0,0,14)});
            var body=new Grid();body.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(132)});body.ColumnDefinitions.Add(new ColumnDefinition());body.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(244)});
            var tools=new StackPanel {Margin=new Thickness(0,0,12,0)};tools.Children.Add(Tool("↖  选择 / 拖动",()=>Scene.Focus()));tools.Children.Add(Tool("⇆  左右换向",()=>Edit(d=>d.FlipAlong=!d.FlipAlong)));tools.Children.Add(Tool("⇅  内外换向",()=>Edit(d=>d.FlipAcross=!d.FlipAcross)));tools.Children.Add(Tool("⊙  沿墙居中",()=>Scene.Center()));tools.Children.Add(Tool("×  删除 / 恢复",()=>Edit(d=>d.Deleted=!d.Deleted)));tools.Children.Add(Tool("↶  撤销",Undo));tools.Children.Add(Tool("↷  重做",Redo));tools.Children.Add(Tool("⌗  缩放适应",()=>Scene.Fit()));
            tools.Children.Add(new TextBlock {Text="蓝点：移动位置\n黄点：开启方向\n绿点：移动编号\n拖动松手 / 再点确认\n宽度在属性里修改\n中键平移 · 滚轮缩放\n右键沿墙居中\nCtrl+Z 撤销草稿",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightSteelBlue,FontSize=12,Margin=new Thickness(0,20,0,0)});body.Children.Add(tools);
            Scene=new CadPlanRegionCanvas(this);Grid.SetColumn(Scene,1);body.Children.Add(Scene);
            var distanceLayer=new Canvas();var distanceFields=new StackPanel();distanceFields.Children.Add(_distanceCaption);distanceFields.Children.Add(_distance);_distanceHost.Child=distanceFields;distanceLayer.Children.Add(_distanceHost);Grid.SetColumn(distanceLayer,1);body.Children.Add(distanceLayer);
            _distance.TextChanged+=(_,__)=>{if(!_distanceSync&&_distanceHost.Visibility==Visibility.Visible){_distanceLocked=true;Scene.LockDistance();}};
            _distance.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Scene.CommitDistance(_distance.Text,_distanceFromStart);e.Handled=true;}else if(e.Key==Key.Escape){Scene.CancelDrag();e.Handled=true;}};
            _distanceHost.PreviewMouseRightButtonDown+=(_,e)=>{Scene.Center();e.Handled=true;};
            var fields=new StackPanel {Margin=new Thickness(16,0,0,0)};_title.FontSize=18;_title.TextWrapping=TextWrapping.Wrap;fields.Children.Add(_title);Field(fields,"编号",_code);Field(fields,"洞口宽 · mm",_width);Field(fields,"洞口高 · mm",_height);Field(fields,"开启角度 · °",_opening);fields.Children.Add(Tool("应用属性",()=>Edit(d=>{d.Width=Number(_width);d.Height=Number(_height);d.Code=_code.Text.Trim();double angle;if(!double.TryParse(_opening.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out angle)||double.IsNaN(angle)||angle<0||angle>180)throw new InvalidOperationException("开启角度应在 0～180° 之间。");var previous=d.OpeningAngle??DefaultAngle(d);if(Math.Abs(previous-angle)>.0001)d.OpeningAngle=angle;})));fields.Children.Add(new TextBlock {Text="门套中心对齐墙中线\n洞口两侧墙端自动封口\n\n编辑只修改草稿。提交成功后可在 CAD 中用 Ctrl+Z 撤销。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightSteelBlue,Margin=new Thickness(0,20,0,0)});
            var presets=new WrapPanel {Margin=new Thickness(0,6,0,0)};foreach(var angle in new[]{0,30,45,60,90,120,180}){var value=angle;var button=Tool(angle+"°",()=>{_opening.Text=value.ToString(CultureInfo.InvariantCulture);Edit(d=>d.OpeningAngle=value);});button.Width=48;button.Padding=new Thickness(0);presets.Children.Add(button);}fields.Children.Insert(fields.Children.IndexOf(_opening)+1,presets);
            var properties=new ScrollViewer {Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(properties,2);body.Children.Add(properties);Grid.SetRow(body,1);root.Children.Add(body);
            var footer=new DockPanel {Margin=new Thickness(0,12,0,0)};var actions=new StackPanel {Orientation=Orientation.Horizontal};actions.Children.Add(Tool("取消",Close));actions.Children.Add(Tool("提交到 CAD",Submit));DockPanel.SetDock(actions,Dock.Right);footer.Children.Add(actions);footer.Children.Add(_status);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
            PreviewKeyDown+=(_,e)=>{if(e.OriginalSource is TextBox)return;if(e.Key==Key.Z&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){Scene.CancelDrag();Undo();e.Handled=true;}else if(e.Key==Key.Y&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){Scene.CancelDrag();Redo();e.Handled=true;}else if(e.Key==Key.Escape){Scene.CancelDrag();e.Handled=true;}else if(e.Key==Key.Enter||e.Key==Key.Space){Scene.ConfirmDrag();e.Handled=true;}else if(e.Key==Key.M){Scene.BeginMove();e.Handled=true;}else if(e.Key==Key.Delete){Edit(d=>d.Deleted=true);e.Handled=true;}};
            Loaded+=(_,__)=>{Scene.Fit();Select(Doors.FirstOrDefault()?.Key);};
            _status.Text="框选区域已载入；单击门选择，拖动或使用夹点编辑，完成后提交到 CAD。";
        }
        public Action CenterRequested;
        public void UpdateDistance(Point? mouse)
        {
            if(!mouse.HasValue||Selected==null){_distanceHost.Visibility=Visibility.Collapsed;_distanceLocked=false;return;}
            var d=Selected;var direction=new Vector(Math.Cos(d.Angle),Math.Sin(d.Angle));var length=(new Point(d.WallEndX,d.WallEndY)-new Point(d.WallStartX,d.WallStartY)).Length*d.Units;var offset=Vector.Multiply(new Point(d.X,d.Y)-new Point(d.WallStartX,d.WallStartY),direction)*d.Units+d.Width/2;bool first;var distance=OpeningPlanGeometry.WallEndClearance(length,new OpeningModel {Width=d.Width,Offset=offset},out first);
            var started=_distanceHost.Visibility!=Visibility.Visible;_distanceHost.Visibility=Visibility.Visible;if(!_distanceLocked){_distanceFromStart=first;_distanceCaption.Text=first?"洞口边 → 起点墙端  mm":"洞口边 → 终点墙端  mm";_distanceSync=true;_distance.Text=distance.ToString("0.###",CultureInfo.InvariantCulture);_distance.SelectAll();_distanceSync=false;}
            Canvas.SetLeft(_distanceHost,Math.Max(4,Math.Min(Scene.ActualWidth-202,mouse.Value.X+18)));Canvas.SetTop(_distanceHost,Math.Max(4,Math.Min(Scene.ActualHeight-90,mouse.Value.Y+22)));if(started)_distance.Focus();
        }
        public void SetMessage(string text)=>_status.Text=text;
        public void Select(string key){Scene.SelectedKey=key;var d=Selected;_title.Text=d==null?"选择一樘门":d.Name+(d.Deleted?" · 待删除":"");_width.Text=d?.Width.ToString("0.###",CultureInfo.InvariantCulture)??"";_height.Text=d?.Height.ToString("0.###",CultureInfo.InvariantCulture)??"";_code.Text=d?.Code??"";_opening.Text=d==null?"":(d.OpeningAngle??DefaultAngle(d)).ToString("0.###",CultureInfo.InvariantCulture);Scene.InvalidateVisual();}
        private static double DefaultAngle(CadPlanDoorDraft d)=>Math.Abs(d.Source.Primitives.Where(p=>p.Kind=="Arc").Select(p=>p.SweepDegrees).DefaultIfEmpty(90).First());
        public IList<CadPlanDoorDraft> Snapshot()=>Doors.Select(d=>d.Copy()).ToArray();
        public bool Edit(Action<CadPlanDoorDraft> action,bool record=true)
        {
            if(record&&Scene.HasGesture){Scene.ConfirmDrag();if(Scene.HasGesture){SetMessage("请先确认墙端净距或取消当前夹点操作。");return false;}}
            if(Selected==null)return false;var before=Snapshot();
            try{action(Selected);Selected.Changed=true;BackgroundLines=PreviewRequested?.Invoke(Doors)??BackgroundLines;if(record){_undo.Push(before);_redo.Clear();}Select(Scene.SelectedKey);_status.Text="草稿已更新；尚未提交到 CAD。";return true;}
            catch(Exception ex){Doors=before;Select(Scene.SelectedKey);_status.Text=ex.Message;return false;}
        }
        public void CommitGesture(IList<CadPlanDoorDraft> before){_undo.Push(before);_redo.Clear();}
        public void Restore(IList<CadPlanDoorDraft> drafts){Doors=drafts.Select(d=>d.Copy()).ToArray();BackgroundLines=PreviewRequested?.Invoke(Doors)??BackgroundLines;Select(Scene.SelectedKey);}
        public void Undo(){if(Scene.HasGesture)Scene.CancelDrag();if(_undo.Count==0)return;_redo.Push(Snapshot());Restore(_undo.Pop());_status.Text="已撤销草稿。";}
        public void Redo(){if(Scene.HasGesture)Scene.CancelDrag();if(_redo.Count==0)return;_undo.Push(Snapshot());Restore(_redo.Pop());_status.Text="已重做草稿。";}
        public void RefreshPreview(){BackgroundLines=PreviewRequested?.Invoke(Doors)??BackgroundLines;Select(Scene.SelectedKey);}
        public void Submit(){try{if(Scene.HasGesture){Scene.ConfirmDrag();if(Scene.HasGesture)throw new InvalidOperationException("请先按 Enter 确认墙端净距，或 Esc 取消当前夹点操作。");}if(SubmitRequested==null)throw new InvalidOperationException("尚未连接 CAD 提交。");SubmitRequested(Doors);Close();}catch(Exception ex){_status.Text="提交未完成，CAD 保持原状："+ex.Message;}}
        private static double Number(TextBox field){double v;if(!double.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out v)||double.IsNaN(v)||double.IsInfinity(v)||v<200||v>10000)throw new InvalidOperationException("洞口宽高应在 200～10000 mm 之间。");return v;}
        private static void Field(StackPanel panel,string label,TextBox field){panel.Children.Add(new TextBlock {Text=label,Margin=new Thickness(0,20,0,6),Foreground=Brushes.LightSteelBlue});field.Height=36;field.Padding=new Thickness(8,0,8,0);field.VerticalContentAlignment=VerticalAlignment.Center;field.Background=new SolidColorBrush(Color.FromRgb(16,25,35));field.Foreground=Brushes.WhiteSmoke;panel.Children.Add(field);}
        private static Button Tool(string text,Action action){var button=new Button {Content=text,Height=38,Margin=new Thickness(0,0,6,8),Padding=new Thickness(8,0,8,0),Foreground=Brushes.WhiteSmoke,Background=new SolidColorBrush(Color.FromRgb(38,60,78))};button.Click+=(_,__)=>action();return button;}
    }
    public sealed class CadPlanRegionCanvas:FrameworkElement
    {
        private readonly CadPlanRegionWindow _owner;private double _scale=1;private Point _center;private Vector _offset;
        private Point? _mouse;private Point _dragWorld;private int _grip,_hover=-1;private IList<CadPlanDoorDraft> _before;private bool _pan,_changed,_clickMode,_fromGrip,_releasing,_distanceLocked;
        public string SelectedKey;
        public bool HasGesture=>_before!=null;
        public CadPlanRegionCanvas(CadPlanRegionWindow owner){_owner=owner;ClipToBounds=true;Focusable=true;Cursor=Cursors.Cross;}
        public Point Map(Point p)=>new Point(ActualWidth/2+(p.X-_center.X)*_scale+_offset.X,ActualHeight/2-(p.Y-_center.Y)*_scale+_offset.Y);
        public Point Unmap(Point p)=>new Point((p.X-ActualWidth/2-_offset.X)/_scale+_center.X,-(p.Y-ActualHeight/2-_offset.Y)/_scale+_center.Y);
        public void Fit(){var points=_owner.BackgroundLines.Concat(_owner.Doors.Where(d=>!d.Deleted&&d.Preview!=null).SelectMany(d=>d.Preview.Primitives)).SelectMany(Samples).ToArray();if(_owner.RegionBounds.HasValue){var bounds=_owner.RegionBounds.Value;points=new[]{bounds.TopLeft,bounds.BottomRight};}if(points.Length==0)return;_center=new Point((points.Min(p=>p.X)+points.Max(p=>p.X))/2,(points.Min(p=>p.Y)+points.Max(p=>p.Y))/2);_scale=Math.Max(1e-6,Math.Min(Math.Max(1,ActualWidth-70)/Math.Max(1,points.Max(p=>p.X)-points.Min(p=>p.X)),Math.Max(1,ActualHeight-70)/Math.Max(1,points.Max(p=>p.Y)-points.Min(p=>p.Y))));_offset=new Vector();InvalidateVisual();}
        public void ZoomAt(Point mouse,double factor){var world=Unmap(mouse);_scale=Math.Max(1e-6,Math.Min(10000,_scale*factor));_offset+=mouse-Map(world);InvalidateVisual();}
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(14,24,34)),new Pen(new SolidColorBrush(Color.FromRgb(52,76,96)),1),new Rect(RenderSize));
            foreach(var p in _owner.BackgroundLines)Draw(dc,p,new Pen(new SolidColorBrush(Color.FromRgb(153,177,196)),1.2));
            foreach(var door in _owner.Doors){if(door.Preview==null)continue;for(var i=0;i<door.Preview.Primitives.Count;i++){var role=ComponentPlanSymbols.PrimitiveRole(door.Preview,i);var pen=new Pen(door.Deleted?Brushes.DimGray:door.Key==SelectedKey?Brushes.DeepSkyBlue:role=="Frame"||role=="Casing"?Brushes.Orange:role=="OpeningSymbol"?Brushes.SlateGray:Brushes.Khaki,door.Key==SelectedKey?1.8:1.2);if(role=="OpeningSymbol"||door.Deleted)pen.DashStyle=DashStyles.Dash;Draw(dc,door.Preview.Primitives[i],pen);}}
            foreach(var door in _owner.Doors.Where(v=>!v.Deleted&&!string.IsNullOrWhiteSpace(v.Code))){var text=new FormattedText(door.Code,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),Math.Max(.1,door.LabelHeight/door.Units*_scale),Brushes.Khaki,VisualTreeHelper.GetDpi(this).PixelsPerDip);var at=Map(LabelPoint(door));dc.PushTransform(new RotateTransform(-door.Angle*180/Math.PI,at.X,at.Y));dc.DrawText(text,at-new Vector(0,text.Height));dc.Pop();}
            var d=_owner.Selected;if(d!=null&&!d.Deleted){var grips=Grips(d);dc.DrawLine(new Pen(Brushes.Gold,1){DashStyle=DashStyles.Dash},Map(grips[0]),Map(grips[1]));for(var i=0;i<3;i++){var color=i==_hover||(_before!=null&&i==_grip)?Brushes.Orange:i==0?Brushes.DeepSkyBlue:i==1?Brushes.Gold:Brushes.LightGreen;var at=Map(grips[i]);if(i==1)dc.DrawEllipse(color,new Pen(Brushes.Black,1),at,7,7);else dc.DrawRectangle(color,new Pen(Brushes.Black,1),new Rect(at-new Vector(7,7),new Size(14,14)));}if(_before!=null&&_grip==0)DrawClearance(dc,d);}
        }
        private void Draw(DrawingContext dc,ComponentPlanPrimitive p,Pen pen){var samples=Samples(p);var path=new StreamGeometry();using(var geometry=path.Open()){geometry.BeginFigure(Map(samples[0]),false,false);geometry.PolyLineTo(samples.Skip(1).Select(Map).ToArray(),true,false);}dc.DrawGeometry(null,pen,path);}
        public static Point[] Samples(ComponentPlanPrimitive p){if(p.Kind=="Line")return new[]{new Point(p.X1,p.Y1),new Point(p.X2,p.Y2)};var sweep=p.Kind=="Circle"?360:p.SweepDegrees;var count=Math.Max(8,(int)Math.Ceiling(Math.Abs(sweep)/4));return Enumerable.Range(0,count+1).Select(i=>{var a=(p.StartDegrees+sweep*i/count)*Math.PI/180;return new Point(p.X1+p.Radius*Math.Cos(a),p.Y1+p.Radius*Math.Sin(a));}).ToArray();}
        protected override void OnMouseWheel(MouseWheelEventArgs e){base.OnMouseWheel(e);ZoomAt(e.GetPosition(this),Math.Pow(1.15,e.Delta/120d));e.Handled=true;}
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);Focus();var at=e.GetPosition(this);if(e.ChangedButton==MouseButton.Middle){if(_before!=null)return;_pan=true;_mouse=at;CaptureMouse();e.Handled=true;return;}
            if(e.ChangedButton==MouseButton.Right){Center();e.Handled=true;return;}if(e.ChangedButton!=MouseButton.Left)return;if(_clickMode){ConfirmDrag();e.Handled=true;return;}
            var selected=_owner.Selected;_grip=-1;if(selected!=null&&!selected.Deleted){var grips=Grips(selected);for(var i=0;i<grips.Length;i++)if((Map(grips[i])-at).Length<=12)_grip=i;}
            _fromGrip=_grip>=0;if(_grip<0){var hit=_owner.Doors.Where(d=>!d.Deleted&&d.Preview!=null).Select(d=>new {D=d,Distance=d.Preview.Primitives.SelectMany(p=>{var s=Samples(p).Select(Map).ToArray();return Enumerable.Range(1,s.Length-1).Select(i=>Distance(at,s[i-1],s[i]));}).DefaultIfEmpty(double.MaxValue).Min()}).OrderBy(v=>v.Distance).FirstOrDefault();if(hit==null||hit.Distance>14){_owner.Select(null);return;}_owner.Select(hit.D.Key);e.Handled=true;return;}
            BeginGrip(_grip,Unmap(at));_clickMode=false;CaptureMouse();e.Handled=true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);var at=e.GetPosition(this);if(!_mouse.HasValue){_hover=-1;var selected=_owner.Selected;if(selected!=null&&!selected.Deleted){var grips=Grips(selected);for(var i=0;i<grips.Length;i++)if((Map(grips[i])-at).Length<=12)_hover=i;}InvalidateVisual();return;}if(_pan){_offset+=at-_mouse.Value;_mouse=at;InvalidateVisual();return;}var d=_owner.Selected;if(d==null)return;var original=_before.First(v=>v.Key==d.Key);var world=Unmap(at);var delta=world-_dragWorld;var direction=new Vector(Math.Cos(original.Angle),Math.Sin(original.Angle));
            if(_distanceLocked||(at-_mouse.Value).Length<3&&!_changed)return;
            MoveGrip(world);
        }
        protected override void OnMouseUp(MouseButtonEventArgs e){base.OnMouseUp(e);if(!_mouse.HasValue)return;if(!_pan&&!_changed&&_fromGrip){_clickMode=true;ReleaseCapture();e.Handled=true;return;}if(_distanceLocked)return;ConfirmDrag();e.Handled=true;}
        private void ReleaseCapture(){_releasing=true;try{ReleaseMouseCapture();}finally{_releasing=false;}}
        public void ConfirmDrag(){if(_distanceLocked)return;if(_before!=null&&_changed)_owner.CommitGesture(_before);_before=null;_mouse=null;_pan=false;_clickMode=false;_owner.UpdateDistance(null);ReleaseCapture();Focus();InvalidateVisual();}
        public void Center(){if(_before!=null&&_grip!=0)return;_distanceLocked=false;_owner.CenterRequested?.Invoke();if(_before!=null){_changed=true;ConfirmDrag();}}
        public void BeginMove(){BeginGrip(0);}
        public void BeginGrip(int grip,Point? world=null){var d=_owner.Selected;if(d==null||d.Deleted||grip<0||grip>2)return;CancelDrag();d=_owner.Selected;_before=_owner.Snapshot();_grip=grip;_dragWorld=world??Grips(d)[grip];_mouse=Map(_dragWorld);_changed=false;_clickMode=true;_fromGrip=true;_distanceLocked=false;if(grip==0)_owner.UpdateDistance(_mouse);InvalidateVisual();}
        public void MoveGrip(Point world){if(_before==null||_distanceLocked||_owner.Selected==null)return;var original=_before.First(v=>v.Key==_owner.Selected.Key);_changed|=UpdateGrip(world,original);if(_grip==0)_owner.UpdateDistance(Map(world));InvalidateVisual();}
        public void CancelDrag(){if(_before!=null)_owner.Restore(_before);_before=null;_mouse=null;_pan=false;_clickMode=false;_distanceLocked=false;_owner.UpdateDistance(null);ReleaseCapture();Focus();InvalidateVisual();}
        public void LockDistance(){if(_before!=null&&_grip==0)_distanceLocked=true;}
        public bool CommitDistance(string text,bool fromStart)
        {
            var d=_owner.Selected;if(d==null||_before==null||_grip!=0)return false;double distance;var length=(new Point(d.WallEndX,d.WallEndY)-new Point(d.WallStartX,d.WallStartY)).Length*d.Units;if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out distance)||double.IsNaN(distance)||double.IsInfinity(distance)||distance<0||distance>length-d.Width){_owner.SetMessage("净距须不小于 0 且不超出墙长。");return false;}
            var start=OpeningPlanGeometry.OffsetFromWallEnd(length,d.Width,distance,fromStart)-d.Width/2;var valid=_owner.Edit(value=>{value.X=d.WallStartX+Math.Cos(d.Angle)*start/d.Units;value.Y=d.WallStartY+Math.Sin(d.Angle)*start/d.Units;},false);if(valid){_changed=true;_distanceLocked=false;ConfirmDrag();}return valid;
        }
        public bool UpdateGrip(Point world,CadPlanDoorDraft original)
        {
            var delta=world-_dragWorld;var direction=new Vector(Math.Cos(original.Angle),Math.Sin(original.Angle));return _owner.Edit(value=>{if(_grip==1){var grips=Grips(original);var reference=grips[1]-grips[0];var target=world-grips[0];var normal=new Vector(-direction.Y,direction.X);var a=Vector.Multiply(target,direction);var b=Vector.Multiply(target,normal);if(Math.Abs(a)>4/_scale)value.FlipAlong=original.FlipAlong^((a<0)!=(Vector.Multiply(reference,direction)<0));if(Math.Abs(b)>4/_scale)value.FlipAcross=original.FlipAcross^((b<0)!=(Vector.Multiply(reference,normal)<0));}else if(_grip==2){value.LabelAlong=original.LabelAlong+Vector.Multiply(delta,direction)*original.Units;value.LabelNormal=original.LabelNormal+Vector.Multiply(delta,new Vector(-direction.Y,direction.X))*original.Units;}else {value.X=original.X+delta.X;value.Y=original.Y+delta.Y;}},false);
        }
        public static Point LabelPoint(CadPlanDoorDraft d){var normal=-d.WallThickness/2-140/d.Units+d.LabelNormal/d.Units;var minimum=d.WallThickness/2+70/d.Units;if(Math.Abs(normal)<minimum)normal=(normal<0?-1:1)*minimum;return new Point(d.X,d.Y)+new Vector(Math.Cos(d.Angle),Math.Sin(d.Angle))*((d.Width/2+d.LabelAlong)/d.Units)+new Vector(-Math.Sin(d.Angle),Math.Cos(d.Angle))*normal;}
        public static Point[] Grips(CadPlanDoorDraft d){var position=new Point(d.X,d.Y)+new Vector(Math.Cos(d.Angle),Math.Sin(d.Angle))*(d.Width/d.Units/2);var handle=ComponentPlanSymbols.DirectionHandle(d.Preview);var target=handle==null?position+new Vector(-Math.Sin(d.Angle),Math.Cos(d.Angle))*(d.Width/d.Units*.9):new Point(handle.X,handle.Y);return new[]{position,target,LabelPoint(d)};}
        private void DrawClearance(DrawingContext dc,CadPlanDoorDraft d){var direction=new Vector(Math.Cos(d.Angle),Math.Sin(d.Angle));var length=(new Point(d.WallEndX,d.WallEndY)-new Point(d.WallStartX,d.WallStartY)).Length*d.Units;var offset=Vector.Multiply(new Point(d.X,d.Y)-new Point(d.WallStartX,d.WallStartY),direction)*d.Units+d.Width/2;bool first;OpeningPlanGeometry.WallEndClearance(length,new OpeningModel {Offset=offset,Width=d.Width},out first);var end=Map(first?new Point(d.WallStartX,d.WallStartY):new Point(d.WallEndX,d.WallEndY));var jamb=Map(new Point(d.X,d.Y)+(first?new Vector():direction*(d.Width/d.Units)));var shift=new Vector(Math.Sin(d.Angle),Math.Cos(d.Angle))*(d.WallThickness*_scale/2+22);var pen=new Pen(Brushes.Cyan,1);dc.DrawLine(pen,end,end+shift);dc.DrawLine(pen,jamb,jamb+shift);dc.DrawLine(pen,end+shift,jamb+shift);}
        protected override void OnLostMouseCapture(MouseEventArgs e){base.OnLostMouseCapture(e);if(_before!=null&&!_releasing)CancelDrag();}
        private static double Distance(Point p,Point a,Point b){var v=b-a;var t=v.LengthSquared<1e-12?0:Math.Max(0,Math.Min(1,Vector.Multiply(p-a,v)/v.LengthSquared));return (p-(a+v*t)).Length;}
    }
}
