using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;

namespace BatchPdfPublisher.Services
{
    public static class DrawingScaleService
    {
        private const string RegAppName = "WL_DRAWING_SCALE";

        public static int ReadScale(Entity entity)
        {
            if (entity == null) return 1;
            try
            {
                using (var data = entity.GetXDataForApplication(RegAppName))
                {
                    if (data == null) return 1;
                    foreach (var value in data.AsArray())
                    {
                        if (value.TypeCode == (int)DxfCode.ExtendedDataInteger32 || value.TypeCode == (int)DxfCode.ExtendedDataInteger16)
                        {
                            var scale = Convert.ToInt32(value.Value);
                            if (scale > 0) return scale;
                        }
                    }
                }
            }
            catch { }
            return 1;
        }

        public static void ApplyScale(Database database, Transaction transaction, Entity entity, int targetScale, Point3d basePoint)
        {
            if (database == null || transaction == null || entity == null) return;
            if (targetScale <= 0) throw new ArgumentOutOfRangeException("targetScale");
            EnsureRegApp(database, transaction);
            var currentScale = ReadScale(entity);
            var factor = targetScale / (double)Math.Max(1, currentScale);
            if (Math.Abs(factor - 1d) > 0.0000001d) entity.TransformBy(Matrix3d.Scaling(factor, basePoint));
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, targetScale));
        }

        public static bool ApplyStandardizedScale(Database database, Transaction transaction, Entity entity, int targetScale, DraftingStandardResources resources, ObjectId dimensionStyle, AutoLayerSettings autoLayers)
        {
            if (entity == null) return false;
            var dimension = entity as Dimension;
            if (dimension != null)
            {
                dimension.DimensionStyle = dimensionStyle;
                if (autoLayers != null && autoLayers.Enabled) dimension.LayerId = EnsureLayer(database, transaction, autoLayers.DimensionLayer);
                WriteScale(database, transaction, entity, targetScale);
                return true;
            }

            var isText = false;
            Point3d anchor = Point3d.Origin;
            var dbText = entity as DBText; if (dbText != null) { dbText.TextStyleId = resources.BodyTextStyleId; anchor = TextAnchor(dbText); isText = true; }
            var mText = entity as MText; if (mText != null) { mText.TextStyleId = resources.BodyTextStyleId; anchor = mText.Location; isText = true; }
            var attribute = entity as AttributeReference; if (attribute != null) { attribute.TextStyleId = resources.AnnotationTextStyleId; anchor = TextAnchor(attribute); isText = true; }
            var definition = entity as AttributeDefinition; if (definition != null) { definition.TextStyleId = resources.AnnotationTextStyleId; anchor = TextAnchor(definition); isText = true; }
            var block = entity as BlockReference;
            if (block != null)
            {
                foreach (ObjectId attributeId in block.AttributeCollection)
                {
                    var blockAttribute = transaction.GetObject(attributeId, OpenMode.ForWrite, false) as AttributeReference;
                    if (blockAttribute == null) continue;
                    blockAttribute.TextStyleId = resources.AnnotationTextStyleId; if (autoLayers != null && autoLayers.Enabled) blockAttribute.LayerId = EnsureLayer(database, transaction, autoLayers.AttributeLayer);
                    var oldScale = ReadScale(blockAttribute); var textFactor = targetScale / (double)Math.Max(1, oldScale);
                    if (Math.Abs(textFactor - 1d) > 0.0000001d) blockAttribute.TransformBy(Matrix3d.Scaling(textFactor, TextAnchor(blockAttribute)));
                    WriteScale(database, transaction, blockAttribute, targetScale);
                    isText = true;
                }
                return isText;
            }
            if (isText && autoLayers != null && autoLayers.Enabled) entity.LayerId = EnsureLayer(database, transaction, attribute != null || definition != null ? autoLayers.AttributeLayer : autoLayers.TextLayer);

            if (isText)
            {
                var currentScale = ReadScale(entity);
                var factor = targetScale / (double)Math.Max(1, currentScale);
                if (Math.Abs(factor - 1d) > 0.0000001d) entity.TransformBy(Matrix3d.Scaling(factor, anchor));
            }
            if (isText) WriteScale(database, transaction, entity, targetScale);
            return isText;
        }

        private static void WriteScale(Database database, Transaction transaction, Entity entity, int targetScale)
        {
            EnsureRegApp(database, transaction);
            entity.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName), new TypedValue((int)DxfCode.ExtendedDataInteger32, targetScale));
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];
            SymbolUtilityServices.ValidateSymbolName(name, false); table.UpgradeOpen();
            var record = new LayerTableRecord { Name = name, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, 7), LineWeight = LineWeight.ByLineWeightDefault };
            var id = table.Add(record); transaction.AddNewlyCreatedDBObject(record, true); return id;
        }

        private static Point3d TextAnchor(DBText text)
        {
            return text.HorizontalMode == TextHorizontalMode.TextLeft && text.VerticalMode == TextVerticalMode.TextBase
                ? text.Position
                : text.AlignmentPoint;
        }

        public static Point3d Center(Entity entity)
        {
            try
            {
                var extents = entity.GeometricExtents;
                return new Point3d((extents.MinPoint.X + extents.MaxPoint.X) / 2d, (extents.MinPoint.Y + extents.MaxPoint.Y) / 2d, (extents.MinPoint.Z + extents.MaxPoint.Z) / 2d);
            }
            catch { return Point3d.Origin; }
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
    }
}
