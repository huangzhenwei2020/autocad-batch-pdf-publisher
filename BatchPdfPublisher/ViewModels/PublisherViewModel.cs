using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Threading.Tasks;
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
        private bool _outputNextToCadFile;
        private bool _includeProjectNameInFileName = true;
        private bool _includeBuildingNameInFileName = true;
        private bool _overwriteExistingPdf;
        private bool _scanModelSpace = true;
        private bool _scanAllLayouts = true;
        private bool _mergeByBuilding;
        private bool _previewEnabled;
        private string _status = "请先录入图框，再扫描当前图纸。";
        private int _publishProgressValue;
        private int _publishProgressMaximum = 1;
        private int _scanProgressValue;
        private int _scanProgressMaximum = 1;
        private bool _isScanning;
        private bool _isPublishing;
        private SheetItem _previewErrorSheet;
        private bool _loadingProject;

        public PublisherViewModel()
        {
            Projects = new ObservableCollection<ProjectProfile>(_store.LoadProjects());
            Frames = new ObservableCollection<FrameDefinition>(_store.LoadFrames());
            Sheets = new ObservableCollection<SheetItem>();
            Buildings = new ObservableCollection<string>();
            CadFiles = new ObservableCollection<CadFileItem>();
            PublishBuildings = new ObservableCollection<BuildingPublishItem>();
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
        public ObservableCollection<CadFileItem> CadFiles { get; }
        public ObservableCollection<BuildingPublishItem> PublishBuildings { get; }
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
        public int ScanProgressValue { get => _scanProgressValue; private set { _scanProgressValue = value; OnPropertyChanged(); } }
        public int ScanProgressMaximum { get => _scanProgressMaximum; private set { _scanProgressMaximum = value; OnPropertyChanged(); } }
        public bool IsScanning { get => _isScanning; private set { _isScanning = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }
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
        public bool OutputNextToCadFile { get => _outputNextToCadFile; set { _outputNextToCadFile = value; if (!_loadingProject && _selectedProject != null) _selectedProject.OutputNextToCadFile = value; OnPropertyChanged(); } }
        public bool IncludeProjectNameInFileName { get => _includeProjectNameInFileName; set { _includeProjectNameInFileName = value; if (!_loadingProject && _selectedProject != null) _selectedProject.IncludeProjectNameInFileName = value; OnPropertyChanged(); } }
        public bool IncludeBuildingNameInFileName { get => _includeBuildingNameInFileName; set { _includeBuildingNameInFileName = value; if (!_loadingProject && _selectedProject != null) _selectedProject.IncludeBuildingNameInFileName = value; OnPropertyChanged(); } }
        public bool OverwriteExistingPdf { get => _overwriteExistingPdf; set { _overwriteExistingPdf = value; if (!_loadingProject && _selectedProject != null) _selectedProject.OverwriteExistingPdf = value; OnPropertyChanged(); } }
        public bool ScanModelSpace { get => _scanModelSpace; set { _scanModelSpace = value; if (!_loadingProject && _selectedProject != null) _selectedProject.ScanModelSpace = value; OnPropertyChanged(); } }
        public bool ScanAllLayouts { get => _scanAllLayouts; set { _scanAllLayouts = value; if (!_loadingProject && _selectedProject != null) _selectedProject.ScanAllLayouts = value; OnPropertyChanged(); } }
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
            var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename) ? document.Name : document.Database.Filename;
            AddCadFiles(new[] { document.Database.Filename });
            ScanCadFiles(new[] { sourceFile });
        }

        public void ScanCadFiles(System.Collections.Generic.IEnumerable<string> files)
        {
            var paths = (files ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x) && System.IO.File.Exists(x))
                .Select(System.IO.Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0) { Status = "没有选择可读取的 DWG 文件。"; return; }

            var active = Application.DocumentManager.MdiActiveDocument;
            var activePath = active == null ? null : active.Database.Filename;
            var previousBuilding = SelectedBuilding;
            _preview.Clear();
            var failures = new System.Collections.Generic.List<string>();
            var tianzhengFiles = new System.Collections.Generic.List<string>();
            var successfulPaths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scannedSheets = new System.Collections.Generic.List<SheetItem>();
            ScanProgressValue = 0;
            ScanProgressMaximum = Math.Max(paths.Count, 1);
            IsScanning = true;
            Status = "正在扫描 CAD：0 / " + ScanProgressMaximum;
            foreach (var path in paths)
            {
                try
                {
                    if (active != null && string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase))
                    {
                        using (active.LockDocument())
                        {
                            if (CadCompatibilityService.IsTianzhengDrawing(active.Database)) tianzhengFiles.Add(path);
                            scannedSheets.AddRange(_scanner.Scan(active, Frames, ScanModelSpace, ScanAllLayouts, _selectedProject?.SelectedLayouts));
                        }
                    }
                    else
                    {
                        using (var database = new Database(false, true))
                        {
                            database.ReadDwgFile(path, System.IO.FileShare.Read, true, string.Empty);
                            if (CadCompatibilityService.IsTianzhengDrawing(database)) tianzhengFiles.Add(path);
                            scannedSheets.AddRange(_scanner.Scan(database, path, Frames, ScanModelSpace, ScanAllLayouts, _selectedProject?.SelectedLayouts));
                        }
                    }
                    var cadItem = CadFiles.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (cadItem != null) cadItem.IsTianzheng = tianzhengFiles.Contains(path, StringComparer.OrdinalIgnoreCase);
                    successfulPaths.Add(path);
                }
                catch (System.Exception exception)
                {
                    failures.Add(System.IO.Path.GetFileName(path) + "（" + exception.Message + "）");
                }
                ScanProgressValue++;
                Status = "正在扫描 CAD：" + ScanProgressValue + " / " + ScanProgressMaximum + " · " + System.IO.Path.GetFileName(path);
                // Scanning uses AutoCAD's document/database API and therefore
                // stays on the CAD thread. Pump only the WinForms paint queue
                // so the progress track can visibly advance between files.
                System.Windows.Forms.Application.DoEvents();
            }
            IsScanning = false;
            // Only replace catalog rows belonging to files that were actually
            // rescanned. Other project DWGs stay in the accumulated catalog.
            var retained = Sheets.Where(x => string.IsNullOrWhiteSpace(x.SourceFile) || !successfulPaths.Contains(System.IO.Path.GetFullPath(x.SourceFile))).ToList();
            Sheets.Clear();
            foreach (var item in retained.Concat(scannedSheets)) Sheets.Add(item);
            NormalizeSheetOrder();
            RebuildBuildings(previousBuilding);
            SaveCurrentProject();
            var compatibilityWarning = tianzhengFiles.Count > 0 && !CadCompatibilityService.IsTianzhengHostLoaded()
                ? " 警告：检测到 " + tianzhengFiles.Count + " 个天正图纸，当前是纯 AutoCAD 环境；请用对应版本天正打开后发布，否则专业对象可能缺失。"
                : string.Empty;
            Status = $"已更新 {successfulPaths.Count} 个 CAD 文件，本次读取 {scannedSheets.Count} 张，工程清单共 {Sheets.Count} 张。"
                + compatibilityWarning + (failures.Count == 0 ? string.Empty : " 未读取：" + string.Join("；", failures));
        }

        public void AddCadFiles(System.Collections.Generic.IEnumerable<string> files)
        {
            var paths = (files ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var path in paths)
                if (!CadFiles.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
                    CadFiles.Add(new CadFileItem { Path = path, IsSelected = true });
            SaveCurrentProject();
        }

        public void ScanSelectedCadFiles()
        {
            var selected = CadFiles.Where(x => x.IsSelected).Select(x => x.Path).ToList();
            if (selected.Count == 0) { Status = "请在工程文件列表中至少勾选一个 CAD 文件。"; return; }
            ScanCadFiles(selected);
        }

        public void SetCadFileSelected(string path, bool isSelected)
        {
            var item = CadFiles.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (item != null) item.IsSelected = isSelected;
            SaveCurrentProject();
        }

        public void RemoveCadFile(string path)
        {
            var item = CadFiles.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            CadFiles.Remove(item);
            SaveCurrentProject();
            Status = "已从工程文件列表移除：" + System.IO.Path.GetFileName(path);
        }

        public void OpenCadFile(string path)
        {
            var existing = FindOpenDocument(path);
            if (existing != null)
            {
                Application.DocumentManager.MdiActiveDocument = existing;
                Status = "已激活 CAD 文件：" + System.IO.Path.GetFileName(path);
                return;
            }
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) { Status = "CAD 文件不存在：" + path; return; }
            Application.DocumentManager.Open(path, false);
            Status = "已打开 CAD 文件：" + System.IO.Path.GetFileName(path);
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
            if (note.IndexOf("总平图", StringComparison.OrdinalIgnoreCase) >= 0 || (sheet?.SheetNumber ?? string.Empty).IndexOf("总平图", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 3;
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
            CreateOrSelectProject(NewProjectName);
        }

        public bool CreateOrSelectProject(string requestedName)
        {
            var name = (requestedName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) { Status = "请输入工程名称。"; return false; }
            var existing = Projects.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { SelectedProject = existing; Status = "已切换到已有工程：" + name; return true; }
            var project = _store.CreateProject(name);
            Projects.Add(project);
            SelectedProject = project;
            NewProjectName = "新工程";
            Status = "已创建工程：" + name;
            return true;
        }

        public bool DeleteProject(ProjectProfile project)
        {
            if (project == null) { Status = "请选择要删除的工程。"; return false; }
            if (Projects.Count <= 1) { Status = "至少保留一个工程，不能删除最后一个工程。"; return false; }
            var wasSelected = ReferenceEquals(_selectedProject, project);
            if (!_store.DeleteProject(project.Name)) { Status = "删除工程失败。"; return false; }
            Projects.Remove(project);
            if (wasSelected)
            {
                // Do not go through SelectedProject here: its normal setter first saves
                // the old project, which would accidentally write the deleted profile back.
                _selectedProject = Projects.FirstOrDefault();
                if (_selectedProject != null)
                {
                    _store.SetActiveProject(_selectedProject.Name);
                    LoadProjectIntoEditor(_selectedProject);
                    RefreshPlotStyles();
                    OnPropertyChanged(nameof(SelectedProject));
                }
            }
            Status = "已删除工程参数：" + project.Name + "。项目文件夹中的 CAD 文件已保留。";
            return true;
        }

        public void SaveProjectParameters()
        {
            SaveCurrentProject();
            Status = "工程参数已保存。";
        }

        public string GetProjectFolder(ProjectProfile project = null)
        {
            return _store.GetProjectFolder(project ?? SelectedProject);
        }

        public void SetProjectFolder(string folder)
        {
            if (SelectedProject == null || string.IsNullOrWhiteSpace(folder)) return;
            SelectedProject.ProjectFolder = folder.Trim();
            SaveCurrentProject();
            Status = "项目文件夹已更新。";
        }

        public bool SaveCurrentCadToProjectFolder(out string destination, out string error)
        {
            destination = string.Empty;
            error = string.Empty;
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null || SelectedProject == null) { error = "请先打开 CAD 图纸并选择工程。"; return false; }
            try
            {
                var folder = System.IO.Path.Combine(GetProjectFolder(), "CAD");
                System.IO.Directory.CreateDirectory(folder);
                var sourceName = System.IO.Path.GetFileName(document.Database.Filename);
                if (string.IsNullOrWhiteSpace(sourceName)) sourceName = "未命名图纸.dwg";
                destination = System.IO.Path.Combine(folder, sourceName);
                using (document.LockDocument()) document.Database.SaveAs(destination, DwgVersion.Current);
                AddCadFiles(new[] { destination });
                SaveCurrentProject();
                Status = "已将当前 CAD 保存到工程文件夹：" + destination;
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void SaveCurrentProject()
        {
            if (_selectedProject == null || _loadingProject) return;
            _selectedProject.Frames = Frames.ToList();
            _selectedProject.SavedSheets = Sheets.Select(ToCatalogItem).ToList();
            _selectedProject.PlotStyle = PlotStyle;
            _selectedProject.MarginMode = MarginMode;
            _selectedProject.OutputDirectory = OutputDirectory;
            _selectedProject.OutputNextToCadFile = OutputNextToCadFile;
            _selectedProject.IncludeProjectNameInFileName = IncludeProjectNameInFileName;
            _selectedProject.IncludeBuildingNameInFileName = IncludeBuildingNameInFileName;
            _selectedProject.OverwriteExistingPdf = OverwriteExistingPdf;
            _selectedProject.ScanModelSpace = ScanModelSpace;
            _selectedProject.ScanAllLayouts = ScanAllLayouts;
            _selectedProject.SelectedPublishBuildings = PublishBuildings.Where(x => x.IsSelected).Select(x => x.Name).ToList();
            _selectedProject.CadFiles = CadFiles.Select(x => x.Path).ToList();
            _selectedProject.SelectedCadFiles = CadFiles.Where(x => x.IsSelected).Select(x => x.Path).ToList();
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
                CadFiles.Clear();
                var selectedCadFiles = project.SelectedCadFiles ?? new System.Collections.Generic.List<string>();
                foreach (var path in project.CadFiles ?? new System.Collections.Generic.List<string>())
                    CadFiles.Add(new CadFileItem { Path = path, IsSelected = selectedCadFiles.Count == 0 || selectedCadFiles.Contains(path) });
                foreach (var frame in project.Frames ?? new System.Collections.Generic.List<FrameDefinition>()) Frames.Add(frame);
                Sheets.Clear();
                foreach (var sheet in project.SavedSheets ?? new System.Collections.Generic.List<SheetCatalogItem>()) Sheets.Add(ToSheetItem(sheet));
                NormalizeSheetOrder();
                RebuildBuildings(null);
                PlotStyle = project.PlotStyle;
                MarginMode = project.MarginMode;
                OutputDirectory = project.OutputDirectory;
                OutputNextToCadFile = project.OutputNextToCadFile;
                IncludeProjectNameInFileName = project.IncludeProjectNameInFileName;
                IncludeBuildingNameInFileName = project.IncludeBuildingNameInFileName;
                OverwriteExistingPdf = project.OverwriteExistingPdf;
                ScanModelSpace = project.ScanModelSpace;
                ScanAllLayouts = project.ScanAllLayouts;
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
            var selectedForPublish = _selectedProject?.SelectedPublishBuildings ?? new System.Collections.Generic.List<string>();
            PublishBuildings.Clear();
            foreach (var building in Buildings)
                PublishBuildings.Add(new BuildingPublishItem { Name = building, ProjectName = _selectedProject?.Name, IsSelected = selectedForPublish.Count == 0 || selectedForPublish.Contains(building) });
        }

        public void SetPublishBuilding(string building, bool isSelected)
        {
            var item = PublishBuildings.FirstOrDefault(x => string.Equals(x.Name, building, StringComparison.Ordinal));
            if (item != null) item.IsSelected = isSelected;
            SaveCurrentProject();
        }

        public System.Collections.Generic.IReadOnlyList<string> GetActiveLayoutNames()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return new System.Collections.Generic.List<string>();
            using (document.LockDocument()) return _scanner.GetLayoutNames(document.Database);
        }

        public void SetSelectedLayouts(System.Collections.Generic.IEnumerable<string> names)
        {
            if (_selectedProject == null) return;
            _selectedProject.SelectedLayouts = (names ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            ScanAllLayouts = false;
            SaveCurrentProject();
        }

        public void SetScanScope(bool scanModelSpace, System.Collections.Generic.IEnumerable<string> layouts, bool allLayouts)
        {
            if (_selectedProject == null) return;
            _selectedProject.SelectedLayouts = (layouts ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            ScanModelSpace = scanModelSpace;
            ScanAllLayouts = allLayouts;
            SaveCurrentProject();
        }

        private static SheetCatalogItem ToCatalogItem(SheetItem sheet)
        {
            return new SheetCatalogItem
            {
                Order = sheet.Order, BlockHandle = sheet.BlockHandle, Building = sheet.Building, SheetNumber = sheet.SheetNumber, SheetName = sheet.SheetName,
                Frame = sheet.Frame, Extension = sheet.Extension, FrameNote = sheet.FrameNote, PaperOrientation = sheet.PaperOrientation,
                PrintScale = sheet.PrintScale, PlotStyle = sheet.PlotStyle, SourceFile = sheet.SourceFile, SourceLayout = sheet.SourceLayout,
                MinX = sheet.MinX, MinY = sheet.MinY, MaxX = sheet.MaxX, MaxY = sheet.MaxY
            };
        }

        private static SheetItem ToSheetItem(SheetCatalogItem sheet)
        {
            return new SheetItem
            {
                Order = sheet.Order, BlockHandle = sheet.BlockHandle, Building = sheet.Building, SheetNumber = sheet.SheetNumber, SheetName = sheet.SheetName,
                Frame = sheet.Frame, Extension = sheet.Extension, FrameNote = sheet.FrameNote, PaperOrientation = sheet.PaperOrientation,
                PrintScale = sheet.PrintScale, PlotStyle = sheet.PlotStyle, SourceFile = sheet.SourceFile, SourceLayout = sheet.SourceLayout,
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
                var document = Application.DocumentManager.MdiActiveDocument;
                var currentFile = document?.Database?.Filename;
                _preview.Show(document,
                    Sheets.Where(x => string.Equals(x.Building, SelectedBuilding, StringComparison.Ordinal)
                        && (string.IsNullOrWhiteSpace(currentFile) || string.Equals(x.SourceFile, currentFile, StringComparison.OrdinalIgnoreCase))),
                    SelectedSheet != null && string.Equals(SelectedSheet.SourceFile, currentFile, StringComparison.OrdinalIgnoreCase) ? SelectedSheet : null,
                    _previewErrorSheet != null && string.Equals(_previewErrorSheet.SourceFile, currentFile, StringComparison.OrdinalIgnoreCase) ? _previewErrorSheet : null);
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

        private async void PublishPdf()
        {
            var initialDocument = Application.DocumentManager.MdiActiveDocument;
            if (initialDocument == null) { Status = "没有打开的图纸。"; return; }
            if (IsPublishing) return;
            IsPublishing = true;
            var selectedNames = PublishBuildings.Where(x => x.IsSelected).Select(x => x.Name).ToList();
            var sheetsForPublish = Sheets.Where(x => selectedNames.Contains(x.Building)).ToList();
            if (sheetsForPublish.Count == 0) { Status = "请至少勾选一个要发布的子项目。"; IsPublishing = false; return; }
            if (!ConfirmTianzhengPublish(sheetsForPublish)) { IsPublishing = false; return; }
            PublishProgressValue = 0;
            PublishProgressMaximum = Math.Max(sheetsForPublish.Count, 1);
            try
            {
                var validationIssues = _publisher.ValidateAndNormalizeSheets(sheetsForPublish);
                OnPropertyChanged(nameof(Sheets));
                if (validationIssues.Count > 0)
                {
                    var issue = validationIssues[0];
                    _previewErrorSheet = issue.Sheet;
                    SelectedBuilding = issue.Sheet.Building;
                    SelectedSheet = issue.Sheet;
                    if (!PreviewEnabled) PreviewEnabled = true;
                    else UpdatePreview();
                    SaveCurrentProject();
                    Status = $"发布前检查发现 {validationIssues.Count} 个图框问题。第一个：{issue.Sheet.SheetNumber} {issue.Sheet.SheetName}。{issue.Message}";
                    Application.ShowAlertDialog(Status);
                    return;
                }
                _previewErrorSheet = null;
                _preview.Clear();
                SaveCurrentProject();
                Status = $"正在生成 PDF：0 / {PublishProgressMaximum}";
                var project = _selectedProject ?? new ProjectProfile
                {
                    Name = "默认工程",
                    PlotStyle = PlotStyle,
                    MarginMode = MarginMode,
                    OutputDirectory = OutputDirectory,
                    MergeByBuilding = MergeByBuilding
                };
                PdfPublishResult result = new PdfPublishResult();
                var completedBeforeGroup = 0;
                // Materialize every source group before activating another DWG.
                // AutoCAD can invalidate the original document wrapper while an
                // application-context continuation is resumed; deferred GroupBy
                // code must therefore never read document.Database mid-publish.
                var initialSourcePath = SafeDocumentPath(initialDocument);
                // A scanned row may retain a short/display path (for example
                // "Drawing1.dwg" or "[天正] Drawing1.dwg") even after the
                // drawing has been saved. Rebind those rows to the canonical
                // path of the already-open document before grouping jobs.
                foreach (var sheet in sheetsForPublish)
                {
                    var open = FindOpenDocument(sheet.SourceFile);
                    if (open != null)
                    {
                        var canonical = SafeDocumentPath(open);
                        if (!string.IsNullOrWhiteSpace(canonical)) sheet.SourceFile = canonical;
                    }
                }
                var sourceGroups = sheetsForPublish
                    .GroupBy(x => string.IsNullOrWhiteSpace(x.SourceFile) ? initialSourcePath : x.SourceFile, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new { SourcePath = x.Key, Sheets = x.Where(sheet => sheet != null).ToList() })
                    .Where(x => x.Sheets.Count > 0)
                    .ToList();
                WritePublishStage("发布计划已固化：" + sourceGroups.Count + " 个 CAD，" + sheetsForPublish.Count + " 张图纸。");
                foreach (var sourceGroup in sourceGroups)
                {
                    var sourcePath = sourceGroup.SourcePath;
                    WritePublishStage("准备切换 CAD：" + sourcePath + "；" + sourceGroup.Sheets.Count + " 张。");
                    var sourceDocument = FindOpenDocument(sourcePath);
                    if (sourceDocument == null && string.Equals(sourcePath, initialSourcePath, StringComparison.OrdinalIgnoreCase))
                        sourceDocument = initialDocument;
                    if (sourceDocument == null && !string.IsNullOrWhiteSpace(sourcePath))
                    {
                        if (!System.IO.File.Exists(sourcePath))
                            throw new System.IO.FileNotFoundException("发布所需的 CAD 文件不存在。", sourcePath);
                        sourceDocument = Application.DocumentManager.Open(sourcePath, false);
                    }
                    if (sourceDocument == null || sourceDocument.Database == null)
                        throw new System.InvalidOperationException("AutoCAD 无法打开发布所需的 CAD 文件：" + sourcePath);

                    // ExecuteInCommandContextAsync binds its command context to
                    // the document that is active when it is called. Opening and
                    // switching DWGs inside one callback leaves the callback bound
                    // to the original document, so PlotInfoValidator reports
                    // eLayoutNotCurrent even when the layout ObjectId is correct.
                    Application.DocumentManager.MdiActiveDocument = sourceDocument;
                    if (!ReferenceEquals(Application.DocumentManager.MdiActiveDocument, sourceDocument))
                        throw new System.InvalidOperationException("AutoCAD 无法把图纸切换为当前文档：" + sourcePath);
                    var baseProgress = completedBeforeGroup;
                    PdfPublishResult groupResult = null;
                    System.Exception groupException = null;
                    await Application.DocumentManager.ExecuteInCommandContextAsync(async unused =>
                    {
                        try
                        {
                            var publishedGroup = _publisher.Publish(sourceDocument, sourceGroup.Sheets, project, progress =>
                            {
                                PublishProgressMaximum = Math.Max(sheetsForPublish.Count, 1);
                                PublishProgressValue = baseProgress + progress.Current;
                                Status = $"正在生成 PDF：{PublishProgressValue} / {PublishProgressMaximum} · {progress.SheetLabel}";
                            });
                            // Assign through the outer variable after a successful
                            // publish so the application-context continuation can
                            // aggregate the result.
                            groupResult = publishedGroup;
                        }
                        catch (System.Exception exception)
                        {
                            // AutoCAD 2022 can consume exceptions thrown out of a
                            // command-context callback. Preserve the real error and
                            // rethrow it after returning to the modeless UI context.
                            groupException = exception;
                        }
                        await Task.CompletedTask;
                    }, null);
                    if (groupException != null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(groupException).Throw();
                    if (groupResult == null)
                        throw new System.InvalidOperationException("AutoCAD 没有返回当前 CAD 文件的 PDF 发布结果，请重新打开该图纸后再试。");
                    result.Files.AddRange(groupResult.Files);
                    result.SheetCount += groupResult.SheetCount;
                    completedBeforeGroup += sourceGroup.Sheets.Count;
                    WritePublishStage("CAD 发布完成：" + sourcePath + "；累计 " + completedBeforeGroup + " / " + sheetsForPublish.Count + " 张。");
                }
                if (result == null)
                    throw new System.InvalidOperationException("AutoCAD 没有返回 PDF 发布结果，请重新打开当前图纸后再试。");
                var outputDirectories = result.Files
                    .Select(System.IO.Path.GetDirectoryName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var outputSummary = outputDirectories.Count == 1
                    ? outputDirectories[0]
                    : outputDirectories.Count + " 个目录";
                Status = $"发布完成：{result.SheetCount} 张图纸，生成 {result.Files.Count} 个 PDF。输出到 {outputSummary}";
                if (PreviewEnabled) UpdatePreview();
            }
            catch (System.Exception exception)
            {
                Status = "PDF 发布失败：" + exception.Message;
                try
                {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BatchPdfPublisher.publish.log"),
                        System.DateTime.Now.ToString("s") + " 发布入口异常" + System.Environment.NewLine + exception + System.Environment.NewLine + System.Environment.NewLine);
                }
                catch { }
                Application.ShowAlertDialog(Status);
            }
            finally
            {
                IsPublishing = false;
            }
        }

        private static string SafeDocumentPath(Document document)
        {
            try
            {
                if (document == null || document.Database == null) return string.Empty;
                return string.IsNullOrWhiteSpace(document.Database.Filename) ? document.Name : document.Database.Filename;
            }
            catch { return string.Empty; }
        }

        private static Document FindOpenDocument(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return Application.DocumentManager.MdiActiveDocument;
            var requested = sourcePath.Trim();
            var requestedFull = requested;
            try { if (System.IO.Path.IsPathRooted(requested)) requestedFull = System.IO.Path.GetFullPath(requested); } catch { }
                var requestedName = string.Empty;
                var requestedStem = string.Empty;
            try
            {
                requestedName = System.IO.Path.GetFileName(requested);
                requestedStem = System.IO.Path.GetFileNameWithoutExtension(requestedName);
            }
            catch { }
            // Prefer the active document for an unsaved Drawing1/Drawing1.dwg
            // record, then compare exact paths, then compare file names. CAD
            // can expose an unsaved document with an empty Database.Filename,
            // so a strict File.Exists check is incorrect here.
            var active = Application.DocumentManager.MdiActiveDocument;
            if (active != null && IsSameOpenDocument(active, requested, requestedFull, requestedName, requestedStem)) return active;
            foreach (Document candidate in Application.DocumentManager)
            {
                try
                {
                    if (IsSameOpenDocument(candidate, requested, requestedFull, requestedName, requestedStem)) return candidate;
                }
                catch { }
            }
            return null;
        }

        private static bool IsSameOpenDocument(Document candidate, string requested, string requestedFull, string requestedName, string requestedStem)
        {
            var candidatePath = SafeDocumentPath(candidate);
            if (string.IsNullOrWhiteSpace(candidatePath)) return false;
            if (string.Equals(candidatePath, requested, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                if (string.Equals(System.IO.Path.GetFullPath(candidatePath), requestedFull, StringComparison.OrdinalIgnoreCase)) return true;
                var candidateName = System.IO.Path.GetFileName(candidatePath);
                if (!string.IsNullOrWhiteSpace(requestedName) && (string.Equals(candidateName, requestedName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(CleanCadDisplayName(candidateName), CleanCadDisplayName(requestedName), StringComparison.OrdinalIgnoreCase))) return true;
                var candidateStem = System.IO.Path.GetFileNameWithoutExtension(candidateName);
                if (!string.IsNullOrWhiteSpace(requestedStem) && (string.Equals(candidateStem, requestedStem, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(CleanCadDisplayName(candidateStem), CleanCadDisplayName(requestedStem), StringComparison.OrdinalIgnoreCase))) return true;
            }
            catch { }
            return false;
        }

        private static string CleanCadDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = value.Trim();
            if (text[0] == '[')
            {
                var end = text.IndexOf(']');
                if (end >= 0 && end + 1 < text.Length) text = text.Substring(end + 1).Trim();
            }
            return text;
        }

        private static void WritePublishStage(string message)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BatchPdfPublisher.publish.log"),
                    System.DateTime.Now.ToString("s") + " " + message + System.Environment.NewLine);
            }
            catch { }
        }

        private bool ConfirmTianzhengPublish(System.Collections.Generic.IEnumerable<SheetItem> sheets)
        {
            if (CadCompatibilityService.IsTianzhengHostLoaded()) return true;
            var sourcePaths = sheets.Select(x => x.SourceFile).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var detected = CadFiles.Where(x => x.IsTianzheng && sourcePaths.Contains(x.Path, StringComparer.OrdinalIgnoreCase)).Select(x => x.DisplayName).ToList();
            if (detected.Count == 0)
            {
                var active = Application.DocumentManager.MdiActiveDocument;
                try
                {
                    if (active != null && CadCompatibilityService.IsTianzhengDrawing(active.Database))
                        detected.Add(System.IO.Path.GetFileName(SafeDocumentPath(active)));
                }
                catch { }
            }
            if (detected.Count == 0) return true;
            var message = "检测到天正图纸，但当前 AutoCAD 进程没有加载天正运行环境：\r\n\r\n"
                + string.Join("\r\n", detected.Take(6))
                + (detected.Count > 6 ? "\r\n…另有 " + (detected.Count - 6) + " 个文件" : string.Empty)
                + "\r\n\r\n直接发布可能缺少天正专业对象。建议取消，改用对应版本天正打开插件后再发布。\r\n仍要继续吗？";
            return System.Windows.Forms.MessageBox.Show(message, "天正图纸兼容性提醒",
                System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning,
                System.Windows.Forms.MessageBoxDefaultButton.Button2) == System.Windows.Forms.DialogResult.Yes;
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
