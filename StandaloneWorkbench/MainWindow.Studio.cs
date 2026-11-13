using System;
using System.Threading.Tasks;
using System.Windows;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.StandaloneWorkbench
{
    public partial class MainWindow
    {
        private bool _openingStudio;

        private async void StudioWorkspace_Click(object sender, RoutedEventArgs args) => await OpenStudioAsync();

        internal async Task OpenStudioAsync()
        {
            if (_openingStudio) return;
            if (StudioEditor.IsRunning) { Workspace.SelectedIndex = 2; return; }
            if (Session.ModelPath == null) { Workspace.SelectedIndex = 1; Status.Text = "请先新建或选择建筑模型。"; return; }
            if (!ConfirmDraft()) return;
            _openingStudio = true;
            Workspace.SelectedIndex = 2;
            StudioMessage.Text = "正在打开建筑模型…";
            StudioTitle.Text = StudioMessage.Text;
            StudioMessage.Visibility = Visibility.Visible;
            StudioPanel.UpdateLayout();
            try
            {
                var state = await StudioEditor.OpenAsync(Session.ModelPath);
                AdoptStudioModel(state);
                StudioMessage.Visibility = Visibility.Collapsed;
                StudioTitle.Text = Session.Model.Name;
                Floors.IsReadOnly = true;
            }
            catch (Exception error) { StudioTitle.Text = StudioMessage.Text = "打开三维模型失败。" + error.Message; }
            finally { _openingStudio = false; }
        }

        private void AdoptStudioModel(StudioWorkspaceState state)
        {
            if (string.IsNullOrWhiteSpace(state.ModelPath)) return;
            Session.OpenModel(state.ModelPath);
            _dirty = false;
            ShowModel();
            ReloadLists();
        }

        private async Task<bool> CloseStudioAsync()
        {
            if (_openingStudio) { Status.Text = "模型正在打开，请稍后切换。"; return false; }
            if (!StudioEditor.IsRunning) return true;
            // Make the save decision visible even when invoked from 2D or management.
            Workspace.SelectedIndex = 2;
            try
            {
                var state = await StudioEditor.CloseAsync();
                if (!state.Success) { StudioTitle.Text = state.Message; return false; }
                AdoptStudioModel(state);
                Floors.IsReadOnly = false;
                return true;
            }
            catch (Exception error) { StudioTitle.Text = error.Message; return false; }
        }

        private async void SaveStudio_Click(object sender, RoutedEventArgs args) => await SaveStudioAsync(false);
        private async void SaveActive_Click(object sender, RoutedEventArgs args)
        {
            if (Workspace.SelectedIndex == 0) await CadEditor.SaveWithDialogAsync(false);
            else if (StudioEditor.IsRunning) await SaveStudioAsync(false);
            else SaveCurrent();
        }

        private async Task SaveStudioAsync(bool saveAs)
        {
            if (!StudioEditor.IsRunning) { SaveCurrent(); return; }
            Workspace.SelectedIndex = 2;
            try
            {
                var state = await StudioEditor.RequestAsync(saveAs ? "save-as" : "save");
                StudioTitle.Text = state.Message;
                if (state.Success) AdoptStudioModel(state);
            }
            catch (Exception error) { StudioTitle.Text = error.Message; }
        }

        private async void CloseStudio_Click(object sender, RoutedEventArgs args)
        {
            if (await CloseStudioAsync()) Workspace.SelectedIndex = 1;
        }
    }
}
