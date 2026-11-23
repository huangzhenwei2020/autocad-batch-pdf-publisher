using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadClippingSelfTest
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
        internal static async Task RunAsync(CadWorkspace workspace,string root,Action<Window,string> capture)
        {
            var client=workspace.Client;
            var original=client.State.ActiveId;
            workspace.NewDrawing();
            var document=client.State.ActiveId;
            void Point(double x,double y) => CadNative.Check(CadNative.wl_point(client.Handle,x,y));
            void Assert(bool result,string message) { if (!result) throw new InvalidOperationException(message); }
            client.Command("LINE"); Point(-10000,5000); Point(20000,5000); client.Command(""); client.Command("@cancel");
            client.Command("LINE"); Point(-10000,7500); Point(20000,7500); client.Command(""); client.Command("@cancel");
            CadNative.Check(CadNative.wl_layout_manage(client.Handle,0,0,"裁剪平面"));
            var parameters=new CadViewportParameters {X=150,Y=100,Width=300,Height=200,ModelX=5000,ModelY=5000,Ratio=50};
            CadNative.Check(CadNative.wl_viewport_apply(client.Handle,document,0,ref parameters,Array.Empty<uint>(),0));
            var viewport=CadNative.PaperViewports(client.Handle).Single();
            void Start() => CadNative.Check(CadNative.wl_viewport_clip(client.Handle,client.State.ActiveId,CadNative.PaperViewports(client.Handle).Single().Id,0));
            Start(); Point(0,0); Point(300,200); Point(0,200); Point(300,0); client.Command("");
            Assert(CadNative.PaperViewports(client.Handle).Single().ClipCount==0,"Self-intersecting boundary was committed"); client.Command("@cancel");
            Action<CadParameterWindow> opened=form => form.Dispatcher.BeginInvoke(new Action(() =>
            {
                capture(form,Path.Combine(root,"viewport-clip-dialog.png"));
                form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            workspace.ParameterWindowOpened+=opened;
            try { workspace.ClipViewport(); } finally { workspace.ParameterWindowOpened-=opened; }
            Point(0,0); Point(300,0); Point(999,999);
            SendMessage(((CadViewport)workspace.FindName("Viewport")).Handle,0x0100,new IntPtr(8),IntPtr.Zero);
            Point(300,200); Point(150,200); Point(150,100); Point(0,100); client.Command("");
            var clipped=CadNative.PaperViewports(client.Handle).Single();
            Assert(clipped.ClipCount==6 && Math.Abs(clipped.Ratio-50)<1e-9 && clipped.ModelX==5000,"Concave clip lost boundary or content transform");
            client.Command("@undo"); Assert(CadNative.PaperViewports(client.Handle).Single().ClipCount==0,"Clip undo failed");
            client.Command("@redo"); Assert(CadNative.PaperViewports(client.Handle).Single().ClipCount==6,"Clip redo failed");
            client.Command("@fit"); await Task.Delay(120); workspace.RefreshState();
            capture(Window.GetWindow(workspace),Path.Combine(root,"viewport-concave-clip.png"));
            foreach(var extension in new[]{".dwg",".dxf"})
            {
                var path=Path.Combine(root,"多边形视口"+extension);
                await client.SaveAsync(path); client.Close(document); await client.OpenAsync(path); document=client.State.ActiveId;
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
                clipped=CadNative.PaperViewports(client.Handle).Single();
                Assert(clipped.ClipCount==6 && clipped.Ratio==50,"Clip roundtrip failed "+extension);
                Assert(client.State.EntityCount==3,"Hidden clip boundary leaked into editable scene");
                await client.SaveAsync(path);
                CadNative.Check(CadNative.wl_viewport_clip(client.Handle,document,clipped.Id,1));
                Assert(CadNative.PaperViewports(client.Handle).Single().ClipCount==0,"Restore rectangle failed");
                await client.SaveAsync(Path.Combine(root,"恢复矩形视口"+extension));
                client.Command("@undo"); Assert(CadNative.PaperViewports(client.Handle).Single().ClipCount==6,"Restore rectangle undo failed");
                Start(); Point(10,20); Point(200,20); Point(200,180); Point(10,180); client.Command("");
                var redrawn=CadNative.PaperViewports(client.Handle).Single();
                Assert(redrawn.ClipCount==4 && redrawn.Ratio==50 && redrawn.Width==190 && redrawn.ModelX==2750,"Redrawing clip changed model mapping");
                await client.SaveAsync(Path.Combine(root,"裁剪重画"+extension));
                client.Command("@undo");
                var layout=client.State.Layouts.Single(item=>item.Current);
                CadNative.Check(CadNative.wl_layout_update(client.Handle,document,layout.Key,"裁剪副本",layout.Width,layout.Height,1,1));
                await client.SaveAsync(Path.Combine(root,"裁剪布局复制"+extension));
                client.Command("@undo");
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
            }
            var current = CadNative.PaperViewports(client.Handle).Single();
            void StartCurve(int kind)
            {
                Action<CadParameterWindow> choose=form => form.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ((ComboBox)form.Inputs["method"]).SelectedIndex=kind;
                    capture(form,Path.Combine(root,"viewport-curve-dialog-"+kind+".png"));
                    form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                workspace.ParameterWindowOpened+=choose;
                try { workspace.ClipViewport(); } finally { workspace.ParameterWindowOpened-=choose; }
            }
            StartCurve(2);
            Point(0,0); Point(0,0);
            Assert(!CadNative.PaperViewports(client.Handle).Single().ClipEllipse,"Zero radius was committed");
            SendMessage(((CadViewport)workspace.FindName("Viewport")).Handle,0x0100,new IntPtr(8),IntPtr.Zero);
            Point(150,100);
            Assert(!CadNative.PaperViewports(client.Handle).Single().ClipEllipse,"First circle point committed a boundary");
            Point(230,100);
            current=CadNative.PaperViewports(client.Handle).Single();
            Assert(current.ClipEllipse && Math.Abs(current.Width-160)<.001 && Math.Abs(current.Ratio-50)<.001,"Circle clipping failed");
            client.Command("@cancel"); Assert(CadNative.PaperViewports(client.Handle).Single().ClipEllipse,"Esc reverted committed circle");
            client.Command("@undo"); Assert(!CadNative.PaperViewports(client.Handle).Single().ClipEllipse,"Circle undo failed"); client.Command("@redo");
            foreach(var extension in new[]{".dwg",".dxf"})
            {
                var file=Path.Combine(root,"圆形视口"+extension);
                await client.SaveAsync(file); client.Close(document); await client.OpenAsync(file); document=client.State.ActiveId;
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
                Assert(CadNative.PaperViewports(client.Handle).Single().ClipEllipse,"Circle shape lost during roundtrip");
            }
            StartCurve(3);
            Point(10,20); Point(290,180);
            Assert(CadNative.PaperViewports(client.Handle).Single().ClipEllipse,"Ellipse clipping failed");
            client.Command("@fit"); await Task.Delay(100); workspace.RefreshState();
            capture(Window.GetWindow(workspace),Path.Combine(root,"viewport-ellipse-clip.png"));
            foreach(var extension in new[]{".dwg",".dxf"})
            {
                var file=Path.Combine(root,"椭圆视口"+extension);
                await client.SaveAsync(file); client.Close(document); await client.OpenAsync(file); document=client.State.ActiveId;
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
                var curve=CadNative.PaperViewports(client.Handle).Single();
                Assert(curve.ClipEllipse && Math.Abs(curve.Width-280)<.001 && Math.Abs(curve.Height-160)<.001,"Ellipse shape lost during roundtrip");
                var page=client.State.Layouts.Single(item=>item.Current);
                CadNative.Check(CadNative.wl_layout_update(client.Handle,document,page.Key,"曲线副本",page.Width,page.Height,1,1));
                await client.SaveAsync(Path.Combine(root,"椭圆复制"+extension)); client.Command("@undo");
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
                CadNative.Check(CadNative.wl_viewport_clip(client.Handle,document,CadNative.PaperViewports(client.Handle).Single().Id,1));
                Assert(!CadNative.PaperViewports(client.Handle).Single().ClipEllipse,"Curve restore rectangle failed");
                await client.SaveAsync(Path.Combine(root,"曲线恢复矩形"+extension)); client.Command("@undo");
            }
            StartCurve(3); Point(70,-40); Point(230,240);
            foreach(var extension in new[]{".dwg",".dxf"})
            {
                var file=Path.Combine(root,"竖向椭圆视口"+extension);
                await client.SaveAsync(file); client.Close(document); await client.OpenAsync(file); document=client.State.ActiveId;
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
                var curve=CadNative.PaperViewports(client.Handle).Single();
                Assert(curve.ClipEllipse && Math.Abs(curve.Width-160)<.001 && Math.Abs(curve.Height-280)<.001,"Vertical ellipse roundtrip failed");
            }
            var diameter = 2 * Math.Sqrt(100*100 + 40*40);
            void CheckRotatedClip()
            {
                var curve = CadNative.PaperViewports(client.Handle).Single();
                Assert(curve.ClipEllipse && Math.Abs(curve.Width-diameter)<.001 && Math.Abs(curve.Height-diameter)<.001 &&
                    Math.Abs(curve.Ratio-50)<1e-8 && Math.Abs(curve.ModelX-6000)<1e-8 && Math.Abs(curve.ModelY-5500)<1e-8,
                    "Rotated ellipse changed bounds, scale or model-to-paper transform.");
            }
            StartCurve(4);
            Point(170,110); Point(270,210); Point(220,160);
            Assert(Math.Abs(CadNative.PaperViewports(client.Handle).Single().Width-160)<.001, "A zero minor-axis radius was accepted.");
            SendMessage(((CadViewport)workspace.FindName("Viewport")).Handle,0x0100,new IntPtr(8),IntPtr.Zero);
            Point(270,210); Point(130,150); CheckRotatedClip();
            client.Command("@undo");
            Assert(Math.Abs(CadNative.PaperViewports(client.Handle).Single().Width-160)<.001,"Rotated ellipse undo did not restore the vertical ellipse.");
            client.Command("@redo"); CheckRotatedClip();
            client.Command("@fit"); await Task.Delay(100);
            capture(Window.GetWindow(workspace),Path.Combine(root,"viewport-rotated-clip.png"));
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                var file = Path.Combine(root,"旋转椭圆视口"+extension);
                await client.SaveAsync(file); client.Close(document); await client.OpenAsync(file); document=client.State.ActiveId;
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
                CheckRotatedClip();
                var page = client.State.Layouts.Single(item=>item.Current);
                CadNative.Check(CadNative.wl_layout_update(client.Handle,document,page.Key,"旋转椭圆副本",page.Width,page.Height,1,1));
                await client.SaveAsync(Path.Combine(root,"旋转椭圆布局复制"+extension)); client.Command("@undo");
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
            }
            client.Command("PLINE");
            Point(100,60); Point(200,60); client.Command("A"); Point(250,110); Point(200,160);
            client.Command("L"); Point(100,160); client.Command("C");
            client.Command("@fit");
            var host = (CadViewport)workspace.FindName("Viewport");
            void Click(double x, double y)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle,x,y,out var px,out var py));
                var position = new IntPtr((py << 16) | (px & 0xffff));
                SendMessage(host.Handle,0x0201,new IntPtr(1),position);
                SendMessage(host.Handle,0x0202,IntPtr.Zero,position);
            }
            StartCurve(5);
            Click(175,110);
            Assert(CadNative.PaperViewports(client.Handle).Single().ClipEllipse,"Interior click selected a polyline edge.");
            Click(250,110);
            void CheckArcClip()
            {
                var view = CadNative.PaperViewports(client.Handle).Single();
                Assert(!view.ClipEllipse && view.ClipCount>4 && Math.Abs(view.Width-150)<1e-8 &&
                    Math.Abs(view.Height-100)<1e-8 && Math.Abs(view.Ratio-50)<1e-8 && client.State.EntityCount==4,
                    "Arc clip lost its curve, scale or editable source polyline.");
            }
            CheckArcClip();
            client.Command("@undo");
            Assert(CadNative.PaperViewports(client.Handle).Single().ClipEllipse,"Undo did not restore the prior ellipse.");
            client.Command("@redo"); CheckArcClip();
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                var file = Path.Combine(root,"圆弧多段线视口"+extension);
                await client.SaveAsync(file); client.Close(document); await client.OpenAsync(file); document=client.State.ActiveId;
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
                CheckArcClip();
                var layout = client.State.Layouts.Single(item=>item.Current);
                CadNative.Check(CadNative.wl_layout_update(client.Handle,document,layout.Key,"圆弧轮廓副本",layout.Width,layout.Height,1,1));
                await client.SaveAsync(Path.Combine(root,"圆弧多段线布局复制"+extension)); client.Command("@undo");
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
            }
            client.Command("SPLINE");
            Point(70,110); Point(140,50); Point(225,110); Point(140,180);
            client.Command("C");
            client.Command("");
            Assert(client.State.EntityCount==5,"Source spline was not created.");
            client.Command("@fit");
            StartCurve(6);
            Click(140,110);
            Assert(CadNative.PaperViewports(client.Handle).Single().Width==150,"Interior click selected the spline.");
            Click(70,110);
            void CheckSplineClip()
            {
                var view = CadNative.PaperViewports(client.Handle).Single();
                Assert(view.ClipCount>=100 && !view.ClipEllipse && view.Width>=155-1e-7 && view.Height>=130-1e-7 &&
                    Math.Abs(view.Ratio-50)<1e-8 && client.State.EntityCount==5,
                    $"Spline clip lost its shape, scale or editable source. Points={view.ClipCount}, size={view.Width}×{view.Height}, ratio={view.Ratio}, entities={client.State.EntityCount}, prompt={client.State.Prompt}.");
            }
            CheckSplineClip();
            client.Command("@undo");
            Assert(CadNative.PaperViewports(client.Handle).Single().Width==150,"Spline undo did not restore the arc boundary.");
            client.Command("@redo"); CheckSplineClip();
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                var file = Path.Combine(root,"样条视口"+extension);
                await client.SaveAsync(file); client.Close(document); await client.OpenAsync(file); document=client.State.ActiveId;
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
                CheckSplineClip();
                var layout = client.State.Layouts.Single(item=>item.Current);
                CadNative.Check(CadNative.wl_layout_update(client.Handle,document,layout.Key,"样条轮廓副本",layout.Width,layout.Height,1,1));
                await client.SaveAsync(Path.Combine(root,"样条布局复制"+extension)); client.Command("@undo");
                CadNative.Check(CadNative.wl_layout(client.Handle,client.State.Layouts.Single(item=>item.Name=="裁剪平面").Id));
            }
            client.Close(document); client.Activate(original); workspace.RefreshState();
            File.WriteAllText(Path.Combine(root,"cad-clipping-result.txt"),"CAD_CLIPPING_OK concave circle ellipse vertical rotated bulges spline UI validation undo redo clear DWG DXF");
        }
    }
}
