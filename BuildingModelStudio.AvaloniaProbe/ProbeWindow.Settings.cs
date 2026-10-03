using BatchPdfPublisher.BuildingModel;
namespace BuildingModelStudio.AvaloniaProbe;
internal sealed partial class ProbeWindow
{
    private StudioInteractionSettings _interactionSettings=StudioInteractionSettings.Load();
    private void ApplyInteractionSettings() {
        _planCanvas.GridVisible=_interactionSettings.GridVisible;_planCanvas.PickboxSize=_interactionSettings.PickboxSize;
        _planCanvas.CrosshairPercent=_interactionSettings.CrosshairPercent;_planCanvas.CrosshairColor=_interactionSettings.CrosshairColor;
        _planCanvas.InvalidateVisual();
    }
    private async Task OpenSystemSettingsAsync() {
        var dialog=new StudioSettingsWindow(_interactionSettings,_session.Model.DrawingScales);
        if(!await dialog.ShowDialog<bool>(this))return;
        if(!_session.TrySetDrawingScales(dialog.Scales,dialog.ApplyExisting,out var error)){_status.Text=error;return;}
        _interactionSettings=dialog.Interaction;ApplyInteractionSettings();
        try {_interactionSettings.Save();}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){_status.Text="设置已生效，但无法保存用户偏好："+ex.Message;return;}
        await RefreshModelAsync("系统设置已应用；出图比例随模型保存");
    }
    private void ToggleGrid() {
        _interactionSettings.GridVisible=!_interactionSettings.GridVisible;ApplyInteractionSettings();
        try {_interactionSettings.Save();}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){_status.Text="栅格已切换，但无法保存偏好："+ex.Message;return;}
        _status.Text=_interactionSettings.GridVisible?"栅格已显示（F7）":"栅格已隐藏（F7）";
    }
}
