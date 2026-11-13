using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public partial class BppValidationWindow : Window
    {
        private readonly Action<SheetValidationIssue> _locate;
        private readonly Action<SheetValidationIssue> _edit;
        public BppValidationWindow(IEnumerable<SheetValidationIssue> issues, Action<SheetValidationIssue> locate, Action<SheetValidationIssue> edit)
        {
            _locate = locate;
            _edit = edit;
            InitializeComponent();
            Issues.ItemsSource = issues.Where(issue => issue?.Sheet != null).Select(issue => new Row(issue)).ToList();
            if (Issues.Items.Count > 0)
                Issues.SelectedIndex = 0;
        }

        private void Locate_Click(object sender, RoutedEventArgs e)
        {
            if (Issues.SelectedItem is Row row)
                _locate?.Invoke(row.Issue);
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (Issues.SelectedItem is Row row)
                _edit?.Invoke(row.Issue);
        }

        private void Issues_DoubleClick(object sender, MouseButtonEventArgs e) => Locate_Click(sender, e);
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private sealed class Row
        {
            internal SheetValidationIssue Issue { get; }
            public string Text { get; }

            internal Row(SheetValidationIssue issue)
            {
                Issue = issue;
                Text = System.IO.Path.GetFileName(issue.Sheet.SourceFile) + " / " + issue.Sheet.SourceLayout + " / " + issue.Sheet.SheetNumber + " " + issue.Sheet.SheetName + "\n" + issue.Message;
            }
        }
    }
}
