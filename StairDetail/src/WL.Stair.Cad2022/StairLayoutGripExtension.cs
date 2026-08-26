using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WL.Stair.Cad2022
{
    /// <summary>
    /// Installs the midpoint grip used by the native whole-stair layout grid.
    /// Grid entities remain ordinary CAD lines; the overrule only constrains an
    /// internal divider and moves the native entities in its neighbouring cells.
    /// </summary>
    public sealed class StairLayoutGripExtension : IExtensionApplication
    {
        private static StairLayoutGripOverrule overrule;

        public void Initialize()
        {
            if (overrule != null) return;
            overrule = new StairLayoutGripOverrule();
            Overrule.AddOverrule(RXClass.GetClass(typeof(Line)), overrule, false);
            overrule.SetXDataFilter(StairLayoutGridService.RegAppName);
            Overrule.Overruling = true;
        }

        public void Terminate()
        {
            if (overrule == null) return;
            try { Overrule.RemoveOverrule(RXClass.GetClass(typeof(Line)), overrule); }
            catch { }
            overrule = null;
        }
    }

    internal sealed class StairLayoutGripOverrule : GripOverrule
    {
        private static readonly object QueueSync = new object();
        private static readonly Queue<GridMoveRequest> Pending = new Queue<GridMoveRequest>();
        private static bool idleAttached;

        public override void GetGripPoints(Entity entity, GripDataCollection grips,
            double currentViewUnitSize, int gripSize, Vector3d currentViewDir,
            GetGripPointsFlags bitFlags)
        {
            var line = entity as Line;
            GridLineData data;
            if (line == null || !GridLineData.TryRead(line, out data))
            {
                base.GetGripPoints(entity, grips, currentViewUnitSize, gripSize,
                    currentViewDir, bitFlags);
                return;
            }
            // Exterior and per-cell edge segments stay selectable, but cannot be
            // distorted separately.  Only a genuine internal divider has a grip.
            if (!data.IsDivider) return;
            grips.Add(new StairDividerGripData { GripPoint = MidPoint(line) });
        }

        public override void MoveGripPointsAt(Entity entity, GripDataCollection grips,
            Vector3d offset, MoveGripPointsFlags bitFlags)
        {
            var line = entity as Line;
            GridLineData data;
            if (line == null || !GridLineData.TryRead(line, out data) || !data.IsDivider)
                return;
            var midpoint = MidPoint(line);
            var desired = data.IsVertical ? midpoint.X + offset.X : midpoint.Y + offset.Y;
            desired = Math.Max(data.Minimum, Math.Min(data.Maximum, desired));
            var actual = desired - (data.IsVertical ? midpoint.X : midpoint.Y);
            if (Math.Abs(actual) < 1e-7) return;
            line.TransformBy(Matrix3d.Displacement(data.IsVertical
                ? new Vector3d(actual, 0.0, 0.0)
                : new Vector3d(0.0, actual, 0.0)));
        }

        public override void OnGripStatusChanged(Entity entity, GripStatus status)
        {
            var line = entity as Line;
            GridLineData data;
            if (status == GripStatus.GripsDone && line != null
                && !line.ObjectId.IsNull && GridLineData.TryRead(line, out data)
                && data.IsDivider)
            {
                var midpoint = MidPoint(line);
                var coordinate = data.IsVertical ? midpoint.X : midpoint.Y;
                var delta = coordinate - data.BaseCoordinate;
                if (Math.Abs(delta) > 1e-7)
                    Enqueue(new GridMoveRequest(line.Database, line.ObjectId, data, delta));
            }
            base.OnGripStatusChanged(entity, status);
        }

        private static Point3d MidPoint(Line line)
        {
            return new Point3d((line.StartPoint.X + line.EndPoint.X) / 2.0,
                (line.StartPoint.Y + line.EndPoint.Y) / 2.0,
                (line.StartPoint.Z + line.EndPoint.Z) / 2.0);
        }

        private static void Enqueue(GridMoveRequest request)
        {
            lock (QueueSync)
            {
                Pending.Enqueue(request);
                if (idleAttached) return;
                idleAttached = true;
                Application.Idle += OnIdle;
            }
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            GridMoveRequest request;
            lock (QueueSync)
            {
                if (Pending.Count == 0)
                {
                    Application.Idle -= OnIdle;
                    idleAttached = false;
                    return;
                }
                request = Pending.Dequeue();
                if (Pending.Count == 0)
                {
                    Application.Idle -= OnIdle;
                    idleAttached = false;
                }
            }
            Apply(request);
        }

        private static void Apply(GridMoveRequest request)
        {
            Document document = null;
            foreach (Document candidate in Application.DocumentManager)
                if (ReferenceEquals(candidate.Database, request.Database))
                {
                    document = candidate;
                    break;
                }
            if (document == null) return;
            try
            {
                using (document.LockDocument())
                using (var transaction = request.Database.TransactionManager.StartTransaction())
                {
                    var space = (BlockTableRecord)transaction.GetObject(
                        request.Database.CurrentSpaceId, OpenMode.ForRead);
                    foreach (ObjectId id in space)
                    {
                        var line = transaction.GetObject(id, OpenMode.ForRead, false) as Line;
                        GridLineData data;
                        if (line == null || !GridLineData.TryRead(line, out data)
                            || data.LayoutId != request.Data.LayoutId
                            || data.Page != request.Data.Page)
                            continue;
                        var changed = false;
                        line.UpgradeOpen();
                        if (data.Kind == request.Data.Kind
                            && data.BoundaryIndex == request.Data.BoundaryIndex)
                        {
                            // AutoCAD has already moved the selected segment while
                            // dragging.  All other segments of this divider follow it.
                            if (id != request.ObjectId)
                                line.TransformBy(Matrix3d.Displacement(request.Data.IsVertical
                                    ? new Vector3d(request.Delta, 0.0, 0.0)
                                    : new Vector3d(0.0, request.Delta, 0.0)));
                            data.BaseCoordinate += request.Delta;
                            data.Write(line);
                            changed = true;
                        }
                        else if (request.Data.IsVertical && data.IsHorizontal)
                        {
                            if (data.SegmentIndex == request.Data.BoundaryIndex)
                            {
                                line.EndPoint = line.EndPoint.Add(new Vector3d(request.Delta, 0, 0));
                                changed = true;
                            }
                            else if (data.SegmentIndex == request.Data.BoundaryIndex + 1)
                            {
                                line.StartPoint = line.StartPoint.Add(new Vector3d(request.Delta, 0, 0));
                                changed = true;
                            }
                        }
                        else if (!request.Data.IsVertical && data.IsVertical)
                        {
                            if (data.SegmentIndex == request.Data.BoundaryIndex)
                            {
                                line.StartPoint = line.StartPoint.Add(new Vector3d(0, request.Delta, 0));
                                changed = true;
                            }
                            else if (data.SegmentIndex == request.Data.BoundaryIndex + 1)
                            {
                                line.EndPoint = line.EndPoint.Add(new Vector3d(0, request.Delta, 0));
                                changed = true;
                            }
                        }
                        if (!changed) line.DowngradeOpen();
                    }
                    MoveAdjacentGroups(request, transaction);
                    transaction.Commit();
                }
                document.Editor.Regen();
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\n楼梯排版分格调整失败：" + exception.Message);
            }
        }

        private static void MoveAdjacentGroups(GridMoveRequest request, Transaction transaction)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in request.Data.Slots)
            {
                if (slot.Page != request.Data.Page) continue;
                var touches = request.Data.IsVertical
                    ? slot.Column + slot.ColumnSpan == request.Data.BoundaryIndex + 1
                        || slot.Column == request.Data.BoundaryIndex + 1
                    : slot.Row + slot.RowSpan == request.Data.BoundaryIndex + 1
                        || slot.Row == request.Data.BoundaryIndex + 1;
                if (touches) names.Add(slot.GroupName);
            }
            if (names.Count == 0) return;
            var dictionary = (DBDictionary)transaction.GetObject(
                request.Database.GroupDictionaryId, OpenMode.ForRead);
            var movement = request.Data.IsVertical
                ? new Vector3d(request.Delta / 2.0, 0, 0)
                : new Vector3d(0, request.Delta / 2.0, 0);
            var matrix = Matrix3d.Displacement(movement);
            foreach (var name in names)
            {
                if (!dictionary.Contains(name)) continue;
                var group = (Group)transaction.GetObject(dictionary.GetAt(name), OpenMode.ForRead);
                foreach (ObjectId id in group.GetAllEntityIds())
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (entity != null) entity.TransformBy(matrix);
                }
            }
        }
    }

    internal sealed class StairDividerGripData : GripData
    {
        public StairDividerGripData() { }
    }

    internal sealed class GridMoveRequest
    {
        public GridMoveRequest(Database database, ObjectId objectId,
            GridLineData data, double delta)
        {
            Database = database;
            ObjectId = objectId;
            Data = data;
            Delta = delta;
        }
        public Database Database { get; private set; }
        public ObjectId ObjectId { get; private set; }
        public GridLineData Data { get; private set; }
        public double Delta { get; private set; }
    }

    internal sealed class GridSlotData
    {
        public string GroupName;
        public int Page;
        public int Row;
        public int Column;
        public int RowSpan;
        public int ColumnSpan;
    }

    internal sealed class GridLineData
    {
        public string LayoutId;
        public string Kind;
        public int BoundaryIndex;
        public int SegmentIndex;
        public int Page;
        public double BaseCoordinate;
        public double Minimum;
        public double Maximum;
        public readonly List<GridSlotData> Slots = new List<GridSlotData>();
        public bool IsDivider { get { return Kind == "D-H" || Kind == "D-V"; } }
        public bool IsVertical { get { return Kind == "D-V" || Kind == "E-V"; } }
        public bool IsHorizontal { get { return Kind == "D-H" || Kind == "E-H"; } }

        public static bool TryRead(Entity entity, out GridLineData data)
        {
            data = null;
            try
            {
                using (var buffer = entity.GetXDataForApplication(StairLayoutGridService.RegAppName))
                {
                    if (buffer == null) return false;
                    var values = buffer.AsArray();
                    if (values.Length < 9) return false;
                    var result = new GridLineData
                    {
                        LayoutId = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture),
                        Kind = Convert.ToString(values[2].Value, CultureInfo.InvariantCulture),
                        BoundaryIndex = Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture),
                        SegmentIndex = Convert.ToInt32(values[4].Value, CultureInfo.InvariantCulture),
                        Page = Convert.ToInt32(values[5].Value, CultureInfo.InvariantCulture),
                        BaseCoordinate = Convert.ToDouble(values[6].Value, CultureInfo.InvariantCulture),
                        Minimum = Convert.ToDouble(values[7].Value, CultureInfo.InvariantCulture),
                        Maximum = Convert.ToDouble(values[8].Value, CultureInfo.InvariantCulture)
                    };
                    for (var index = 9; index < values.Length; index++)
                    {
                        var raw = values[index].Value as string;
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        var parts = raw.Split('|');
                        int page, row, column, rowSpan, columnSpan;
                        if (parts.Length != 7 || parts[0] != "S"
                            || !int.TryParse(parts[2], out page)
                            || !int.TryParse(parts[3], out row)
                            || !int.TryParse(parts[4], out column)
                            || !int.TryParse(parts[5], out rowSpan)
                            || !int.TryParse(parts[6], out columnSpan)) continue;
                        result.Slots.Add(new GridSlotData
                        {
                            GroupName = parts[1], Page = page, Row = row,
                            Column = column, RowSpan = rowSpan, ColumnSpan = columnSpan
                        });
                    }
                    data = result;
                    return true;
                }
            }
            catch { return false; }
        }

        public void Write(Entity entity)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, StairLayoutGridService.RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, LayoutId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, Kind),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, BoundaryIndex),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, SegmentIndex),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, Page),
                new TypedValue((int)DxfCode.ExtendedDataReal, BaseCoordinate),
                new TypedValue((int)DxfCode.ExtendedDataReal, Minimum),
                new TypedValue((int)DxfCode.ExtendedDataReal, Maximum)
            };
            foreach (var slot in Slots)
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString,
                    string.Format(CultureInfo.InvariantCulture, "S|{0}|{1}|{2}|{3}|{4}|{5}",
                        slot.GroupName, slot.Page, slot.Row, slot.Column,
                        slot.RowSpan, slot.ColumnSpan)));
            entity.XData = new ResultBuffer(values.ToArray());
        }
    }
}
