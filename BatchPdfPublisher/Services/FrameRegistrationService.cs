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
    public sealed class FrameProjectScanIssue
    {
        public string FilePath { get; set; }
        public string BlockName { get; set; }
        public string DuplicateTags { get; set; }
        public string DisplayText => System.IO.Path.GetFileName(FilePath) + " / " + BlockName + "：" + DuplicateTags;
        public override string ToString() => DisplayText;
    }

    public sealed class FrameProjectScanReport
    {
        public int ScannedFiles { get; set; }
        public int MatchingFiles { get; set; }
        public int VariantCount { get; set; }
        public List<FrameProjectScanIssue> Issues { get; } = new List<FrameProjectScanIssue>();
        public List<string> Failures { get; } = new List<string>();
        public string Summary => "已扫描 " + ScannedFiles + " 个工程 CAD；" + MatchingFiles + " 个文件含同名图块；识别到 " + VariantCount + " 个版本；重复 TAG " + Issues.Count + " 处" + (Failures.Count > 0 ? "；读取失败 " + Failures.Count + " 个" : string.Empty) + "。";
    }

    public sealed class FrameRegistrationService
    {
        public void Register(Document document, ObjectId objectId)
        {
            var context = ReadReference(document, objectId);
            if (context == null) return;
            if (!ConfirmUniqueAttributeTags(document, context)) return;

            var store = new PublishPlanStore();
            var frames = store.LoadFrames();
            var sameName = frames.Where(x => string.Equals(x.BlockName, context.BlockName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameName.Any(x => FrameIdentityService.IsSameVariant(x, context.AttributeTagSignature, context.DefinitionSignature, context.AspectRatio)))
            {
                MessageBox.Show("图块“" + context.BlockName + "”的这个版本已经登记。请在图框库中双击对应项进行修改。", "重复图框", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (sameName.Count > 0 && MessageBox.Show("已存在同名图框，但当前图块的属性或几何定义不同。是否把它登记为同名图块的另一个版本？", "同名图框版本", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var definition = ShowDialog(document, context, null);
            if (definition == null) return;
            frames.Add(definition);
            store.SaveFrames(frames);
        }

        public void Edit(Document document, FrameDefinition existing)
        {
            if (document == null || existing == null) return;
            FrameContext context;
            using (document.LockDocument()) context = FindReference(document, existing);
            if (!ConfirmUniqueAttributeTags(document, context)) return;
            var definition = ShowDialog(document, context, existing);
            if (definition == null) return;

            var store = new PublishPlanStore();
            var frames = store.LoadFrames();
            var index = frames.FindIndex(x => !string.IsNullOrWhiteSpace(existing.RegistrationId) && string.Equals(x.RegistrationId, existing.RegistrationId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = frames.FindIndex(x => ReferenceMatchesLegacy(x, existing));
            if (index < 0) frames.Add(definition);
            else frames[index] = definition;
            store.SaveFrames(frames);
        }

        private static FrameDefinition ShowDialog(Document document, FrameContext context, FrameDefinition existing)
        {
            var dialog = new FrameRegistrationWindow(context.BlockName, context.Guess, context.Attributes, existing,
                context.AttributeTagSignature, context.DefinitionSignature, context.AspectRatio,
                () => ScanProjectCadFiles(document, context.BlockName));
            new WindowInteropHelper(dialog).Owner = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle;
            var accepted = dialog.ShowDialog() == true;
            if (dialog.RequestedIssues.Count > 0) ScheduleIssueAction(dialog.RequestedIssues, dialog.OpenAllRequested);
            return accepted ? dialog.Definition : null;
        }

        private static FrameProjectScanReport ScanProjectCadFiles(Document currentDocument, string blockName)
        {
            var report = new FrameProjectScanReport();
            var project = new PublishPlanStore().GetActiveProject();
            var paths = new HashSet<string>((project?.CadFiles ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            var currentPath = SafeDocumentPath(currentDocument);
            if (!string.IsNullOrWhiteSpace(currentPath)) paths.Add(currentPath);
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                try
                {
                    var open = FindOpenDocument(path);
                    FrameFileScan scan;
                    if (open != null)
                    {
                        if (ReferenceEquals(open, Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument)) scan = ScanDatabase(open.Database, path, blockName);
                        else using (open.LockDocument()) scan = ScanDatabase(open.Database, path, blockName);
                    }
                    else
                    {
                        if (!System.IO.File.Exists(path)) { report.Failures.Add(System.IO.Path.GetFileName(path) + "：文件不存在"); continue; }
                        using (var database = new Database(false, true))
                        {
                            database.ReadDwgFile(path, System.IO.FileShare.Read, true, string.Empty);
                            scan = ScanDatabase(database, path, blockName);
                        }
                    }
                    report.ScannedFiles++;
                    if (scan.Found) report.MatchingFiles++;
                    foreach (var signature in scan.Signatures) signatures.Add(signature);
                    foreach (var issue in scan.Issues)
                        if (!report.Issues.Any(x => string.Equals(x.FilePath, issue.FilePath, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(x.BlockName, issue.BlockName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(x.DuplicateTags, issue.DuplicateTags, StringComparison.OrdinalIgnoreCase))) report.Issues.Add(issue);
                }
                catch (Exception exception) { report.Failures.Add(System.IO.Path.GetFileName(path) + "：" + exception.Message); }
            }
            report.VariantCount = signatures.Count;
            return report;
        }

        private static FrameFileScan ScanDatabase(Database database, string path, string blockName)
        {
            var result = new FrameFileScan();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (blockTable == null) return result;
                foreach (ObjectId recordId in blockTable)
                {
                    var space = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                    if (space == null || !space.IsLayout) continue;
                    foreach (ObjectId id in space)
                    {
                        BlockReference reference;
                        try { reference = transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference; } catch { continue; }
                        if (reference == null) continue;
                        BlockTableRecord definition;
                        try { definition = transaction.GetObject(reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord; } catch { continue; }
                        if (definition == null || !string.Equals(definition.Name, blockName, StringComparison.OrdinalIgnoreCase)) continue;
                        result.Found = true;
                        var definitionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (ObjectId entityId in definition)
                        {
                            AttributeDefinition attributeDefinition;
                            try { attributeDefinition = transaction.GetObject(entityId, OpenMode.ForRead, false) as AttributeDefinition; } catch { continue; }
                            if (attributeDefinition == null || string.IsNullOrWhiteSpace(attributeDefinition.Tag)) continue;
                            var definitionTag = attributeDefinition.Tag.Trim();
                            definitionCounts[definitionTag] = definitionCounts.TryGetValue(definitionTag, out var definitionCount) ? definitionCount + 1 : 1;
                        }
                        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (ObjectId attributeId in reference.AttributeCollection)
                        {
                            AttributeReference attribute;
                            try { attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference; } catch { continue; }
                            if (attribute == null || string.IsNullOrWhiteSpace(attribute.Tag)) continue;
                            var tag = attribute.Tag.Trim(); counts[tag] = counts.TryGetValue(tag, out var count) ? count + 1 : 1;
                        }
                        var duplicateTags = counts.Where(x => x.Value > 1).Select(x => x.Key)
                            .Concat(definitionCounts.Where(x => x.Value > 1).Select(x => x.Key))
                            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
                        if (duplicateTags.Count > 0) result.Issues.Add(new FrameProjectScanIssue { FilePath = path, BlockName = blockName, DuplicateTags = string.Join("、", duplicateTags) });
                        var attributeSignature = FrameIdentityService.AttributeSignature(counts.Keys);
                        result.Signatures.Add(FrameIdentityService.DefinitionSignature(reference, transaction) + "|" + attributeSignature);
                    }
                }
            }
            return result;
        }

        private static Document FindOpenDocument(string path)
        {
            var full = NormalizePath(path);
            foreach (Document document in Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager)
                if (string.Equals(NormalizePath(SafeDocumentPath(document)), full, StringComparison.OrdinalIgnoreCase)) return document;
            return null;
        }

        private static string SafeDocumentPath(Document document)
        {
            try { return string.IsNullOrWhiteSpace(document?.Database?.Filename) ? document?.Name : document.Database.Filename; }
            catch { return string.Empty; }
        }

        private static string NormalizePath(string path)
        {
            try { return System.IO.Path.GetFullPath((path ?? string.Empty).Trim()); }
            catch { return (path ?? string.Empty).Trim(); }
        }

        private static void ScheduleIssueAction(IReadOnlyList<FrameProjectScanIssue> issues, bool openAll)
        {
            if (issues == null || issues.Count == 0) return;
            EventHandler handler = null;
            handler = (sender, args) =>
            {
                Autodesk.AutoCAD.ApplicationServices.Application.Idle -= handler;
                try
                {
                    var targets = (openAll ? issues : issues.Take(1)).GroupBy(x => NormalizePath(x.FilePath), StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
                    Document first = null;
                    foreach (var issue in targets)
                    {
                        var document = FindOpenDocument(issue.FilePath);
                        if (document == null && System.IO.File.Exists(issue.FilePath)) document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.Open(issue.FilePath, false);
                        if (first == null) first = document;
                    }
                    var selected = issues[0];
                    var editDocument = FindOpenDocument(selected.FilePath) ?? first;
                    if (editDocument != null)
                    {
                        Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument = editDocument;
                        Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(new AttributeDefinitionEditorForm(editDocument, selected.BlockName));
                    }
                }
                catch (Exception exception) { Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog("打开问题图框失败：" + exception.Message); }
            };
            Autodesk.AutoCAD.ApplicationServices.Application.Idle += handler;
        }

        private sealed class FrameFileScan
        {
            public bool Found { get; set; }
            public HashSet<string> Signatures { get; } = new HashSet<string>(StringComparer.Ordinal);
            public List<FrameProjectScanIssue> Issues { get; } = new List<FrameProjectScanIssue>();
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
                    var candidate = CreateContext(reference, record.Name, transaction);
                    if (FrameIdentityService.IsSameVariant(existing, candidate.AttributeTagSignature, candidate.DefinitionSignature, candidate.AspectRatio)) return candidate;
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
            var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = transaction.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (attribute == null || string.IsNullOrWhiteSpace(attribute.Tag)) continue;
                var tag = attribute.Tag.Trim();
                tagCounts[tag] = tagCounts.TryGetValue(tag, out var count) ? count + 1 : 1;
                if (!attributes.ContainsKey(tag)) attributes[tag] = attribute.TextString;
            }
            var scaleTags = new[] { "比例", "SCALE", "PRINTSCALE" };
            var knownScale = attributes.FirstOrDefault(x => scaleTags.Any(tag => string.Equals(tag, x.Key, StringComparison.OrdinalIgnoreCase))).Value;
            return new FrameContext
            {
                BlockName = blockName,
                Guess = FrameSizeDetector.Guess(reference.GeometricExtents, knownScale),
                Attributes = attributes,
                AttributeTagSignature = FrameIdentityService.AttributeSignature(attributes.Keys),
                DefinitionSignature = FrameIdentityService.DefinitionSignature(reference, transaction),
                AspectRatio = FrameIdentityService.AspectRatio(reference.GeometricExtents),
                DuplicateAttributeTags = tagCounts.Where(x => x.Value > 1).Select(x => x.Key).OrderBy(x => x).ToList()
            };
        }

        private static bool ConfirmUniqueAttributeTags(Document document, FrameContext context)
        {
            if (context?.DuplicateAttributeTags == null || context.DuplicateAttributeTags.Count == 0) return true;
            var tags = string.Join("、", context.DuplicateAttributeTags);
            var message = "图框“" + context.BlockName + "”中存在重复的属性标记：" + tags
                + "。\r\n\r\n同一个图块中的每个属性 TAG 必须唯一，否则扫描时无法判断应读取哪一个。"
                + "请在块编辑器中修改其中一个属性定义的 TAG，然后执行 ATTSYNC 同步现有图块。\r\n\r\n是否现在打开这个图块的块编辑器？";
            if (MessageBox.Show(message, "图框属性标记重复", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                var name = (context.BlockName ?? string.Empty).Replace("\"", "\"\"");
                document.SendStringToExecute("_.BEDIT \"" + name + "\" ", true, false, false);
            }
            return false;
        }

        private static bool ReferenceMatchesLegacy(FrameDefinition left, FrameDefinition right) =>
            string.Equals(left.BlockName, right.BlockName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.PaperSize, right.PaperSize, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Extension, right.Extension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Note, right.Note, StringComparison.OrdinalIgnoreCase);

        private sealed class FrameContext
        {
            public string BlockName { get; set; }
            public FrameSizeGuess Guess { get; set; }
            public IDictionary<string, string> Attributes { get; set; }
            public string AttributeTagSignature { get; set; }
            public string DefinitionSignature { get; set; }
            public double AspectRatio { get; set; }
            public List<string> DuplicateAttributeTags { get; set; } = new List<string>();
        }
    }
}
