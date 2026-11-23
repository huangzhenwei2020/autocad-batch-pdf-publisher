using System;
using System.Windows;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public partial class BppMissingCompanyFrameWindow : Window
    {
        private readonly FrameDefinition _frame;
        internal bool Deleted { get; private set; }

        internal BppMissingCompanyFrameWindow(FrameDefinition frame, string reason)
        {
            _frame = frame;
            InitializeComponent();
            FrameTitle.Text = frame.SelectionName;
            Status.Text = reason;
        }

        private void Choose_Click(object sender, RoutedEventArgs e)
        {
            var chooser = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择包含「" + _frame.BlockName + "」的图纸",
                Filter = "CAD 图纸|*.dwg"
            };
            if (chooser.ShowDialog(this) != true) return;
            if (FrameTemplateStore.TryRecover(_frame, new[] { chooser.FileName }, out var error))
                DialogResult = true;
            else
                Status.Text = error;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "从公司图框库删除「" + _frame.SelectionName + "」？使用该公司的项目将无法再选用这条登记。",
                "删除公司图框", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            try
            {
                new PublishPlanStore().DeleteMissingCompanyFrame(_frame);
                Deleted = true;
                DialogResult = false;
            }
            catch (Exception error)
            {
                Status.Text = error.Message;
            }
        }
    }
}
