using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Data;

namespace Wanluo.StandaloneWorkbench
{
    internal static partial class CadNative
    {
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr wl_style_state(IntPtr session);
        internal static CadStyleState StyleState(IntPtr session)
        {
            var text = Text(wl_style_state(session));
            if (string.IsNullOrEmpty(text)) Check(0);
            return JsonSerializer.Deserialize<CadStyleState>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_text_style_manage(IntPtr session, ulong document, int action, uint id,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string font,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string bigFont, double height, double width, double oblique);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_mline_style_manage(IntPtr session, ulong document, int action, uint id,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string description,
            [In] CadMLineElement[] elements, int count, int startCap, int endCap, int joints);
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CadMLineElement { public double Offset; public int Color; }
    internal sealed class CadStyleState
    {
        public uint CurrentText { get; set; }
        public uint CurrentMLine { get; set; }
        public List<CadNamedStyle> Text { get; set; }
        public List<CadNamedStyle> MLine { get; set; }
    }
    internal sealed class CadNamedStyle
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public string Font { get; set; }
        public string BigFont { get; set; }
        public double Height { get; set; }
        public double WidthFactor { get; set; }
        public double Oblique { get; set; }
        public string Description { get; set; }
        public bool StartCap { get; set; }
        public bool EndCap { get; set; }
        public bool Joints { get; set; }
        public List<CadMLineElementValue> Elements { get; set; }
        public override string ToString() => Name;
    }
    internal sealed class CadMLineElementValue
    {
        public double Offset { get; set; }
        public int Color { get; set; }
    }
    internal sealed class CadMLineRow
    {
        public string Offset { get; set; }
        public string Color { get; set; }
    }

