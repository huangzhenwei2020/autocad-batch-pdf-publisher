using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 把所选对象归到指定图层。
    ///
    /// 只改 LayerId 是不够的：对象若显式指定过颜色、线型或线宽，挪层后不会跟着变，
    /// 用户看到的是"挪过去了但颜色没变"。因此提供 setByLayer 开关，把这些属性一并
    /// 设为随层。块参照的属性（AttributeReference）位于宿主空间，需要单独移动；
    /// 块定义内部的图元由参照决定显示图层，不能也不应逐个改写。
    /// </summary>
    public static class LayerAssignmentService
    {
        public sealed class Outcome
        {
            /// <summary>实际发生改动的对象数（含块属性）。</summary>
            public int Changed;
            /// <summary>已经在目标图层上、无需处理的对象数。</summary>
            public int AlreadyOnLayer;
            /// <summary>因所在图层被锁定而跳过的对象数。</summary>
            public int Locked;
            /// <summary>处理过程中出错的对象数。</summary>
            public int Failed;
            public string TargetLayerName;

            public bool HasChanges { get { return Changed > 0; } }

            public string Describe()
            {
                var text = "已归层 " + Changed + " 个对象";
                if (AlreadyOnLayer > 0) text += "，已在“" + TargetLayerName + "” " + AlreadyOnLayer + " 个";
                if (Locked > 0) text += "，因图层锁定跳过 " + Locked + " 个";
                if (Failed > 0) text += "，失败 " + Failed + " 个";
                return text + "。";
            }
        }

        /// <summary>目标图层当前是否允许写入。锁定图层不能接收对象。</summary>
        public static bool CanTargetLayer(Transaction transaction, Database database, ObjectId layerId, out string reason)
        {
            reason = null;
            if (layerId.IsNull || layerId.IsErased) { reason = "目标图层无效。"; return false; }
            try
            {
                var record = transaction.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                if (record == null) { reason = "目标图层无效。"; return false; }
                if (record.IsLocked) { reason = "目标图层“" + record.Name + "”已锁定，无法接收对象。"; return false; }
                if (record.IsFrozen || record.IsOff) { reason = "目标图层“" + record.Name + "”已关闭或冻结，归层后对象将不可见。"; return false; }
                return true;
            }
            catch (System.Exception exception) { reason = "读取目标图层失败：" + exception.Message; return false; }
        }

        /// <summary>按名称取图层；找不到返回 ObjectId.Null。</summary>
        public static ObjectId FindLayer(Transaction transaction, Database database, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            return table.Has(name) ? table[name] : ObjectId.Null;
        }

        /// <summary>
        /// 制图标准（BZS）里定义的图层名，即本插件的"系统图层"。
        /// 对话框据此提供"只显示系统图层"的筛选。
        /// </summary>
        public static HashSet<string> GetSystemLayerNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var profile = DraftingStandardService.LoadProfile();
                foreach (var layer in profile.Layers)
                    if (!string.IsNullOrWhiteSpace(layer.Name)) names.Add(layer.Name);
            }
            catch { }
            return names;
        }

        /// <summary>所选对象当前分布在哪些图层上（用于"合并旧图层"预览）。</summary>
        public static List<string> GetLayersOfObjects(Document document, IEnumerable<ObjectId> ids)
        {
            var names = new List<string>();
            if (document == null) return names;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (var id in ids ?? new ObjectId[0])
                    {
                        if (id.IsNull) continue;
                        try
                        {
                            var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                            if (entity != null && !string.IsNullOrWhiteSpace(entity.Layer) && seen.Add(entity.Layer))
                                names.Add(entity.Layer);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public sealed class MergeOutcome
        {
            /// <summary>合并到目标图层的对象数。</summary>
            public int Moved;
            /// <summary>已被清理掉的空旧图层。</summary>
            public readonly List<string> PurgedLayers = new List<string>();
            /// <summary>仍在使用中、被保留下来的旧图层。</summary>
            public readonly List<string> KeptLayers = new List<string>();
            /// <summary>因图层锁定而整体跳过的图层。</summary>
            public readonly List<string> LockedLayers = new List<string>();

            public string Describe()
            {
                var text = "已合并 " + Moved + " 个对象到“" + TargetLayerName + "”";
                if (PurgedLayers.Count > 0) text += "，清理空图层 " + PurgedLayers.Count + " 个";
                if (KeptLayers.Count > 0) text += "，保留仍在使用或有其他内容的图层：" + string.Join("、", KeptLayers.ToArray());
                if (LockedLayers.Count > 0) text += "，锁定的图层未处理：" + string.Join("、", LockedLayers.ToArray());
                return text + "。";
            }

            public string TargetLayerName;
        }

        /// <summary>
        /// 把若干旧图层整体并入目标图层：图层上的**所有**对象都移过去，
        /// 空的旧图层顺手清理，避免留下垃圾图层。
        ///
        /// 只清理确实清空的图层，并且受 Xref/依赖约束无法删除时改为保留并说明，
        /// 不会为了"看起来干净"而删除还有内容的图层。
        /// </summary>
        public static MergeOutcome MergeLayers(Document document, Transaction transaction,
            IEnumerable<string> sourceLayerNames, ObjectId targetLayer, bool setByLayer)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (transaction == null) throw new ArgumentNullException("transaction");

            var database = document.Database;
            var outcome = new MergeOutcome();
            var targetRecord = transaction.GetObject(targetLayer, OpenMode.ForRead, false) as LayerTableRecord;
            if (targetRecord == null) return outcome;
            outcome.TargetLayerName = targetRecord.Name;
            var targetName = targetRecord.Name;

            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            var sources = new List<string>();
            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in sourceLayerNames ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenSources.Add(name)) continue;
                sources.Add(name);
            }

            foreach (var sourceName in sources)
            {
                if (!layerTable.Has(sourceName)) { outcome.KeptLayers.Add(sourceName); continue; }
                var sourceRecord = transaction.GetObject(layerTable[sourceName], OpenMode.ForRead, false) as LayerTableRecord;
                if (sourceRecord == null) { outcome.KeptLayers.Add(sourceName); continue; }
                if (sourceRecord.IsLocked) { outcome.LockedLayers.Add(sourceName); continue; }

                outcome.Moved += MoveAllEntitiesOnLayer(transaction, database, sourceName, targetLayer, targetName, setByLayer);

                // 清空后才清理图层。仍有对象（含其他布局上的对象）就保留。
                if (HasObjectsOnLayer(transaction, database, sourceName)) { outcome.KeptLayers.Add(sourceName); continue; }
                try
                {
                    var writable = transaction.GetObject(layerTable[sourceName], OpenMode.ForWrite, false) as LayerTableRecord;
                    if (writable == null) { outcome.KeptLayers.Add(sourceName); continue; }
                    writable.Erase();
                    outcome.PurgedLayers.Add(sourceName);
                }
                catch { outcome.KeptLayers.Add(sourceName); }
            }
            return outcome;
        }

        private static int MoveAllEntitiesOnLayer(Transaction transaction, Database database, string sourceName,
            ObjectId targetLayer, string targetName, bool setByLayer)
        {
            var moved = 0;
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId recordId in blockTable)
            {
                var space = transaction.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) continue;
                foreach (ObjectId entityId in space)
                {
                    try
                    {
                        var entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                        if (entity == null) continue;
                        if (!string.Equals(entity.Layer, sourceName, StringComparison.OrdinalIgnoreCase)) continue;
                        var owner = transaction.GetObject(entity.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                        if (owner != null && owner.IsLocked) continue;
                        var writable = transaction.GetObject(entityId, OpenMode.ForWrite, false) as Entity;
                        if (writable == null) continue;
                        writable.LayerId = targetLayer;
                        moved++;
                        if (setByLayer) ApplyByLayer(writable);
                    }
                    catch { }
                }
            }
            return moved;
        }

        /// <summary>该图层上是否还有任何对象（用于判断能否安全清理）。</summary>
        public static bool HasObjectsOnLayer(Transaction transaction, Database database, string layerName)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId recordId in blockTable)
            {
                var space = transaction.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) continue;
                foreach (ObjectId entityId in space)
                {
                    try
                    {
                        var entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                        if (entity != null && string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        /// <summary>
        /// 把选中的对象归到目标图层。必须由调用方负责事务与文档锁。
        /// </summary>
        public static Outcome MoveToLayer(Document document, Transaction transaction, IEnumerable<ObjectId> ids,
            ObjectId targetLayer, bool setByLayer, bool includeBlockAttributes)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (transaction == null) throw new ArgumentNullException("transaction");

            var database = document.Database;
            var outcome = new Outcome();
            var targetRecord = transaction.GetObject(targetLayer, OpenMode.ForRead, false) as LayerTableRecord;
            if (targetRecord == null) { outcome.Failed++; return outcome; }
            outcome.TargetLayerName = targetRecord.Name;
            var targetName = targetRecord.Name;

            // 按"顶层实体"归类结果：块参照的多个属性可能同时出现"已归层"和"已在该层"，
            // 需分别累计，而不是让其中一个掩盖另一个。
            foreach (var id in ids ?? new ObjectId[0])
            {
                if (id.IsNull) continue;
                var already = 0;
                var changed = 0;
                var locked = 0;
                var failed = 0;
                try
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null) continue;
                    if (!MoveEntity(transaction, entity, targetLayer, targetName, setByLayer, includeBlockAttributes,
                            ref changed, ref already, ref locked))
                        failed++;
                }
                catch (System.Exception) { failed++; }

                outcome.Changed += changed;
                outcome.AlreadyOnLayer += already;
                outcome.Locked += locked;
                outcome.Failed += failed;
                // 既没改动、也没命中任何统计的情形（例如对象类型不参与归层）按"已在目标层"计，
                // 以免用户以为漏掉了对象。
                if (changed == 0 && already == 0 && locked == 0 && failed == 0) outcome.AlreadyOnLayer++;
            }
            return outcome;
        }

        /// <summary>返回 false 表示处理失败。</summary>
        private static bool MoveEntity(Transaction transaction, Entity entity, ObjectId targetLayer, string targetName,
            bool setByLayer, bool includeBlockAttributes, ref int changed, ref int already, ref int locked)
        {
            if (entity == null) return true;

            // 图层锁定的对象不能改。这里查对象自身所在图层的锁定状态，
            // 而不是设置里是否勾了"允许编辑锁定图层"。
            var owner = transaction.GetObject(entity.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
            if (owner != null && owner.IsLocked) { locked++; return true; }

            var sameLayer = string.Equals(entity.Layer, targetName, StringComparison.OrdinalIgnoreCase);
            if (sameLayer && !setByLayer) { already++; return true; }

            if (!sameLayer)
            {
                var writable = transaction.GetObject(entity.ObjectId, OpenMode.ForWrite, false) as Entity;
                if (writable == null) return false;
                writable.LayerId = targetLayer;
                entity = writable;
                changed++;
            }

            if (setByLayer && ApplyByLayer(entity)) changed++;

            // 块参照的属性位于宿主空间，必须单独归层，否则会留在原图层。
            var block = entity as BlockReference;
            if (block != null && includeBlockAttributes)
            {
                foreach (ObjectId attributeId in block.AttributeCollection)
                {
                    var attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                    if (attribute == null) continue;
                    var attributeOwner = transaction.GetObject(attribute.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                    if (attributeOwner != null && attributeOwner.IsLocked) { locked++; continue; }
                    var attributeSame = string.Equals(attribute.Layer, targetName, StringComparison.OrdinalIgnoreCase);
                    if (attributeSame && !setByLayer) { already++; continue; }
                    if (!attributeSame)
                    {
                        var writableAttribute = transaction.GetObject(attributeId, OpenMode.ForWrite, false) as AttributeReference;
                        if (writableAttribute == null) return false;
                        writableAttribute.LayerId = targetLayer;
                        attribute = writableAttribute;
                        changed++;
                    }
                    if (setByLayer && ApplyByLayer(attribute)) changed++;
                }
            }
            return true;
        }

        /// <summary>
        /// 把实体属性置为"随层"。返回是否发生了实际变化，
        /// 避免对已经是随层的对象重复计入改动数。
        /// </summary>
        public static bool ApplyByLayer(Entity entity)
        {
            var text = entity as DBText;
            if (text != null)
            {
                // 文字有独立的高度；颜色/线型/线宽才是随层相关项。
                return SetByLayer(text);
            }
            var block = entity as BlockReference;
            if (block != null) return SetByLayer(block);
            return SetByLayer(entity);
        }

        private static bool SetByLayer(Entity entity)
        {
            var changed = false;
            try
            {
                if (entity.ColorIndex != 256) { entity.ColorIndex = 256; changed = true; }
            }
            catch (System.Exception) { }
            try
            {
                if (!entity.LinetypeId.IsNull)
                {
                    // 256 = ByLayer 的线型索引约定；用 Linetype 名称判断更可靠。
                    if (!string.Equals(entity.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase))
                    { entity.Linetype = "ByLayer"; changed = true; }
                }
            }
            catch (System.Exception) { }
            try
            {
                if (entity.LineWeight != LineWeight.ByLayer) { entity.LineWeight = LineWeight.ByLayer; changed = true; }
            }
            catch (System.Exception) { }
            return changed;
        }

        /// <summary>
        /// 取选择集：优先使用已有的预选对象；没有预选时提示用户选择。
        /// 返回 null 表示用户取消。
        /// </summary>
        public static ObjectId[] ResolveSelection(Document document, string message)
        {
            if (document == null) return null;
            var editor = document.Editor;
            var implied = editor.SelectImplied();
            if (implied != null && implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                var ids = implied.Value.GetObjectIds();
                editor.SetImpliedSelection(new ObjectId[0]);
                return ids;
            }
            var options = new PromptSelectionOptions { MessageForAdding = message };
            var result = editor.GetSelection(options);
            return result.Status == PromptStatus.OK && result.Value != null ? result.Value.GetObjectIds() : null;
        }
    }
}
