using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    internal sealed class ExternalCadAsset
    {
        internal string Path, Name, Category;
        internal bool IsCollected => Path.EndsWith(ExternalCadLibrary.Suffix, StringComparison.OrdinalIgnoreCase);
        internal Point3d Base;
        internal Extents3d Bounds;
        internal UnitsValue Units;
        internal List<DetailPreviewPrimitive> Preview;
        internal string Error;
        internal long Size;
        internal DateTime LastWriteUtc;
    }

    internal static class ExternalCadLibrary
    {
        internal const string Suffix = ".wlblock.dwg";
        internal const int PreviewLimit = 2500;
        internal static string LocationFile => Path.Combine(UserDataPaths.RootDirectory, "external-cad-library.txt");

        internal static List<ExternalCadAsset> Read(string root)
        {
            if (!Directory.Exists(root)) return new List<ExternalCadAsset>();
            return Scan(root, "").OrderBy(asset => asset.Category, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static List<ExternalCadAsset> ReadFolder(string root, string category) =>
            ScanFolder(Path.Combine(root, category), category)
                .OrderBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        internal static List<string> ReadSubfolders(string root, string category) =>
            Directory.EnumerateDirectories(Path.Combine(root, category))
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                .Select(path => Path.Combine(category, Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToList();

        private static IEnumerable<ExternalCadAsset> Scan(string directory, string category)
        {
            foreach (var asset in ScanFolder(directory, category)) yield return asset;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var asset in Scan(child, Path.Combine(category, Path.GetFileName(child)))) yield return asset;
            }
        }

        private static IEnumerable<ExternalCadAsset> ScanFolder(string directory, string category)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dwg"))
            {
                if (!string.Equals(Path.GetExtension(file), ".dwg", StringComparison.OrdinalIgnoreCase)) continue;
                var fileName = Path.GetFileName(file);
                var name = Path.GetFileNameWithoutExtension(fileName);
                // Save writes a staging drawing in the same folder before publishing its final name.
                if (name.Length == 33 && name[0] == '.' && Guid.TryParseExact(name.Substring(1), "N", out _)) continue;
                if (fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                    name = fileName.Substring(0, fileName.Length - Suffix.Length);
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                yield return new ExternalCadAsset { Path = file, Name = name, Category = category, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
            }
        }

        internal static Database OpenDrawing(string path)
        {
            var database = new Database(false, true);
            try
            {
                database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, "");
                database.CloseInput(true);
                return database;
            }
            catch { database.Dispose(); throw; }
        }

        internal static ObjectIdCollection Entities(Database db, Transaction transaction)
        {
            var table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
            var space = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            return new ObjectIdCollection(space.Cast<ObjectId>().Where(id => !id.IsErased).ToArray());
        }

        internal static void LoadPreview(ExternalCadAsset asset)
        {
            if (asset.Preview != null) return;
            var file = new FileInfo(asset.Path);
            asset.Size = file.Length;
            asset.LastWriteUtc = file.LastWriteTimeUtc;
            using (var source = OpenDrawing(asset.Path))
                LoadPreview(asset, source);
        }

        internal static void LoadPreview(ExternalCadAsset asset, Database source)
        {
            RequireSameDrawing(asset);
            using (var transaction = source.TransactionManager.StartOpenCloseTransaction())
            {
                var ids = Entities(source, transaction);
                asset.Base = source.Insbase;
                asset.Units = source.Insunits;
                var preview = new List<DetailPreviewPrimitive>();
                asset.Bounds = Measure(ids, transaction, preview);
                RequireSameDrawing(asset);
                asset.Preview = preview.Where(p => p.Kind != DetailPreviewPrimitiveKind.Box).ToList();
            }
        }

        internal static void RequireSameDrawing(ExternalCadAsset asset)
        {
            var file = new FileInfo(asset.Path);
            if (!file.Exists || file.Length != asset.Size || file.LastWriteTimeUtc != asset.LastWriteUtc)
                throw new InvalidOperationException("“" + asset.Name + "”已变更，请刷新图库后重新插入。");
        }

        internal static Extents3d Measure(ObjectIdCollection ids, Transaction transaction, IList<DetailPreviewPrimitive> preview = null)
        {
            Extents3d? bounds = null;
            foreach (ObjectId id in ids)
            {
                var entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null) continue;
                try
                {
                    var actual = entity.GeometricExtents;
                    var combined = bounds ?? actual;
                    combined.AddExtents(actual);
                    bounds = combined;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    throw new InvalidOperationException("选择中有无法确定边界的对象，请单独处理后再收录。");
                }
                if (preview != null && preview.Count < PreviewLimit)
                    DetailLayoutService.CapturePreview(entity, preview, 0, true);
            }
            return bounds ?? throw new InvalidOperationException("没有可收录的图形。");
        }

        internal static string Save(Document document, ObjectIdCollection ids, Point3d basePoint, string root, string category, string name)
        {
            name = ValidateName(name);
            var folder = Path.GetFullPath(root);
            foreach (var part in (category ?? "").Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                folder = Path.Combine(folder, ValidateName(part));
                if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("请选择实际的图库子文件夹。");
            }
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, name + Suffix);
            if (File.Exists(target)) throw new InvalidOperationException("这个分类已有同名对象，请换一个名称。");
            var temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".dwg");
            try
            {
                using (var copy = new Database(true, true))
                {
                    copy.Insunits = document.Database.Insunits;
                    copy.Insbase = basePoint;
                    document.Database.WblockCloneObjects(ids, copy.CurrentSpaceId, new IdMapping(), DuplicateRecordCloning.Replace, false);
                    copy.SaveAs(temp, DwgVersion.Current);
                }
                File.Move(temp, target);
                return target;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static string ValidateName(string value)
        {
            var name = (value ?? "").Trim();
            if (name.Length == 0 || name.Length > 80 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.EndsWith(".", StringComparison.Ordinal) || name == "." || name == "..")
                throw new InvalidOperationException("请输入有效的名称，长度不超过 80 个字符。");
            return name;
        }

        internal static double ToUnits(UnitsValue from, UnitsValue to) =>
            from == UnitsValue.Undefined || to == UnitsValue.Undefined || from == to ? 1d : UnitsConverter.GetConversionFactor(from, to);
    }
}
