using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using WL.Stair.Core.Geometry;
using WL.Stair.CadShared;

namespace WL.Stair.Cad2022
{
    internal sealed class CadLineRenderer
    {
        private const string OutlineLayer = "WL_楼梯_轮廓";
        private const string TreadLayer = "WL_楼梯_踏步";
        private const string StructuralLayer = "WL_楼梯_剖面";
        private const string HiddenLayer = "WL-楼梯侧面";
        private const string AuxiliaryLayer = "WL_剖面墙";
        private const string HandrailLayer = "WL_扶手";
        private const string AnnotationTextLayer = "WL-注释-文字";
        private const string AnnotationDimensionLayer = "WL-注释-标注";
        private const string CutHatchLayer = "WL_楼梯_剖切填充";
        private const string BreakLineLayer = "WL_折断线";
        private const string AxisLayer = "A_DOTE";
        private const string HiddenLineType = "HIDDEN";
        private const string AxisLineType = "DASHDOT2";
        private static string _hatchPatternDirectory;

        public void Render(Database database, Transaction transaction, DrawingView view, Point3d insertionPoint)
        {
            if (database == null)
            {
                throw new ArgumentNullException(nameof(database));
            }

            if (transaction == null)
            {
                throw new ArgumentNullException(nameof(transaction));
            }

            EnsureHatchPatternAssets();
            EnsureLayers(database, transaction);
            var dimensionStyleId = EnsureDimensionStyle(database, transaction, view.Scale);

            var currentSpace = (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId,
                OpenMode.ForWrite);

            foreach (var drawingLine in view.Lines.Where(line =>
                line.Role != StairLineRole.BreakLine && line.Role != StairLineRole.HatchBoundary))
            {
                var line = new Line(
                    ToCadPoint(drawingLine.Start, insertionPoint),
                    ToCadPoint(drawingLine.End, insertionPoint))
                {
                    Layer = GetLayerName(drawingLine)
                };

                currentSpace.AppendEntity(line);
                transaction.AddNewlyCreatedDBObject(line, true);
            }

            foreach (var group in view.Lines.Where(line => line.Role == StairLineRole.BreakLine)
                .GroupBy(line => line.ComponentId ?? string.Empty))
                RenderConnectedPolyline(currentSpace, transaction, group, insertionPoint);

            foreach (var region in view.HatchRegions)
            {
                RenderSectionRegion(currentSpace, transaction, region, view.Scale, insertionPoint);
            }

            foreach (var drawingText in view.Texts)
            {
                var text = new DBText
                {
                    Position = ToCadPoint(drawingText.Position, insertionPoint),
                    Height = drawingText.Height,
                    TextString = drawingText.Content,
                    Layer = AnnotationTextLayer
                };
                text.HorizontalMode = TextHorizontalMode.TextCenter;
                text.AlignmentPoint = ToCadPoint(drawingText.Position, insertionPoint);

                currentSpace.AppendEntity(text);
                transaction.AddNewlyCreatedDBObject(text, true);
            }

            foreach (var drawingLeader in view.Leaders)
                RenderLeader(currentSpace, transaction, drawingLeader, insertionPoint);

            if (view.Title != null)
                StairTitleService.Insert(database, currentSpace, transaction,
                    ToCadPoint(view.Title.Position, insertionPoint), view.Title.Text,
                    view.Title.Scale, view.Title.TargetWidth, AnnotationTextLayer);

            WriteAssetTrace("render breakPolylines="
                + view.Lines.Where(line => line.Role == StairLineRole.BreakLine)
                    .Select(line => line.ComponentId ?? string.Empty).Distinct().Count()
                + " leaders=" + view.Leaders.Count
                + " title=" + (view.Title != null));

            foreach (var drawingDimension in view.Dimensions)
            {
                var dimension = new RotatedDimension(
                    drawingDimension.Orientation == DrawingDimensionOrientation.Horizontal
                        ? 0.0
                        : Math.PI / 2.0,
                    ToCadPoint(drawingDimension.FirstExtensionOrigin, insertionPoint),
                    ToCadPoint(drawingDimension.SecondExtensionOrigin, insertionPoint),
                    ToCadPoint(drawingDimension.DimensionLinePoint, insertionPoint),
                    drawingDimension.TextOverride,
                    dimensionStyleId)
                {
                    Layer = AnnotationDimensionLayer
                };
                currentSpace.AppendEntity(dimension);
                transaction.AddNewlyCreatedDBObject(dimension, true);
            }

            foreach (var drawingTable in view.Tables)
            {
                var table = new Table
                {
                    Position = ToCadPoint(drawingTable.Position, insertionPoint),
                    Layer = AnnotationTextLayer
                };
                table.SetSize(drawingTable.Rows.Count, drawingTable.ColumnWidths.Count);
                table.SetRowHeight(drawingTable.RowHeight);
                table.SetColumnWidth(drawingTable.ColumnWidths.Average());
                for (var column = 0; column < drawingTable.ColumnWidths.Count; column++)
                    table.Columns[column].Width = drawingTable.ColumnWidths[column];
                for (var row = 0; row < drawingTable.Rows.Count; row++)
                {
                    for (var column = 0; column < drawingTable.ColumnWidths.Count; column++)
                    {
                        table.Cells[row, column].TextString = column < drawingTable.Rows[row].Count
                            ? drawingTable.Rows[row][column]
                            : string.Empty;
                        table.Cells[row, column].TextHeight = 2.5 * view.Scale;
                        table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                    }
                }
                table.GenerateLayout();
                currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
            }
        }

