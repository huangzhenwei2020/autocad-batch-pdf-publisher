namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private ComponentLibraryWindow? _componentLibraryWindow;
    private async Task OpenComponentLibraryAsync()
    {
        if(_componentLibraryWindow!=null){_componentLibraryWindow.Activate();return;}
        var window=new ComponentLibraryWindow(_session.Model,_filePath);_componentLibraryWindow=window;
        try{await window.ShowDialog(this);if(window.OpenParameterPlacement)await OpenOpeningPlacementAsync();}
        finally{_componentLibraryWindow=null;}
    }
}
