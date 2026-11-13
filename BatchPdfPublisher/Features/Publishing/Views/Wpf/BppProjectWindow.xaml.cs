using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BatchPdfPublisher.Models;
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
            _viewModel.CreateOrSelectProject(ProjectName.Text, !exists && _folderChosen ? ProjectFolder.Text : null);
            RefreshProjects();
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
