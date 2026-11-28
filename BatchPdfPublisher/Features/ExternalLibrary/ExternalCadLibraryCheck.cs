using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    internal static class ExternalCadLibraryCheck
    {
        internal static void Run(Document doc)
        {
            var root = Environment.GetEnvironmentVariable("WANLUO_EXTERNAL_LIBRARY_QA_ROOT");
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new InvalidOperationException("此检查需要独立测试目录和临时图纸。");
            var db = doc.Database;
            ObjectId first, block;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                first = Add(space, tr, new Line(new Point3d(100, 100, 0), new Point3d(6140, 4500, 0)));
                var definition = new BlockTableRecord { Name = "WL_EXTERNAL_FIXTURE_BLOCK" };
                var definitionId = table.Add(definition);
                tr.AddNewlyCreatedDBObject(definition, true);
                Add(definition, tr, new Line(Point3d.Origin, new Point3d(1000, 500, 0)));
                var reference = new BlockReference(new Point3d(100, 300, 0), definitionId);
                block = Add(space, tr, reference);
                var attribute = new AttributeReference { Tag = "TEST", TextString = "Block payload", Position = reference.Position, Height = 100 };
                reference.AttributeCollection.AppendAttribute(attribute);
                tr.AddNewlyCreatedDBObject(attribute, true);
                var frame = new BlockTableRecord { Name = "WL_EXTERNAL_FIXTURE_FRAME" };
                table.Add(frame);
                tr.AddNewlyCreatedDBObject(frame, true);
                Add(frame, tr, new Line(Point3d.Origin, new Point3d(420, 297, 0)));
                Add(frame, tr, new Line(new Point3d(380, 0, 0), new Point3d(380, 297, 0)));
                tr.Commit();
            }
            var basis = new Point3d(100, 100, 0);
            var lineFile = ExternalCadLibrary.Save(doc, new ObjectIdCollection(new[] { first }), basis, root, "Details", "Line");
            ExternalCadLibrary.Save(doc, new ObjectIdCollection(new[] { block }), new Point3d(100, 300, 0), root, "Blocks", "Block");
            ExternalCadLibrary.Save(doc, new ObjectIdCollection(new[] { first, block }), basis, root, "Details", "Group");
            var folder = Directory.CreateDirectory(Path.Combine(root, "已有图库", "详图")).FullName;
            var ordinaryFile = Path.Combine(folder, "原有详图.DWG");
            File.Copy(lineFile, ordinaryFile);
            File.Copy(lineFile, Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".dwg"));
            File.Copy(lineFile, Path.Combine(folder, "备份.bak"));
            var assets = ExternalCadLibrary.Read(root);
            Check(assets.Count == 4, "外部文件夹扫描失败");
            var line = assets.Single(a => a.Path == lineFile);
            var group = assets.Single(a => a.Name == "Group");
            var ordinary = assets.Single(a => a.Path == ordinaryFile);
            Check(line.IsCollected && !ordinary.IsCollected && ordinary.Name == "原有详图"
                && ordinary.Category == Path.Combine("已有图库", "详图"), "普通 DWG 的名称或分类错误");
            var empty = Directory.CreateDirectory(Path.Combine(root, "空分类"));
            Check(ExternalCadLibrary.ReadSubfolders(root, "").SequenceEqual(new[] { "Blocks", "Details", empty.Name, "已有图库" }.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)),
                "目录首层未保留独立分类");
            Check(ExternalCadLibrary.ReadFolder(root, "").Count == 0
                && ExternalCadLibrary.ReadFolder(root, "已有图库").Count == 0
                && ExternalCadLibrary.ReadSubfolders(root, "已有图库").Single() == Path.Combine("已有图库", "详图")
                && ExternalCadLibrary.ReadFolder(root, Path.Combine("已有图库", "详图")).Single().Path == ordinaryFile,
                "文件夹浏览混入了后代目录");
            var frameSpec = new FrameDefinition { BlockName = "WL_EXTERNAL_FIXTURE_FRAME", PaperSize = "A3", PaperOrientation = "横向",
                HasLayoutRange = true, LayoutLeftMargin = 10, LayoutRightMargin = 65, LayoutBottomMargin = 10, LayoutTopMargin = 10 };
            var sheet = ExternalCadSheet.Arrange(new[] { ordinary, group }, frameSpec, 25, 10, true, true, true, 11);
            Check(sheet.PageCount == 2 && sheet.Slots[0].Number == "11" && sheet.Slots[1].Number == "12", "图框分页或编号错误");
            CheckFrameInset(frameSpec, ordinary, sheet);
            Check(ordinary.Base.IsEqualTo(basis) && ordinary.Preview.Count == 1, "普通 DWG 丢失基点或预览");
            Check(ExternalCadPreview.Frame(doc, frameSpec, 25).Count >= 2, "真实图框预览错误");
            var rejected = false;
            try { ExternalCadSheet.Arrange(new[] { ordinary, group }, frameSpec, 25, 10, false, true, true, 11); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "关闭分页后仍然插入多张图框");
            var rotate = new Point3d(19000, 21000, 0);
            var turns = ExternalCadPreview.NextClockwiseTurn(0);
            Check(Enumerable.Range(0, 4).Aggregate(0, (angle, _) => ExternalCadPreview.NextClockwiseTurn(angle)) == 0, "右键旋转四次未回到起始方向");
            var singleSheet = new ExternalCadSheet();
            var singlePreview = ExternalCadPreview.Single(singleSheet, ordinary, turns).Single();
            Check(singlePreview.X1 == 0 && singlePreview.Y1 == 0 && singlePreview.X2 == 4400 && singlePreview.Y2 == -6040,
                "顺时针旋转预览方向或基点错误");
            using (var source = ExternalCadLibrary.OpenDrawing(ordinary.Path))
            {
                var cold = ExternalCadLibrary.ReadFolder(root, ordinary.Category).Single();
                ExternalCadLibrary.LoadPreview(cold, source);
                ExternalCadSheet.Insert(doc, singleSheet, cold, rotate, turns, source);
            }
            var sheetOrigin = new Point3d(30000, 25000, 0);
            var drawingUnit = ExternalCadLibrary.ToUnits(UnitsValue.Millimeters, db.Insunits);
            ExternalCadSheet.Insert(doc, sheet, null, sheetOrigin, 0);
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var entities = ExternalCadLibrary.Entities(db, tr).Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead)).ToList();
                Check(entities.OfType<Line>().Any(value => value.StartPoint.IsEqualTo(rotate)
                    && value.EndPoint.IsEqualTo(rotate + new Vector3d(4400, -6040, 0))), "单项顺时针 90° 旋转或基点错误");
                Check(entities.OfType<BlockReference>().Count(value => value.Position.X >= sheetOrigin.X) >= 3, "图框或源块丢失");
                Check(entities.OfType<BlockReference>().Any(value => value.Position.X >= sheetOrigin.X
                    && value.AttributeCollection.Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead))
                        .OfType<AttributeReference>().Any(attr => attr.TextString == "Block payload")), "块属性丢失");
                Check(entities.OfType<Circle>().Count() == 2 && entities.OfType<DBText>().Any(value => value.TextString == "12"), "图名圈号丢失");
                foreach (var slot in sheet.Slots)
                {
                    var lower = sheetOrigin + new Vector3d((slot.X + slot.Page * (sheet.PageWidth + sheet.PageGap)) * drawingUnit, slot.Y * drawingUnit, 0);
                    var upper = lower + new Vector3d(6040, 4400, 0);
                    Check(entities.OfType<Line>().Any(value => value.StartPoint.IsEqualTo(lower) && value.EndPoint.IsEqualTo(upper)),
                        "实际放置未使用对应页的边框留白");
                }
            }
            Check(File.ReadAllBytes(lineFile).SequenceEqual(File.ReadAllBytes(ordinaryFile)), "读取或插入改写了普通 DWG");
            CheckComplexPreview(doc, root);
            CheckExistingCopies(doc);
            doc.Editor.WriteMessage("\nEXTERNAL_FRAME_INSET_OK zero default5 custom10.5 bounds pagination annotation inserted\n");
            doc.Editor.WriteMessage("\nEXTERNAL_CAD_LIBRARY_OK files ordinaryDwg folderScope preview base geometry rotation frames pagination annotations blockAttributes\n");
        }

        private static void CheckFrameInset(FrameDefinition frame, ExternalCadAsset asset, ExternalCadSheet baseline)
        {
            var standard = ExternalCadSheet.Arrange(new[] { asset }, frame, 25, 10, true, false, false, 1, 0);
            var custom = ExternalCadSheet.Arrange(new[] { asset }, frame, 25, 10, true, false, false, 1, 10.5);
            foreach (var pair in new[] { (Sheet: baseline, Inset: 5d), (Sheet: custom, Inset: 10.5) })
            {
                var offset = pair.Inset * pair.Sheet.Scale;
                Check(pair.Sheet.Left - standard.Left == offset && standard.Right - pair.Sheet.Right == offset
                    && pair.Sheet.Bottom - standard.Bottom == offset && standard.Top - pair.Sheet.Top == offset
                    && pair.Sheet.PageWidth == standard.PageWidth && pair.Sheet.PageHeight == standard.PageHeight,
                    "输入的留白未作为纸面单位应用到登记排版范围四边");
            }
            var preview = ExternalCadPreview.Page(custom, 0, Array.Empty<DetailPreviewPrimitive>()).Single();
            Check(preview.X1 == custom.Left && Math.Abs(preview.Y2 - custom.Top) < 1e-7, "预览未使用留白后的左、上边界");

            var narrow = new ExternalCadAsset { Name = "近边界测试", Preview = new List<DetailPreviewPrimitive>(),
                Units = UnitsValue.Millimeters, Bounds = new Extents3d(Point3d.Origin, new Point3d(170, 252.7, 0)) };
            var noInset = ExternalCadSheet.Arrange(new[] { narrow, narrow }, frame, 1, 0, true, true, true, 9999998, 0);
            var withInset = ExternalCadSheet.Arrange(new[] { narrow, narrow }, frame, 1, 0, true, true, true, 9999998);
            Check(noInset.PageCount == 1 && withInset.PageCount == 2, "留白缩小可用空间后未重新分页");
            var labels = ExternalCadPreview.Page(withInset, 1, Array.Empty<DetailPreviewPrimitive>());
            Check(labels.Any() && labels.All(part => Math.Min(part.Y1, part.Y2) >= withInset.Bottom - 1e-7
                && Math.Min(part.X1, part.X2) >= withInset.Left - 1e-7 && Math.Max(part.X1, part.X2) <= withInset.Right + 1e-7),
                "随图标注越过留白边界");
        }

        private static void CheckComplexPreview(Document doc, string root)
        {
            const int count = 3200;
            var path = Path.Combine(root, "密集预览.dwg");
            using (var db = new Database(true, true))
            {
                db.Insunits = doc.Database.Insunits;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    for (var i = 0; i < count; i++)
                        Add(space, tr, new Line(new Point3d(i, 0, 0), new Point3d(i, i + 1, 0)));
                    tr.Commit();
                }
                db.SaveAs(path, DwgVersion.Current);
            }
            var dense = ExternalCadLibrary.ReadFolder(root, "").Single();
            var sheet = new ExternalCadSheet();
            var origin = new Point3d(40000, 45000, 0);
            using (var source = ExternalCadLibrary.OpenDrawing(dense.Path))
            {
                ExternalCadLibrary.LoadPreview(dense, source);
                Check(dense.Bounds.MaxPoint.Y == count && dense.Preview.Count == ExternalCadLibrary.PreviewLimit
                    && ExternalCadPreview.Single(sheet, dense, 0).Count() == 500, "预览限量改变了实际对象边界或拖动预算");
                ExternalCadSheet.Insert(doc, sheet, dense, origin, 0, source);
            }
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
                Check(ExternalCadLibrary.Entities(doc.Database, tr).Cast<ObjectId>()
                    .Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<Line>()
                    .Count(line => Math.Abs(line.StartPoint.Y - origin.Y) < 1e-7 && line.StartPoint.X >= origin.X) == count,
                    "缩减拖动预览影响了 CAD 实际插入对象");
            doc.Editor.WriteMessage("\nEXTERNAL_PERFORMANCE_OK clockwise sourceReuse drag500 fullInsertion3200\n");
        }

        private static void CheckExistingCopies(Document doc)
        {
            var folder = Environment.GetEnvironmentVariable("WANLUO_EXTERNAL_LIBRARY_QA_EXISTING");
            if (string.IsNullOrWhiteSpace(folder)) return;
            var assets = ExternalCadLibrary.Read(folder);
            Check(assets.Count >= 2 && assets.All(a => !a.IsCollected), "未读到普通 DWG 样本");
            var bytes = assets.ToDictionary(a => a.Path, a => File.ReadAllBytes(a.Path));
            foreach (var asset in assets)
            {
                ExternalCadLibrary.LoadPreview(asset);
                Check(asset.Preview.Count > 0, "样本缺少实际几何预览：" + asset.Name);
            }
            var plan = ExternalCadSheet.Arrange(assets, null, 25, 10, true, false, false, 1);
            ExternalCadSheet.Insert(doc, new ExternalCadSheet(), assets[0], new Point3d(70000, 25000, 0), 1);
            ExternalCadSheet.Insert(doc, plan, null, new Point3d(100000, 25000, 0), 0);
            Check(assets.All(a => bytes[a.Path].SequenceEqual(File.ReadAllBytes(a.Path))), "插入改写了已有图库样本");
            doc.Editor.WriteMessage("\nEXTERNAL_EXISTING_DWG_OK " + assets.Count + " previews single batch readOnly\n");
        }

        private static ObjectId Add(BlockTableRecord target, Transaction tr, Entity value)
        {
            var id = target.AppendEntity(value);
            tr.AddNewlyCreatedDBObject(value, true);
            return id;
        }
        private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
    }
}
