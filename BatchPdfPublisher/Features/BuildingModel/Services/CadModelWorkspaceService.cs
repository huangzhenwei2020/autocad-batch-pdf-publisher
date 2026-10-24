using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.Views;
using Application=Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Features.BuildingModel.Services
{
    internal sealed class CadModelWorkspaceService : IDisposable
    {
        private static readonly Dictionary<Document,CadModelWorkspaceService> Windows=new Dictionary<Document,CadModelWorkspaceService>();
        private readonly Document _document;
        private readonly CadFloorRegistrationWindow _window;
        private bool _sourceClosed,_disposed;

        private CadModelWorkspaceService(Document document,CadFloorRegistrationWindow window)
        {
            _document=document;_window=window;
            Application.DocumentManager.DocumentActivated+=DocumentActivated;
            Application.DocumentManager.DocumentToBeDestroyed+=DocumentClosing;
            _window.Activated+=WindowActivated;_window.Closed+=WindowClosed;
        }
        internal static bool Activate(Document document)
        {
            if(!Windows.TryGetValue(document,out var entry))return false;
            if(entry._window.WindowState==WindowState.Minimized)entry._window.WindowState=WindowState.Normal;
            entry._window.Activate();entry.UpdateContext();return true;
        }
        internal static void Show(Document document,CadFloorRegistrationWindow window)
        {
            if(Activate(document))return;
            var entry=new CadModelWorkspaceService(document,window);Windows.Add(document,entry);
            try{Application.ShowModelessWindow(window);entry.UpdateContext();}
            catch{entry.Dispose();throw;}
        }
        internal static async Task RunCadAsync(Document document,Action ensure,Action action)
        {
            void Validate()
            {
                ensure();
                if(!string.IsNullOrEmpty(document.CommandInProgress))
                    throw new InvalidOperationException("CAD 正在执行命令，请先完成或取消该命令，再返回工作台操作。");
            }
            Validate();
            void Execute(){ensure();action();}
            if(Application.DocumentManager.IsApplicationContext)
                await CadCommandContext.ExecuteAsync(Execute);
            else Execute();
        }
        internal static IDisposable BeginInteraction(Document document,Window owner)
        {
            // WorkspaceCad hides/restores the modeless owner outside the native command callback.
            // Only child modal dialogs need AutoCAD's modal interaction scope.
            if(owner is CadFloorRegistrationWindow)return null;
            return document.Editor.StartUserInteraction(owner);
        }
        private void DocumentActivated(object sender,DocumentCollectionEventArgs args)=>UpdateContext();
        private void DocumentClosing(object sender,DocumentCollectionEventArgs args)
        {
            if(!ReferenceEquals(args.Document,_document))return;
            _sourceClosed=true;Windows.Remove(_document);UpdateContext();
        }
        private void WindowActivated(object sender,EventArgs args)=>UpdateContext();
        private void UpdateContext()
        {
            if(_disposed)return;
            var open=!_sourceClosed&&Application.DocumentManager.Cast<Document>().Any(d=>ReferenceEquals(d,_document));
            _window.SetCadContextState(!open?"源 CAD 图纸已关闭，当前草稿保留；登记已暂停。":
                ReferenceEquals(Application.DocumentManager.MdiActiveDocument,_document)?null:
                "登记已暂停：请切回工作台顶部所示的 CAD 图纸。当前模型与楼层草稿保留。");
        }
        private void WindowClosed(object sender,EventArgs args)=>Dispose();
        public void Dispose()
        {
            if(_disposed)return;_disposed=true;
            if(Windows.TryGetValue(_document,out var entry)&&ReferenceEquals(entry,this))Windows.Remove(_document);
            Application.DocumentManager.DocumentActivated-=DocumentActivated;
            Application.DocumentManager.DocumentToBeDestroyed-=DocumentClosing;
            _window.Activated-=WindowActivated;_window.Closed-=WindowClosed;
        }
    }
}
