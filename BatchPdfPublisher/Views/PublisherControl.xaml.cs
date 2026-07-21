using System.Windows.Controls;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    public partial class PublisherControl : UserControl
    {
        public PublisherControl()
        {
            InitializeComponent();
            Unloaded += PublisherControl_Unloaded;
        }

        public void AttachViewModel()
        {
            if (DataContext == null) DataContext = new PublisherViewModel();
        }

        private void FramesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var viewModel = DataContext as PublisherViewModel;
            if (viewModel?.EditFrameCommand.CanExecute(null) == true) viewModel.EditFrameCommand.Execute(null);
        }

        private void PublisherControl_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            (DataContext as System.IDisposable)?.Dispose();
        }
    }
}