        private static void EnsureLayers(Database database, Transaction transaction)
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            var hiddenLineTypeId = EnsureLineType(database, transaction, HiddenLineType);
            var axisLineTypeId = EnsureLineType(database, transaction, AxisLineType);
            EnsureLayer(layerTable, transaction, OutlineLayer, 7, ObjectId.Null);
            EnsureLayer(layerTable, transaction, TreadLayer, 2, ObjectId.Null);
            EnsureLayer(layerTable, transaction, StructuralLayer, 2, ObjectId.Null);
            EnsureLayer(layerTable, transaction, HiddenLayer, 4, hiddenLineTypeId);
            EnsureLayer(layerTable, transaction, AuxiliaryLayer, 7, ObjectId.Null);
            EnsureLayer(layerTable, transaction, HandrailLayer, 4, ObjectId.Null);
            EnsureLayer(layerTable, transaction, AnnotationTextLayer, 2, ObjectId.Null);
            EnsureLayer(layerTable, transaction, AnnotationDimensionLayer, 3, ObjectId.Null);
            EnsureLayer(layerTable, transaction, CutHatchLayer, 8, ObjectId.Null);
            EnsureLayer(layerTable, transaction, BreakLineLayer, 3, ObjectId.Null);
            MigrateHatchLayerColor(layerTable, transaction);
            EnsureLayer(layerTable, transaction, AxisLayer, 1, axisLineTypeId);
        }

        private static void RenderConnectedPolyline(BlockTableRecord space, Transaction transaction,
            IEnumerable<DrawingLine> source, Point3d insertionPoint)
        {
            var lines = source.ToArray();
            if (lines.Length == 0) return;
            var polyline = new Polyline(lines.Length + 1) { Layer = BreakLineLayer };
            var points = new List<Point2D> { lines[0].Start };
            points.AddRange(lines.Select(line => line.End));
            for (var index = 0; index < points.Count; index++)
                polyline.AddVertexAt(index, new Point2d(points[index].X + insertionPoint.X,
                    points[index].Y + insertionPoint.Y), 0.0, 0.0, 0.0);
            space.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
        }

