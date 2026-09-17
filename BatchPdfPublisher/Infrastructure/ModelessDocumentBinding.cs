using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;

namespace BatchPdfPublisher.Services
{
    internal sealed class ModelessDocumentBinding : IDisposable
    {
        private readonly Form _form;
        private readonly Document _document;
        private readonly string _baseTitle;
        private bool _disposed;

        internal ModelessDocumentBinding(Form form, Document document)
        {
            _form = form ?? throw new ArgumentNullException(nameof(form));
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _baseTitle = form.Text;
            var manager = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
            manager.DocumentActivated += OnDocumentActivated;
            manager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            form.Activated += OnFormActivated;
            form.FormClosed += OnFormClosed;
            UpdateTitle(manager.MdiActiveDocument);
        }

        private void OnFormActivated(object sender, EventArgs args)
        {
            if (_disposed) return;
            var manager = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
            if (!IsOpen(manager)) { CloseForm(); return; }
            if (!ReferenceEquals(manager.MdiActiveDocument, _document))
            {
                try { manager.MdiActiveDocument = _document; }
                catch { CloseForm(); return; }
            }
            UpdateTitle(_document);
        }

        private void OnDocumentActivated(object sender, DocumentCollectionEventArgs args)
        {
            UpdateTitle(args == null ? null : args.Document);
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
        {
            if (args != null && ReferenceEquals(args.Document, _document)) CloseForm();
        }

        private void UpdateTitle(Document active)
        {
            if (_disposed || _form.IsDisposed) return;
            var drawing = DrawingName(_document);
            var suffix = ReferenceEquals(active, _document)
                ? "  [" + drawing + "]"
                : "  [已暂停，点击后切回 " + drawing + "]";
            SetTitle(_baseTitle + suffix);
        }

        private void SetTitle(string title)
        {
            if (_form.IsDisposed) return;
            if (_form.InvokeRequired)
            {
                try { _form.BeginInvoke(new Action<string>(SetTitle), title); } catch { }
                return;
            }
            _form.Text = title;
        }

        private bool IsOpen(DocumentCollection manager)
        {
            try { return manager.Cast<Document>().Any(item => ReferenceEquals(item, _document)); }
            catch { return false; }
        }

        private void CloseForm()
        {
            if (_disposed || _form.IsDisposed) return;
            if (_form.InvokeRequired)
            {
                try { _form.BeginInvoke(new Action(CloseForm)); } catch { }
                return;
            }
            try { _form.Close(); } catch { }
        }

        private static string DrawingName(Document document)
        {
            try
            {
                var name = document == null ? string.Empty : document.Name;
                return string.IsNullOrWhiteSpace(name) ? "未命名图纸" : Path.GetFileName(name);
            }
            catch { return "原图纸"; }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs args) { Dispose(); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var manager = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
            manager.DocumentActivated -= OnDocumentActivated;
            manager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            _form.Activated -= OnFormActivated;
            _form.FormClosed -= OnFormClosed;
        }
    }
}
