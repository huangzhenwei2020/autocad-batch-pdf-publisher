using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;      // DoorWindowElevationPreference / DoorWindowElevationTemplate
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// CAD 侧的两个搬运工（P0）：
    ///
    /// - <see cref="PlaceView"/>（命令 <c>LTTZ</c>）：读建模程序产出的视图 JSON，
    ///   按视图图层表建成 CAD 实体（线 / 文字 / 填充）。
    /// - <see cref="ExportDrawing"/>（命令 <c>TQTZ</c>）：把当前 DWG 的图元导成中间格式，
    ///   交给建模程序去识别（识别本身是 P3，这版只负责把数据搬出去）。
    ///
    /// 设计约束（见开发计划 v3）：模型与投影都在程序侧，插件只做"进出"两件事，
    /// 因此这里刻意不引入任何几何算法；数据库访问全部在宿主线程内、用事务包住。
    /// </summary>
    internal static class BuildingModelCadBridge
    {
        public static void PlaceView(Document document)
        {
            if (document == null) return;
            var editor = document.Editor;
            SelectedViewId = null;                       // 每次落图重新记
            var path = PickViewFile(document, editor);
            if (string.IsNullOrWhiteSpace(path)) return;
            var placedViewId = SelectedViewId;

            ViewDocument view;
            try { view = BuildingModelJson.LoadView(path); }
            catch (Exception exception)
            {
                editor.WriteMessage("\n视图文件读取失败：" + exception.Message);
                return;
            }
            SaveLastViewFolder(Path.GetDirectoryName(path));

            var pointResult = editor.GetPoint("\n指定视图插入点（视图左下角）：");
            if (pointResult.Status != PromptStatus.OK) return;
            var anchor = pointResult.Value;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hatchIds = new List<ObjectId>();
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                EnsureViewLayers(document.Database, transaction);
                var space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite);

                // 图纸（Kind=Sheet）：先看看项目里有没有同纸张的登记图框，有就套用它，
                // 并跳过图纸自带的图框与标题栏（那一层叫 WL-模型-图纸框），避免双层图框。
                var skipLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var frame = view.Kind == ViewKind.Sheet ? InsertProjectFrame(document, transaction, space, view, anchor) : null;
                if (frame != null) skipLayers.Add(ViewLayers.SheetFrame);

                foreach (var line in view.Lines ?? new List<ViewLine>())
                {
                    if (!string.IsNullOrWhiteSpace(line.Layer) && skipLayers.Contains(line.Layer)) continue;
                    var entity = new Line(
                        new Point3d(anchor.X + line.X1, anchor.Y + line.Y1, 0d),
                        new Point3d(anchor.X + line.X2, anchor.Y + line.Y2, 0d));
                    ApplyLayer(entity, line.Layer);
                    ApplyLineType(transaction, document.Database, entity, line.LineType);
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    Bump(counts, line.Layer);
                }

                foreach (var text in view.Texts ?? new List<ViewText>())
                {
                    var entity = new DBText
                    {
                        TextString = text.Text ?? string.Empty,
                        Height = text.Height > 0.5d ? text.Height : 250d,
                        Position = new Point3d(anchor.X + text.X, anchor.Y + text.Y, 0d)
                    };
                    ApplyLayer(entity, text.Layer);
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    Bump(counts, text.Layer);
                }

                // 圆（轴号圆圈等）
                foreach (var circle in view.Circles ?? new List<ViewCircle>())
                {
                    if (circle == null || Math.Abs(circle.Radius) < 0.5d) continue;
                    var entity = new Circle(new Point3d(anchor.X + circle.X, anchor.Y + circle.Y, 0d), Vector3d.ZAxis,
                        Math.Abs(circle.Radius));
                    ApplyLayer(entity, circle.Layer);
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    Bump(counts, circle.Layer);
                }

                // 尺寸标注：建成**真的 CAD 标注**（可拉伸、可改），不是拆成线 +
                // 文字。样式用插件统一的"万落建筑工具 1:N 标注样式"，与门窗立面一致。
                if (view.Dimensions != null && view.Dimensions.Count > 0)
                {
                    ObjectId dimensionStyle;
                    try
                    {
                        dimensionStyle = DraftingStandardService.EnsureDimensionStyleForScale(
                            document.Database, transaction, Math.Max(1, view.Scale));
                    }
                    catch (Exception exception)
                    {
                        dimensionStyle = document.Database.Dimstyle;
                        editor.WriteMessage("\n标注样式创建失败，改用当前标注样式：" + exception.Message);
                    }
                    foreach (var dimension in view.Dimensions)
                    {
                        if (dimension == null) continue;
                        var span = Math.Abs(dimension.To - dimension.From);
                        if (span < 0.5d) continue;
                        try
                        {
                            var entity = CreateDimension(dimension, anchor, dimensionStyle);
                            ApplyLayer(entity, string.IsNullOrWhiteSpace(dimension.Layer) ? ViewLayers.Dimension : dimension.Layer);
                            space.AppendEntity(entity);
                            transaction.AddNewlyCreatedDBObject(entity, true);
                            Bump(counts, string.IsNullOrWhiteSpace(dimension.Layer) ? ViewLayers.Dimension : dimension.Layer);
                        }
                        catch (Exception exception)
                        {
                            editor.WriteMessage("\n一条尺寸标注失败已跳过（" + dimension.Note + "）：" + exception.Message);
                        }
                    }
                }

                foreach (var hatch in view.Hatches ?? new List<ViewHatch>())
                {                    if (hatch.Boundary == null || hatch.Boundary.Count < 3) continue;
                    var entity = new Hatch { Associative = false };
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    entity.SetDatabaseDefaults(document.Database);
                    ApplyLayer(entity, hatch.Layer);
                    ApplyHatchPattern(entity, hatch, editor);
                    var loop = new Point2dCollection();
                    foreach (var point in hatch.Boundary) loop.Add(new Point2d(anchor.X + point.X, anchor.Y + point.Y));
                    try
                    {
                        entity.AppendLoop(HatchLoopTypes.External, loop, new DoubleCollection());
                        entity.EvaluateHatch(true);
                        hatchIds.Add(entity.ObjectId);
                        Bump(counts, hatch.Layer);
                    }
                    catch (Exception exception)
                    {
                        entity.Erase();
                        editor.WriteMessage("\n一处剖切填充失败已跳过：" + exception.Message);
                    }
                }

                // 填充压到最底层：否则实心/图案会把断面轮廓线盖住，看着"糊成一团"。
                if (hatchIds.Count > 0)
                {
                    try
                    {
                        var order = (DrawOrderTable)transaction.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
                        order.MoveToBottom(new ObjectIdCollection(hatchIds.ToArray()));
                    }
                    catch (Exception exception)
                    {
                        editor.WriteMessage("\n提示：填充置底未成功（不影响出图）：" + exception.Message);
                    }
                }
                transaction.Commit();
            }

            editor.WriteMessage("\n落图完成：" + (view.Title ?? view.Id) + "（1:" + view.Scale + "）");
            foreach (var pair in counts.OrderByDescending(x => x.Value))
                editor.WriteMessage("\n  " + pair.Key + "：" + pair.Value + " 个实体");
            if (view.Warnings != null && view.Warnings.Count > 0)
                editor.WriteMessage("\n提示：" + string.Join("；", view.Warnings.ToArray()));

            // 落完图把这一条的"待落图"标记去掉（建模程序推过来的那几张，落一张少一张）
            if (!string.IsNullOrWhiteSpace(placedViewId))
            {
                try
                {
                    var project = new PublishPlanStore().GetActiveProject();
                    var modelFolder = StudioLaunch.FindModelFolder(project == null ? null : project.ProjectFolder,
                        project == null ? null : project.Name);
                    var pending = StudioLaunch.ReadPending(modelFolder);
                    if (pending != null && pending.Entries.Any(entry => entry != null
                        && string.Equals(entry.Id, placedViewId, StringComparison.OrdinalIgnoreCase)))
                    {
                        StudioLaunch.RemovePending(modelFolder, placedViewId);
                        var left = StudioLaunch.ReadPending(modelFolder);
                        var remaining = left == null ? 0 : left.Entries.Count;
                        editor.WriteMessage(remaining > 0
                            ? "\n（还有 " + remaining + " 张待落图：再执行 LTTZ 即可，带 ★ 的就是）\n"
                            : "\n（建模程序推过来的图纸已全部落图）\n");
                    }
                }
                catch
                {
                    // 清标记失败不影响已经落好的图
                }
            }
        }

        public static void ExportDrawing(Document document)
        {
            if (document == null) return;
            var editor = document.Editor;
            string path;
            using (var dialog = new SaveFileDialog
            {
                Title = "导出给建模程序的中间文件",
                Filter = "提取文件 (*.json)|*.json",
                FileName = "import.json",
                InitialDirectory = LastViewFolder()
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                path = dialog.FileName;
            }

            var payload = new DrawingImportDocument
            {
                DrawingName = Path.GetFileName(document.Name ?? string.Empty),
                DrawingPath = document.Name,
                ExtractedAt = DateTime.Now.ToString("O")
            };

            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (var record in (LayerTable)transaction.GetObject(document.Database.LayerTableId, OpenMode.ForRead))
                {
                    var layer = (LayerTableRecord)transaction.GetObject(record, OpenMode.ForRead);
                    payload.Layers.Add(new LayerInfoModel
                    {
                        Name = layer.Name,
                        Color = (short)layer.Color.ColorIndex,
                        LineType = LinetypeName(transaction, layer.LinetypeObjectId)
                    });
                }

                var space = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead);
                foreach (var id in space)
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;
                    var model = Describe(entity, transaction);
                    if (model != null) payload.Entities.Add(model);
                }
                transaction.Commit();
            }

            try
            {
                BuildingModelJson.SaveImport(path, payload);
                editor.WriteMessage("\n提取完成：" + payload.Entities.Count + " 个图元、"
                    + payload.Layers.Count + " 个图层 → " + path);
                editor.WriteMessage("\n在建模程序里用这个文件做识别（P3 之前仅用于核对数据）。");
            }
            catch (Exception exception)
            {
                editor.WriteMessage("\n提取文件写入失败：" + exception.Message);
            }
            SaveLastViewFolder(Path.GetDirectoryName(path));
        }

        private static DrawingEntityModel Describe(Entity entity, Transaction transaction)
        {
            var model = new DrawingEntityModel { Layer = entity.Layer, Handle = entity.Handle.ToString() };
            var line = entity as Line;
            if (line != null)
            {
                model.Type = "LINE";
                model.Points.Add(new PointModel(line.StartPoint.X, line.StartPoint.Y));
                model.Points.Add(new PointModel(line.EndPoint.X, line.EndPoint.Y));
                return model;
            }
            var polyline = entity as Polyline;
            if (polyline != null)
            {
                model.Type = "POLYLINE";
                for (var index = 0; index < polyline.NumberOfVertices; index++)
                {
                    var point = polyline.GetPoint2dAt(index);
                    model.Points.Add(new PointModel(point.X, point.Y));
                }
                model.X = polyline.Elevation;
                return model;
            }
            var lightweight = entity as Polyline2d;
            if (lightweight != null)
            {
                model.Type = "POLYLINE2D";
                foreach (ObjectId vertexId in lightweight)
                {
                    var vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as Vertex2d;
                    if (vertex == null) continue;
                    model.Points.Add(new PointModel(vertex.Position.X, vertex.Position.Y));
                }
                return model;
            }
            var circle = entity as Circle;
            if (circle != null)
            {
                model.Type = "CIRCLE";
                model.X = circle.Center.X;
                model.Y = circle.Center.Y;
                model.Radius = circle.Radius;
                return model;
            }
            var arc = entity as Arc;
            if (arc != null)
            {
                model.Type = "ARC";
                model.X = arc.Center.X;
                model.Y = arc.Center.Y;
                model.Radius = arc.Radius;
                return model;
            }
            var block = entity as BlockReference;
            if (block != null)
            {
                model.Type = "INSERT";
                model.X = block.Position.X;
                model.Y = block.Position.Y;
                model.Rotation = block.Rotation;
                model.Scale = block.ScaleFactors.X;
                try
                {
                    var record = (BlockTableRecord)transaction.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                    model.BlockName = record.Name;
                }
                catch { model.BlockName = string.Empty; }
                return model;
            }
            var text = entity as DBText;
            if (text != null)
            {
                model.Type = "TEXT";
                model.Text = text.TextString;
                model.X = text.Position.X;
                model.Y = text.Position.Y;
                model.TextHeight = text.Height;
                return model;
            }
            var mtext = entity as MText;
            if (mtext != null)
            {
                model.Type = "MTEXT";
                model.Text = mtext.Contents;
                model.X = mtext.Location.X;
                model.Y = mtext.Location.Y;
                model.TextHeight = mtext.TextHeight;
                return model;
            }
            return null;
        }

        private static string LinetypeName(Transaction transaction, ObjectId id)
        {
            try
            {
                if (id.IsNull) return string.Empty;
                var record = (LinetypeTableRecord)transaction.GetObject(id, OpenMode.ForRead);
                return record.Name;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 导出门窗类型库（命令 <c>TQLX</c>）：把**插件里已有的**门窗参数与立面模板写成
        /// <c>openings.json</c>，让建模程序放门窗时直接选类型，而不是重新录一遍。
        ///
        /// 参数来源：
        /// - 洞口尺寸与做法：当前项目的 `DoorWindowElevationPreference`（门窗立面的"按工程记忆"）；
        /// - 做法模板：`DoorWindowElevationTemplate`（普通双扇推拉窗之类）。
        /// </summary>
        public static void ExportOpeningLibrary(Document document)
        {
            if (document == null) return;
            var editor = document.Editor;

            string projectName;
            List<DoorWindowElevationPreference> preferences;
            List<DoorWindowElevationTemplate> templates;
            try
            {
                var project = new PublishPlanStore().GetActiveProject();
                projectName = project == null ? "默认项目" : project.Name;
                preferences = new DoorWindowElevationStore().LoadForActiveProject();
                templates = new DoorWindowElevationTemplateStore().Load();
            }
            catch (Exception exception)
            {
                editor.WriteMessage("\n读取门窗参数失败：" + exception.Message);
                return;
            }

            var library = new OpeningTypeLibraryDocument
            {
                ProjectName = projectName,
                ExportedAt = DateTime.Now.ToString("O")
            };
            foreach (var template in templates ?? new List<DoorWindowElevationTemplate>())
            {
                if (template == null || string.IsNullOrWhiteSpace(template.Name)) continue;
                library.Templates.Add(new OpeningTemplateModel
                {
                    Name = template.Name,
                    ElevationType = template.ElevationType,
                    DivisionPreset = template.DivisionPreset,
                    OpeningMode = template.OpeningMode
                });
            }

            // 同一编号可能有多个尺寸（改过洞口尺寸的历史记录）：保留最后处理到的那条
            var byCode = new Dictionary<string, OpeningTypeModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var preference in preferences ?? new List<DoorWindowElevationPreference>())
            {
                if (preference == null || string.IsNullOrWhiteSpace(preference.Code)) continue;
                var code = preference.Code.Trim();
                var kind = KindOf(preference, code);
                byCode[code] = new OpeningTypeModel
                {
                    Code = code,
                    Kind = kind,
                    Width = preference.Width > 0.5d ? preference.Width : DefaultWidth(kind),
                    Height = preference.Height > 0.5d ? preference.Height : DefaultHeight(kind),
                    Sill = preference.HasSillHeight && !preference.SillHeightSuppressed ? preference.SillHeight : DefaultSill(kind),
                    ElevationType = preference.ElevationType,
                    DivisionPreset = preference.DivisionPreset,
                    OpeningMode = preference.OpeningMode,
                    HasOuterFrame = preference.HasOuterFrame,
                    OuterFrameWidth = preference.OuterFrameWidth,
                    HasMullion = preference.HasMullion,
                    MullionWidth = preference.MullionWidth,
                    HasInstallationGap = preference.HasInstallationGap,
                    InstallationGap = preference.InstallationGap,
                    DoorFrameType = preference.DoorFrameType,
                    DoorFrameWidth = preference.DoorFrameWidth,
                    CustomColumnRatios = preference.CustomColumnRatios,
                    CustomRowRatios = preference.CustomRowRatios,
                    CustomColumnWidths = preference.CustomColumnWidths,
                    CustomRowHeights = preference.CustomRowHeights,
                    CustomCellLayout = preference.CustomCellLayout,
                    CellOpeningModes = preference.CellOpeningModes,
                    DoorPlacement = preference.DoorPlacement,
                    DoorEdgeDistance = preference.DoorEdgeDistance,
                    BayLeftSide = preference.BayLeftSide,
                    BayRightSide = preference.BayRightSide,
                    BayLeftDepth = preference.BayLeftDepth,
                    BayRightDepth = preference.BayRightDepth,
                    BayLeftCellLayout = preference.BayLeftCellLayout,
                    BayRightCellLayout = preference.BayRightCellLayout,
                    Material = preference.Material,
                    AtlasName = preference.AtlasName,
                    Remarks = preference.Remarks,
                    Source = "项目参数：" + projectName
                };
            }
            library.Types.AddRange(byCode.Values.OrderBy(t => t.Code, StringComparer.OrdinalIgnoreCase));

            string path;
            using (var dialog = new SaveFileDialog
            {
                Title = "导出门窗类型库（建模程序读取它来放门窗）",
                Filter = "门窗类型库 (*.json)|*.json",
                FileName = "openings.json",
                InitialDirectory = LastViewFolder()
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                path = dialog.FileName;
            }

            try
            {
                BuildingModelJson.SaveOpeningLibrary(path, library);
                editor.WriteMessage("\n门窗类型库已导出：" + library.Types.Count + " 个类型、"
                    + library.Templates.Count + " 个做法模板 → " + path);
                if (library.Types.Count == 0)
                    editor.WriteMessage("\n提示：当前项目还没有门窗参数。可先在“门窗立面（MCLM）”里录一次，"
                        + "再导出；也可以先导出空库供程序使用。");
            }
            catch (Exception exception)
            {
                editor.WriteMessage("\n门窗类型库写入失败：" + exception.Message);
            }
            SaveLastViewFolder(Path.GetDirectoryName(path));
        }

        private static string KindOf(DoorWindowElevationPreference preference, string code)
        {
            var type = (preference.ElevationType ?? string.Empty).Trim();
            if (type.Length > 0) return type;
            if (!string.IsNullOrWhiteSpace(preference.DoorFrameType)) return "门";
            if (code.StartsWith("M", StringComparison.OrdinalIgnoreCase)) return "门";
            if (code.StartsWith("C", StringComparison.OrdinalIgnoreCase)) return "窗";
            return "窗";
        }

        private static double DefaultWidth(string kind)
        {
            return string.Equals(kind, "门", StringComparison.OrdinalIgnoreCase) ? 900d : 1500d;
        }

        private static double DefaultHeight(string kind)
        {
            return string.Equals(kind, "门", StringComparison.OrdinalIgnoreCase) ? 2100d : 1800d;
        }

        private static double DefaultSill(string kind)
        {
            return string.Equals(kind, "门", StringComparison.OrdinalIgnoreCase) ? 0d : 900d;
        }

        /// <summary>
        /// 设置填充图案。
        /// 优先用"用户定义 45° 细线 + 模型单位间距"——图纸上看起来就是常规的剖面斜线填充，
        /// 而且间距是我们自己给的毫米值，不依赖图案自身的基准间距；失败则退到预定义图案，最后退到实心。
        /// </summary>
        private static void ApplyHatchPattern(Hatch entity, ViewHatch hatch, Editor editor)
        {
            var pattern = string.IsNullOrWhiteSpace(hatch.Pattern) ? "ANSI31" : hatch.Pattern;
            if (hatch.Spacing > 0.01d)
            {
                try
                {
                    entity.SetHatchPattern(HatchPatternType.UserDefined, pattern);
                    entity.PatternAngle = hatch.Angle != 0d ? hatch.Angle * Math.PI / 180d : Math.PI / 4d;
                    entity.PatternSpace = hatch.Spacing;
                    entity.PatternDouble = false;
                    entity.PatternScale = 1d;
                    return;
                }
                catch (Exception exception)
                {
                    editor.WriteMessage("\n提示：用户定义填充不可用，改用预定义图案：" + exception.Message);
                }
            }
            try
            {
                entity.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                // ANSI31 的基准间距约 0.125 图形单位，按目标间距反算比例
                entity.PatternScale = hatch.Scale > 0.01d
                    ? hatch.Scale
                    : Math.Max(1d, hatch.Spacing > 0.01d ? hatch.Spacing / 0.125d : 1d);
                entity.PatternAngle = hatch.Angle * Math.PI / 180d;
            }
            catch
            {
                entity.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            }
        }

        private static void EnsureViewLayers(Database database, Transaction transaction)
        {
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            foreach (var style in ViewLayers.All)
            {
                if (table.Has(style.Name)) continue;
                table.UpgradeOpen();
                var record = new LayerTableRecord
                {
                    Name = style.Name,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, style.Color),
                    LineWeight = (LineWeight)style.LineWeight
                };
                if (!string.Equals(style.LineType, "Continuous", StringComparison.OrdinalIgnoreCase))
                {
                    var lineTypes = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
                    if (!lineTypes.Has(style.LineType))
                    {
                        try { database.LoadLineTypeFile(style.LineType, "acad.lin"); } catch { }
                        lineTypes = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
                    }
                    if (lineTypes.Has(style.LineType)) record.LinetypeObjectId = lineTypes[style.LineType];
                }
                table.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
        }

        /// <summary>
        /// 落图选文件：**先列出当前项目已生成的图纸/视图让你挑序号**（不用翻文件对话框），
        /// 输入 0 才走文件浏览（落别处的文件或没建项目时用）。
        /// </summary>
        private static string PickViewFile(Document document, Editor editor)
        {
            string projectFolder = null;
            string modelName = null;
            try
            {
                var project = new PublishPlanStore().GetActiveProject();
                projectFolder = project == null ? null : project.ProjectFolder;
                modelName = project == null ? null : project.Name;
            }
            catch
            {
                // 读项目失败就当没有项目，直接走文件浏览
            }
            var modelFolder = StudioLaunch.FindModelFolder(projectFolder, modelName);
            var entries = StudioLaunch.ListViews(modelFolder);
            if (entries.Count == 0)
            {
                if (modelFolder != null)
                    editor.WriteMessage("\n当前项目的模型目录里还没有视图：" + modelFolder
                        + "\n先在建模程序里「生成全部视图」（CAD 里执行 JZMX 打开建模程序）。");
                return BrowseViewFile();
            }

            editor.WriteMessage("\n请选择要落图的图纸/视图（当前项目：" + (modelName ?? "未命名") + "）：");
            for (var index = 0; index < entries.Count; index++)
                editor.WriteMessage("\n  " + (index + 1).ToString("00") + "）" + entries[index].Display);
            editor.WriteMessage("\n  00）浏览其它文件…");
            var pendingCount = entries.Count(entry => entry.Pending);
            if (pendingCount > 0)
                editor.WriteMessage("\n（★ = 建模程序刚推过来、还没落图的 " + pendingCount + " 张，默认落第一张）");
            var options = new PromptIntegerOptions("\n输入序号")
            {
                DefaultValue = 1, AllowNone = false, AllowZero = true, UseDefaultValue = true
            };
            var result = editor.GetInteger(options);
            if (result.Status != PromptStatus.OK) return null;
            if (result.Value <= 0) return BrowseViewFile();
            if (result.Value > entries.Count) return entries[entries.Count - 1].FilePath;
            // 记下选的是哪一条：落图成功后要把它的"待落图"标记去掉
            SelectedViewId = entries[result.Value - 1].Id;
            return entries[result.Value - 1].FilePath;
        }

        /// <summary>刚刚选中的视图 id（落图成功后用来清"待落图"标记）。</summary>
        [ThreadStatic]
        private static string SelectedViewId;

        private static string BrowseViewFile()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "选择建模程序生成的视图文件（views\\*.json）",
                Filter = "视图文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                InitialDirectory = LastViewFolder()
            })
            {
                return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            }
        }

        /// <summary>
        /// 图纸落图时套用项目已登记的图框模板：
        /// 按图纸的纸张规格（<see cref="ViewDocument.PaperName"/>，或用 <see cref="ViewDocument.FrameTemplate"/> 指定块名）
        /// 在项目图框里找一条匹配的，把它插到插入点（按纸张缩放到 1:1 纸面），并回填图名/图号/比例属性。
        /// 找不到就返回 null —— 调用方照常使用图纸自带的图框。
        /// </summary>
        private static FrameDefinition InsertProjectFrame(Document document, Transaction transaction,
            BlockTableRecord space, ViewDocument view, Point3d anchor)
        {
            try
            {
                var frames = new PublishPlanStore().LoadFrames()
                    .Where(f => f != null && !string.IsNullOrWhiteSpace(f.BlockName)).ToList();
                if (frames.Count == 0)
                {
                    document.Editor.WriteMessage("\n（项目里还没有登记图框：本次用图纸自带的图框；"
                        + "在「图框登记」里登记 " + (view.PaperName ?? "同规格") + " 图框后会自动套用。）");
                    return null;
                }
                var frame = PickFrame(frames, view);
                if (frame == null)
                {
                    document.Editor.WriteMessage("\n（项目里没有 " + (view.PaperName ?? "同规格")
                        + " 的登记图框：本次用图纸自带的图框。）");
                    return null;
                }
                FrameTemplateStore.EnsureAvailable(document, frame);
                var blockTable = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                if (!blockTable.Has(frame.BlockName))
                {
                    document.Editor.WriteMessage("\n（图框块“" + frame.BlockName + "”不在当前图纸里：本次用图纸自带的图框。）");
                    return null;
                }
                var definitionId = blockTable[frame.BlockName];
                var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
                var bounds = DefinitionBounds(definition, transaction);
                if (bounds == null) return null;

                var paperWidth = view.PaperWidth > 1d ? view.PaperWidth : 420d;
                var paperHeight = view.PaperHeight > 1d ? view.PaperHeight : 297d;
                var factor = Math.Min(paperWidth / bounds.Width, paperHeight / bounds.Height);
                if (!(factor > 0d) || double.IsInfinity(factor)) return null;

                var position = new Point3d(anchor.X - bounds.MinX * factor, anchor.Y - bounds.MinY * factor, anchor.Z);
                var reference = new BlockReference(position, definitionId) { ScaleFactors = new Scale3d(factor) };
                // 图框放到制图标准的图框图层上（与门窗立面、大样插入保持一致）
                try { reference.LayerId = DraftingStandardService.EnsureFrameLayer(document.Database, transaction); } catch { }
                space.AppendEntity(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);
                FillFrameAttributes(definition, transaction, reference, frame, view);
                document.Editor.WriteMessage("\n已套用项目图框：“" + frame.DisplayName + "”（按纸张缩放到 "
                    + paperWidth.ToString("0") + "×" + paperHeight.ToString("0") + "，图纸自带图框已跳过）。");
                return frame;
            }
            catch (Exception exception)
            {
                document.Editor.WriteMessage("\n套用项目图框失败（将使用图纸自带图框）：" + exception.Message);
                return null;
            }
        }

        /// <summary>按纸张规格与横竖方向挑一条项目图框（<see cref="ViewDocument.FrameTemplate"/> 指定了块名就优先用它）。</summary>
        private static FrameDefinition PickFrame(List<FrameDefinition> frames, ViewDocument view)
        {
            if (!string.IsNullOrWhiteSpace(view.FrameTemplate))
            {
                var named = frames.FirstOrDefault(f => string.Equals(f.BlockName, view.FrameTemplate, StringComparison.OrdinalIgnoreCase));
                if (named != null) return named;
            }
            var wanted = (view.PaperName ?? string.Empty).Trim();
            if (wanted.Length == 0) return frames.FirstOrDefault();
            var landscape = view.PaperWidth >= view.PaperHeight;
            var byPaper = frames.Where(f => string.Equals((f.PaperSize ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byPaper.Count == 0) return null;
            var byOrientation = byPaper.Where(f => SameOrientation(f.PaperOrientation, landscape)).ToList();
            return (byOrientation.Count > 0 ? byOrientation : byPaper).First();
        }

        private static bool SameOrientation(string orientation, bool landscape)
        {
            var text = (orientation ?? string.Empty).Trim();
            if (text.Length == 0) return true;                       // 没登记方向的先用着
            var isLandscape = text.IndexOf("横", StringComparison.Ordinal) >= 0;
            return isLandscape == landscape;
        }

        private static void FillFrameAttributes(BlockTableRecord definition, Transaction transaction,
            BlockReference reference, FrameDefinition frame, ViewDocument view)
        {
            foreach (ObjectId id in definition)
            {
                var attributeDefinition = transaction.GetObject(id, OpenMode.ForRead, false) as AttributeDefinition;
                if (attributeDefinition == null || attributeDefinition.Constant) continue;
                var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                var tag = (attributeDefinition.Tag ?? string.Empty).Trim();
                var value = attributeDefinition.TextString;
                if (TagMatches(tag, frame.PrintScaleAttributeTag, "比例")) value = "1:1";
                else if (TagMatches(tag, frame.SheetNameAttributeTag, "图纸名称", "图名")) value = view.Title ?? string.Empty;
                else if (TagMatches(tag, frame.SheetNumberAttributeTag, "图号")) value = SheetNumberOf(view);
                else if (TagMatches(tag, frame.BuildingAttributeTag, "子项目名称") && !string.IsNullOrWhiteSpace(frame.DefaultBuilding))
                    value = frame.DefaultBuilding;
                attribute.TextString = string.IsNullOrWhiteSpace(value) || value.StartsWith("<", StringComparison.Ordinal) ? tag : value;
                reference.AttributeCollection.AppendAttribute(attribute);
                transaction.AddNewlyCreatedDBObject(attribute, true);
            }
        }

        /// <summary>从图纸标题里取图号（标题形如"建施-01　一层 平面图　A3 横…"）。</summary>
        private static string SheetNumberOf(ViewDocument view)
        {
            var title = view.Title ?? string.Empty;
            var parts = title.Split(new[] { '　', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 && parts[0].IndexOf("建施", StringComparison.Ordinal) >= 0 ? parts[0] : (view.Id ?? "SHEET");
        }

        private static bool TagMatches(string tag, string configured, params string[] fallbacks)
        {
            var value = (tag ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(configured) && string.Equals(value, configured.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var fallback in fallbacks)
                if (!string.IsNullOrWhiteSpace(fallback) && value.IndexOf(fallback, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private sealed class DefinitionBoundsInfo
        {
            public double MinX, MinY, Width, Height;
        }

        private static DefinitionBoundsInfo DefinitionBounds(BlockTableRecord definition, Transaction transaction)
        {
            var first = true;
            var extents = new Extents3d();
            foreach (ObjectId id in definition)
            {
                var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity == null) continue;
                try
                {
                    if (first) { extents = entity.GeometricExtents; first = false; }
                    else extents.AddExtents(entity.GeometricExtents);
                }
                catch { }
            }
            if (first) return null;
            var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
            var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            if (width < 1e-6d || height < 1e-6d) return null;
            return new DefinitionBoundsInfo
            {
                MinX = extents.MinPoint.X, MinY = extents.MinPoint.Y, Width = width, Height = height
            };
        }

        /// <summary>
        /// 把一条视图尺寸变成 CAD 的 `RotatedDimension`：
        /// 竖直尺寸旋转 90°（量 Z），水平尺寸旋转 0°（量 U）；
        /// 两个被量点在 (AnchorPosition, From/To)，尺寸线摆在 LinePosition 处。
        /// 文字留空 → CAD 按实际距离自己量出数值（改了模型重算视图，数值也跟着对）。
        /// </summary>
        private static RotatedDimension CreateDimension(ViewDimension dimension, Point3d anchor, ObjectId dimensionStyle)
        {
            Point3d first, second, linePoint;
            double rotation;
            if (dimension.Vertical)
            {
                rotation = Math.PI / 2d;
                first = new Point3d(anchor.X + dimension.AnchorPosition, anchor.Y + dimension.From, 0d);
                second = new Point3d(anchor.X + dimension.AnchorPosition, anchor.Y + dimension.To, 0d);
                linePoint = new Point3d(anchor.X + dimension.LinePosition, anchor.Y + (dimension.From + dimension.To) / 2d, 0d);
            }
            else
            {
                rotation = 0d;
                first = new Point3d(anchor.X + dimension.From, anchor.Y + dimension.AnchorPosition, 0d);
                second = new Point3d(anchor.X + dimension.To, anchor.Y + dimension.AnchorPosition, 0d);
                linePoint = new Point3d(anchor.X + (dimension.From + dimension.To) / 2d, anchor.Y + dimension.LinePosition, 0d);
            }
            return new RotatedDimension(rotation, first, second, linePoint, dimension.Text ?? string.Empty, dimensionStyle);
        }

        private static void ApplyLayer(Entity entity, string layer)
        {
            if (string.IsNullOrWhiteSpace(layer)) return;
            try
            {
                entity.Layer = layer;
                entity.ColorIndex = 256;      // 随层
                entity.LineWeight = LineWeight.ByLayer;
            }
            catch { }
        }

        private static void ApplyLineType(Transaction transaction, Database database, Entity entity, string lineType)
        {
            if (string.IsNullOrWhiteSpace(lineType)) return;
            try
            {
                var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
                if (!table.Has(lineType))
                {
                    try { database.LoadLineTypeFile(lineType, "acad.lin"); } catch { }
                    table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
                }
                if (table.Has(lineType)) entity.LinetypeId = table[lineType];
            }
            catch { }
        }

        private static void Bump(Dictionary<string, int> counts, string layer)
        {
            var key = string.IsNullOrWhiteSpace(layer) ? "(随层)" : layer;
            int value;
            counts.TryGetValue(key, out value);
            counts[key] = value + 1;
        }

        private static string LastViewFolder()
        {
            try
            {
                var path = Path.Combine(UserDataPaths.SettingsDirectory, "building-model-last-folder.txt");
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path).Trim();
                    if (Directory.Exists(text)) return text;
                }
            }
            catch { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private static void SaveLastViewFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            try
            {
                Directory.CreateDirectory(UserDataPaths.SettingsDirectory);
                File.WriteAllText(Path.Combine(UserDataPaths.SettingsDirectory, "building-model-last-folder.txt"), folder);
            }
            catch { }
        }
    }
}
