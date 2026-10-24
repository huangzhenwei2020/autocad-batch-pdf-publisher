using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadModelWorkspaceHost
    {
        public string ProjectFolder, ProjectName, DrawingName, DrawingFingerprint;
        public Func<string> CurrentModel;
        public Action EnsureDocument;
        public Func<Action,Task> RunCad;
        public Func<ResourceDictionary> Theme;
        public Func<string, Task> Open;
        public Func<string, Task> Save;
        public Func<string, string, List<StoreyModel>, Task> SaveFloors;
        public Func<Window, string, Task<string>> SaveAs;
        public Func<CadFloorPlanRegistry, Task> Generate;
        public Func<CadFloorPlanRegistry,Action<CadGenerationProgress>,Task> GenerateWithProgress;
        public Action<Window> Probe;
        public Func<string,string,MessageBoxButton,MessageBoxResult> Confirm;
    }
}
