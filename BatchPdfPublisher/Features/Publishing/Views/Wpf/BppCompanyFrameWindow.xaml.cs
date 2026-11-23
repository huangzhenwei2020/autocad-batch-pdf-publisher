using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public partial class BppCompanyFrameWindow : Window
    {
        private readonly PublishPlanStore _store = new PublishPlanStore();
        private readonly FrameDefinition _selected;
        private readonly List<FrameDefinition> _frames;

        internal CompanyFrameLibrary SavedLibrary { get; private set; }

        internal BppCompanyFrameWindow(FrameDefinition selected, List<FrameDefinition> frames)
        {
            _selected = selected;
            _frames = frames;
            InitializeComponent();
            CompanyName.ItemsSource = _store.LoadCompanyFrameLibraries().Select(item => item.Name).OrderBy(name => name).ToList();
            SelectedFrame.Text = "所选图框  " + selected.DisplayName;
            IncludeAll.Content = "保存本项目全部 " + frames.Count + " 项已登记图框";
            IncludeAll.IsEnabled = frames.Count > 1;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var name = (CompanyName.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                Status.Text = "请输入公司名称。";
                CompanyName.Focus();
                return;
            }

            try
            {
                var company = _store.LoadCompanyFrameLibraries().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (company != null
                    && MessageBox.Show(this, "将新增图框或更新同版本的登记。选用「" + name + "」的项目下次扫描时会使用更新后的规则。继续吗？",
                        "更新公司图框库", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                var selected = IncludeAll.IsChecked == true ? _frames : new List<FrameDefinition> { _selected };
                if (!PrepareSnapshots(selected, company)) return;
                SavedLibrary = _store.SaveCompanyFrames(name, selected);
                DialogResult = true;
            }
            catch (Exception error)
            {
                Status.Text = "保存失败：" + error.Message;
            }
        }

        private bool PrepareSnapshots(List<FrameDefinition> selected, CompanyFrameLibrary company)
        {
            var missing = selected.Where(frame => FrameTemplatePaths.ExistingSnapshot(frame) == null
                && FrameTemplatePaths.ExistingSnapshot(PublishPlanStore.FindCompanyFrame(company, frame)) == null
                && !FrameTemplateStore.TryRestoreSnapshot(frame, out _)).ToList();
            if (missing.Count == 0) return true;

            Status.Text = "还有 " + missing.Count + " 项缺少模板，请选取含这些图框的原 DWG。";
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择含待补齐图框的原 DWG",
                Filter = "CAD 图纸|*.dwg",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) != true) return false;
            var failures = new List<string>();
            foreach (var frame in missing)
            {
                if (!FrameTemplateStore.TryRecover(frame, dialog.FileNames, out var error))
                    failures.Add(frame.BlockName + "：" + error);
            }
            if (failures.Count == 0) return true;

            Status.Text = "仍有 " + failures.Count + " 项缺少模板，公司图框库未写入。";
            MessageBox.Show(this, string.Join(Environment.NewLine, failures), "补齐公司图框模板", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
