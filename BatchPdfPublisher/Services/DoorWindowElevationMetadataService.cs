using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;
using System.Text;

namespace BatchPdfPublisher.Services
{
    [DataContract]
    internal sealed class DoorWindowElevationMetadata
    {
        [DataMember]
        public string GroupId { get; set; }
        [DataMember]
        public string Code { get; set; }
        [DataMember]
        public double Width { get; set; }
        [DataMember]
        public double Height { get; set; }
        [DataMember]
        public string ElevationType { get; set; }
        [DataMember]
        public string DivisionPreset { get; set; }
        [DataMember]
        public string OpeningMode { get; set; }
        [DataMember]
        public double InstallationGap { get; set; }
        [DataMember(EmitDefaultValue = false)]
        public bool? HasInstallationGap { get; set; }
        [DataMember]
        public double OuterFrameWidth { get; set; }
        [DataMember(EmitDefaultValue = false)]
        public bool? HasOuterFrame { get; set; }
        [DataMember]
        public double MullionWidth { get; set; }
        [DataMember(EmitDefaultValue = false)]
        public bool? HasMullion { get; set; }
        [DataMember(EmitDefaultValue = false)]
        public string DoorFrameType { get; set; }
        [DataMember]
        public string CustomColumnRatios { get; set; }
        [DataMember]
        public string CustomRowRatios { get; set; }
        [DataMember]
        public string CustomColumnWidths { get; set; }
        [DataMember]
        public string CustomRowHeights { get; set; }
        [DataMember]
        public string CustomCellLayout { get; set; }
        [DataMember]
        public string CellOpeningModes { get; set; }
        [DataMember]
        public string DoorPlacement { get; set; }
        [DataMember]
        public double DoorEdgeDistance { get; set; }
        [DataMember]
        public int DrawingScale { get; set; }
        [DataMember]
        public double OriginX { get; set; }
        [DataMember]
        public double OriginY { get; set; }
        [DataMember]
        public double OriginZ { get; set; }

        public Point3d Origin { get { return new Point3d(OriginX, OriginY, OriginZ); } }

        public static DoorWindowElevationMetadata Create(DoorWindowScheduleItem item, Point3d origin, int scale, string groupId)
        {
            return new DoorWindowElevationMetadata
            {
                GroupId = string.IsNullOrWhiteSpace(groupId) ? Guid.NewGuid().ToString("N") : groupId,
                Code = item.Code,
                Width = item.Width,
                Height = item.Height,
                ElevationType = item.ElevationType,
                DivisionPreset = item.DivisionPreset,
                OpeningMode = item.OpeningMode,
                HasInstallationGap = item.HasInstallationGap,
                InstallationGap = item.InstallationGap,
                HasOuterFrame = item.HasOuterFrame,
                OuterFrameWidth = item.OuterFrameWidth,
                HasMullion = item.HasMullion,
                MullionWidth = item.MullionWidth,
                DoorFrameType = item.DoorFrameType,
                CustomColumnRatios = item.CustomColumnRatios,
                CustomRowRatios = item.CustomRowRatios,
                CustomColumnWidths = item.CustomColumnWidths,
                CustomRowHeights = item.CustomRowHeights,
                CustomCellLayout = item.CustomCellLayout,
                CellOpeningModes = item.CellOpeningModes,
                DoorPlacement = item.DoorPlacement,
                DoorEdgeDistance = item.DoorEdgeDistance,
                DrawingScale = scale,
                OriginX = origin.X,
                OriginY = origin.Y,
                OriginZ = origin.Z
            };
        }

        public DoorWindowScheduleItem ToItem()
        {
            return new DoorWindowScheduleItem
            {
                Selected = true,
                Code = Code,
                Width = Width,
                Height = Height,
                Quantity = 1,
                ElevationType = ElevationType,
                DivisionPreset = DivisionPreset,
                OpeningMode = OpeningMode,
                HasInstallationGap = HasInstallationGap ?? true,
                InstallationGap = InstallationGap,
                HasOuterFrame = HasOuterFrame ?? true,
                OuterFrameWidth = OuterFrameWidth,
                HasMullion = HasMullion ?? true,
                MullionWidth = MullionWidth,
                DoorFrameType = string.IsNullOrWhiteSpace(DoorFrameType) ? "N型" : DoorFrameType,
                CustomColumnRatios = CustomColumnRatios,
                CustomRowRatios = CustomRowRatios,
                CustomColumnWidths = CustomColumnWidths,
                CustomRowHeights = CustomRowHeights,
                CustomCellLayout = CustomCellLayout,
                CellOpeningModes = CellOpeningModes,
                DoorPlacement = DoorPlacement,
                DoorEdgeDistance = DoorEdgeDistance,
                DrawingScale = DrawingScale,
                Status = "参数完整，可生成"
            };
        }
    }

    internal static class DoorWindowElevationMetadataService
    {
        internal const string RegAppName = "WL_MCLM";

        public static void EnsureRegistered(Database database, Transaction transaction)
        {
            var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        public static void Attach(Entity entity, DoorWindowElevationMetadata metadata)
        {
            var payload = Serialize(metadata);
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "1"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, metadata.GroupId)
            };
            for (var index = 0; index < payload.Length; index += 240)
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, payload.Substring(index, Math.Min(240, payload.Length - index))));
            entity.XData = new ResultBuffer(values.ToArray());
        }

        public static bool TryRead(Entity entity, out DoorWindowElevationMetadata metadata)
        {
            metadata = null;
            try
            {
                using (var buffer = entity.GetXDataForApplication(RegAppName))
                {
                    if (buffer == null) return false;
                    var values = buffer.AsArray();
                    if (values.Length < 4) return false;
                    var payload = string.Concat(values.Skip(3).Select(x => Convert.ToString(x.Value)));
                    metadata = Deserialize(payload);
                    return metadata != null && !string.IsNullOrWhiteSpace(metadata.GroupId);
                }
            }
            catch { return false; }
        }

        public static DoorWindowElevationMetadata PromptForGeneratedElevation(Document document, out int entityCount)
        {
            entityCount = 0;
            var options = new PromptEntityOptions("\n请选择一个由万落工具生成的门窗立面对象：");
            var result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return null;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var selected = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Entity;
                DoorWindowElevationMetadata metadata;
                if (selected == null || !TryRead(selected, out metadata))
                    throw new InvalidOperationException("所选对象不是新版万落门窗立面。请先用当前版本生成一次立面。");
                var space = (BlockTableRecord)transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    DoorWindowElevationMetadata candidate;
                    if (entity != null && TryRead(entity, out candidate) && string.Equals(candidate.GroupId, metadata.GroupId, StringComparison.Ordinal)) entityCount++;
                }
                return metadata;
            }
        }

        public static List<ObjectId> FindGroup(BlockTableRecord space, Transaction transaction, string groupId)
        {
            var result = new List<ObjectId>();
            foreach (ObjectId id in space)
            {
                var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                DoorWindowElevationMetadata metadata;
                if (entity != null && TryRead(entity, out metadata) && string.Equals(metadata.GroupId, groupId, StringComparison.Ordinal)) result.Add(id);
            }
            return result;
        }

        private static string Serialize(DoorWindowElevationMetadata metadata)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(DoorWindowElevationMetadata)).WriteObject(stream, metadata);
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        private static DoorWindowElevationMetadata Deserialize(string payload)
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(payload)))
                return (DoorWindowElevationMetadata)new DataContractJsonSerializer(typeof(DoorWindowElevationMetadata)).ReadObject(stream);
        }
    }
}
