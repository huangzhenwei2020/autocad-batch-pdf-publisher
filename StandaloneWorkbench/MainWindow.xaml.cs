using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using Microsoft.Win32;

namespace Wanluo.StandaloneWorkbench
{
    public partial class MainWindow : Window
    {
        internal readonly WorkbenchSession Session = new WorkbenchSession();
        private bool _refreshing;
        private bool _dirty;
        private bool _closingApproved;
        private bool _confirmingClose;
        private bool _changingProject;

        public MainWindow(string projectFolder)
        {
            InitializeComponent();
            StudioEditor.Exited += () =>
            {
                Floors.IsReadOnly = false;
                StudioMessage.Text = "三维模型已关闭，点击“三维建模”可重新打开。";
                StudioMessage.Visibility = Visibility.Visible;
                if (Session.ModelPath != null) Try(() => { Session.OpenModel(Session.ModelPath); ShowModel(); ReloadLists(); });
            };
            Title += " · " + WanluoArchitectureTools.ProductVersion.Display;
            FontSize = 12;
            StateChanged += (sender, args) => MaximizeButton.Icon = WindowState == WindowState.Maximized ? "copy" : "square";
            CadEditor.DrawingOpened += path => { Session.AddDrawing(path); ReloadLists(); };
            CadEditor.ActiveDrawingChanged += label => Title = "万落独立工作台 · " + WanluoArchitectureTools.ProductVersion.Display + (label.Length > 0 ? " · " + label : "");
            CadEditor.UiActionRequested += action =>
            {
                if (action == "project") ProjectPanel_Click(this, null);
                if (action == "models") ModelWorkspace_Click(this, null);
                if (action == "cad") Cad_Click(this, null);
                if (action == "clean") ToggleCleanScreen();
            };
            Loaded += (sender, args) =>
            {
                Width = Math.Min(Width, SystemParameters.WorkArea.Width);
                Height = Math.Min(Height, SystemParameters.WorkArea.Height);
                MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
                MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
                var projects = Session.Projects();
                var selected = projects.FirstOrDefault(project => projectFolder != null && SameFolder(project.ProjectFolder, projectFolder))
                    ?? projects.FirstOrDefault(project => project.Name == new BatchPdfPublisher.Services.PublishPlanStore().LoadActiveProjectName())
                    ?? projects.FirstOrDefault();
                ReloadProjects(selected);
            };
            Closing += ClosingWindow;
            Closed += (sender, args) => CadEditor.Dispose();
            PreviewKeyDown += async (sender, args) =>
            {
                if (args.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { ToggleCleanScreen(); args.Handled = true; return; }
                if (args.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (Workspace.SelectedIndex == 0) await CadEditor.SaveWithDialogAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    else if (StudioEditor.IsRunning) await SaveStudioAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    else SaveCurrent();
                    args.Handled = true;
                }
            };
        }

        private static bool SameFolder(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
                && string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private void ReloadProjects(ProjectProfile selected)
        {
            _refreshing = true;
            var projects = Session.Projects();
            Projects.ItemsSource = projects;
            Projects.SelectedItem = projects.FirstOrDefault(project => project.Name == selected?.Name) ?? projects.FirstOrDefault();
            _refreshing = false;
            if (Projects.SelectedItem is ProjectProfile project) SelectProject(project);
        }

        private void SelectProject(ProjectProfile project)
        {
            Session.SelectProject(project);
            ProjectLocation.Text = Session.ProjectFolder;
            ProjectLocation.ToolTip = Session.ProjectFolder;
            ReloadLists();
            _dirty = false;
            ShowModel();
            Status.Text = "已打开项目“" + project.Name + "”";
        }

        private async void Projects_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing || _changingProject || !(Projects.SelectedItem is ProjectProfile project)) return;
            _changingProject = true;
            Projects.IsEnabled = false;
            try
            {
                if (!await CloseStudioAsync() || !ConfirmDraft() || !await CadEditor.ConfirmCloseAllAsync(true))
                {
                    _refreshing = true;
                    Projects.SelectedItem = ((IEnumerable<ProjectProfile>)Projects.ItemsSource).FirstOrDefault(item => item.Name == Session.Project?.Name);
                    _refreshing = false;
                    return;
                }
                Try(() => SelectProject(project));
            }
            finally { _changingProject = false; Projects.IsEnabled = true; }
        }

        private void ReloadLists()
        {
            _refreshing = true;
            Drawings.ItemsSource = Session.Project?.CadFiles.Select(path => new DrawingFile(path)).ToList();
            Models.ItemsSource = Session.Models();
            Models.SelectedItem = ((IEnumerable<BuildingModelCatalogEntry>)Models.ItemsSource).FirstOrDefault(model => model.ModelPath == Session.ModelPath);
            _refreshing = false;
        }