        private static void RenderLeader(BlockTableRecord space, Transaction transaction,
            DrawingLeader source, Point3d insertionPoint)
        {
            var textPoint = ToCadPoint(source.Vertices[source.Vertices.Count - 1], insertionPoint);
            // Use the same proven Leader + independent MText construction as
            // the door/window bay-fold annotation.
            var leader = new Leader { Layer = AnnotationTextLayer, HasArrowHead = true };
            foreach (var vertex in source.Vertices)
                leader.AppendVertex(ToCadPoint(vertex, insertionPoint));
            space.AppendEntity(leader);
            transaction.AddNewlyCreatedDBObject(leader, true);
            var text = new MText
            {
                Contents = source.Text,
                Location = textPoint,
                Attachment = AttachmentPoint.MiddleRight,
                TextHeight = source.TextHeight,
                TextStyleId = FindTextStyle(space.Database, transaction, "WL-文字-标注"),
                Layer = AnnotationTextLayer
            };
            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
        }

        private static ObjectId FindTextStyle(Database database, Transaction transaction, string name)
        {
            var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            return table.Has(name) ? table[name] : database.Textstyle;
        }

        private static void RenderSectionRegion(
            BlockTableRecord currentSpace,
            Transaction transaction,
            DrawingHatchRegion region,
            int drawingScale,
            Point3d insertionPoint)
        {
            var boundary = CreateClosedPolyline(region.Boundary, insertionPoint);
            boundary.Layer = region.IsWall ? AuxiliaryLayer : StructuralLayer;
            // This closed polyline only supplies the hatch loop. Visible
            // outlines are emitted separately, so the base wall stays as two
            // open horizontal lines without vertical end caps.
            boundary.Visible = false;
            currentSpace.AppendEntity(boundary);
            transaction.AddNewlyCreatedDBObject(boundary, true);

            if (!region.IsWall && !string.Equals(region.ComponentId, "BASE-WALL",
                StringComparison.OrdinalIgnoreCase))
            {
                var offset = FindInnerOffset(boundary, 0.2 * Math.Max(1, drawingScale));
                if (offset == null)
                {
                    // AutoCAD can reject an offset for a self-touching stair
                    // outline even though the hatch boundary is valid. Keep
                    // the original closed boundary as a visible bold outline
                    // rather than silently dropping the bolding feature.
                    offset = (Polyline)boundary.Clone();
                }
                if (offset != null)
                {
                    offset.Visible = true;
                    offset.Layer = StructuralLayer;
                    offset.ConstantWidth = 0.4 * Math.Max(1, drawingScale);
                    currentSpace.AppendEntity(offset);
                    transaction.AddNewlyCreatedDBObject(offset, true);
                }
            }
            else if (region.IsWall)
            {
                RenderWallFacesWide(currentSpace, transaction, region, drawingScale, insertionPoint);
            }

            var hatch = new Hatch { Layer = CutHatchLayer, Associative = false };
            hatch.SetDatabaseDefaults(currentSpace.Database);
            currentSpace.AppendEntity(hatch);
            transaction.AddNewlyCreatedDBObject(hatch, true);
            hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { boundary.ObjectId });
            var requestedPatternName = string.IsNullOrWhiteSpace(region.PatternName) ? "ANSI31" : region.PatternName;
            var appliedPatternName = requestedPatternName;
            var appliedPatternType = ResolveHatchPatternType(appliedPatternName);
            var appliedPatternScale = Math.Max(0.001, region.PatternScale);
            // Custom patterns are otherwise parsed once at AutoCAD's default
            // scale 1. On a full stair region that can exceed the hatch-line
            // limit before the requested (usually 10/50) scale is applied.
            try { hatch.PatternScale = appliedPatternScale; } catch { }
            try
            {
                SetHatchPattern(hatch, appliedPatternType, appliedPatternName);
            }
            catch (System.Exception exception)
            {
                WriteAssetTrace("pattern fallback requested=" + requestedPatternName
                    + " type=" + appliedPatternType
                    + " directory=" + (_hatchPatternDirectory ?? string.Empty)
                    + " exists=" + File.Exists(Path.Combine(_hatchPatternDirectory ?? string.Empty,
                        requestedPatternName + ".pat"))
                    + " error=" + exception);
                appliedPatternName = "ANSI31";
                appliedPatternType = HatchPatternType.PreDefined;
                SetHatchPattern(hatch, appliedPatternType, appliedPatternName);
            }
            hatch.PatternScale = appliedPatternScale;
            // Autodesk requires SetHatchPattern again after changing PatternScale.
            // It rebuilds the internal pattern definition using the final scale;
            // changing only PatternScale leaves the property value correct while
            // the initially generated hatch lines can still use the old density.
            SetHatchPattern(hatch, appliedPatternType, appliedPatternName);
            hatch.EvaluateHatch(true);
            hatch.RecordGraphicsModified(true);
            WriteHatchTrace(region, drawingScale, requestedPatternName, appliedPatternName,
                appliedPatternScale, appliedPatternType, hatch);
            WriteAssetTrace("bold boundary kind=" + (region.IsWall ? "WALL" : "STRUCTURE")
                + " offset=" + (!region.IsWall) + " pattern=" + appliedPatternName);
        }

        private static void EnsureHatchPatternAssets()
        {
            try
            {
                var packageRoot = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                var userRoot = !string.IsNullOrWhiteSpace(packageRoot) && Directory.Exists(packageRoot)
                    ? Path.Combine(packageRoot, "用户配置文件")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "WanluoArchitectureTools", "用户配置文件");
                var target = Path.Combine(userRoot, "填充素材");
                Directory.CreateDirectory(target);
                var source = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "HatchPatterns");
                if (Directory.Exists(source))
                    foreach (var file in Directory.GetFiles(source, "*.pat"))
                        File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
                AddCadSupportPath(target);
                _hatchPatternDirectory = target;
                WriteAssetTrace("assets ready directory=" + target
                    + " acad=" + (Environment.GetEnvironmentVariable("ACAD") ?? string.Empty)
                    + " files=" + string.Join(",", Directory.GetFiles(target, "*.pat")
                        .Select(Path.GetFileName)));
            }
            catch (System.Exception exception)
            {
                WriteAssetTrace("deploy failed: " + exception);
            }
        }

        private static void AddCadSupportPath(string directory)
        {
            var supportPath = Environment.GetEnvironmentVariable("ACAD") ?? string.Empty;
            if (!supportPath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(path => string.Equals(path.Trim(), directory, StringComparison.OrdinalIgnoreCase)))
            {
                var updated = string.IsNullOrWhiteSpace(supportPath) ? directory : supportPath.TrimEnd(';') + ";" + directory;
                Environment.SetEnvironmentVariable("ACAD", updated, EnvironmentVariableTarget.Process);
            }
            AddActiveCadSupportPath(directory);
        }

        private static void AddActiveCadSupportPath(string directory)
        {
            try
            {
                var application = Autodesk.AutoCAD.ApplicationServices.Application.AcadApplication;
                var preferences = application.GetType().InvokeMember("Preferences",
                    System.Reflection.BindingFlags.GetProperty, null, application, null,
                    CultureInfo.CurrentCulture);
                var files = preferences.GetType().InvokeMember("Files",
                    System.Reflection.BindingFlags.GetProperty, null, preferences, null,
                    CultureInfo.CurrentCulture);
                var current = Convert.ToString(files.GetType().InvokeMember("SupportPath",
                    System.Reflection.BindingFlags.GetProperty, null, files, null,
                    CultureInfo.CurrentCulture), CultureInfo.CurrentCulture) ?? string.Empty;
                if (current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(path => string.Equals(path.Trim(), directory, StringComparison.OrdinalIgnoreCase))) return;
                files.GetType().InvokeMember("SupportPath", System.Reflection.BindingFlags.SetProperty,
                    null, files, new object[] { string.IsNullOrWhiteSpace(current)
                        ? directory : current.TrimEnd(';') + ";" + directory }, CultureInfo.CurrentCulture);
            }
            catch (System.Exception exception)
            {
                WriteAssetTrace("active support path failed directory=" + directory + " error=" + exception);
            }
        }

        private static HatchPatternType ResolveHatchPatternType(string patternName)
        {
            return !string.IsNullOrWhiteSpace(_hatchPatternDirectory)
                && File.Exists(Path.Combine(_hatchPatternDirectory, patternName + ".pat"))
                ? HatchPatternType.CustomDefined
                : HatchPatternType.PreDefined;
        }

        private static readonly object HatchPatternDirectoryLock = new object();

        private static void SetHatchPattern(Hatch hatch, HatchPatternType patternType, string patternName)
        {
            if (patternType != HatchPatternType.CustomDefined || string.IsNullOrWhiteSpace(_hatchPatternDirectory))
            {
                hatch.SetHatchPattern(patternType, patternName);
                return;
            }

            // AutoCAD caches its support paths at process startup. Changing ACAD after the
            // plugin is loaded does not refresh the custom PAT lookup in AutoCAD 2022.
            // Custom PAT lookup does include the current directory, so use the portable
            // user-config asset folder only for the duration of this API call.
            lock (HatchPatternDirectoryLock)
            {
                var originalDirectory = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = _hatchPatternDirectory;
                    hatch.SetHatchPattern(patternType, patternName);
                }
                finally
                {
                    Environment.CurrentDirectory = originalDirectory;
                }
            }
        }

        private static void WriteAssetTrace(string message)
        {
            try
            {
                var root = !string.IsNullOrWhiteSpace(_hatchPatternDirectory)
                    ? Directory.GetParent(_hatchPatternDirectory).FullName
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "WanluoArchitectureTools", "用户配置文件");
                var logs = Path.Combine(root, "Logs");
                Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, "stair-hatch.log"),
                    DateTime.Now.ToString("O") + " CAD2022 " + message + Environment.NewLine);
            }
            catch { }
        }

        private static void WriteHatchTrace(
            DrawingHatchRegion region,
            int drawingScale,
            string requestedPatternName,
            string appliedPatternName,
            double appliedPatternScale,
            HatchPatternType appliedPatternType,
            Hatch hatch)
        {
            try
            {
                var packageRoot = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                var root = !string.IsNullOrWhiteSpace(packageRoot) && Directory.Exists(packageRoot)
                    ? Path.Combine(packageRoot, "用户配置文件")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "WanluoArchitectureTools", "用户配置文件");
                var logDirectory = Path.Combine(root, "Logs");
                Directory.CreateDirectory(logDirectory);
                var line = string.Format(CultureInfo.InvariantCulture,
                    "{0:O} CAD2022 kind={1} requestedPattern={2} appliedPattern={3} patternType={4} requestedScale={5:R} appliedScale={6:R} hatchScale={7:R} solid={8} loops={9} boundaryPoints={10} drawingScale={11}\r\n",
                    DateTime.Now,
                    region.IsWall ? "WALL" : "STRUCTURE",
                    requestedPatternName,
                    appliedPatternName,
                    appliedPatternType,
                    region.PatternScale,
                    appliedPatternScale,
                    hatch.PatternScale,
                    hatch.IsSolidFill,
                    hatch.NumberOfLoops,
                    region.Boundary.Count,
                    drawingScale);
                File.AppendAllText(Path.Combine(logDirectory, "stair-hatch.log"), line);
            }
            catch
            {
                // Diagnostics must never interrupt drawing generation.
            }
        }

        private static void MigrateHatchLayerColor(LayerTable layerTable, Transaction transaction)
        {
            if (!layerTable.Has(CutHatchLayer)) return;
            var layer = (LayerTableRecord)transaction.GetObject(layerTable[CutHatchLayer], OpenMode.ForWrite);
            if (layer.Color.ColorMethod == ColorMethod.ByAci && layer.Color.ColorIndex == 1)
                layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 8);
        }

        private static Polyline CreateClosedPolyline(IEnumerable<Point2D> points, Point3d insertionPoint)
        {
            var polyline = new Polyline();
            var index = 0;
            foreach (var point in points)
                polyline.AddVertexAt(index++, new Point2d(insertionPoint.X + point.X, insertionPoint.Y + point.Y), 0.0, 0.0, 0.0);
            polyline.Closed = true;
            return polyline;
        }

        private static void RenderWallFacesWide(
            BlockTableRecord currentSpace, Transaction transaction, DrawingHatchRegion region,
            int drawingScale, Point3d insertionPoint)
        {
            if (region.Boundary.Count != 4) return;
            var offset = 0.2 * Math.Max(1, drawingScale);
            var width = 0.4 * Math.Max(1, drawingScale);
            foreach (var x in new[] { region.Boundary[0].X + offset, region.Boundary[1].X - offset })
            {
                var face = new Polyline();
                face.AddVertexAt(0, new Point2d(insertionPoint.X + x, insertionPoint.Y + region.Boundary[0].Y), 0, 0, 0);
                face.AddVertexAt(1, new Point2d(insertionPoint.X + x, insertionPoint.Y + region.Boundary[2].Y), 0, 0, 0);
                face.ConstantWidth = width;
                face.Layer = AuxiliaryLayer;
                currentSpace.AppendEntity(face);
                transaction.AddNewlyCreatedDBObject(face, true);
            }
        }

        private static Polyline FindInnerOffset(Polyline boundary, double distance)
        {
            var sourceArea = Math.Abs(boundary.Area);
            Polyline best = null;
            foreach (var signedDistance in new[] { distance, -distance })
            {
                DBObjectCollection curves;
                try { curves = boundary.GetOffsetCurves(signedDistance); }
                catch { continue; }
                foreach (DBObject curve in curves)
                {
                    var candidate = curve as Polyline;
                    if (candidate == null || !candidate.Closed || Math.Abs(candidate.Area) >= sourceArea)
                    {
                        curve.Dispose();
                        continue;
                    }
                    if (best == null || Math.Abs(candidate.Area) < Math.Abs(best.Area))
                    {
                        if (best != null) best.Dispose();
                        best = candidate;
                    }
                    else candidate.Dispose();
                }
            }
            return best;
        }

        private static ObjectId EnsureDimensionStyle(Database database, Transaction transaction, int scale)
        {
            var sharedStyle = TryEnsureSharedDimensionStyle(database, transaction, scale);
            if (!sharedStyle.IsNull) return sharedStyle;
            var name = "WL-标注-1_" + Math.Max(1, scale);
            var table = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var textStyles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            var annotationTextStyle = textStyles.Has("WL-文字-标注")
                ? textStyles["WL-文字-标注"]
                : database.Textstyle;
            var record = new DimStyleTableRecord
            {
                Name = name,
                Dimscale = Math.Max(1, scale),
                Dimtxt = 2.5,
                Dimasz = 2.5,
                Dimtsz = 0.0,
                Dimblk = EnsureFallbackArrowBlock(database, transaction),
                Dimexe = 1.25,
                Dimexo = 0.625,
                Dimdli = 3.75,
                Dimgap = 0.625,
                Dimdec = 0,
                Dimclrd = Color.FromColorIndex(ColorMethod.ByAci, 0),
                Dimclre = Color.FromColorIndex(ColorMethod.ByAci, 0),
                Dimclrt = Color.FromColorIndex(ColorMethod.ByAci, 2),
                Dimtad = 1,
                Dimjust = 0,
                Dimtih = false,
                Dimtoh = false,
                Dimtmove = 2,
                Dimtxsty = annotationTextStyle
            };
            var id = table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return id;
        }

        private static ObjectId TryEnsureSharedDimensionStyle(
            Database database,
            Transaction transaction,
            int scale)
        {
            try
            {
                var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(
                        "BatchPdfPublisher.Services.DraftingStandardService",
                        false))
                    .FirstOrDefault(type => type != null);
                var method = serviceType == null
                    ? null
                    : serviceType.GetMethod(
                        "EnsureDimensionStyleForScale",
                        new[] { typeof(Database), typeof(Transaction), typeof(int) });
                if (method == null) return ObjectId.Null;
                var result = method.Invoke(
                    null,
                    new object[] { database, transaction, Math.Max(1, scale) });
                return result is ObjectId ? (ObjectId)result : ObjectId.Null;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static ObjectId EnsureFallbackArrowBlock(Database database, Transaction transaction)
        {
            const string blockName = "WS-cj";
            var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (table.Has(blockName)) return table[blockName];
            table.UpgradeOpen();
            var block = new BlockTableRecord { Name = blockName };
            var id = table.Add(block);
            transaction.AddNewlyCreatedDBObject(block, true);
            var tick = new Line(new Point3d(-0.5, -0.5, 0.0), new Point3d(0.5, 0.5, 0.0));
            block.AppendEntity(tick);
            transaction.AddNewlyCreatedDBObject(tick, true);
            return id;
        }

        private static ObjectId EnsureLineType(Database database, Transaction transaction, string lineTypeName)
        {
            var lineTypeTable = (LinetypeTable)transaction.GetObject(
                database.LinetypeTableId,
                OpenMode.ForRead);

            if (!lineTypeTable.Has(lineTypeName))
            {
                try
                {
                    database.LoadLineTypeFile(lineTypeName, "acadiso.lin");
                }
                catch (System.Exception)
                {
                    return ObjectId.Null;
                }
            }

            lineTypeTable = (LinetypeTable)transaction.GetObject(
                database.LinetypeTableId,
                OpenMode.ForRead);
            return lineTypeTable.Has(lineTypeName)
                ? lineTypeTable[lineTypeName]
                : ObjectId.Null;
        }

        private static void EnsureLayer(
            LayerTable layerTable,
            Transaction transaction,
            string layerName,
            short colorIndex,
            ObjectId lineTypeId)
        {
            if (layerTable.Has(layerName))
            {
                return;
            }

            layerTable.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = layerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex)
            };
            if (!lineTypeId.IsNull)
            {
                record.LinetypeObjectId = lineTypeId;
            }
            layerTable.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static Point3d ToCadPoint(Point2D point, Point3d insertionPoint)
        {
            return new Point3d(
                insertionPoint.X + point.X,
                insertionPoint.Y + point.Y,
                insertionPoint.Z);
        }

        private static string GetLayerName(DrawingLine line)
        {
            if (line.IsHidden)
            {
                return HiddenLayer;
            }

            switch (line.Role)
            {
                case StairLineRole.Outline:
                case StairLineRole.SectionProfile:
                case StairLineRole.Landing:
                    return OutlineLayer;
                case StairLineRole.Tread:
                    return TreadLayer;
                case StairLineRole.StructuralEdge:
                case StairLineRole.CutBoundary:
                case StairLineRole.CutFlightProfile:
                case StairLineRole.BeamBoundary:
                    return StructuralLayer;
                case StairLineRole.AxisLine:
                    return AxisLayer;
                case StairLineRole.Handrail:
                    return HandrailLayer;
                case StairLineRole.BreakLine:
                    return BreakLineLayer;
                case StairLineRole.WallBoundary:
                    return AuxiliaryLayer;
                default:
                    return AuxiliaryLayer;
            }
        }
    }
}
