using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Views;

namespace BatchPdfPublisher.Services
{
    public sealed class FrameRegistrationService
    {
        public void Register(Document document, ObjectId objectId)
        {
            string blockName;
            FrameSizeGuess guess;
            List<string> attributes;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var reference = transaction.GetObject(objectId, OpenMode.ForRead) as BlockReference;
                if (reference == null) return;
                var record = (BlockTableRecord)transaction.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead);
                blockName = record.Name;
                guess = FrameSizeDetector.Guess(reference.GeometricExtents);
                attributes = reference.AttributeCollection.Cast<ObjectId>().Select(id => (transaction.GetObject(id, OpenMode.ForRead) as AttributeReference)?.Tag).Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList();
                transaction.Commit();
            }
            var dialog = new FrameRegistrationWindow(blockName, guess, attributes);
            new WindowInteropHelper(dialog).Owner = Application.MainWindow.Handle;
            if (dialog.ShowDialog() != true || dialog.Definition == null) return;
            var store = new PublishPlanStore();
            var frames = store.LoadFrames();
            frames.RemoveAll(x => string.Equals(x.BlockName, dialog.Definition.BlockName, System.StringComparison.OrdinalIgnoreCase));
            frames.Add(dialog.Definition);
            store.SaveFrames(frames);
        }
    }
}
