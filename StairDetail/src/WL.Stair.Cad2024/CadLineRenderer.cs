using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using WL.Stair.Core.Geometry;

namespace WL.Stair.Cad2024
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
        private const string AxisLayer = "A_DOTE";
        private const string HiddenLineType = "HIDDEN";
        private const string AxisLineType = "DASHDOT2";

        public void Render(
            Database database,
            Transaction transaction,
            DrawingView view,
            Point3d insertionPoint)
        {
            if (database == null)
            {
                throw new ArgumentNullException(nameof(database));
            }

            if (transaction == null)
            {
                throw new ArgumentNullException(nameof(transaction));
            }

            EnsureLayers(database, transaction);
            var dimensionStyleId = EnsureDimensionStyle(database, transaction, view.Scale);

            var currentSpace = (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId,
                OpenMode.ForWrite);

            foreach (var drawingLine in view.Lines)
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
                    dimensionStyleId) { Layer = AnnotationDimensionLayer };
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
                    for (var column = 0; column < drawingTable.ColumnWidths.Count; column++)
                    {
                        table.Cells[row, column].TextString = column < drawingTable.Rows[row].Count
                            ? drawingTable.Rows[row][column]
                            : string.Empty;
                        table.Cells[row, column].TextHeight = 2.5 * view.Scale;
                        table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
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
            EnsureLayer(layerTable, transaction, CutHatchLayer, 1, ObjectId.Null);
            EnsureLayer(layerTable, transaction, AxisLayer, 1, axisLineTypeId);
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
            // Wall ranges are construction-only hatch loops; their horizontal
            // closure must not become a visible wall edge.
            boundary.Visible = !region.IsWall;
            currentSpace.AppendEntity(boundary);
            transaction.AddNewlyCreatedDBObject(boundary, true);

            if (!region.IsWall)
            {
                var offsetDistance = 0.2 * Math.Max(1, drawingScale);
                var offset = FindInnerOffset(boundary, offsetDistance);
                if (offset != null)
                {
                    offset.Layer = StructuralLayer;
                    offset.ConstantWidth = 0.4 * Math.Max(1, drawingScale);
                    currentSpace.AppendEntity(offset);
                    transaction.AddNewlyCreatedDBObject(offset, true);
                }
            }

            var hatch = new Hatch { Layer = CutHatchLayer, Associative = false };
            try
            {
                hatch.SetHatchPattern(HatchPatternType.PreDefined,
                    string.IsNullOrWhiteSpace(region.PatternName) ? "ANSI31" : region.PatternName);
            }
            catch
            {
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31");
            }
            hatch.PatternScale = Math.Max(0.001, region.PatternScale * Math.Max(1, drawingScale));
            currentSpace.AppendEntity(hatch);
            transaction.AddNewlyCreatedDBObject(hatch, true);
            hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { boundary.ObjectId });
            hatch.EvaluateHatch(true);
        }

        private static Polyline CreateClosedPolyline(IEnumerable<Point2D> points, Point3d insertionPoint)
        {
            var polyline = new Polyline();
            var index = 0;
            foreach (var point in points)
            {
                polyline.AddVertexAt(index++, new Point2d(
                    insertionPoint.X + point.X,
                    insertionPoint.Y + point.Y), 0.0, 0.0, 0.0);
            }
            polyline.Closed = true;
            return polyline;
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

                case StairLineRole.WallBoundary:
                    return AuxiliaryLayer;

                default:
                    return AuxiliaryLayer;
            }
        }
    }
}
