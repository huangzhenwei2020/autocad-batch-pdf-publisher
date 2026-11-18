using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Wanluo.StandaloneWorkbench
{
    internal static partial class CadNative
    {
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_standard_view(IntPtr session, int view);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_visual_style(IntPtr session, int style);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr wl_xref_state(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_xref_locate(IntPtr session, ulong document, [MarshalAs(UnmanagedType.LPUTF8Str)] string folder);
        internal static List<CadExternalReference> References(IntPtr session)
        {
            var json = Text(wl_xref_state(session)); if (string.IsNullOrEmpty(json)) Check(0);
            return JsonSerializer.Deserialize<List<CadExternalReference>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
    }
    internal sealed class CadExternalReference
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Status { get; set; }
        public override string ToString() => Name + " · " + Status;
    }
    public partial class CadWorkspace
    {
        private ComboBox _standardViews;
        internal void LocateReferences()
        {
            if (Client.State.ActiveId == 0) { Status.Text = "请先新建或打开图纸。"; return; }
            var document = Client.State.ActiveId;
            var references = CadNative.References(Client.Handle);
            if (references.Count == 0) { Status.Text = "当前图纸没有外部参照。"; return; }
            var drawing = Client.State.Documents.Single(item => item.Id == document);
            var folder = string.IsNullOrEmpty(drawing.Path) ? "" : Path.GetDirectoryName(drawing.Path);
            var dialog = new CadParameterWindow("外部参照 · XREF", form =>
                CadNative.Check(CadNative.wl_xref_locate(Client.Handle, document, form.Value("folder"))));
            dialog.ApplyButton.Content = "查找未解析参照";
            dialog.Choice("reference", "当前图纸的外部参照", references, references[0]);
            dialog.Text("path", "图纸中保存的参照路径", references[0].Path, enabled: false);
            dialog.Text("folder", "查找目录", folder);
            var browse = new Button { Content = "选择查找目录…", HorizontalAlignment = HorizontalAlignment.Right };
            browse.Click += (sender, args) =>
            {
                var picker = new OpenFolderDialog { Title = "选择外部参照所在文件夹" };
                if (picker.ShowDialog(dialog) == true) ((TextBox)dialog.Inputs["folder"]).Text = picker.FolderName;
            };
            dialog.Field("browse", "", browse);
            ((ComboBox)dialog.Inputs["reference"]).SelectionChanged += (sender, args) =>
                ((TextBox)dialog.Inputs["path"]).Text = ((CadExternalReference)dialog.Selected("reference")).Path;
            dialog.Note("查找当前图纸的未解析参照，加载成功后更新参照路径。保存图纸可保留新路径，外部源文件保持独立。");
            ShowParameters(dialog);
        }
        internal async Task SaveAllDrawingsAsync()
        {
            if (Client.IsBusy || _parameterOpen) return;
            var original = Client.State.ActiveId;
            try
            {
                foreach (var drawing in Client.State.Documents.Where(item => item.Dirty || string.IsNullOrEmpty(item.Path)).ToArray())
                {
                    Client.Activate(drawing.Id); RefreshState();
                    if (!await SaveWithDialogAsync(false)) return;
                }
                Status.Text = "全部图纸已保存。";
            }
            finally { if (Client.State.Documents.Any(item => item.Id == original)) Client.Activate(original); RefreshState(); }
        }
        private void StandardView(int view) => Run(() => CadNative.Check(CadNative.wl_standard_view(Client.Handle, view)));
        private ComboBox StandardViews()
        {
            var views = new ComboBox { Width = 142, Height = 30, Margin = new Thickness(5), ItemsSource = new[] { "俯视", "仰视", "前视", "后视", "左视", "右视", "西南等轴测", "东南等轴测", "东北等轴测", "西北等轴测" }, SelectedIndex = 0, ToolTip = "切换标准视图" };
            views.Style = (Style)FindResource("CadComboBox");
            views.SelectionChanged += (sender, args) => { if (!_updating && views.SelectedIndex >= 0 && Client.State.ActiveId != 0) StandardView(views.SelectedIndex); };
            _standardViews = views;
            return views;
        }
    }
}
