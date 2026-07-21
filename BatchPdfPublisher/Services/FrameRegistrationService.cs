using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
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
            var context = ReadReference(document, objectId);
            if (context == null) return;

            var store = new PublishPlanStore();
            var frames = store.LoadFrames();
            if (frames.Any(x => string.Equals(x.BlockName, context.BlockName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("图块“" + context.BlockName + "”已经登记，不能重复登记。请在图框库中双击该项进行修改。", "重复图框", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var definition = ShowDialog(context, null);
            if (definition == null) return;
            frames.Add(definition);
            store.SaveFrames(frames);
        }

        public void Edit(Document document, FrameDefinition existing)
        {
            if (document == null || existing == null) return;
            FrameContext context;
            using (document.LockDocument()) context = FindReference(document, existing);
            var definition = ShowDialog(context, existing);
            if (definition == null) return;

            var store = new PublishPlanStore();
            var frames = store.LoadFrames();
            var index = frames.FindIndex(x => string.Equals(x.BlockName, existing.BlockName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) frames.Add(definition);
            else frames[index] = definition;
            store.SaveFrames(frames);
        }

        private static FrameDefinition ShowDialog(FrameContext context, FrameDefinition existing)
        {
            var dialog = new FrameRegistrationWindow(context.BlockName, context.Guess, context.Attributes, existing);
            new WindowInteropHelper(dialog).Owner = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle;
            return dialog.ShowDialog() == true ? dialog.Definition : null;
        }

        private static FrameContext FindReference(Document document, FrameDefinition existing)
        {
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    var reference = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (reference == null) continue;
                    var record = (BlockTableRecord)transaction.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead);
                    if (!string.Equals(record.Name, existing.BlockName, StringComparison.OrdinalIgnoreCase)) continue;
                    return CreateContext(reference, record.Name, transaction);
                }
            }

            return new FrameContext
            {
                BlockName = existing.BlockName,
                Guess = new FrameSizeGuess
                {
                    PaperSize = existing.PaperSize,
                    Extension = existing.Extension,
                    PaperOrientation = string.IsNullOrWhiteSpace(existing.PaperOrientation) ? "横向" : existing.PaperOrientation,
                    PrintScale = string.IsNullOrWhiteSpace(existing.DefaultPrintScale) ? "1:1" : existing.DefaultPrintScale,
                    MeasuredSize = "当前图中未找到该图块实例"
                },
                Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static FrameContext ReadReference(Document document, ObjectId objectId)
        {
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var reference = transaction.GetObject(objectId, OpenMode.ForRead) as BlockReference;
                if (reference == null) return null;
                var record = (BlockTableRecord)transaction.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead);
                return CreateContext(reference, record.Name, transaction);
            }
        }

        private static FrameContext CreateContext(BlockReference reference, string blockName, Transaction transaction)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = transaction.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (attribute != null && !string.IsNullOrWhiteSpace(attribute.Tag)) attributes[attribute.Tag] = attribute.TextString;
            }
            var scaleTags = new[] { "比例", "SCALE", "PRINTSCALE" };
            var knownScale = attributes.FirstOrDefault(x => scaleTags.Any(tag => string.Equals(tag, x.Key, StringComparison.OrdinalIgnoreCase))).Value;
            return new FrameContext
            {
                BlockName = blockName,
                Guess = FrameSizeDetector.Guess(reference.GeometricExtents, knownScale),
                Attributes = attributes
            };
        }

        private sealed class FrameContext
        {
            public string BlockName { get; set; }
            public FrameSizeGuess Guess { get; set; }
            public IDictionary<string, string> Attributes { get; set; }
        }
    }
}
