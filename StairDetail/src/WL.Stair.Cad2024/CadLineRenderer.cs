using System;
using System.Collections.Generic;
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
        private const string StructuralLayer = "WL_楼梯_结构";
        private const string HiddenLayer = "WL_楼梯_隐藏";
        private const string AuxiliaryLayer = "WL_楼梯_辅助";
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

            foreach (var drawingText in view.Texts)
            {
                var text = new DBText
                {
                    Position = ToCadPoint(drawingText.Position, insertionPoint),
                    Height = drawingText.Height,
                    TextString = drawingText.Content,
                    Layer = AuxiliaryLayer
                };
                text.HorizontalMode = TextHorizontalMode.TextCenter;
                text.AlignmentPoint = ToCadPoint(drawingText.Position, insertionPoint);

                currentSpace.AppendEntity(text);
                transaction.AddNewlyCreatedDBObject(text, true);
            }
        }

        private static void EnsureLayers(Database database, Transaction transaction)
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            var hiddenLineTypeId = EnsureLineType(database, transaction, HiddenLineType);
            var axisLineTypeId = EnsureLineType(database, transaction, AxisLineType);
            EnsureLayer(layerTable, transaction, OutlineLayer, 7, ObjectId.Null);
            EnsureLayer(layerTable, transaction, TreadLayer, 2, ObjectId.Null);
            EnsureLayer(layerTable, transaction, StructuralLayer, 1, ObjectId.Null);
            EnsureLayer(layerTable, transaction, HiddenLayer, 8, hiddenLineTypeId);
            EnsureLayer(layerTable, transaction, AuxiliaryLayer, 3, ObjectId.Null);
            EnsureLayer(layerTable, transaction, CutHatchLayer, 1, ObjectId.Null);
            EnsureLayer(layerTable, transaction, AxisLayer, 1, axisLineTypeId);
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

                default:
                    return AuxiliaryLayer;
            }
        }
    }
}