    public partial class CadWorkspace
    {
        private CadParameterWindow StyleDialog(ulong document, bool multiline)
        {
            var state = CadNative.StyleState(Client.Handle);
            var styles = multiline ? state.MLine : state.Text;
            var current = styles.FirstOrDefault(item => item.Id == (multiline ? state.CurrentMLine : state.CurrentText)) ?? styles[0];
            var operations = new[] { "设为当前样式", "修改所选样式", "以所选样式新建", "重命名所选样式", "删除未使用样式" };
            var rows = new ObservableCollection<CadMLineRow>();
            var dialog = new CadParameterWindow(multiline ? "多线样式 · MLSTYLE" : "文字样式 · STYLE", form =>
            {
                var selected = (CadNamedStyle)form.Selected("style");
                var action = new[] { 2, 1, 0, 3, 4 }[form.Index("operation")];
                if (multiline)
                {
                    var grid = (DataGrid)form.Inputs["elements"];
                    grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
                    var elements = action > 1 ? Array.Empty<CadMLineElement>() : ReadMLineElements(rows);
                    CadNative.Check(CadNative.wl_mline_style_manage(Client.Handle, document, action, selected.Id, form.Value("name"),
                        form.Value("description"), elements, elements.Length, form.Checked("start") ? 1 : 0, form.Checked("end") ? 1 : 0, form.Checked("joints") ? 1 : 0));
                }
                else CadNative.Check(CadNative.wl_text_style_manage(Client.Handle, document, action, selected.Id, form.Value("name"), form.Value("font"),
                    form.Value("bigFont"), action > 1 ? 0 : form.Number("height"), action > 1 ? 1 : form.Number("width"), action > 1 ? 0 : form.Number("oblique")));
            });
            dialog.Choice("style", "图纸样式 · 当前为“" + current.Name + "”", styles, current);
            dialog.Choice("operation", "操作", operations, operations[0]);
            dialog.Text("name", "样式名称", current.Name);
            if (multiline)
            {
                dialog.Text("description", "说明", current.Description);
                var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true, Height = 165, MinRowHeight = 28, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionUnit = DataGridSelectionUnit.FullRow };
                grid.Columns.Add(new DataGridTextColumn { Header = "偏移系数", Binding = new Binding("Offset") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
                grid.Columns.Add(new DataGridTextColumn { Header = "颜色 · ByLayer / #RRGGBB", Binding = new Binding("Color") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
                grid.InitializingNewItem += (sender, args) => { var row = (CadMLineRow)args.NewItem; row.Offset = "0"; row.Color = "ByLayer"; };
                dialog.Field("elements", "元素线 · 点击末行新增，选中行按 Delete 删除", grid);
                dialog.Toggle("start", "起点封口", current.StartCap);
                dialog.Toggle("end", "终点封口", current.EndCap);
                dialog.Toggle("joints", "顶点连接线", current.Joints);
                dialog.Note("多线按偏移 × 绘制比例生成。修改样式用于后续绘制，已绘多线保留原有形状。");
            }
            else
            {
                dialog.File("font", "主字体文件 · TTF／TTC／OTF／SHX", current.Font);
                dialog.File("bigFont", "SHX大字体 · 可留空", current.BigFont);
                dialog.Numeric("height", "固定字高 mm · 0为绘制时设置", current.Height);
                dialog.Numeric("width", "宽度因子", current.WidthFactor);
                dialog.Numeric("oblique", "倾斜角 °（-85至85）", current.Oblique);
                dialog.Note("修改字体、宽度因子和倾斜角会更新引用该样式的文字。固定字高用于新建文字。");
            }
            void UpdateEnabled()
            {
                var index = dialog.Index("operation");
                foreach (var field in dialog.Inputs.Where(item => item.Key != "style" && item.Key != "operation"))
                    field.Value.IsEnabled = field.Key == "name" ? index == 2 || index == 3 : index == 1 || index == 2;
                dialog.ApplyButton.Content = index == 4 ? "删除样式" : "确定";
            }
            void LoadStyle()
            {
                var item = (CadNamedStyle)dialog.Selected("style");
                ((TextBox)dialog.Inputs["name"]).Text = item.Name + (dialog.Index("operation") == 2 ? "_副本" : "");
                if (multiline)
                {
                    var grid = (DataGrid)dialog.Inputs["elements"];
                    grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
                    ((TextBox)dialog.Inputs["description"]).Text = item.Description;
                    rows.Clear();
                    foreach (var element in item.Elements) rows.Add(new CadMLineRow { Offset = element.Offset.ToString("G17", CultureInfo.InvariantCulture), Color = element.Color < 0 ? "ByLayer" : "#" + element.Color.ToString("X6") });
                    ((CheckBox)dialog.Inputs["start"]).IsChecked = item.StartCap;
                    ((CheckBox)dialog.Inputs["end"]).IsChecked = item.EndCap;
                    ((CheckBox)dialog.Inputs["joints"]).IsChecked = item.Joints;
                }
                else
                {
                    foreach (var field in new[] { "font", "bigFont", "height", "width", "oblique" })
                        ((TextBox)dialog.Inputs[field]).Text = field switch
                        {
                            "font" => item.Font, "bigFont" => item.BigFont,
                            "height" => item.Height.ToString("G17", CultureInfo.CurrentCulture),
                            "width" => item.WidthFactor.ToString("G17", CultureInfo.CurrentCulture),
                            _ => item.Oblique.ToString("G17", CultureInfo.CurrentCulture)
                        };
                }
                UpdateEnabled();
            }
            ((ComboBox)dialog.Inputs["style"]).SelectionChanged += (sender, args) => LoadStyle();
            ((ComboBox)dialog.Inputs["operation"]).SelectionChanged += (sender, args) => LoadStyle();
            LoadStyle();
            return dialog;
        }

        internal static CadMLineElement[] ReadMLineElements(IEnumerable<CadMLineRow> rows)
        {
            var result = new List<CadMLineElement>();
            foreach (var row in rows)
            {
                if ((!double.TryParse(row.Offset, NumberStyles.Float, CultureInfo.CurrentCulture, out var offset) &&
                     !double.TryParse(row.Offset, NumberStyles.Float, CultureInfo.InvariantCulture, out offset)) || !double.IsFinite(offset))
                    throw new InvalidOperationException("第" + (result.Count + 1) + "条元素线的偏移系数无效，请输入数字。");
                var colorText = (row.Color ?? "").Trim();
                var color = -1;
                if (!colorText.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) &&
                    (colorText.Length != 7 || colorText[0] != '#' || !int.TryParse(colorText.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out color)))
                    throw new InvalidOperationException("颜色请填写 ByLayer 或六位十六进制颜色，例如 #FF0000。");
                result.Add(new CadMLineElement { Offset = offset, Color = color });
            }
            if (result.Count < 1 || result.Count > 64) throw new InvalidOperationException("请填写1至64条元素线。");
            return result.ToArray();
        }
    }
}
