using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    internal static partial class CadNative
    {
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr wl_drawing_settings(IntPtr session);
        internal static CadDrawingSettings DrawingSettings(IntPtr session)
        {
            var json = Text(wl_drawing_settings(session));
            if (string.IsNullOrEmpty(json)) Check(0);
            return JsonSerializer.Deserialize<CadDrawingSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_linetype_manage(IntPtr session, ulong document, int action, uint id,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string description, [In] double[] pattern, int count, double scale, int paperScale);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_point_style(IntPtr session, ulong document, int mode, double size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_layer_details(IntPtr session, ulong document, uint id,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int rgb, int weight, uint lineType);
    }
    internal sealed class CadDrawingSettings
    {
        public uint Current { get; set; }
        public double Scale { get; set; }
        public bool PaperScale { get; set; }
        public int PointMode { get; set; }
        public double PointSize { get; set; }
        public List<CadLineType> Types { get; set; }
    }
    internal sealed class CadLineType
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Complex { get; set; }
        public List<double> Pattern { get; set; }
        public override string ToString() => Name;
    }
    public partial class CadWorkspace
    {
        internal void LineTypes()
        {
            LineTypes(false);
        }
        private void LineTypes(bool scaleOnly)
        {
            if (Client.State.ActiveId == 0) { Status.Text = "请先新建或打开图纸。"; return; }
            var document = Client.State.ActiveId;
            var state = CadNative.DrawingSettings(Client.Handle);
            var operations = new[] { "设为当前线型", "修改所选线型", "以所选线型新建", "设置全局线型比例" };
            var current = state.Types.Single(item => item.Id == state.Current);
            var dialog = new CadParameterWindow("线型管理 · LINETYPE", form =>
            {
                var type = (CadLineType)form.Selected("type");
                var action = new[] { 2, 1, 0, 3 }[form.Index("operation")];
                var pattern = action == 0 || action == 1 ? ParseLinePattern(form.Value("pattern")) : Array.Empty<double>();
                CadNative.Check(CadNative.wl_linetype_manage(Client.Handle, document, action, type.Id, form.Value("name"), form.Value("description"),
                    pattern, pattern.Length, action == 3 ? form.Number("scale") : state.Scale, form.Checked("paperScale") ? 1 : 0));
            });
            dialog.Choice("type", "图纸线型 · 当前为“" + current.Name + "”", state.Types, current);
            dialog.Choice("operation", "操作", operations, operations[scaleOnly ? 3 : 0]);
            dialog.Text("name", "线型名称", current.Name);
            dialog.Text("description", "说明", current.Description);
            dialog.Text("pattern", "图案段长度 · 正数为实线，负数为空白，0为点", "");
            dialog.Numeric("scale", "全局线型比例 LTSCALE", state.Scale);
            dialog.Toggle("paperScale", "布局视口按图纸单位显示线型 PSLTSCALE", state.PaperScale);
            dialog.Note("图案示例 12.7,-6.35。空图案为连续线。图层可在“修改图层”中选择线型。");
            void Load()
            {
                var type = (CadLineType)dialog.Selected("type"); var operation = dialog.Index("operation");
                ((TextBox)dialog.Inputs["name"]).Text = type.Name + (operation == 2 ? "_副本" : "");
                ((TextBox)dialog.Inputs["description"]).Text = type.Description;
                ((TextBox)dialog.Inputs["pattern"]).Text = string.Join(",", type.Pattern.Select(value => value.ToString("G17", CultureInfo.InvariantCulture)));
                dialog.Inputs["name"].IsEnabled = operation == 2;
                dialog.Inputs["description"].IsEnabled = operation == 1 || operation == 2;
                dialog.Inputs["pattern"].IsEnabled = (operation == 1 || operation == 2) && !type.Complex;
                dialog.Inputs["scale"].IsEnabled = dialog.Inputs["paperScale"].IsEnabled = operation == 3;
            }
            ((ComboBox)dialog.Inputs["type"]).SelectionChanged += (sender, args) => Load();
            ((ComboBox)dialog.Inputs["operation"]).SelectionChanged += (sender, args) => Load();
            Load(); ShowParameters(dialog);
        }
        internal static double[] ParseLinePattern(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<double>();
            return text.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(part =>
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                    throw new InvalidOperationException("图案段长度需要有效数字，小数使用英文句点。");
                return value;
            }).ToArray();
        }
        internal void PointStyle()
        {
            if (Client.State.ActiveId == 0) { Status.Text = "请先新建或打开图纸。"; return; }
            var document = Client.State.ActiveId; var state = CadNative.DrawingSettings(Client.Handle);
            var forms = new[] { "点", "不显示", "十字 +", "交叉 ×", "竖线" };
            var frames = new[] { "无边框", "圆形", "方形", "圆形与方形" };
            var modes = new[] { "随视口自动", "固定绘图单位", "占视口百分比" };
            var dialog = new CadParameterWindow("点样式 · PTYPE", form =>
            {
                var mode = form.Index("shape") + form.Index("frame") * 32;
                var units = form.Index("units"); var size = units == 0 ? 0 : form.Number("size");
                if (units != 0 && size <= 0) throw new InvalidOperationException("点大小需要大于0。");
                CadNative.Check(CadNative.wl_point_style(Client.Handle, document, mode, units == 2 ? -size : size));
            });
            dialog.Choice("shape", "点形状", forms, forms[Math.Min(4, state.PointMode & 31)]);
            dialog.Choice("frame", "点边框", frames, frames[Math.Min(3, (state.PointMode >> 5) & 3)]);
            dialog.Choice("units", "大小方式", modes, modes[state.PointSize == 0 ? 0 : state.PointSize > 0 ? 1 : 2]);
            dialog.Numeric("size", "大小（绘图单位或百分比）", state.PointSize == 0 ? 5 : Math.Abs(state.PointSize));
            ((ComboBox)dialog.Inputs["units"]).SelectionChanged += (sender, args) => dialog.Inputs["size"].IsEnabled = dialog.Index("units") != 0;
            dialog.Inputs["size"].IsEnabled = dialog.Index("units") != 0;
            ShowParameters(dialog);
        }
    }
}
