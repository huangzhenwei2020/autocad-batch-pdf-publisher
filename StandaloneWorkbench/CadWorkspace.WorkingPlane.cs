using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows;

namespace Wanluo.StandaloneWorkbench
{
    internal sealed class CadWorkPlane
    {
        public bool World { get; set; } = true;
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double OriginZ { get; set; }
        public double AxisXX { get; set; } = 1;
        public double AxisXY { get; set; }
        public double AxisXZ { get; set; }
        public double AxisYX { get; set; }
        public double AxisYY { get; set; } = 1;
        public double AxisYZ { get; set; }
    }

    internal static partial class CadNative
    {
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int wl_workplane(IntPtr session, int fromView, double x, double y, double z);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int wl_world_to_screen3(IntPtr session, double x, double y, double z, out int px, out int py);
    }

    public partial class CadWorkspace
    {
        private void WorkingPlane_Click(object sender, RoutedEventArgs args) => Run(WorkingPlaneSettings);
        internal void WorkingPlaneSettings()
        {
            var state = Client.State;
            if (state.Layouts.Any(item => item.Current && !item.Model) && state.EditingViewport == 0)
            {
                Status.Text = "请切换到模型空间设置绘图工作平面。";
                return;
            }
            var plane = state.WorkPlane;
            var dialog = new CadParameterWindow("绘图工作平面 · UCS", form =>
            {
                var fromView = form.Index("plane") == 1;
                CadNative.Check(CadNative.wl_workplane(Client.Handle, fromView ? 1 : 0,
                    fromView ? form.Number("x") : 0, fromView ? form.Number("y") : 0, fromView ? form.Number("z") : 0));
                Status.Text = fromView ? "已设置视图工作平面，输入坐标以此平面为基准。" : "已恢复世界 XY 工作平面。";
            });
            var modes = new[] { "世界 XY", "当前视图平面" };
            dialog.Choice("plane", "工作平面", modes, modes[plane.World ? 0 : 1]);
            dialog.Numeric("x", "原点 X（世界坐标）", plane.OriginX);
            dialog.Numeric("y", "原点 Y（世界坐标）", plane.OriginY);
            dialog.Numeric("z", "原点 Z（世界坐标）", plane.OriginZ);
            void EnableOrigin()
            {
                foreach (var key in new[] { "x", "y", "z" }) dialog.Inputs[key].IsEnabled = dialog.Index("plane") == 1;
            }
            ((ComboBox)dialog.Inputs["plane"]).SelectionChanged += (sender, args) => EnableOrigin();
            dialog.Note("当前视图的屏幕右方作为 X 轴，上方作为 Y 轴。支持直线、多段线、点、圆、圆弧、椭圆、椭圆弧、移动、复制和填充，以及端点／中点捕捉、正交、极轴和坐标输入。Ctrl+Z 撤销设置。UCS WORLD 恢复世界坐标。");
            EnableOrigin();
            ShowParameters(dialog);
        }
    }
}
