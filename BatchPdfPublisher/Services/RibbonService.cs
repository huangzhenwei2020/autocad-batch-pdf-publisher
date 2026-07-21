using System;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;

namespace BatchPdfPublisher.Services
{
    public static class RibbonService
    {
        private const string TabId = "BPP_BATCH_PDF_TAB";
        private static EventHandler _idleHandler;

        public static void InstallWhenReady()
        {
            if (_idleHandler != null) return;
            _idleHandler = (sender, args) =>
            {
                var ribbon = ComponentManager.Ribbon;
                if (ribbon == null) return;
                var existing = ribbon.FindTab(TabId);
                if (existing != null)
                {
                    // Switching drawings or workspaces can keep the tab object
                    // but hide it. Restore visibility instead of returning silently.
                    existing.IsVisible = true;
                    return;
                }

                var tab = new RibbonTab { Id = TabId, Title = "批量打印" };
                var source = new RibbonPanelSource { Title = "PDF 发布工具" };
                var panel = new RibbonPanel { Source = source };
                source.Items.Add(CreateButton("打开面板", "BPPUBLISH ", RibbonItemSize.Standard));
                source.Items.Add(CreateButton("扫描图纸", "BPPSCAN ", RibbonItemSize.Standard));
                source.Items.Add(CreateButton("登记图框", "BPPICKFRAME ", RibbonItemSize.Standard));
                source.Items.Add(CreateButton("发布 PDF", "BPPMAKEPDF ", RibbonItemSize.Standard));
                tab.Panels.Add(panel);
                ribbon.Tabs.Add(tab);
            };
            Application.Idle += _idleHandler;
        }

        public static void Remove()
        {
            if (_idleHandler != null)
            {
                Application.Idle -= _idleHandler;
                _idleHandler = null;
            }
            var ribbon = ComponentManager.Ribbon;
            var tab = ribbon?.FindTab(TabId);
            if (tab != null) ribbon.Tabs.Remove(tab);
        }

        private static RibbonButton CreateButton(string text, string command, RibbonItemSize size)
        {
            return new RibbonButton
            {
                Text = text,
                ShowText = true,
                Size = size,
                Orientation = Orientation.Horizontal,
                CommandParameter = command,
                CommandHandler = new CommandHandler(command)
            };
        }

        private sealed class CommandHandler : ICommand
        {
            private readonly string _command;

            public CommandHandler(string command)
            {
                _command = command;
            }

            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter)
            {
                var document = Application.DocumentManager.MdiActiveDocument;
                if (document != null) document.SendStringToExecute(_command, true, false, false);
            }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
