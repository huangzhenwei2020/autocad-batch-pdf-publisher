using System;
using System.IO;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    public partial class BppOutputSettingsWindow : Window
    {
        private readonly PublisherViewModel _viewModel;
        public BppOutputSettingsWindow(PublisherViewModel viewModel)
        {
            _viewModel = viewModel;
            InitializeComponent();
            MarginBox.ItemsSource = new[]
            {
                "自动适配",
                "无白边（满幅）",
                "保留 3 mm 白边"
            };
            DataContext = viewModel;
            RefreshOutput();
            viewModel.PropertyChanged += ViewModelChanged;
            Closed += (sender, args) =>
            {
                viewModel.PropertyChanged -= ViewModelChanged;
                viewModel.SaveProjectCommand.Execute(null);
            };
        }

        private void ViewModelChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(PublisherViewModel.OutputDirectory) || args.PropertyName == nameof(PublisherViewModel.OutputNextToCadFile))
                RefreshOutput();
        }

        private void ChooseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = BppDialog.ChooseFolder(this, _viewModel.OutputDirectory);
            if (folder != null)
                _viewModel.OutputDirectory = folder;
            RefreshOutput();
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e) => BppDialog.OpenOutputDirectory(_viewModel, this);
        private void PublishBuildingChecked_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is BuildingPublishItem item)
                _viewModel.SetPublishBuilding(item.Name, item.IsSelected);
        }

        private void SelectBuildings_Click(object sender, RoutedEventArgs e) => SetBuildings(true);
        private void ClearBuildings_Click(object sender, RoutedEventArgs e) => SetBuildings(false);
        private void SetBuildings(bool selected)
        {
            foreach (var item in _viewModel.PublishBuildings.ToList())
                _viewModel.SetPublishBuilding(item.Name, selected);
        }

        private void RefreshOutput()
        {
            ActualOutputLabel.Text = _viewModel.OutputNextToCadFile ? string.Join(Environment.NewLine, _viewModel.CadFiles.Where(file => file.IsSelected).Select(file => Path.GetDirectoryName(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase)) : _viewModel.OutputDirectory;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
