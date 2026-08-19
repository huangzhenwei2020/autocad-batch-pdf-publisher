using System.ComponentModel;

namespace BatchPdfPublisher.Models
{
    public sealed class CadFileItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private bool _isTianzheng;
        public string Path { get; set; }
        public string DisplayName => (_isTianzheng ? "[天正图纸] " : string.Empty) + System.IO.Path.GetFileName(Path);
        public override string ToString() => DisplayName;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        public bool IsTianzheng { get => _isTianzheng; set { if (_isTianzheng == value) return; _isTianzheng = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTianzheng))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
