using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace BatchPdfPublisher.Views
{
    public sealed class BuildingModelChoice
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Badge { get; set; }
        public int Value { get; set; }
    }

    public partial class BuildingModelChoiceWindow : Window
    {
        public int? SelectedValue { get; private set; }
        public bool BrowseRequested { get; private set; }

        public BuildingModelChoiceWindow(string heading, string description,
            IEnumerable<BuildingModelChoice> choices, bool allowBrowse, string acceptLabel)
        {
            InitializeComponent();
            HeadingText.Text = heading;
            DescriptionText.Text = description;
            AcceptButton.Content = acceptLabel;
            BrowseButton.Visibility = allowBrowse ? Visibility.Visible : Visibility.Collapsed;
            ChoicesList.ItemsSource = (choices ?? Enumerable.Empty<BuildingModelChoice>()).ToList();
            if (ChoicesList.Items.Count > 0) ChoicesList.SelectedIndex = 0;
            AcceptButton.IsEnabled = ChoicesList.SelectedItem != null;
            Loaded += (sender, args) =>
            {
                var area = SystemParameters.WorkArea;
                Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 32));
                Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 32));
                ChoicesList.Focus();
            };
            SourceInitialized += (sender, args) => SetDarkTitleBar();
        }

        private void ChoicesList_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            AcceptButton.IsEnabled = ChoicesList.SelectedItem != null;
        }

        private void ChoicesList_MouseDoubleClick(object sender, MouseButtonEventArgs args)
        {
            if (ChoicesList.SelectedItem != null) Accept();
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs args) { Accept(); }

        private void Accept()
        {
            var choice = ChoicesList.SelectedItem as BuildingModelChoice;
            if (choice == null) return;
            SelectedValue = choice.Value;
            DialogResult = true;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs args)
        {
            BrowseRequested = true;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs args) { DialogResult = false; }

        private void SetDarkTitleBar()
        {
            var enabled = 1;
            var handle = new WindowInteropHelper(this).Handle;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute,
            ref int value, int valueSize);
    }
}
