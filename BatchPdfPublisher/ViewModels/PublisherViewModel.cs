using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.ViewModels
{
    public sealed class PublisherViewModel : INotifyPropertyChanged
    {
        private readonly DrawingScanner _scanner = new DrawingScanner();
        private readonly PublishPlanStore _store = new PublishPlanStore();
        private string _selectedBuilding;
        private SheetItem _selectedSheet;
        private FrameDefinition _selectedFrame;
        private string _status = "请先录入图框，再扫描当前图纸。";

        public PublisherViewModel()
        {
            Frames = new ObservableCollection<FrameDefinition>(_store.LoadFrames());
            Sheets = new ObservableCollection<SheetItem>();
            Buildings = new ObservableCollection<string>();
            SheetView = CollectionViewSource.GetDefaultView(Sheets);
            SheetView.Filter = x => string.IsNullOrEmpty(SelectedBuilding) || ((SheetItem)x).Building == SelectedBuilding;
            ScanCommand = new RelayCommand(ScanCurrentDrawing);
            RegisterFrameCommand = new RelayCommand(StartFrameRegistration);
            RemoveFrameCommand = new RelayCommand(RemoveSelectedFrame, () => SelectedFrame != null);
            MoveUpCommand = new RelayCommand(() => Move(-1), () => SelectedSheet != null && SelectedSheet.Order > 1);
            MoveDownCommand = new RelayCommand(() => Move(1), () => SelectedSheet != null && SelectedSheet.Order < Sheets.Count);
            SaveFrameLibraryCommand = new RelayCommand(() => { _store.SaveFrames(Frames.ToList()); Status = "图框库已保存。"; });
            PublishPlanStore.FramesChanged += ReloadFrames;
        }

        public ObservableCollection<FrameDefinition> Frames { get; }
        public ObservableCollection<SheetItem> Sheets { get; }
        public ObservableCollection<string> Buildings { get; }
        public ICollectionView SheetView { get; }
        public ICommand ScanCommand { get; }
        public ICommand RegisterFrameCommand { get; }
        public ICommand RemoveFrameCommand { get; }
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand SaveFrameLibraryCommand { get; }
        public FrameDefinition SelectedFrame { get => _selectedFrame; set { _selectedFrame = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }
        public string SelectedBuilding { get => _selectedBuilding; set { _selectedBuilding = value; OnPropertyChanged(); SheetView.Refresh(); } }
        public SheetItem SelectedSheet { get => _selectedSheet; set { _selectedSheet = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }
        public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }

        private void ScanCurrentDrawing()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) { Status = "没有打开的图纸。"; return; }
            Sheets.Clear();
            foreach (var item in _scanner.Scan(document, Frames)) Sheets.Add(item);
            Buildings.Clear();
            foreach (var building in Sheets.Select(x => x.Building).Distinct()) Buildings.Add(building);
            SelectedBuilding = Buildings.FirstOrDefault();
            Status = Sheets.Count == 0 ? "没有找到已登记图框。请检查图框名称或图框库。" : $"已读取 {Sheets.Count} 个图框，图号、图名、楼栋和比例均来自图块属性。";
        }

        private void StartFrameRegistration()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) { Status = "没有打开的图纸。"; return; }
            document.SendStringToExecute("BPPICKFRAME ", true, false, false);
            Status = "请在图中选择图框图块，随后会弹出登记对话框。";
        }

        private void RemoveSelectedFrame()
        {
            Frames.Remove(SelectedFrame);
            SelectedFrame = null;
            _store.SaveFrames(Frames.ToList());
            Status = "已删除选中的图框登记。";
        }

        private void ReloadFrames()
        {
            Frames.Clear();
            foreach (var frame in _store.LoadFrames()) Frames.Add(frame);
            Status = "图框登记已保存。";
        }

        private void Move(int delta)
        {
            var index = Sheets.IndexOf(SelectedSheet); var target = index + delta;
            if (index < 0 || target < 0 || target >= Sheets.Count) return;
            Sheets.Move(index, target);
            for (var i = 0; i < Sheets.Count; i++) Sheets[i].Order = i + 1;
            SheetView.Refresh();
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
