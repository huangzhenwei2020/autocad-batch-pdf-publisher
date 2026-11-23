using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    public partial class BppProjectWindow : Window
    {
        private readonly PublisherViewModel _viewModel;
        private readonly List<LayoutChoice> _layouts = new List<LayoutChoice>();
        private bool _folderChosen;
        public BppProjectWindow(PublisherViewModel viewModel)
        {
            _viewModel = viewModel;
            InitializeComponent();
            RefreshProjects();
        }

        private void RefreshProjects()
        {
            var selected = _viewModel.SelectedProject;
            Projects.ItemsSource = _viewModel.Projects.Where(project => string.IsNullOrWhiteSpace(Search.Text) || project.Name.IndexOf(Search.Text, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            Projects.SelectedItem = selected;
            Status.Text = _viewModel.Status;
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            if (Projects != null)
                RefreshProjects();
        }

        private void Project_Changed(object sender, SelectionChangedEventArgs e)
        {
            var project = Projects.SelectedItem as ProjectProfile;
            if (project == null)
                return;
            ProjectName.Text = project.Name;
            ProjectFolder.Text = _viewModel.GetProjectFolder(project);
            RefreshCompanyFrames(project);
            AutoSave.Text = _viewModel.GetProjectAutoSaveMinutes(project).ToString();
            ModelSpace.IsChecked = project.ScanModelSpace;
            AllLayouts.IsChecked = project.ScanAllLayouts;
            _layouts.Clear();
            foreach (var name in _viewModel.GetActiveLayoutNames())
                _layouts.Add(new LayoutChoice { Name = name, IsSelected = project.ScanAllLayouts || project.SelectedLayouts.Contains(name) });
            LayoutItems.ItemsSource = null;
            LayoutItems.ItemsSource = _layouts;
            _folderChosen = false;
        }

        private void RefreshCompanyFrames(ProjectProfile project)
        {
            CompanyFrames.IsEnabled = true;
            try
            {
                var libraries = new PublishPlanStore().LoadCompanyFrameLibraries().OrderBy(item => item.Name).ToList();
                CompanyFramesInfo.Text = libraries.Count == 0
                    ? "可在「图框登记」中将现有图框存入公司图框库。"
                    : "用于默认扫描和自动套框。扫描时可临时切换，手动插入可选择任意公司。";
                libraries.Insert(0, new CompanyFrameLibrary { Id = string.Empty, Name = "仅使用本项目登记" });
                var currentId = project.CompanyFrameLibraryId ?? string.Empty;
                if (!libraries.Any(item => item.Id == currentId))
                    libraries.Add(new CompanyFrameLibrary { Id = currentId, Name = "本机缺少所选公司图框库" });
                CompanyFrames.ItemsSource = libraries;
                CompanyFrames.SelectedValue = currentId;
            }
            catch (Exception error)
            {
                CompanyFrames.IsEnabled = false;
                CompanyFramesInfo.Text = "公司图框库读取失败：" + error.Message;
            }
        }

        private void ActivateSelected()
        {
            if (Projects.SelectedItem is ProjectProfile project)
                _viewModel.SelectedProject = project;
        }

        private void ChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = BppDialog.ChooseFolder(this, ProjectFolder.Text);
            if (folder != null)
            {
                ProjectFolder.Text = folder;
                _folderChosen = true;
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            var exists = _viewModel.Projects.Any(project => string.Equals(project.Name, ProjectName.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            var companyId = CompanyFrames.IsEnabled ? CompanyFrames.SelectedValue as string : null;
            if (_viewModel.CreateOrSelectProject(ProjectName.Text, !exists && _folderChosen ? ProjectFolder.Text : null)
                && !exists && !string.IsNullOrWhiteSpace(companyId))
            {
                try { _viewModel.SetCompanyFrameLibrary(companyId); }
                catch (Exception error) { RefreshProjects(); Status.Text = "项目已创建，公司图框选择保存失败：" + error.Message; return; }
            }
            RefreshProjects();
        }

        private void SaveCompanyFrames_Click(object sender, RoutedEventArgs e)
        {
            if (!(Projects.SelectedItem is ProjectProfile) || !CompanyFrames.IsEnabled || !(CompanyFrames.SelectedValue is string companyId))
                return;
            ActivateSelected();
            try
            {
                _viewModel.SetCompanyFrameLibrary(companyId.Length == 0 ? null : companyId);
                Status.Text = _viewModel.Status;
            }
            catch (Exception error)
            {
                Status.Text = "公司图框选择保存失败：" + error.Message;
            }
        }

        private void Switch_Click(object sender, RoutedEventArgs e)
        {
            ActivateSelected();
            RefreshProjects();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!(Projects.SelectedItem is ProjectProfile project))
                return;
            if (MessageBox.Show(this, "删除项目记录“" + project.Name + "”？", "删除项目", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            _viewModel.DeleteProject(project);
            RefreshProjects();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ActivateSelected();
            if (!int.TryParse(AutoSave.Text, out var minutes) || minutes < 0 || minutes > 600)
            {
                Status.Text = "请输入 0～600 分钟。";
                return;
            }

            if (!_viewModel.SetProjectFolder(ProjectFolder.Text))
            {
                Status.Text = _viewModel.Status;
                return;
            }

            _viewModel.SetProjectAutoSaveMinutes(minutes);
            _viewModel.SaveProjectParameters();
            Status.Text = _viewModel.Status;
        }

        private void SaveNow_Click(object sender, RoutedEventArgs e)
        {
            ActivateSelected();
            Status.Text = _viewModel.SaveProjectAutoSaveNow();
        }

        private void Sync_Click(object sender, RoutedEventArgs e)
        {
            ActivateSelected();
            Status.Text = "已同步 " + _viewModel.SyncAutoSaveFiles() + " 个自动保存文件";
        }

        private void SaveScope_Click(object sender, RoutedEventArgs e)
        {
            ActivateSelected();
            _viewModel.SetScanScope(ModelSpace.IsChecked == true, _layouts.Where(layout => layout.IsSelected).Select(layout => layout.Name), AllLayouts.IsChecked == true);
            Status.Text = _viewModel.Status;
        }

        private void SaveCad_Click(object sender, RoutedEventArgs e)
        {
            ActivateSelected();
            Status.Text = _viewModel.SaveCurrentCadToProjectFolder(out var path, out var error) ? path : error;
        }

        private void Migrate_Click(object sender, RoutedEventArgs e)
        {
            ActivateSelected();
            if (MessageBox.Show(this, "将项目复制到填写的新目录并切换关联？", "复制迁移项目", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            _viewModel.MigrateProjectFolder(ProjectFolder.Text);
            RefreshProjects();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = ProjectFolder.Text;
            if (System.IO.Directory.Exists(folder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private sealed class LayoutChoice
        {
            public string Name { get; set; }
            public bool IsSelected { get; set; }
        }
    }
}
