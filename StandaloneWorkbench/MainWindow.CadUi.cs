using System;
using System.Windows;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    public partial class MainWindow
    {
        private bool _cleanScreen;
        private bool _restoreProjectPanel;
        private void Minimize_Click(object sender, RoutedEventArgs args) => SystemCommands.MinimizeWindow(this);
        private void Maximize_Click(object sender, RoutedEventArgs args)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
        private void CloseWindow_Click(object sender, RoutedEventArgs args) => Close();

        private void ProjectPanel_Click(object sender, RoutedEventArgs args)
        {
            var show = ProjectPanel.Visibility != Visibility.Visible;
            ProjectPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ProjectColumn.Width = new GridLength(show ? 220 : 0);
        }
        private void DrawingWorkspace_Click(object sender, RoutedEventArgs args) => Workspace.SelectedIndex = 0;
        private void ModelWorkspace_Click(object sender, RoutedEventArgs args) => Workspace.SelectedIndex = 1;
        private void NewDrawing_Click(object sender, RoutedEventArgs args) { Workspace.SelectedIndex = 0; CadEditor.NewDrawing(); }
        private async void OpenDrawing_Click(object sender, RoutedEventArgs args) { Workspace.SelectedIndex = 0; await CadEditor.PickDrawingAsync(); }
        private async void SaveDrawing_Click(object sender, RoutedEventArgs args) { Workspace.SelectedIndex = 0; await CadEditor.SaveWithDialogAsync(false); }
        private async void SaveDrawingAs_Click(object sender, RoutedEventArgs args) { Workspace.SelectedIndex = 0; await CadEditor.SaveWithDialogAsync(true); }
        private async void CloseDrawing_Click(object sender, RoutedEventArgs args) => await CadEditor.CloseCurrentAsync();
        private async void QuickCommand_Click(object sender, RoutedEventArgs args)
        {
            var command = (string)((Button)sender).Tag;
            if (Workspace.SelectedIndex == 2 && StudioEditor.IsRunning)
            {
                try { var state = await StudioEditor.RequestAsync(command.TrimStart('@')); StudioTitle.Text = state.Message; }
                catch (Exception error) { StudioTitle.Text = error.Message; }
                return;
            }
            Workspace.SelectedIndex = 0;
            CadEditor.ExecuteCommand(command);
        }
        private void CleanScreen_Click(object sender, RoutedEventArgs args) => ToggleCleanScreen();
        private void ToggleCleanScreen()
        {
            Workspace.SelectedIndex = 0;
            _cleanScreen = !_cleanScreen;
            if (_cleanScreen)
            {
                _restoreProjectPanel = ProjectPanel.Visibility == Visibility.Visible;
                ProjectPanel.Visibility = Visibility.Collapsed;
                ProjectColumn.Width = new GridLength(0);
            }
            else if (_restoreProjectPanel)
            {
                ProjectPanel.Visibility = Visibility.Visible;
                ProjectColumn.Width = new GridLength(220);
            }
            CadEditor.SetCleanScreen(_cleanScreen);
        }
    }
}
