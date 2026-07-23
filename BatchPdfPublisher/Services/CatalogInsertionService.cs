using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public sealed class CatalogSettings
    {
        public bool IncludeBuilding = true, IncludeNumber = true, IncludeName = true, IncludePaper = true, IncludeScale = true;
        public int RowsPerPage = 30; public double RowHeight = 7, TextHeight = 3.5, Scale = 1;
        public double[] ColumnWidths = { 20, 30, 70, 24, 24 };
        public string Font = "黑体"; public Autodesk.AutoCAD.Colors.Color Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
    }

    public static class CatalogInsertionService
    {
        public static bool Insert(Document document, IList<SheetItem> sheets, CatalogSettings settings)
        {
            if (document == null || sheets == null || sheets.Count == 0) return false;
            var point = document.Editor.GetPoint("\n指定目录左上角插入点: "); if (point.Status != PromptStatus.OK) return false;
            var allColumns = new[] { "序号", "图号", "图名", "图框", "比例" };
            var enabled = new[] { settings.IncludeBuilding, settings.IncludeNumber, settings.IncludeName, settings.IncludePaper, settings.IncludeScale };
            var columns = allColumns.Where((name, index) => enabled[index]).ToList();
            if (columns.Count == 0) throw new InvalidOperationException("请至少选择一列目录内容。");
            var widths = new List<double>(); var sourceWidths = settings.ColumnWidths ?? new double[0]; for (var index = 0; index < allColumns.Length; index++) if (enabled[index]) widths.Add((index < sourceWidths.Length ? sourceWidths[index] : 30) * settings.Scale);
            var rowHeight = settings.RowHeight * settings.Scale; var textHeight = settings.TextHeight * settings.Scale; var tableWidth = Sum(widths); var headerHeight = rowHeight;
            // 本方法由 BPPINSERTCATALOG 原生命令调用，CAD 已经持有当前文档锁；
            // 再次 LockDocument 会在部分 AutoCAD/T20 环境抛出 eNotApplicable。
            using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite); var style = FrameCreationService.EnsureTextStyle(document.Database, tr, settings.Font);
                var verticalOffset = 0d;
                foreach (var group in sheets.GroupBy(s => string.IsNullOrWhiteSpace(s.Building) ? "未分组" : s.Building))
                {
                    var groupSheets = group.ToList();
                    AddText(space, tr, group.Key + " 图纸目录", new Point3d(point.Value.X + tableWidth / 2, point.Value.Y - verticalOffset - rowHeight / 2, 0), textHeight, style, settings.Color);
                    verticalOffset += rowHeight * 1.5;
                    for (var start = 0; start < groupSheets.Count; start += Math.Max(1, settings.RowsPerPage))
                    {
                    var count = Math.Min(Math.Max(1, settings.RowsPerPage), groupSheets.Count - start); var top = point.Value.Y - verticalOffset; var left = point.Value.X;
                    AddLine(space, tr, new Point3d(left, top, 0), new Point3d(left + tableWidth, top, 0)); AddLine(space, tr, new Point3d(left, top - headerHeight - count * rowHeight, 0), new Point3d(left + tableWidth, top - headerHeight - count * rowHeight, 0));
                    var x = left; for (var c = 0; c <= widths.Count; c++) { AddLine(space, tr, new Point3d(x, top, 0), new Point3d(x, top - headerHeight - count * rowHeight, 0)); if (c < widths.Count) x += widths[c]; }
                    for (var row = 0; row < count; row++) { var y = top - headerHeight - row * rowHeight; AddLine(space, tr, new Point3d(left, y, 0), new Point3d(left + tableWidth, y, 0)); }
                    var currentX = left; for (var c = 0; c < columns.Count; c++) { AddText(space, tr, columns[c], new Point3d(currentX + widths[c] / 2, top - headerHeight / 2, 0), textHeight, style, settings.Color); currentX += widths[c]; }
                    for (var row = 0; row < count; row++) { var sheet = groupSheets[start + row]; var values = Values(sheet, settings, start + row + 1); currentX = left; for (var c = 0; c < values.Length; c++) { AddText(space, tr, values[c], new Point3d(currentX + widths[c] / 2, top - headerHeight - row * rowHeight - rowHeight / 2, 0), textHeight, style, settings.Color); currentX += widths[c]; } }
                    verticalOffset += headerHeight + count * rowHeight + rowHeight;
                    }
                    verticalOffset += rowHeight;
                }
                tr.Commit();
            }
            document.Editor.Regen();
            return true;
        }
        private static string[] Values(SheetItem sheet, CatalogSettings s, int number) { var all = new[] { number.ToString(), sheet.SheetNumber, sheet.SheetName, sheet.FrameDisplay, sheet.PrintScale }; var enabled = new[] { s.IncludeBuilding, s.IncludeNumber, s.IncludeName, s.IncludePaper, s.IncludeScale }; return all.Where((value, index) => enabled[index]).ToArray(); }
        private static double Sum(IList<double> values) { var result = 0d; foreach (var value in values) result += value; return result; }
        private static void AddLine(BlockTableRecord space, Transaction tr, Point3d a, Point3d b) { var line = new Line(a, b); space.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true); }
        private static void AddText(BlockTableRecord space, Transaction tr, string value, Point3d center, double height, ObjectId style, Autodesk.AutoCAD.Colors.Color color)
        {
            // AutoCAD/T20 要求先设置对齐模式，再设置 AlignmentPoint，否则会抛出 eNotApplicable。
            var text = new DBText();
            text.TextString = value ?? string.Empty;
            text.Height = height;
            text.Position = center;
            text.TextStyleId = style;
            text.HorizontalMode = TextHorizontalMode.TextCenter;
            text.VerticalMode = TextVerticalMode.TextVerticalMid;
            text.AlignmentPoint = center;
            if (color == null || color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci) text.ColorIndex = color == null ? (short)7 : color.ColorIndex;
            else text.Color = color;
            space.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true); text.AdjustAlignment(space.Database);
        }
    }
}
