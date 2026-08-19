using System;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace BatchPdfPublisher.Services
{
    public static class CadCompatibilityService
    {
        private static readonly string[] TianzhengTokens = { "TCH", "TARCH", "TIANZHENG", "TWT", "THMEP", "THVAC", "THWSS" };

        /// <summary>
        /// 检测图纸是否包含天正对象（增强版）
        /// </summary>
        public static bool IsTianzhengDrawing(Database database)
        {
            if (database == null) return false;
            try
            {
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    var table = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead) as RegAppTable;
                    if (table != null)
                    {
                        foreach (ObjectId id in table)
                        {
                            var record = transaction.GetObject(id, OpenMode.ForRead) as RegAppTableRecord;
                            if (record != null && ContainsTianzhengToken(record.Name)) return true;
                        }
                    }

                    var blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                    if (blockTable != null)
                    {
                        // 【修复】限制检查数量，避免超大图纸导致性能问题
                        var checkedEntities = 0;
                        const int MaxEntitiesToCheck = 10000;

                        foreach (ObjectId recordId in blockTable)
                        {
                            var record = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                            if (record == null) continue;
                            foreach (ObjectId entityId in record)
                            {
                                var className = entityId.ObjectClass == null ? string.Empty : entityId.ObjectClass.Name;
                                if (ContainsTianzhengToken(className)) return true;

                                checkedEntities++;
                                if (checkedEntities > MaxEntitiesToCheck) break;
                            }
                            if (checkedEntities > MaxEntitiesToCheck) break;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 检测天正环境是否已加载
        /// </summary>
        public static bool IsTianzhengHostLoaded()
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .Select(x => x.GetName().Name ?? string.Empty)
                    .Any(ContainsTianzhengToken);
            }
            catch { return false; }
        }

        /// <summary>
        /// 获取天正环境描述信息
        /// </summary>
        public static string DescribeTianzhengHost()
        {
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetName().Name ?? string.Empty).FirstOrDefault(ContainsTianzhengToken);
                return string.IsNullOrWhiteSpace(loaded) ? "未加载天正运行环境" : (loaded.IndexOf("T30", StringComparison.OrdinalIgnoreCase) >= 0 ? "T30 天正运行环境" : loaded.IndexOf("T20", StringComparison.OrdinalIgnoreCase) >= 0 ? "T20 天正运行环境" : "天正运行环境");
            }
            catch { return "未加载天正运行环境"; }
        }

        /// <summary>
        /// 【新增】检测天正对象的严重程度
        /// </summary>
        public static TianzhengSeverity DetectTianzhengSeverity(Database database)
        {
            if (database == null) return TianzhengSeverity.None;
            try
            {
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    var tianzhengObjectCount = 0;
                    var blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                    if (blockTable != null)
                    {
                        var checkedEntities = 0;
                        const int MaxEntitiesToCheck = 5000;

                        foreach (ObjectId recordId in blockTable)
                        {
                            var record = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                            if (record == null) continue;
                            foreach (ObjectId entityId in record)
                            {
                                var className = entityId.ObjectClass == null ? string.Empty : entityId.ObjectClass.Name;
                                if (ContainsTianzhengToken(className)) tianzhengObjectCount++;

                                checkedEntities++;
                                if (checkedEntities > MaxEntitiesToCheck) break;
                            }
                            if (checkedEntities > MaxEntitiesToCheck) break;
                        }
                    }

                    if (tianzhengObjectCount == 0) return TianzhengSeverity.None;
                    if (tianzhengObjectCount < 10) return TianzhengSeverity.Low;
                    if (tianzhengObjectCount < 50) return TianzhengSeverity.Medium;
                    return TianzhengSeverity.High;
                }
            }
            catch { return TianzhengSeverity.Unknown; }
        }

        private static bool ContainsTianzhengToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return TianzhengTokens.Any(token => value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    /// <summary>
    /// 天正对象严重程度
    /// </summary>
    public enum TianzhengSeverity
    {
        None,       // 无天正对象
        Low,        // 少量天正对象（<10）
        Medium,     // 中等天正对象（10-50）
        High,       // 大量天正对象（>50）
        Unknown     // 检测失败
    }
}
