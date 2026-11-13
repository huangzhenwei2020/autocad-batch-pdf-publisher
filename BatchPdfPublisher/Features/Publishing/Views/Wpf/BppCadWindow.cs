using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Services;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Views
{
    public class BppCadWindow : Window
    {
        protected Document SourceDocument { get; private set; }
        protected bool IsCadBusy { get; private set; }

        private bool _closed;
        protected void BindDocument(Document document)
        {
            SourceDocument = document ?? throw new ArgumentNullException(nameof(document));
            AcApplication.DocumentManager.DocumentToBeDestroyed += DocumentClosing;
            Closed += (sender, args) =>
            {
                _closed = true;
                AcApplication.DocumentManager.DocumentToBeDestroyed -= DocumentClosing;
            };
            Closing += (sender, args) =>
            {
                if (IsCadBusy)
                    args.Cancel = true;
            };
        }

        private void DocumentClosing(object sender, DocumentCollectionEventArgs e)
        {
            if (!ReferenceEquals(e.Document, SourceDocument))
                return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closed)
                    Close();
            }));
        }

        protected async void RunCad(Action action, Action completed = null)
        {
            if (IsCadBusy || _closed)
                return;
            if (!AcApplication.DocumentManager.Cast<Document>().Any(document => ReferenceEquals(document, SourceDocument)))
            {
                MessageBox.Show(this, "来源图纸已关闭，请从目标图纸重新打开窗口。", Title);
                Close();
                return;
            }

            var owner = Owner;
            var restoreOwner = owner?.IsVisible == true;
            IsCadBusy = true;
            Hide();
            if (restoreOwner)
                owner.Hide();
            Exception failure = null;
            try
            {
                AcApplication.DocumentManager.MdiActiveDocument = SourceDocument;
                await CadCommandContext.ExecuteAsync(action);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                IsCadBusy = false;
                if (!_closed)
                {
                    if (restoreOwner)
                        owner.Show();
                    Show();
                    Activate();
                }
            }

            if (_closed)
                return;
            if (failure != null)
                MessageBox.Show(this, failure.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            else
            {
                try
                {
                    completed?.Invoke();
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    internal static class BppSettings
    {
        internal static Dictionary<string, string> Read(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
                return result;
            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator > 0)
                    result[line.Substring(0, separator)] = line.Substring(separator + 1);
            }

            return result;
        }

        internal static string Value(IDictionary<string, string> values, string name, string fallback) => values.TryGetValue(name, out var value) ? value : fallback;
        internal static void Save(string path, IDictionary<string, string> values)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temporary, values.Select(pair => pair.Key + "=" + pair.Value));
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }
}
