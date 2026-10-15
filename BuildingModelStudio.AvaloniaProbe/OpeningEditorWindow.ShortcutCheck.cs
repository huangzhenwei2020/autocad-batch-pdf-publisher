using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BatchPdfPublisher.Models;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class OpeningEditorWindow
{
    internal async Task RunShortcutCheckAsync()
    {
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        void KeyPress(Key key,KeyModifiers modifiers=KeyModifiers.None,Control? source=null)
            =>(source??this).RaiseEvent(new KeyEventArgs {RoutedEvent=KeyDownEvent,Key=key,KeyModifiers=modifiers});
        string Layout()=>DoorWindowElevationGeometryBuilder.SerializeCellLayout(_canvas.State.Cells);
        void Select(params int[] indices){_canvas.Selected.Clear();_canvas.Selected.UnionWith(indices);_canvas.Notify(false);}
        void Fixture(bool rows=false)
        {
            var cells=new List<DoorWindowLayoutCell>();var edges=new[]{0d,600,1800,3000};
            for(var i=0;i<3;i++)cells.Add(new(){Left=rows?0:edges[i],Right=rows?3000:edges[i+1],
                Bottom=rows?edges[i]:0,Top=rows?edges[i+1]:3000,Material=i==1?"实板":"玻璃",Opening="固定",IsDoor=i==1});
            _canvas.State=new(3000,3000,cells);Select(0,1,2);StoreFace();AcceptDraft();
            _draftUndo.Clear();_draftRedo.Clear();_timer.Stop();
        }
        async Task Flush(){_timer.Stop();await PreviewAsync();await Task.Delay(100);}
        await Task.Delay(200);await Flush();
        var initial=Layout();
        var arrows=this.GetVisualDescendants().OfType<Button>().Where(b=>b.Name?.StartsWith("DivisionCount")==true).ToArray();
        var up=arrows.SingleOrDefault(b=>b.Name=="DivisionCountIncrease");
        var down=arrows.SingleOrDefault(b=>b.Name=="DivisionCountDecrease");
        Check(up!=null&&down!=null,"等分数量没有真实上下箭头："+string.Join(",",arrows.Select(b=>b.Name)));
        up!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(_divisionCount.Value==3,"上箭头没有增加 1。");
        down!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(_divisionCount.Value==2,"下箭头没有减少 1。");
        down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(_divisionCount.Value==2,"等分数量突破下限。");
        _divisionCount.Value=20;up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(_divisionCount.Value==20,"等分数量突破上限。");
        _divisionCount.Value=3;
        KeyPress(Key.X);Check(_canvas.State.Cells.Count==3,"X 未竖向三等分。");
        Select(0);KeyPress(Key.S);Check(_canvas.State.Cells.Count==5,"S 未横向三等分。");
        KeyPress(Key.Z,KeyModifiers.Control);Check(_canvas.State.Cells.Count==3,"快速连续等分没有独立撤销。");
        KeyPress(Key.Z,KeyModifiers.Control);Check(Layout()==initial,"第二次撤销未恢复单格。");
        KeyPress(Key.Y,KeyModifiers.Control);Check(_canvas.State.Cells.Count==3,"快捷键等分无法重做。");
        Select(0,1,2);KeyPress(Key.H);Check(_canvas.State.Cells.Count==1,"H 未合并。");
        KeyPress(Key.X);Select(0);KeyPress(Key.D);Check(_canvas.State.Cells[0].IsDeleted,"D 未删除。");
        KeyPress(Key.D);Check(!_canvas.State.Cells[0].IsDeleted,"D 未恢复。");
        Fixture();var before=Layout();KeyPress(Key.F);
        Check(_canvas.State.Cells.All(c=>c.Right-c.Left==1000),"F 未等宽。");
        KeyPress(Key.Z,KeyModifiers.Control);Check(Layout()==before,"等宽未一步撤销。");
        Fixture(true);KeyPress(Key.G);Check(_canvas.State.Cells.All(c=>c.Top-c.Bottom==1000),"G 未等高。");
        Fixture();Select(1);before=Layout();KeyPress(Key.Z);
        Check(_canvas.State.Cells[1].Left==900&&_canvas.State.Cells[1].Right==2100,"Z 未居中。");
        KeyPress(Key.Z,KeyModifiers.Control);Check(Layout()==before,"Ctrl+Z 被居中快捷键占用。");
        KeyPress(Key.Y,KeyModifiers.Control);Check(_canvas.State.Cells[1].Left==900,"居中未重做。");
        Fixture();Select(0,1);before=Layout();KeyPress(Key.K);
        Check(_canvas.State.Cells.Take(2).All(c=>c.Opening=="左平开")&&_canvas.State.Cells[2].Opening=="固定",
            "K 没有仅修改选中分格的开启方式。");
        Check(_canvas.State.Cells[1].Material=="实板"&&_canvas.State.Cells[1].IsDoor,"K 覆盖了混选材料或门扇标记。");
        KeyPress(Key.K);Check(_canvas.State.Cells[0].Opening=="右平开","K 未依次切换。");
        KeyPress(Key.Z,KeyModifiers.Control);Check(_canvas.State.Cells[0].Opening=="左平开","连续 K 没有独立撤销。");
        KeyPress(Key.Z,KeyModifiers.Control);Check(Layout()==before,"开启方式不能撤销。");
        var frame=_doorFrame.SelectedItem;var placement=_doorPlacement.SelectedItem;
        KeyPress(Key.N);Check(!Equals(_doorFrame.SelectedItem,frame)&&Equals(_doorPlacement.SelectedItem,placement),"N 未切换门框或误改靠位。");
        KeyPress(Key.N);Check(Equals(_doorFrame.SelectedItem,frame),"N 未来回切换。");
        KeyPress(Key.Z,KeyModifiers.Control);Check(!Equals(_doorFrame.SelectedItem,frame),"门框切换没有独立撤销。");
        KeyPress(Key.Z,KeyModifiers.Control);Check(Equals(_doorFrame.SelectedItem,frame),"门框无法撤销。");
        var signature=_acceptedDraft!.Signature;
        foreach(var key in new[]{Key.X,Key.S,Key.H,Key.D,Key.F,Key.G,Key.Z,Key.K,Key.N})
        {
            KeyPress(key,source:_fields["width"]);KeyPress(key,source:_divisionCount);
            KeyPress(key,source:_openingMode);KeyPress(key,KeyModifiers.Alt);KeyPress(key,KeyModifiers.Control);
        }
        Check(_acceptedDraft!.Signature==signature,"输入框／下拉列表或修饰键触发了字母命令。");
        _openingMode.SelectedItem="无";await Flush();KeyPress(Key.K);
        Check(_canvas.State.Cells.Take(2).All(c=>c.Opening=="固定"),"K 到末项没有循环。");
        Select();before=Layout();KeyPress(Key.K);Check(Layout()==before,"K 无选择仍修改分格。");
        _kind.SelectedItem="普通窗";await Flush();frame=_doorFrame.SelectedItem;KeyPress(Key.N);
        Check(Equals(_doorFrame.SelectedItem,frame),"普通窗仍触发门框快捷键。");
        _kind.SelectedItem="门联窗";await Flush();Fixture();Select(1);
        foreach(var pair in _layoutShortcutButtons)
            Check(pair.Value.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==pair.Key.ToString()),"工具未显示按键："+pair.Key);
        await Task.Delay(100);
        await SaveCheckImage(Width<1300?"shortcuts-compact.png":"shortcuts-large.png");
        Check(_divisionCount.Bounds.Width>=50&&up.Bounds.Width>=18&&down.Bounds.Width>=18
            &&down.TranslatePoint(default,this)!.Value.Y>up.TranslatePoint(default,this)!.Value.Y,"数字或上下箭头空间不足／没有上下排列。");
        var countText=_divisionCount.GetVisualDescendants().OfType<TextBox>().Single();
        Check(countText.Text=="3"&&countText.Bounds.Width>=40,"等分数量数字不可见。");
        await SaveCheckImage(Width<1300?"shortcuts-compact.png":"shortcuts-large.png");
        Console.WriteLine("OPENING_SHORTCUTS_UI_OK X S H D F G Z K N actualSpinner range rapidCommandUndo redo mixedProperties textComboIsolation modifierIsolation windowLabels");
        Close();
    }
}
