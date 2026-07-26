using System;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace BatchPdfPublisher.Services
{
    public static class CadCompatibilityService
    {
        private static readonly string[] TianzhengTokens = { "TCH", "TARCH", "TIANZHENG", "TWT", "THMEP", "THVAC", "THWSS" };

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
                        foreach (ObjectId recordId in blockTable)
                        {
                            var record = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                            if (record == null) continue;
                            foreach (ObjectId entityId in record)
                            {
                                var className = entityId.ObjectClass == null ? string.Empty : entityId.ObjectClass.Name;
                                if (ContainsTianzhengToken(className)) return true;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

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

        public static string DescribeTianzhengHost()
        {
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetName().Name ?? string.Empty).FirstOrDefault(ContainsTianzhengToken);
                return string.IsNullOrWhiteSpace(loaded) ? "未加载天正运行环境" : (loaded.IndexOf("T30", StringComparison.OrdinalIgnoreCase) >= 0 ? "T30 天正运行环境" : loaded.IndexOf("T20", StringComparison.OrdinalIgnoreCase) >= 0 ? "T20 天正运行环境" : "天正运行环境");
            }
            catch { return "未加载天正运行环境"; }
        }

        private static bool ContainsTianzhengToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return TianzhengTokens.Any(token => value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
