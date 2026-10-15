using Avalonia;
using Avalonia.Input;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class OpeningEditorWindow
{
    internal async Task RunRoundedFitApplyCheckAsync(bool reject,bool compact=false)
    {
        if(compact){Width=1120;Height=720;}
        await Task.Delay(180);_timer.Stop();await PreviewAsync();
        if(_opening.Width!=3768||_fields["width"].Text!="3768")throw new InvalidOperationException("MLC04B 打开后仍把 3768 强制放大到 3770。");
        _canvas.Selected.Clear();_canvas.Selected.UnionWith(Enumerable.Range(0,_canvas.State.Cells.Count).Where(i=>_canvas.State.Cells[i].Bottom==2200));
        _canvas.Notify(false);_openingMode.SelectedItem="上悬";
        _fields["width"].Text=reject?"3800":"3770";_timer.Stop();await PreviewAsync();await Task.Delay(150);
        if(!reject&&(_opening.Width!=3768||_fields["width"].Text!="3768"||!_status.Text!.Contains("自动适配")))
            throw new InvalidOperationException("取整误差未动态适配并明确显示实际尺寸。");
        await SaveCheckImage(reject?"rounded-fit-rejected.png":"rounded-fit-mlc04b.png");
        this.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b=>b.Name=="ApplyOpeningEditor")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await _applyTask;
        if(reject)
        {
            if(_closed||!_status.Text!.Contains("未应用")||!_status.Text.Contains("超"))
                throw new InvalidOperationException("真实超限没有明确提示未应用并保留窗口。");
            Close();
        }
        else if(!_closed)throw new InvalidOperationException("MLC04B 取整适配后应用没有关闭："+_status.Text);
    }
    internal async Task RunDefaultConstructionCheckAsync(bool combined)
    {
        await Task.Delay(200);_timer.Stop();await PreviewAsync();await Task.Delay(150);
        if(_sash.IsChecked!=true||_draft.SashWidth!=50)throw new InvalidOperationException("默认窗／MLC 的扇框开关未选中。");
        if(combined)
        {
            var doors=_canvas.State.Cells.Where(c=>c.IsDoor).OrderBy(c=>c.Left).ToArray();
            if(doors.Length!=2||doors.Any(c=>c.Top!=2200||c.Right-c.Left!=750)
                ||Math.Abs(doors[0].Left-(_opening.Width-1500)/2)>.01||doors[0].Opening!="左平开"||doors[1].Opening!="右平开")
                throw new InvalidOperationException("MLC 窗口默认分格未显示 1500×2200 居中双开扇。");
        }
        _inspectorTabs.SelectedIndex=1;await Task.Delay(120);
        await SaveCheckImage(combined?"default-mlc-construction.png":"default-window-construction.png");
        _inspectorTabs.SelectedIndex=0;await Task.Delay(120);
        await SaveCheckImage(combined?"default-mlc-layout.png":"default-window-layout.png");
        if(combined)
        {
            var indices=Enumerable.Range(0,_canvas.State.Cells.Count).Where(i=>_canvas.State.Cells[i].IsDoor).OrderBy(i=>_canvas.State.Cells[i].Left).ToArray();
            var before=DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells);
            for(var i=0;i<indices.Length;i++)
            {
                _canvas.Selected.Clear();_canvas.Selected.Add(indices[i]);_canvas.Notify(false);
                _openingMode.SelectedItem=i==0?"右平开":"左平开";
                _timer.Stop();await PreviewAsync();await Task.Delay(120);
            }
            var geometry=DoorWindowElevationGeometryBuilder.Build(OpeningElevationAdapter.ToScheduleItem(_opening,_draft,_opening.Width,_opening.Height));
            var leaves=geometry.Cells.Where(c=>c.IsDoor).OrderBy(c=>c.Left).ToArray();
            if(leaves.Length!=2||leaves[0].Opening!="右平开"||leaves[1].Opening!="左平开")
                throw new InvalidOperationException("用户手动设置的相对开启尖头被默认规则覆盖。");
            await SaveCheckImage("manual-mlc-facing-tips.png");
            for(var i=0;i<2;i++)
            {
                RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Z,KeyModifiers=KeyModifiers.Control});
                _timer.Stop();await PreviewAsync();await Task.Delay(120);
            }
            if(DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells)!=before)
                throw new InvalidOperationException("用户手动方向修改无法逐步撤销。");
            Console.WriteLine("OPENING_MANUAL_HINGES_UI_OK facingTipsAllowed propertyChanges previewUnchangedDirections CtrlZ defaultStillExterior");
        }
        Console.WriteLine(combined?"OPENING_DEFAULT_MLC_UI_OK centered1500 height2200 exteriorHinges sashChecked real2D3D":"OPENING_DEFAULT_WINDOW_UI_OK sashChecked exteriorHinges real2D3D");
        Close();
    }
    internal async Task RunCollectionApplyCheckAsync(double height,bool single=false,bool reject=false)
    {
        await Task.Delay(150);_scope.SelectedIndex=single?1:0;_fields["height"].Text=height.ToString(CultureInfo.InvariantCulture);
        _fields["glass"].Text="12";_fields["frameDepth"].Text="130";
        _timer.Stop();await PreviewAsync();
        this.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b=>b.Name=="ApplyOpeningEditor")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await _applyTask;
        if(reject){if(_closed||!_status.Text!.Contains("超"))throw new InvalidOperationException("无效集合合并未保留编辑器或错误提示："+_status.Text);Close();}
        else if(!_closed)throw new InvalidOperationException("集合分格应用失败："+_status.Text);
    }
    internal async Task RunInteractionCheckAsync()
    {
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        async Task Flush(){_timer.Stop();await PreviewAsync();await Task.Delay(150);}
        void PressKey(Key key,KeyModifiers modifiers=KeyModifiers.None)=>RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=key,KeyModifiers=modifiers});
        var pointer=new Pointer(896,PointerType.Mouse,true);
        void Click(Point p,KeyModifiers modifiers=KeyModifiers.None){var root=_canvas.TranslatePoint(p,this)!.Value;
            _canvas.RaiseEvent(new PointerPressedEventArgs(_canvas,pointer,this,root,0,new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),modifiers,1));
            _canvas.RaiseEvent(new PointerReleasedEventArgs(_canvas,pointer,this,root,1,new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),modifiers,MouseButton.Left));}
        void Hover(Point p){var root=_canvas.TranslatePoint(p,this)!.Value;
            _canvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_canvas,pointer,this,root,0,new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));}
        await Task.Delay(200);await Flush();
        var width=_opening.Width;Check(width==7600,"毫米洞口未按 10 mm 归整。");
        _fields["width"].Text="7499";await Flush();Check(_opening.Width==7500,"输入毫米尺寸没有动态归整。");PressKey(Key.Z,KeyModifiers.Control);await Flush();
        Check(_opening.Width==7600,"归整尺寸撤销不完整。");
        Check(!OnlyInstance,"分格设计没有默认同编号集合。");
        var initial=DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells);
        Check(_canvas.State.Split(new[]{0},true,3,out var error),error??"Split failed");_canvas.Notify();await Flush();
        Check(_canvas.State.Cells.Count==3,"竖向等分失败。");PressKey(Key.Z,KeyModifiers.Control);await Flush();
        Check(DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells)==initial,"Ctrl+Z 未撤销分格。");
        PressKey(Key.Y,KeyModifiers.Control);await Flush();Check(_canvas.State.Cells.Count==3,"Ctrl+Y 未恢复分格。");
        var from=_canvas.ModelToScreen(-200,_canvas.State.Height+200);var to=_canvas.ModelToScreen(_canvas.State.Width+200,-200);
        Click(from);Check(_canvas.HasSelectionBox,"空白点没有开始两点框选。");Hover(to);
        await SaveCheckImage("selection-box.png");Click(to);Check(_canvas.Selected.Count==3&&!_canvas.HasSelectionBox,"松开鼠标后两点框选未选中全部分格。");
        _openingMode.SelectedItem="右平开";await Flush();Check(_canvas.State.Cells.All(c=>c.Opening=="右平开"),"框选后批量开启方式没有生效。");
        PressKey(Key.Z,KeyModifiers.Control);await Flush();Check(_canvas.State.Cells.Any(c=>c.Opening!="右平开"),"批量属性未撤销。");
        PressKey(Key.Z,KeyModifiers.Control|KeyModifiers.Shift);await Flush();Check(_canvas.State.Cells.All(c=>c.Opening=="右平开"),"Ctrl+Shift+Z 未重做。");
        var c=_canvas.State.Cells[0];Click(_canvas.ModelToScreen((c.Left+c.Right)/2,(c.Bottom+c.Top)/2));
        var oldWidth=c.Right-c.Left;_fields["cellWidth"].Text=(oldWidth-100).ToString("R",CultureInfo.InvariantCulture);await Flush();
        Check(Math.Abs(_canvas.State.Cells[0].Right-_canvas.State.Cells[0].Left-(oldWidth-100))<.01,"格宽输入未动态修改。");
        PressKey(Key.Z,KeyModifiers.Control);await Flush();Check(Math.Abs(_canvas.State.Cells[0].Right-_canvas.State.Cells[0].Left-oldWidth)<.01,"格宽动态修改不能撤销。");
        var depth=_draft.FrameDepth;_fields["frameDepth"].Text="130";await Flush();Check(_draft.FrameDepth==130,"框料参数未动态修改。");
        PressKey(Key.Z,KeyModifiers.Control);await Flush();Check(_draft.FrameDepth==depth,"框料参数不能撤销。");
        var height=_opening.Height;_fields["height"].Text="2900";await Flush();Check(_opening.Height==2900,"整体尺寸未动态修改。");
        PressKey(Key.Z,KeyModifiers.Control);await Flush();Check(_opening.Height==height,"整体尺寸撤销不完整。");
        _fields["cellWidth"].Text="abc";await Flush();Check(_canvas.State.Cells[0].Right-_canvas.State.Cells[0].Left==oldWidth,"非法格宽改变了有效分格。");
        PressKey(Key.Z,KeyModifiers.Control);await Flush();Check(_fields["cellWidth"].Text!="abc","非法输入未恢复有效值。");
        Click(from);PressKey(Key.Escape);Check(!_closed&&!_canvas.HasSelectionBox,"Esc 取消框选却关闭编辑器。");
        // A narrow right-to-left rectangle crosses one cell without containing it.
        Click(_canvas.ModelToScreen(_canvas.State.Width+200,_canvas.State.Height+200));
        Click(_canvas.ModelToScreen(_canvas.State.Width-100,100));Check(_canvas.Selected.Count==1,"交叉框选没有选择相交的格。");
        await SaveCheckImage("interaction-editor.png");
        Check(_canvas.State.Cells.All(c=>new[]{c.Left,c.Right,c.Bottom,c.Top}.All(v=>v==Math.Round(v))),"分格仍存储小数尺寸。");
        await CheckMultiRowCenterAsync();
        Console.WriteLine("OPENING_EDITOR_INTERACTION_OK openingRound10 wholeMillimetreCells groupDefault CtrlZ CtrlY ShiftRedo wholeDraftUndo liveCellSize liveFrame liveOpening invalidPreserved twoClickWindow crossing batchProperties boxEscape");
        Close();
    }
    private async Task CheckMultiRowCenterAsync()
    {
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        async Task Flush(){_timer.Stop();await PreviewAsync();await Task.Delay(120);}
        _fields["width"].Text="7660";_fields["height"].Text="3000";await Flush();
        var cells=new List<DoorWindowLayoutCell>{new(){Right=1915,Top=3000,Material="玻璃",Opening="固定"}};
        for(var column=0;column<4;column++)foreach(var row in new[]{0,1})cells.Add(new(){
            Left=1915+column*750,Right=1915+(column+1)*750,Bottom=row==0?0:2200,Top=row==0?2200:3000,
            Material="玻璃",Opening="固定",IsDoor=row==0});
        cells.Add(new(){Left=4915,Right=7660,Top=3000,Material="玻璃",Opening="固定"});
        _canvas.State=new(7660,3000,cells);_canvas.Selected.Clear();_canvas.Selected.UnionWith(Enumerable.Range(1,8));_canvas.Notify();await Flush();
        var before=DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells);
        await SaveCheckImage("center-multirow-before.png");
        var button=this.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b=>b.Name=="CenterOpeningLayout");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));await Flush();
        Check(_canvas.Selected.Count==8&&_canvas.State.Cells[0].Right==2330&&_canvas.State.Cells[9].Left==5330,"八格工具栏居中未整体移动。");
        Check(_canvas.State.Cells.Skip(1).Take(8).All(c=>c.Right-c.Left==750&&(c.Top-c.Bottom==800||c.Top-c.Bottom==2200)),"工具栏居中改变了内部尺寸。");
        await SaveCheckImage("center-multirow-after.png");
        var after=DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells);
        RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Z,KeyModifiers=KeyModifiers.Control});await Flush();
        Check(DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells)==before,"八格居中无法一步撤销。");
        RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Y,KeyModifiers=KeyModifiers.Control});await Flush();
        Check(DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells)==after,"八格居中无法重做。");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));await Flush();
        RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=Key.Z,KeyModifiers=KeyModifiers.Control});await Flush();
        Check(DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells)==before,"重复居中产生了多余撤销。");
        Console.WriteLine("OPENING_MULTIROW_CENTER_UI_OK screenshotEightCells rigidWidths rigidHeights equalSides toolbar oneUndo redo noOp preview");
    }
}
