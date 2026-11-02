using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace BatchPdfPublisher.Services
{
    internal sealed class DimensionSegment
    {
        internal ObjectId Id;
        internal Point3d First, Second, Line, Text;
        internal Vector3d Direction, Normal;
        internal double A, B;
        internal string Chain, Literal, Appearance;
        internal ObjectId Style, Layer;
        internal double Factor, Scale;
        internal bool TextMoved, Associated, Reversed, Locked;
    }

    internal sealed class CadDimensionChain
    {
        internal const string Key = "WANLUO_DIMCHAIN_V1";
        internal Point3d Origin;
        internal Vector3d U, V, N;
        internal double Offset, Epsilon;
        internal ObjectId Space;
        internal List<DimensionSegment> Segments = new List<DimensionSegment>();
        internal double T(Point3d p) { return (p - Origin).DotProduct(U); }
        internal Point3d OnLine(double t) { return Origin + U * t + V * Offset; }
        internal double[] Stations { get { return DimensionChainGeometry.Stations(Segments.SelectMany(s => new[] { s.A, s.B }), Epsilon); } }
        private static string Appearance(Dimension dim)
        {
            using (var style = dim.GetDimstyleData())
                return string.Join("|", typeof(DimStyleTableRecord).GetProperties().Where(p => p.Name.StartsWith("Dim", StringComparison.Ordinal) && p.CanRead && p.GetIndexParameters().Length == 0)
                    .OrderBy(p => p.Name).Select(p => { try { return p.Name + "=" + Convert.ToString(p.GetValue(style, null), System.Globalization.CultureInfo.InvariantCulture); } catch { return p.Name + "=?"; } }));
        }
        internal static string ReadTag(Dimension dim, Transaction tx)
        {
            if (dim.ExtensionDictionary.IsNull) return null;
            var dictionary = (DBDictionary)tx.GetObject(dim.ExtensionDictionary, OpenMode.ForRead);
            if (!dictionary.Contains(Key)) return null;
            var record = (Xrecord)tx.GetObject(dictionary.GetAt(Key), OpenMode.ForRead);
            using (var data = record.Data) return data == null ? null : data.AsArray().Select(x => x.Value as string).FirstOrDefault();
        }
        internal static void Tag(Dimension dim, Transaction tx, string chain)
        {
            if (dim.ExtensionDictionary.IsNull) dim.CreateExtensionDictionary();
            var dictionary = (DBDictionary)tx.GetObject(dim.ExtensionDictionary, OpenMode.ForWrite);
            Xrecord record;
            if (dictionary.Contains(Key)) record = (Xrecord)tx.GetObject(dictionary.GetAt(Key), OpenMode.ForWrite);
            else { record = new Xrecord(); dictionary.SetAt(Key, record); tx.AddNewlyCreatedDBObject(record, true); }
            using (var data = new ResultBuffer(new TypedValue((int)DxfCode.Text, chain))) record.Data = data;
        }
        internal static DimensionSegment Read(Dimension dim, Transaction tx)
        {
            if (dim == null || dim.Annotative == AnnotativeStates.True) throw new ArgumentException("首批仅编辑非注释性的原生直线尺寸。");
            var rotated = dim as RotatedDimension; var aligned = dim as AlignedDimension;
            if (rotated == null && aligned == null) throw new ArgumentException("请选择原生线性或对齐尺寸；不转换天正／代理对象。");
            var s = new DimensionSegment { Id = dim.ObjectId, Normal = dim.Normal.GetNormal(), Style = dim.DimensionStyle, Layer = dim.LayerId,
                Factor = dim.Dimlfac, Scale = dim.Dimscale, Literal = dim.DimensionText, Text = dim.TextPosition, TextMoved = !dim.UsingDefaultTextPosition,
                Associated = dim.GetPersistentReactorIds().Count != 0, Chain = ReadTag(dim, tx), Appearance = Appearance(dim) };
            if (rotated != null)
            {
                s.First = rotated.XLine1Point; s.Second = rotated.XLine2Point; s.Line = rotated.DimLinePoint;
                s.Direction = new Vector3d(Math.Cos(rotated.Rotation), Math.Sin(rotated.Rotation), 0).TransformBy(Matrix3d.PlaneToWorld(s.Normal)).GetNormal();
            }
            else { s.First = aligned.XLine1Point; s.Second = aligned.XLine2Point; s.Line = aligned.DimLinePoint; s.Direction = (s.Second - s.First).GetNormal(); }
            s.Locked = ((LayerTableRecord)tx.GetObject(s.Layer, OpenMode.ForRead)).IsLocked;
            return s;
        }
        internal static CadDimensionChain Load(Database db, ObjectId seed)
        {
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var source = Read(tx.GetObject(seed, OpenMode.ForRead, false) as Dimension, tx);
                if (source.Locked) throw new ArgumentException("该尺寸所在图层已锁定。");
                var c = new CadDimensionChain { Origin = (source.Second - source.First).DotProduct(source.Direction) < 0 ? source.Second : source.First,
                    U = source.Direction, N = source.Normal, Space = ((Entity)tx.GetObject(seed, OpenMode.ForRead)).OwnerId };
                if (c.Space != db.CurrentSpaceId) throw new ArgumentException("请在尺寸所属的模型／布局空间编辑，不直接修改块内尺寸。");
                c.V = c.N.CrossProduct(c.U).GetNormal(); c.Offset = (source.Line - c.Origin).DotProduct(c.V);
                // INSUNITS Unitless is deliberately reported as drawing units by the UI.
                c.Epsilon = 1e-6 / (db.Insunits == UnitsValue.Undefined ? 1 : UnitsConverter.GetConversionFactor(db.Insunits, UnitsValue.Millimeters));
                var candidates = new List<DimensionSegment>();
                foreach (ObjectId id in (BlockTableRecord)tx.GetObject(c.Space, OpenMode.ForRead))
                {
                    try
                    {
                        var s = Read(tx.GetObject(id, OpenMode.ForRead, false) as Dimension, tx);
                        if (source.Chain != null && s.Chain != source.Chain || source.Chain == null && s.Chain != null && id != seed) continue;
                        if (source.Chain == null && (s.Style != source.Style || s.Layer != source.Layer || Math.Abs(s.Factor - source.Factor) > 1e-10 || Math.Abs(s.Scale - source.Scale) > 1e-10)) continue;
                        if (Math.Abs(s.Direction.DotProduct(c.U)) < 1 - 1e-10 || s.Normal.DotProduct(c.N) < 1 - 1e-10) continue;
                        if (Math.Abs((s.Line - c.Origin).DotProduct(c.V) - c.Offset) > c.Epsilon || Math.Abs((s.First - c.Origin).DotProduct(c.N)) > c.Epsilon || Math.Abs((s.Second - c.Origin).DotProduct(c.N)) > c.Epsilon) continue;
                        s.A = c.T(s.First); s.B = c.T(s.Second);
                        if (s.A > s.B) { s.Reversed = true; var p = s.First; s.First = s.Second; s.Second = p; var t = s.A; s.A = s.B; s.B = t; }
                        candidates.Add(s);
                    }
                    catch (ArgumentException) { } catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
                var initial = candidates.FirstOrDefault(s => s.Id == seed);
                if (initial == null) throw new ArgumentException("不能读取该尺寸的共面定义点。");
                c.Segments.Add(initial);
                bool added;
                do
                {
                    added = false;
                    foreach (var s in candidates.Where(s => !c.Segments.Contains(s)).ToArray())
                        if (c.Segments.Any(x => Math.Abs(x.A - s.B) <= c.Epsilon || Math.Abs(x.B - s.A) <= c.Epsilon)) { c.Segments.Add(s); added = true; }
                } while (added);
                c.Segments = c.Segments.OrderBy(s => s.A).ToList();
                if (c.Segments.Any(s => s.Locked)) throw new ArgumentException("尺寸串含锁定图层，整串未修改。");
                if (c.Segments.Any(s => s.Style != source.Style || s.Layer != source.Layer || Math.Abs(s.Factor - source.Factor) > 1e-10 || Math.Abs(s.Scale - source.Scale) > 1e-10)) throw new ArgumentException("串内样式／图层／比例已分歧，请先解除成串或恢复统一后再编辑。");
                // Reject branches and overlapping totals, rather than adopting them silently.
                foreach (var s in candidates.Where(s => !c.Segments.Contains(s)))
                    if (c.Segments.Any(x => Math.Min(x.B, s.B) - Math.Max(x.A, s.A) > c.Epsilon)) throw new ArgumentException("同道存在重叠或总尺寸。请先将目标尺寸串移到独立尺寸道，再选择。");
                for (var i = 1; i < c.Segments.Count; i++) if (c.Segments[i].A < c.Segments[i - 1].B - c.Epsilon) throw new ArgumentException("尺寸串分叉／重叠，未修改。请先分开重叠尺寸。");
                return c;
            }
        }
        internal static void Points(Dimension d, Point3d a, Point3d b)
        {
            var r = d as RotatedDimension; if (r != null) { r.XLine1Point = a; r.XLine2Point = b; }
            else { var x = (AlignedDimension)d; x.XLine1Point = a; x.XLine2Point = b; }
        }
        internal static void Line(Dimension d, Point3d p)
        { var r = d as RotatedDimension; if (r != null) r.DimLinePoint = p; else ((AlignedDimension)d).DimLinePoint = p; }
        internal void Validate(Database db, Transaction tx, bool points)
        {
            if (db.CurrentSpaceId != Space) throw new ArgumentException("已切换空间，请重新选择尺寸串。");
            foreach (var old in Segments)
            {
                var now = Read(tx.GetObject(old.Id, OpenMode.ForRead) as Dimension, tx);
                if (now.Locked) throw new ArgumentException("尺寸串含锁定图层，未修改。");
                // Read may reverse the endpoints; compare in original order independently.
                if (Math.Min(now.First.DistanceTo(old.First) + now.Second.DistanceTo(old.Second), now.First.DistanceTo(old.Second) + now.Second.DistanceTo(old.First)) > Epsilon * 2 || now.Line.DistanceTo(old.Line) > Epsilon || now.Literal != old.Literal || now.Appearance != old.Appearance || now.Layer != old.Layer)
                    throw new ArgumentException("尺寸已被外部操作修改，请重新选择。");
                if (points && now.Associated) throw new ArgumentException("该尺寸含关联／反应器。首批只允许移动尺寸线，不静默解除关联。");
            }
        }
        internal ObjectId Apply(Database db, string operation, double value, int node, bool clearLiteral)
        {
            if (operation == "add" && Stations.Any(x => Math.Abs(x - value) <= Epsilon)) return Segments[0].Id;
            using (var tx = db.TransactionManager.StartTransaction())
            {
                Validate(db, tx, operation != "line" && operation != "release");
                var nodes = Stations; var tag = Segments[0].Chain;
                if (tag == null || Segments.Any(s => s.Chain != tag)) tag = Guid.NewGuid().ToString("N");
                else foreach (ObjectId id in (BlockTableRecord)tx.GetObject(Space, OpenMode.ForRead))
                    if (!Segments.Any(s => s.Id == id) && tx.GetObject(id, OpenMode.ForRead) is Dimension && ReadTag((Dimension)tx.GetObject(id, OpenMode.ForRead), tx) == tag) { tag = Guid.NewGuid().ToString("N"); break; }
                if (operation == "node") value = DimensionChainGeometry.MoveStation(nodes, node, value, Epsilon);
                if (operation == "merge" && (node <= 0 || node >= nodes.Length - 1)) throw new ArgumentException("请选择内部界点；首尾请用删除段。");
                var ids = new List<ObjectId>(); var pending = new List<Dimension>();
                try
                {
                foreach (var s in Segments)
                {
                    var d = (Dimension)tx.GetObject(s.Id, OpenMode.ForWrite);
                    Action<Dimension,Point3d,Point3d> set = (dim, first, second) => Points(dim, s.Reversed ? second : first, s.Reversed ? first : second);
                    if (operation == "release")
                    {
                        if (!d.ExtensionDictionary.IsNull) { var dict = (DBDictionary)tx.GetObject(d.ExtensionDictionary, OpenMode.ForWrite); if (dict.Contains(Key)) { var id = dict.GetAt(Key); dict.Remove(Key); tx.GetObject(id, OpenMode.ForWrite).Erase(); } }
                        ids.Add(s.Id); continue;
                    }
                    if (operation == "delete" && s.Id == Segments[node].Id) { d.Erase(); continue; }
                    var a = s.First; var b = s.Second;
                    if (operation == "line") { var delta = V * (value - Offset); Line(d, s.Line + delta); if (s.TextMoved) d.TextPosition = s.Text + delta; }
                    if (operation == "node")
                    {
                        var affected = Math.Abs(s.A - nodes[node]) <= Epsilon || Math.Abs(s.B - nodes[node]) <= Epsilon;
                        if (Math.Abs(s.A - nodes[node]) <= Epsilon) a += U * (value - s.A);
                        if (Math.Abs(s.B - nodes[node]) <= Epsilon) b += U * (value - s.B);
                        if (Math.Abs(T(b) - T(a)) <= Epsilon) { d.Erase(); continue; }
                        if (affected)
                        {
                            if (!string.IsNullOrEmpty(s.Literal) && !s.Literal.Contains("<>")) { if (!clearLiteral) throw new ArgumentException("修改界点前需明确清除固定文字覆写。"); d.DimensionText = ""; }
                            set(d, a, b);
                        }
                    }
                    if (operation == "merge")
                    {
                        if (Math.Abs(s.A - nodes[node]) <= Epsilon) { d.Erase(); continue; }
                        if (Math.Abs(s.B - nodes[node]) <= Epsilon)
                        {
                            var next = Segments.First(x => Math.Abs(x.A - nodes[node]) <= Epsilon);
                            if (s.Style != next.Style || s.Appearance != next.Appearance || s.Literal != next.Literal) throw new ArgumentException("相邻段的样式覆盖或文字不同，不能直接合并。");
                            if (!string.IsNullOrEmpty(s.Literal) && !s.Literal.Contains("<>")) { if (!clearLiteral) throw new ArgumentException("合并前需明确清除固定文字覆写。"); d.DimensionText = ""; }
                            b = next.Second; set(d, a, b);
                        }
                    }
                    if (operation == "add" && value > s.A + Epsilon && value < s.B - Epsilon)
                    {
                        if (!string.IsNullOrEmpty(s.Literal) && !s.Literal.Contains("<>") && !clearLiteral) throw new ArgumentException("该段有固定文字。勾选清除覆写后再拆分，避免生成错误数字。");
                        var clone = (Dimension)d.Clone(); pending.Add(clone);
                        if (clearLiteral) { d.DimensionText = ""; clone.DimensionText = ""; }
                        var cutA = a + U * (value - s.A); var cutB = b + U * (value - s.B);
                        set(d, a, cutB); set(clone, cutA, b);
                        if (s.TextMoved) { d.UsingDefaultTextPosition = true; clone.UsingDefaultTextPosition = true; }
                    }
                    Tag(d, tx, tag); d.RecomputeDimensionBlock(true); ids.Add(d.ObjectId);
                }
                if (operation == "add" && (value < nodes[0] - Epsilon || value > nodes[nodes.Length - 1] + Epsilon))
                {
                    var left = value < nodes[0]; var s = left ? Segments[0] : Segments[Segments.Count - 1];
                    if (!string.IsNullOrEmpty(s.Literal) && !s.Literal.Contains("<>") && !clearLiteral) throw new ArgumentException("端段有固定文字，请先确认清除覆写。");
                    var d = (Dimension)((Dimension)tx.GetObject(s.Id, OpenMode.ForRead)).Clone(); pending.Add(d);
                    if (clearLiteral) d.DimensionText = "";
                    if (left) Points(d, s.Reversed ? s.First : s.First + U * (value - s.A), s.Reversed ? s.First + U * (value - s.A) : s.First);
                    else Points(d, s.Reversed ? s.Second + U * (value - s.B) : s.Second, s.Reversed ? s.Second : s.Second + U * (value - s.B));
                    d.UsingDefaultTextPosition = true;
                }
                var space = (BlockTableRecord)tx.GetObject(Space, OpenMode.ForWrite);
                foreach (var d in pending) { space.AppendEntity(d); tx.AddNewlyCreatedDBObject(d, true); Tag(d, tx, tag); d.RecomputeDimensionBlock(true); ids.Add(d.ObjectId); }
                // A duplicate station is a no-op: callers filter it before creating an undo record.
                tx.Commit(); return ids.Count == 0 ? ObjectId.Null : ids[0];
                }
                finally { foreach (var copy in pending) copy.Dispose(); }
            }
        }
    }
}
