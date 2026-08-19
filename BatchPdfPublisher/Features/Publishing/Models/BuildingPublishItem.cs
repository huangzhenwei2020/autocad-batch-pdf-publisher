using System.ComponentModel;

namespace BatchPdfPublisher.Models
{
    public sealed class BuildingPublishItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        public string Name { get; set; }
        public string ProjectName { get; set; }
        public override string ToString() => Name;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
