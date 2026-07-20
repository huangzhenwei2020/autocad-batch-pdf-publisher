using System.Windows.Controls;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    public partial class PublisherControl : UserControl
    {
        public PublisherControl()
        {
            InitializeComponent();
            DataContext = new PublisherViewModel();
        }
    }
}
