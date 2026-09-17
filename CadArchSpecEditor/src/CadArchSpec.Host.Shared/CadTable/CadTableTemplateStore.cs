using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using CadArchSpec.EditorBridge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal sealed class CadTableTemplate
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime UpdatedAt { get; set; }
        public JObject Payload { get; set; }

        public override string ToString()
        {
            return Name + "    " + UpdatedAt.ToString("yyyy-MM-dd HH:mm");
        }
    }

    internal static class CadTableTemplateStore
    {
        private static string FilePath
        {
            get { return Path.Combine(PortableDataPaths.DirectoryFor("表格模板"), "cad-table-templates.json"); }
        }

        public static List<CadTableTemplate> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<CadTableTemplate>();
                var root = JObject.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                return (root["templates"] as JArray ?? new JArray()).OfType<JObject>().Select(item =>
                    new CadTableTemplate
                    {
                        Id = (string)item["id"] ?? string.Empty,
                        Name = (string)item["name"] ?? "未命名表格",
                        UpdatedAt = (DateTime?)item["updatedAt"] ?? DateTime.MinValue,
                        Payload = item["payload"] as JObject ?? new JObject()
                    }).OrderByDescending(item => item.UpdatedAt).ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("读取常用表格库失败：" + ex.GetBaseException().Message, ex);
            }
        }

        public static CadTableTemplate Save(string name, JObject payload)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0) throw new InvalidOperationException("表格名称不能为空。");
            var templates = Load();
            var existing = templates.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            var saved = new CadTableTemplate
            {
                Id = existing == null ? Guid.NewGuid().ToString("N") : existing.Id,
                Name = name,
                UpdatedAt = DateTime.Now,
                Payload = PreparePayload(payload)
            };
            templates.RemoveAll(item => string.Equals(item.Id, saved.Id, StringComparison.OrdinalIgnoreCase));
            templates.Add(saved);
            Write(templates);
            return saved;
        }

        public static void Delete(string id)
        {
            var templates = Load();
            templates.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            Write(templates);
        }

        private static JObject PreparePayload(JObject payload)
        {
            var result = payload == null ? new JObject() : (JObject)payload.DeepClone();
            result["drawingPath"] = string.Empty;
            result["warnings"] = new JArray();
            result["nativeTable"] = false;
            var table = result["table"] as JObject;
            if (table != null)
            {
                table["sourceDrawingPath"] = string.Empty;
                foreach (var row in (table["rows"] as JArray ?? new JArray()).OfType<JObject>())
                    foreach (var cell in (row["cells"] as JArray ?? new JArray()).OfType<JObject>())
                        cell["sourceHandles"] = new JArray();
            }
            return result;
        }

        private static void Write(IEnumerable<CadTableTemplate> templates)
        {
            var root = new JObject
            {
                ["schemaVersion"] = 1,
                ["templates"] = new JArray(templates.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Select(item =>
                    new JObject
                    {
                        ["id"] = item.Id,
                        ["name"] = item.Name,
                        ["updatedAt"] = item.UpdatedAt,
                        ["payload"] = item.Payload
                    }))
            };
            var path = FilePath;
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (!File.Exists(path)) { File.Move(temporary, path); return; }
            try { File.Replace(temporary, path, null); }
            catch
            {
                File.Delete(path);
                File.Move(temporary, path);
            }
        }
    }

    internal static class CadTableDefaultsStore
    {
        private static string FilePath
        {
            get { return Path.Combine(PortableDataPaths.DirectoryFor("表格模板"), "cad-table-defaults.json"); }
        }

        public static JObject Load()
        {
            try { return File.Exists(FilePath) ? JObject.Parse(File.ReadAllText(FilePath, Encoding.UTF8)) : new JObject(); }
            catch { return new JObject(); }
        }

        public static void Save(JObject value)
        {
            var path = FilePath;
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, (value ?? new JObject()).ToString(Formatting.Indented), new UTF8Encoding(false));
            if (!File.Exists(path)) { File.Move(temporary, path); return; }
            try { File.Replace(temporary, path, null); }
            catch { File.Delete(path); File.Move(temporary, path); }
        }
    }

    internal enum CadTableStartAction { Cancel, PickCad, PickExistingForUpdate, OpenTemplate }

    internal sealed class CadTableStartForm : Form
    {
        private readonly ListBox _templates = new ListBox();
        public CadTableStartAction SelectedAction { get; private set; }
        public CadTableTemplate SelectedTemplate { get { return _templates.SelectedItem as CadTableTemplate; } }

        public CadTableStartForm(IEnumerable<CadTableTemplate> templates)
        {
            Text = "CAD 表格编辑/Excel";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 390);
            MinimumSize = new Size(520, 320);
            Font = new Font("Microsoft YaHei UI", 9f);
            var title = new Label
            {
                Text = "常用表格库",
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(12, 12, 0, 0),
                Font = new Font(Font, FontStyle.Bold)
            };
            _templates.Dock = DockStyle.Fill;
            _templates.IntegralHeight = false;
            foreach (var template in templates) _templates.Items.Add(template);
            if (_templates.Items.Count > 0) _templates.SelectedIndex = 0;
            _templates.DoubleClick += (sender, args) => OpenSelected();
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                Padding = new Padding(8),
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            actions.Controls.Add(Button("取消", () => CloseWith(CadTableStartAction.Cancel)));
            actions.Controls.Add(Button("拾取 CAD 表格", () => CloseWith(CadTableStartAction.PickCad)));
            actions.Controls.Add(Button("拾取现有表格修改", () => CloseWith(CadTableStartAction.PickExistingForUpdate)));
            actions.Controls.Add(Button("打开选中模板", OpenSelected));
            actions.Controls.Add(Button("删除模板", DeleteSelected));
            Controls.Add(_templates);
            Controls.Add(title);
            Controls.Add(actions);
        }

        private void OpenSelected()
        {
            if (SelectedTemplate == null) return;
            CloseWith(CadTableStartAction.OpenTemplate);
        }

        private void DeleteSelected()
        {
            var selected = SelectedTemplate;
            if (selected == null) return;
            if (MessageBox.Show(this, "确定删除常用表格“" + selected.Name + "”？", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            CadTableTemplateStore.Delete(selected.Id);
            _templates.Items.Remove(selected);
        }

        private void CloseWith(CadTableStartAction action) { SelectedAction = action; Close(); }

        private static Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 31, Margin = new Padding(4, 2, 0, 2) };
            button.Click += (sender, args) => action();
            return button;
        }
    }

    internal sealed class CadTableNameForm : Form
    {
        private readonly TextBox _name = new TextBox();
        public string TemplateName { get { return _name.Text.Trim(); } }

        public CadTableNameForm(string initialName)
        {
            Text = "保存为常用表格";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(430, 135);
            Font = new Font("Microsoft YaHei UI", 9f);
            var label = new Label { Text = "表格名称", Left = 16, Top = 20, AutoSize = true };
            _name.SetBounds(88, 16, 325, 28);
            _name.Text = initialName ?? string.Empty;
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 328, Top = 78, Width = 85 };
            var save = new Button { Text = "保存", DialogResult = DialogResult.OK, Left = 235, Top = 78, Width = 85 };
            save.Click += (sender, args) => { if (TemplateName.Length == 0) { DialogResult = DialogResult.None; MessageBox.Show(this, "请输入表格名称。"); } };
            AcceptButton = save;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { label, _name, save, cancel });
        }
    }
}
