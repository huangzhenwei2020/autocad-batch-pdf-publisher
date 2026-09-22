using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;

namespace BatchPdfPublisherLauncher
{
    [DataContract]
    internal sealed class CloudSyncHistoryRecord
    {
        [DataMember] public string RecordedAtUtc { get; set; }
        [DataMember] public CloudSyncOperationKind Kind { get; set; }
        [DataMember] public string LogicalPath { get; set; }
        [DataMember] public string Message { get; set; }

        public DateTime RecordedAt
        {
            get
            {
                DateTime value;
                return DateTime.TryParse(RecordedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out value)
                    ? value.ToLocalTime() : DateTime.MinValue;
            }
        }
    }

    [CollectionDataContract]
    internal sealed class CloudSyncHistoryRecords : List<CloudSyncHistoryRecord> { }

    internal static class CloudSyncHistoryStore
    {
        private static readonly object Sync = new object();
        private static string HistoryPath { get { return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "sync-history.json"); } }

        public static void Append(CloudSyncResult result, Exception failure)
        {
            try
            {
                var now = DateTime.UtcNow.ToString("O");
                var additions = new List<CloudSyncHistoryRecord>();
                if (result != null)
                    additions.AddRange(result.Operations.Where(operation => operation != null && operation.Kind != CloudSyncOperationKind.None)
                        .Select(operation => new CloudSyncHistoryRecord
                        {
                            RecordedAtUtc = now,
                            Kind = operation.Kind,
                            LogicalPath = operation.LogicalPath,
                            Message = operation.Message
                        }));
                if (failure != null)
                    additions.Add(new CloudSyncHistoryRecord
                    {
                        RecordedAtUtc = now,
                        Kind = CloudSyncOperationKind.Error,
                        Message = failure.GetBaseException().Message
                    });
                if (additions.Count == 0) return;

                lock (Sync)
                {
                    var records = LoadCore();
                    records.AddRange(additions);
                    var cutoff = DateTime.Now.AddDays(-90);
                    var retained = new CloudSyncHistoryRecords();
                    retained.AddRange(records.Where(record => record != null && record.RecordedAt >= cutoff)
                        .OrderByDescending(record => record.RecordedAt).Take(1000));
                    SaveCore(retained);
                }
            }
            catch { }
        }

        public static IList<CloudSyncHistoryRecord> Load()
        {
            lock (Sync) return LoadCore().OrderByDescending(record => record.RecordedAt).ToList();
        }

        public static void Clear()
        {
            lock (Sync) SaveCore(new CloudSyncHistoryRecords());
        }

        private static CloudSyncHistoryRecords LoadCore()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return new CloudSyncHistoryRecords();
                using (var stream = File.Open(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return (CloudSyncHistoryRecords)new DataContractJsonSerializer(typeof(CloudSyncHistoryRecords)).ReadObject(stream)
                        ?? new CloudSyncHistoryRecords();
            }
            catch { return new CloudSyncHistoryRecords(); }
        }

        private static void SaveCore(CloudSyncHistoryRecords records)
        {
            var directory = Path.GetDirectoryName(HistoryPath);
            Directory.CreateDirectory(directory);
            var temporary = HistoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    new DataContractJsonSerializer(typeof(CloudSyncHistoryRecords)).WriteObject(stream, records);
                if (File.Exists(HistoryPath)) File.Replace(temporary, HistoryPath, null);
                else File.Move(temporary, HistoryPath);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }

    internal sealed class CloudSyncHistoryForm : Form
    {
        private readonly ComboBox _filter;
        private readonly ListView _records;
        private readonly Label _summary;

        public CloudSyncHistoryForm()
        {
            Text = "万落建筑工具 · 同步记录";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 420);
            Size = new Size(980, 580);
            Font = new Font("Microsoft YaHei UI", 9F);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 9, 12, 7), WrapContents = false };
            toolbar.Controls.Add(new Label { Text = "显示", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
            _filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            _filter.Items.AddRange(new object[] { "全部记录", "上传", "下载", "需要处理" });
            _filter.SelectedIndex = 0;
            _filter.SelectedIndexChanged += delegate { RefreshRecords(); };
            toolbar.Controls.Add(_filter);
            var refresh = new Button { Text = "刷新", AutoSize = true, Height = 28, Margin = new Padding(10, 0, 0, 0) };
            refresh.Click += delegate { RefreshRecords(); };
            toolbar.Controls.Add(refresh);
            var clear = new Button { Text = "清空记录", AutoSize = true, Height = 28, Margin = new Padding(8, 0, 0, 0) };
            clear.Click += ClearRecords;
            toolbar.Controls.Add(clear);
            _summary = new Label { AutoSize = true, Margin = new Padding(18, 7, 0, 0), ForeColor = Color.DimGray };
            toolbar.Controls.Add(_summary);

            _records = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
            _records.Columns.Add("时间", 145);
            _records.Columns.Add("操作", 80);
            _records.Columns.Add("文件名", 210);
            _records.Columns.Add("同步路径", 290);
            _records.Columns.Add("结果", 360);

            Controls.Add(_records);
            Controls.Add(toolbar);
            Shown += delegate { RefreshRecords(); };
        }

        public void RefreshRecords()
        {
            var records = CloudSyncHistoryStore.Load();
            if (_filter.SelectedIndex == 1) records = records.Where(record => record.Kind == CloudSyncOperationKind.Upload).ToList();
            else if (_filter.SelectedIndex == 2) records = records.Where(record => record.Kind == CloudSyncOperationKind.Download).ToList();
            else if (_filter.SelectedIndex == 3) records = records.Where(record => record.Kind == CloudSyncOperationKind.Conflict ||
                record.Kind == CloudSyncOperationKind.Pending || record.Kind == CloudSyncOperationKind.Error).ToList();

            _records.BeginUpdate();
            _records.Items.Clear();
            foreach (var record in records)
            {
                var path = record.LogicalPath ?? string.Empty;
                var row = new ListViewItem(record.RecordedAt == DateTime.MinValue ? string.Empty : record.RecordedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                row.SubItems.Add(OperationText(record.Kind));
                row.SubItems.Add(Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)));
                row.SubItems.Add(path);
                row.SubItems.Add(record.Message ?? string.Empty);
                if (record.Kind == CloudSyncOperationKind.Error || record.Kind == CloudSyncOperationKind.Conflict) row.ForeColor = Color.Firebrick;
                else if (record.Kind == CloudSyncOperationKind.Pending) row.ForeColor = Color.DarkOrange;
                _records.Items.Add(row);
            }
            _records.EndUpdate();
            _summary.Text = records.Count == 0 ? "暂无同步记录" : "最近 " + records.Count + " 条";
        }

        private void ClearRecords(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, "确定清空同步记录吗？这不会删除云端或本机文件。", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            CloudSyncHistoryStore.Clear();
            RefreshRecords();
        }

        private static string OperationText(CloudSyncOperationKind kind)
        {
            switch (kind)
            {
                case CloudSyncOperationKind.Upload: return "上传";
                case CloudSyncOperationKind.Download: return "下载";
                case CloudSyncOperationKind.DeleteLocal:
                case CloudSyncOperationKind.DeleteRemote: return "删除";
                case CloudSyncOperationKind.Conflict: return "冲突";
                case CloudSyncOperationKind.Pending: return "待处理";
                case CloudSyncOperationKind.Error: return "失败";
                default: return "同步";
            }
        }
    }
}