        private async void NewProject_Click(object sender, RoutedEventArgs e)
        {
            if (!await CloseStudioAsync() || !ConfirmDraft() || !await CadEditor.ConfirmCloseAllAsync(true)) return;
            new InputDialog(this, "新建项目", "", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), (name, folder) =>
            {
                ReloadProjects(Session.CreateProject(name, folder));
                return true;
            }).ShowDialog();
        }

        private async void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            if (!await CloseStudioAsync() || !ConfirmDraft() || !await CadEditor.ConfirmCloseAllAsync(true)) return;
            var picker = new OpenFolderDialog { Title = "打开项目文件夹" };
            if (picker.ShowDialog(this) != true) return;
            var existing = Session.Projects().FirstOrDefault(project => SameFolder(project.ProjectFolder, picker.FolderName));
            if (existing != null) ReloadProjects(existing);
            else new InputDialog(this, "关联项目文件夹", Path.GetFileName(picker.FolderName), picker.FolderName, (name, folder) =>
            {
                ReloadProjects(Session.CreateProject(name, folder));
                return true;
            }).ShowDialog();
        }

        private void Cad_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDraft()) return;
            Try(() =>
            {
                var start = new ProcessStartInfo(Path.Combine(WorkbenchRuntime.PackageRoot, "万落建筑工具启动器.exe")) { UseShellExecute = false };
                start.ArgumentList.Add("--cad");
                if (Session.ProjectFolder != null)
                {
                    start.ArgumentList.Add("--project");
                    start.ArgumentList.Add(Session.ProjectFolder);
                }
                Process.Start(start);
            });
        }

        private async void NewModel_Click(object sender, RoutedEventArgs e)
        {
            if (!await CloseStudioAsync() || !ConfirmDraft()) return;
            if (Session.Project == null) { Status.Text = "请先选择项目。"; return; }
            new InputDialog(this, "新建建筑模型", "建筑模型", null, (name, folder) =>
            {
                BuildingModelProjectWorkflow.ValidateName(name);
                var path = BuildingModelJson.ModelFilePath(Session.ProjectFolder, name);
                var replace = false;
                if (File.Exists(path))
                {
                    var choice = InputDialog.Collision(this, name);
                    if (choice == "cancel") return false;
                    if (choice == "open")
                    {
                        LoadModel(path);
                        return true;
                    }
                    replace = true;
                }
                Session.NewModel(name, replace);
                _dirty = false;
                ReloadLists();
                ShowModel();
                Workspace.SelectedIndex = 1;
                Status.Text = "模型已创建并保存，可以设置楼层或打开三维编辑器。";
                return true;
            }).ShowDialog();
        }

        private async void OpenModel_Click(object sender, RoutedEventArgs e)
        {
            if (!await CloseStudioAsync() || !ConfirmDraft()) return;
            var picker = new OpenFileDialog { Title = "打开建筑模型", Filter = "建筑模型 (model.json)|*.json", InitialDirectory = Session.ProjectFolder ?? string.Empty };
            if (picker.ShowDialog(this) == true) Try(() => LoadModel(picker.FileName));
        }

        private async void Models_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing || !(Models.SelectedItem is BuildingModelCatalogEntry entry)) return;
            if (!await CloseStudioAsync() || !ConfirmDraft()) { ReloadLists(); return; }
            Try(() => LoadModel(entry.ModelPath));
        }

        private void LoadModel(string path)
        {
            Session.OpenModel(path);
            _dirty = false;
            ShowModel();
            ReloadLists();
            Workspace.SelectedIndex = 1;
            Status.Text = "已打开模型“" + Session.Model.Name + "”";
        }

        private void ShowModel()
        {
            Floors.ItemsSource = Session.Model?.Storeys;
            Floors.Items.Refresh();
            ModelTitle.Text = Session.Model == null ? "新建或打开建筑模型" : Session.Model.Name;
            ModelLocation.Text = Session.ModelPath ?? string.Empty;
            ModelLocation.ToolTip = Session.ModelPath;
            ModelDetails.Text = Session.Model == null ? "模型与 CAD 协同入口共用项目和模型目录。" :
                $"{Session.Model.Storeys.Count} 层 · 墙 {Session.Model.Walls.Count} · 门窗 {Session.Model.Openings.Count} · 楼板 {Session.Model.Slabs.Count} · 柱 {Session.Model.Columns.Count}。打开三维编辑器可绘制和编辑构件。";
        }

        private bool CommitFloors() => Floors.CommitEdit(DataGridEditingUnit.Cell, true) && Floors.CommitEdit(DataGridEditingUnit.Row, true);
        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (StudioEditor.IsRunning) await SaveStudioAsync(false);
            else SaveCurrent();
        }
        private bool SaveCurrent()
        {
            if (StudioEditor.IsRunning || _openingStudio) { Status.Text = "请使用三维工作区的保存模型。"; return false; }
            if (!CommitFloors()) return false;
            return Try(() =>
            {
                Session.SaveModel();
                _dirty = false;
                ShowModel();
                ReloadLists();
                Status.Text = "模型已保存。";
            });
        }

        private async void SaveCopy_Click(object sender, RoutedEventArgs e)
        {
            if (StudioEditor.IsRunning) { await SaveStudioAsync(true); return; }
            if (Session.Model == null || !CommitFloors()) return;
            new InputDialog(this, "另存模型", Session.Model.Name + "副本", null, (name, folder) =>
            {
                Session.SaveCopy(name);
                _dirty = false;
                ReloadLists();
                ShowModel();
                return true;
            }).ShowDialog();
        }

        private async void Editor_Click(object sender, RoutedEventArgs e) => await OpenStudioAsync();

        private void AddFloor_Click(object sender, RoutedEventArgs e)
        {
            if (StudioEditor.IsRunning) { Status.Text = "请在三维工作区的楼层设置中修改。"; return; }
            if (Session.Model == null || !CommitFloors()) return;
            var floors = Session.Model.Storeys;
            var number = floors.Count + 1;
            while (floors.Any(floor => floor.Id == number + "F")) number++;
            var previous = floors.LastOrDefault();
            floors.Add(new StoreyModel { Id = number + "F", Name = number + "层", Elevation = previous == null ? 0 : previous.Elevation + previous.Height, Height = previous?.Height ?? 3300 });
            _dirty = true;
            ShowModel();
        }

        private void RemoveFloor_Click(object sender, RoutedEventArgs e)
        {
            if (StudioEditor.IsRunning) { Status.Text = "请在三维工作区的楼层设置中修改。"; return; }
            if (Session.Model == null || !(Floors.SelectedItem is StoreyModel floor) || !CommitFloors()) return;
            if (Session.Model.Storeys.Count == 1) { Status.Text = "模型至少保留一个楼层。"; return; }
            var model = Session.Model;
            if (model.Walls.Any(item => item.StoreyId == floor.Id) || model.Slabs.Any(item => item.StoreyId == floor.Id)
                || model.Columns.Any(item => item.StoreyId == floor.Id) || model.Beams.Any(item => item.StoreyId == floor.Id)
                || model.Stairs.Any(item => item.StoreyId == floor.Id) || model.Roofs.Any(item => item.StoreyId == floor.Id)
                || model.Rooms.Any(item => item.StoreyId == floor.Id) || model.StoreyAxes.ContainsKey(floor.Id)
                || model.Storeys.Any(item => item.TemplateStoreyId == floor.Id)
                || model.DrawingViews?.Any(item => item.StoreyIds.Contains(floor.Id)) == true
                || File.Exists(CadFloorPlanRegistry.FilePath(Session.ModelPath)))
            {
                Status.Text = "模型已有构件、楼层引用或 CAD 登记，请在三维编辑器中调整楼层及关联。";
                return;
            }
            if (MessageBox.Show(this, "删除空楼层“" + floor.Name + "”？", "删除楼层", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Session.Model.Storeys.Remove(floor);
            _dirty = true;
            ShowModel();
        }

        private void Floors_EditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit) _dirty = true;
        }

        private void ReloadModel_Click(object sender, RoutedEventArgs e)
        {
            if (Session.ModelPath == null || !ConfirmDraft()) return;
            Try(() => LoadModel(Session.ModelPath));
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Try(ReloadLists);

        private bool ConfirmDraft()
        {
            if (!CommitFloors()) return false;
            if (!_dirty) return true;
            var result = MessageBox.Show(this, "保存当前模型的楼层修改？", "模型尚未保存", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return result == MessageBoxResult.No || result == MessageBoxResult.Yes && SaveCurrent();
        }

        private async void ClosingWindow(object sender, CancelEventArgs e)
        {
            if (App.IsSelfTest && !CadEditor.Client.IsBusy && !StudioEditor.IsRunning) return;
            if (_closingApproved) return;
            e.Cancel = true;
            if (_confirmingClose) return;
            _confirmingClose = true;
            try
            {
                if (!await CloseStudioAsync() || !ConfirmDraft() || !await CadEditor.ConfirmCloseAllAsync()) return;
                _closingApproved = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            finally { _confirmingClose = false; }
        }

        private async void Drawings_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_refreshing && Drawings.SelectedItem is DrawingFile file) await OpenDrawingAsync(file.Path);
        }

        internal async Task OpenDrawingAsync(string path)
        {
            Workspace.SelectedIndex = 0;
            await CadEditor.OpenDrawingAsync(path);
        }

        private bool Try(Action action)
        {
            try { action(); return true; }
            catch (Exception error) { Status.Text = error.Message; return false; }
        }

        private sealed class DrawingFile
        {
            public DrawingFile(string path) { Path = path; }
            public string Path { get; }
            public string Name => System.IO.Path.GetFileName(Path);
        }
    }
}
