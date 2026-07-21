using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.ViewModels
{
    public sealed class PublisherViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly DrawingScanner _scanner = new DrawingScanner();
        private readonly PublishPlanStore _store = new PublishPlanStore();
        private readonly PrintRangePreviewService _preview = new PrintRangePreviewService();
        private readonly PdfPublisherService _publisher = new PdfPublisherService();
        private string _selectedBuilding;
        private SheetItem _selectedSheet;
        private FrameDefinition _selectedFrame;
        private ProjectProfile _selectedProject;
        private string _newProjectName = "新工程";
        private string _plotStyle;
        private string _marginMode;
        private string _outputDirectory;
        private bool _mergeByBuilding;
        private bool _previewEnabled = true;
        private string _status = "请先录入图框，再扫描当前图纸。";
        private int _publishProgressValue;
        private int _publishProgressMaximum = 1;
        private bool _isPublishing;
        private bool _loadingProject;

        public PublisherViewModel()
        {
            Projects = new ObservableCollection<ProjectProfile>(_store.LoadProjects());
            Frames = new ObservableCollection<FrameDefinition>(_store.LoadFrames());
            Sheets = new ObservableCollection<SheetItem>();
            Buildings = new ObservableCollection<string>();
            AvailablePlotStyles = new ObservableCollection<string>();
            SheetView = CollectionViewSource.GetDefaultView(Sheets);
            SheetView.Filter = x => string.IsNullOrEmpty(SelectedBuilding) || ((SheetItem)x).Building == SelectedBuilding;
            ScanCommand = new RelayCommand(ScanCurrentDrawing);
            RegisterFrameCommand = new RelayCommand(StartFrameRegistration);
            EditFrameCommand = new RelayCommand(EditSelectedFrame, () => SelectedFrame != null);
            RemoveFrameCommand = new RelayCommand(RemoveSelectedFrame, () => SelectedFrame != null);
            MoveUpCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
            MoveDownCommand = new RelayCommand(() => Move(1), () => CanMove(1));
            SaveFrameLibraryCommand = new RelayCommand(() => { _store.SaveFrames(Frames.ToList()); Status = "图框库已保存。"; });
            SaveProjectCommand = new RelayCommand(SaveCurrentProject);
            NewProjectCommand = new RelayCommand(CreateProject);
            RefreshPlotStylesCommand = new RelayCommand(RefreshPlotStyles);
            SaveFavoritePlotStyleCommand = new RelayCommand(SaveFavoritePlotStyle, () => !string.IsNullOrWhiteSpace(PlotStyle));
            PublishCommand = new RelayCommand(PublishPdf, () => Sheets.Count > 0);
            PublishPlanStore.FramesChanged += ReloadFrames;

            var activeName = _store.LoadActiveProjectName();
            SelectedProject = Projects.FirstOrDefault(x => string.Equals(x.Name, activeName, StringComparison.OrdinalIgnoreCase)) ?? Projects.FirstOrDefault();
            RefreshPlotStyles();
        }

        public ObservableCollection<ProjectProfile> Projects { get; }
        public ObservableCollection<FrameDefinition> Frames { get; }
        public ObservableCollection<SheetItem> Sheets { get; }
        public ObservableCollection<string> Buildings { get; }
        public ObservableCollection<string> AvailablePlotStyles { get; }
        public ICollectionView SheetView { get; }
        public ICommand ScanCommand { get; }
        public ICommand RegisterFrameCommand { get; }
        public ICommand EditFrameCommand { get; }
        public ICommand RemoveFrameCommand { get; }
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand SaveFrameLibraryCommand { get; }
        public ICommand SaveProjectCommand { get; }
        public ICommand NewProjectCommand { get; }
        public ICommand RefreshPlotStylesCommand { get; }
        public ICommand SaveFavoritePlotStyleCommand { get; }
        public ICommand PublishCommand { get; }
        public int PublishProgressValue { get => _publishProgressValue; private set { _publishProgressValue = value; OnPropertyChanged(); } }
        public int PublishProgressMaximum { get => _publishProgressMaximum; private set { _publishProgressMaximum = value; OnPropertyChanged(); } }
        public bool IsPublishing { get => _isPublishing; private set { _isPublishing = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }

        public ProjectProfile SelectedProject
        {
            get => _selectedProject;
            set
            {
                if (ReferenceEquals(_selectedProject, value) || value == null) return;
                SaveCurrentProject();
                _selectedProject = value;
                _store.SetActiveProject(value.Name);
                LoadProjectIntoEditor(value);
                OnPropertyChanged();
                RefreshPlotStyles();
                Status = "已切换到工程：" + value.Name;
            }
        }

        public string NewProjectName { get => _newProjectName; set { _newProjectName = value; OnPropertyChanged(); } }
        public FrameDefinition SelectedFrame { get => _selectedFrame; set { _selectedFrame = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }
        public string PlotStyle { get => _plotStyle; set { _plotStyle = value; if (!_loadingProject && _selectedProject != null) _selectedProject.PlotStyle = value; OnPropertyChanged(); } }
        public string MarginMode { get => _marginMode; set { _marginMode = value; if (!_loadingProject && _selectedProject != null) _selectedProject.MarginMode = value; OnPropertyChanged(); } }
        public string OutputDirectory { get => _outputDirectory; set { _outputDirectory = value; if (!_loadingProject && _selectedProject != null) _selectedProject.OutputDirectory = value; OnPropertyChanged(); } }
        public bool MergeByBuilding { get => _mergeByBuilding; set { _mergeByBuilding = value; if (!_loadingProject && _selectedProject != null) _selectedProject.MergeByBuilding = value; OnPropertyChanged(); } }
        public bool PreviewEnabled
        {
            get => _previewEnabled;
            set
            {
                if (_previewEnabled == value) return;
                _previewEnabled = value;
                if (!_loadingProject && _selectedProject != null) _selectedProject.PreviewEnabled = value;
                OnPropertyChanged();
                if (value) UpdatePreview(); else _preview.Clear();
            }
        }
        public string SelectedBuilding
        {
            get => _selectedBuilding;
            set
            {
                if (string.Equals(_selectedBuilding, value, StringComparison.Ordinal)) return;
                _selectedBuilding = value;
                OnPropertyChanged();
                SheetView.Refresh();
                var visible = VisibleSheets().ToList();
                if (_selectedSheet == null || !visible.Contains(_selectedSheet)) SelectedSheet = visible.FirstOrDefault();
                else UpdatePreview();
            }
        }
        public SheetItem SelectedSheet
        {
            get => _selectedSheet;
            set
            {
                if (ReferenceEquals(_selectedSheet, value)) return;
                _selectedSheet = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
                UpdatePreview();
            }
        }
        public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }

        private void ScanCurrentDrawing()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) { Status = "没有打开的图纸。"; return; }
            var previousBuilding = SelectedBuilding;
            _preview.Clear();
            Sheets.Clear();
            using (document.LockDocument())
                foreach (var item in _scanner.Scan(document, Frames)) Sheets.Add(item);
            NormalizeSheetOrder();
            RebuildBuildings(previousBuilding);
            SaveCurrentProject();
            Status = Sheets.Count == 0 ? "没有找到已登记图框。请检查图框名称或图框库。" : $"已读取 {Sheets.Count} 个图框；封面和目录已优先排序，同一子项目内可继续手工调整。";
        }

        public void ApplySheetEdits()
        {
            var selected = SelectedSheet;
            var ordered = Sheets.OrderBy(x => x.Building)
                .ThenBy(RequiredTitlePriority)
                .ThenBy(x => x.Order)
                .ThenBy(x => x.SheetNumber)
                .ToList();
            for (var target = 0; target < ordered.Count; target++)
            {
                var current = Sheets.IndexOf(ordered[target]);
                if (current != target) Sheets.Move(current, target);
            }
            NormalizeSheetOrder();
            var preferredBuilding = selected?.Building ?? SelectedBuilding;
            RebuildBuildings(preferredBuilding);
            SelectedSheet = selected;
            SaveCurrentProject();
            Status = "图纸列表已保存；封面和目录保持在当前子项目前面。";
        }

        private static int RequiredTitlePriority(SheetItem sheet)
        {
            var note = sheet?.FrameNote ?? string.Empty;
            if (note.IndexOf("封面", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (note.IndexOf("目录", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 2;
        }

        private void StartFrameRegistration()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) { Status = "没有打开的图纸。"; return; }
            document.SendStringToExecute("BPPICKFRAME ", true, false, false);
            Status = "请在图中选择图框图块，随后会弹出登记对话框。";
        }

        private void EditSelectedFrame()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null || SelectedFrame == null) { Status = "请选择要修改的图框登记。"; return; }
            new FrameRegistrationService().Edit(document, SelectedFrame);
        }

        private void RemoveSelectedFrame()
        {
            Frames.Remove(SelectedFrame);
            SelectedFrame = null;
            _store.SaveFrames(Frames.ToList());
            Status = "已删除选中的图框登记。";
        }

        private void CreateProject()
        {
            var name = (NewProjectName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) { Status = "请输入工程名称。"; return; }
            var existing = Projects.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { SelectedProject = existing; Status = "已切换到已有工程：" + name; return; }
            var project = _store.CreateProject(name);
            Projects.Add(project);
            SelectedProject = project;
            NewProjectName = "新工程";
            Status = "已创建工程：" + name;
        }

        private void SaveCurrentProject()
        {
            if (_selectedProject == null || _loadingProject) return;
            _selectedProject.Frames = Frames.ToList();
            _selectedProject.SavedSheets = Sheets.Select(ToCatalogItem).ToList();
            _selectedProject.PlotStyle = PlotStyle;
            _selectedProject.MarginMode = MarginMode;
            _selectedProject.OutputDirectory = OutputDirectory;
            _selectedProject.MergeByBuilding = MergeByBuilding;
            _selectedProject.PreviewEnabled = PreviewEnabled;
            _store.SaveProject(_selectedProject);
        }

        private void LoadProjectIntoEditor(ProjectProfile project)
        {
            _loadingProject = true;
            try
            {
                Frames.Clear();
                foreach (var frame in project.Frames ?? new System.Collections.Generic.List<FrameDefinition>()) Frames.Add(frame);
                Sheets.Clear();
                foreach (var sheet in project.SavedSheets ?? new System.Collections.Generic.List<SheetCatalogItem>()) Sheets.Add(ToSheetItem(sheet));
                NormalizeSheetOrder();
                RebuildBuildings(null);
                PlotStyle = project.PlotStyle;
                MarginMode = project.MarginMode;
                OutputDirectory = project.OutputDirectory;
                MergeByBuilding = project.MergeByBuilding;
                PreviewEnabled = project.PreviewEnabled;
                SelectedFrame = Frames.FirstOrDefault();
            }
            finally { _loadingProject = false; }
        }

        private void ReloadFrames()
        {
            Frames.Clear();
            foreach (var frame in _store.LoadFrames()) Frames.Add(frame);
            if (_selectedProject != null) _selectedProject.Frames = Frames.ToList();
            Status = "图框登记已保存。";
        }

        private void NormalizeSheetOrder()
        {
            var ordered = Sheets.OrderBy(x => x.Building).ThenBy(RequiredTitlePriority).ThenBy(x => x.Order).ThenBy(x => x.SheetNumber).ToList();
            for (var target = 0; target < ordered.Count; target++)
            {
                var current = Sheets.IndexOf(ordered[target]);
                if (current != target) Sheets.Move(current, target);
            }
            foreach (var group in Sheets.GroupBy(x => x.Building))
            {
                var order = 1;
                foreach (var item in group) item.Order = order++;
            }
        }

        private void RebuildBuildings(string preferredBuilding)
        {
            Buildings.Clear();
            foreach (var building in Sheets.Select(x => x.Building).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()) Buildings.Add(building);
            var selected = Buildings.Contains(preferredBuilding) ? preferredBuilding : Buildings.FirstOrDefault();
            if (!string.Equals(SelectedBuilding, selected, StringComparison.Ordinal)) SelectedBuilding = selected;
            else { SheetView.Refresh(); UpdatePreview(); }
        }

        private static SheetCatalogItem ToCatalogItem(SheetItem sheet)
        {
            return new SheetCatalogItem
            {
                Order = sheet.Order, BlockHandle = sheet.BlockHandle, Building = sheet.Building, SheetNumber = sheet.SheetNumber, SheetName = sheet.SheetName,
                Frame = sheet.Frame, Extension = sheet.Extension, FrameNote = sheet.FrameNote, PaperOrientation = sheet.PaperOrientation,
                PrintScale = sheet.PrintScale, PlotStyle = sheet.PlotStyle, SourceFile = sheet.SourceFile,
                MinX = sheet.MinX, MinY = sheet.MinY, MaxX = sheet.MaxX, MaxY = sheet.MaxY
            };
        }

        private static SheetItem ToSheetItem(SheetCatalogItem sheet)
        {
            return new SheetItem
            {
                Order = sheet.Order, BlockHandle = sheet.BlockHandle, Building = sheet.Building, SheetNumber = sheet.SheetNumber, SheetName = sheet.SheetName,
                Frame = sheet.Frame, Extension = sheet.Extension, FrameNote = sheet.FrameNote, PaperOrientation = sheet.PaperOrientation,
                PrintScale = sheet.PrintScale, PlotStyle = sheet.PlotStyle, SourceFile = sheet.SourceFile,
                MinX = sheet.MinX, MinY = sheet.MinY, MaxX = sheet.MaxX, MaxY = sheet.MaxY
            };
        }

        private void Move(int delta)
        {
            var visible = VisibleSheets().ToList();
            var visibleIndex = visible.IndexOf(SelectedSheet);
            var targetVisibleIndex = visibleIndex + delta;
            if (visibleIndex < 0 || targetVisibleIndex < 0 || targetVisibleIndex >= visible.Count) return;
            var index = Sheets.IndexOf(SelectedSheet);
            var target = Sheets.IndexOf(visible[targetVisibleIndex]);
            Sheets.Move(index, target);
            visible = VisibleSheets().ToList();
            for (var i = 0; i < visible.Count; i++) visible[i].Order = i + 1;
            ApplySheetEdits();
        }

        private bool CanMove(int delta)
        {
            if (SelectedSheet == null) return false;
            var visible = VisibleSheets().ToList();
            var index = visible.IndexOf(SelectedSheet);
            var target = index + delta;
            return index >= 0 && target >= 0 && target < visible.Count;
        }

        private System.Collections.Generic.IEnumerable<SheetItem> VisibleSheets()
        {
            return Sheets.Where(x => string.IsNullOrEmpty(SelectedBuilding) || x.Building == SelectedBuilding);
        }

        private void UpdatePreview()
        {
            if (!PreviewEnabled || string.IsNullOrWhiteSpace(SelectedBuilding))
            {
                _preview.Clear();
                return;
            }
            try
            {
                _preview.Show(Application.DocumentManager.MdiActiveDocument,
                    Sheets.Where(x => string.Equals(x.Building, SelectedBuilding, StringComparison.Ordinal)), SelectedSheet);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                _preview.Clear();
                Status = "部分第三方图框的范围无效，已跳过预览；图纸列表仍可继续使用。";
            }
        }

        private void RefreshPlotStyles()
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string name in PlotSettingsValidator.Current.GetPlotStyleSheetList())
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            catch { }
            if (_selectedProject?.FavoritePlotStyles != null)
                foreach (var name in _selectedProject.FavoritePlotStyles)
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            if (!string.IsNullOrWhiteSpace(PlotStyle)) names.Add(PlotStyle);
            AvailablePlotStyles.Clear();
            foreach (var name in names.OrderBy(x => x)) AvailablePlotStyles.Add(name);
        }

        private void SaveFavoritePlotStyle()
        {
            if (_selectedProject == null || string.IsNullOrWhiteSpace(PlotStyle)) return;
            if (_selectedProject.FavoritePlotStyles == null) _selectedProject.FavoritePlotStyles = new System.Collections.Generic.List<string>();
            if (!_selectedProject.FavoritePlotStyles.Any(x => string.Equals(x, PlotStyle, StringComparison.OrdinalIgnoreCase)))
                _selectedProject.FavoritePlotStyles.Add(PlotStyle);
            SaveCurrentProject();
            RefreshPlotStyles();
            Status = "已收藏打印样式：" + PlotStyle;
        }

        private void PublishPdf()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) { Status = "没有打开的图纸。"; return; }
            try
            {
                _preview.Clear();
                SaveCurrentProject();
                IsPublishing = true;
                PublishProgressValue = 0;
                PublishProgressMaximum = Math.Max(Sheets.Count, 1);
                Status = $"正在生成 PDF：0 / {PublishProgressMaximum}";
                var project = _selectedProject ?? new ProjectProfile
                {
                    Name = "默认工程",
                    PlotStyle = PlotStyle,
                    MarginMode = MarginMode,
                    OutputDirectory = OutputDirectory,
                    MergeByBuilding = MergeByBuilding
                };
                var result = _publisher.Publish(document, Sheets, project, progress =>
                {
                    PublishProgressMaximum = Math.Max(progress.Total, 1);
                    PublishProgressValue = progress.Current;
                    Status = $"正在生成 PDF：{progress.Current} / {progress.Total} · {progress.SheetLabel}";
                });
                Status = $"发布完成：{result.SheetCount} 张图纸，生成 {result.Files.Count} 个 PDF。输出到 {project.OutputDirectory}";
                if (PreviewEnabled) UpdatePreview();
            }
            catch (System.Exception exception)
            {
                Status = "PDF 发布失败：" + exception.Message;
                Application.ShowAlertDialog(Status);
            }
            finally
            {
                IsPublishing = false;
            }
        }

        public void Dispose()
        {
            PublishPlanStore.FramesChanged -= ReloadFrames;
            _preview.Dispose();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute; private readonly Func<bool> _canExecute;
        public RelayCommand(Action execute, Func<bool> canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();
        public void Execute(object parameter) => _execute();
        public event EventHandler CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    }
}
