using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 天正图名标注（TMBZ）只读探针：对用户选中的对象输出 DXF 名称、类名、
    /// COM 属性、XData 和扩展词典，供适配天正图名读取/写入时确认各版本字段。
    /// 探针结果写入 %APPDATA%\WanluoArchitectureTools\Logs\tianzheng-title-probe.log。
    /// </summary>
    internal static class TianzhengTitleProbeService
    {
        private static readonly string[] CandidateProperties =
        {
            "Text", "Title", "TitleText", "Note", "Name", "TextString", "Content", "Contents",
            "DrawingName", "SheetName", "FigName", "FigNameText", "TitleName", "Label", "Value",
            "Scale", "PrintScale", "DrawingScale", "图名", "图号", "比例", "TextHeight", "Height",
            "Position", "InsertionPoint", "Width", "Rotation", "ObjectName", "EntityName", "Handle",
            "StartPoint", "EndPoint", "Layer", "Color", "Visible"
        };

        private static readonly string[] CandidateMethods =
        {
            "GetText", "SetText", "GetTitle", "SetTitle", "GetFigName", "SetFigName",
            "GetNote", "SetNote", "Update", "Recompute", "Refresh", "GetCellText", "SetCellText"
        };

        /// <summary>探针日志完整路径。</summary>
        public static string LogPath { get { return Path.Combine(UserDataPaths.LogsDirectory, "tianzheng-title-probe.log"); } }

        /// <summary>自动扫描当前图纸中所有天正对象（类名/DXF 名含 TCH 等标记）并逐个探针，
        /// 供用户无需精确选择对象即可收集图名接口信息。返回探针的对象数量。</summary>
        public static int ProbeAllTianzheng(Database database)
        {
            if (database == null) return 0;
            var builder = new StringBuilder();
            builder.AppendLine("========== 天正图名探针：全图扫描 ==========");
            builder.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.AppendLine("天正环境: " + CadCompatibilityService.DescribeTianzhengHost());
            var count = 0;
            try
            {
                using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    var blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                    if (blockTable == null) return 0;
                    foreach (ObjectId recordId in blockTable)
                    {
                        var record = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                        if (record == null || record.IsLayout) continue;
                        foreach (ObjectId entityId in record)
                        {
                            if (entityId.ObjectClass == null) continue;
                            var className = entityId.ObjectClass.Name ?? string.Empty;
                            var dxfName = string.Empty;
                            try { dxfName = entityId.ObjectClass.DxfName ?? string.Empty; } catch { }
                            var marker = className + "|" + dxfName;
                            var isTianzheng = marker.IndexOf("TCH", StringComparison.OrdinalIgnoreCase) >= 0
                                || marker.IndexOf("TARCH", StringComparison.OrdinalIgnoreCase) >= 0
                                || marker.IndexOf("TWT", StringComparison.OrdinalIgnoreCase) >= 0
                                || dxfName.IndexOf("TITLE", StringComparison.OrdinalIgnoreCase) >= 0
                                || dxfName.IndexOf("SHEET", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!isTianzheng) continue;
                            var entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                            if (entity == null) continue;
                            builder.AppendLine("--- 扫描对象 #" + (count + 1) + " ---");
                            builder.AppendLine(Probe(entity));
                            count++;
                        }
                    }
                }
            }
            catch (Exception exception) { builder.AppendLine("扫描失败: " + exception.Message); }
            builder.AppendLine("========== 扫描结束，共 " + count + " 个天正对象 ==========");
            builder.AppendLine();
            try { File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8); } catch { }
            return count;
        }

        /// <summary>对选中对象执行探针，结果写入日志并返回摘要（供命令行提示）。</summary>
        public static string Probe(DBObject source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var builder = new StringBuilder();
            builder.AppendLine("========== 天正图名探针 ==========");
            builder.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.AppendLine("天正环境: " + CadCompatibilityService.DescribeTianzhengHost());
            builder.AppendLine("对象 Handle: " + SafeHandle(source));

            // 1) DXF / 类名。
            builder.AppendLine("DXF 名称: " + DxfName(source));
            builder.AppendLine("CLR 类: " + (source.GetType().FullName ?? source.GetType().Name));
            var entity = source as Entity;
            if (entity != null)
            {
                builder.AppendLine("Layer: " + entity.Layer);
                try
                {
                    var extents = entity.GeometricExtents;
                    builder.AppendLine("范围: Min(" + FormatPoint(extents.MinPoint) + ") Max(" + FormatPoint(extents.MaxPoint) + ")");
                }
                catch { }
            }

            // 2) COM 属性探测（IDispatch）。
            object com = null;
            try { com = source.AcadObject; } catch { }
            if (com == null) builder.AppendLine("COM: 无 AcadObject（普通对象或无天正 COM 包装）");
            else
            {
                builder.AppendLine("COM 类型: " + com.GetType().FullName);
                var found = 0;
                foreach (var name in CandidateProperties)
                {
                    var value = TryReadComProperty(com, name);
                    if (value == null) continue;
                    builder.AppendLine("COM[" + name + "] = " + Truncate(value));
                    found++;
                }
                if (found == 0) builder.AppendLine("COM: 候选属性均不可读（可能为 System.__ComObject 无法枚举）");
                // 方法探测：列出可成功调用的候选方法（只读调用，不修改对象）。
                var methodsFound = 0;
                foreach (var name in CandidateMethods)
                {
                    var value = TryInvokeComMethod(com, name);
                    if (value == null) continue;
                    builder.AppendLine("COM方法[" + name + "] 可调用，返回=" + Truncate(value));
                    methodsFound++;
                }
                if (methodsFound == 0) builder.AppendLine("COM方法: 候选方法均不可调用");
                // 注意：不再做 GetIdsOfNames / ITypeInfo 深度探测——这两个 COM 调用路径
                // 未经实际验证，会在某些天正版本触发 Access Violation 导致 CAD 崩溃。
                // 可写属性的确认改用 TianzhengScaleService 中经实际使用验证的反射
                // SetProperty 写法（见 TianzhengTitleService.InsertTitle 的写入路径）。
            }

            // 3) XData。
            try
            {
                var xdata = source.XData;
                if (xdata == null) builder.AppendLine("XData: 无");
                else
                {
                    var parts = new List<string>();
                    foreach (var entry in xdata.AsArray())
                    {
                        var value = entry.Value;
                        parts.Add(value == null ? "null" : value.ToString());
                    }
                    builder.AppendLine("XData(" + xdata.AsArray().Length + "): " + string.Join(" | ", parts));
                }
            }
            catch (Exception exception) { builder.AppendLine("XData 读取失败: " + exception.Message); }

            // 4) 扩展词典。
            try
            {
                var dictionaryId = source.ExtensionDictionary;
                if (dictionaryId.IsNull) builder.AppendLine("扩展词典: 无");
                else
                {
                    var names = new List<string>();
                    var database = source.Database;
                    using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
                    {
                        var dictionary = transaction.GetObject(dictionaryId, OpenMode.ForRead) as DBDictionary;
                        if (dictionary == null) builder.AppendLine("扩展词典: 无法打开");
                        else
                        {
                            foreach (var entry in dictionary)
                            {
                                names.Add(entry.Key);
                                var record = transaction.GetObject(entry.Value, OpenMode.ForRead, false);
                                builder.AppendLine("扩展词典[" + entry.Key + "] = " + (record == null ? "null" : record.GetType().Name));
                            }
                            if (names.Count == 0) builder.AppendLine("扩展词典: 空");
                        }
                    }
                }
            }
            catch (Exception exception) { builder.AppendLine("扩展词典读取失败: " + exception.Message); }

            // 5) 爆炸后的文本内容（天正图名常由多段文字组成）。
            if (entity != null)
            {
                var fragments = new List<string>();
                var objects = new DBObjectCollection();
                try
                {
                    entity.Explode(objects);
                    CollectText(objects, fragments, 0);
                }
                catch { }
                finally { foreach (DBObject item in objects) item.Dispose(); }
                builder.AppendLine("爆炸文字(" + fragments.Count + "): " + (fragments.Count == 0 ? "无" : string.Join(" / ", fragments)));
            }

            builder.AppendLine("========== 探针结束 ==========");
            builder.AppendLine();
            var text = builder.ToString();
            try { File.AppendAllText(LogPath, text, Encoding.UTF8); } catch { }
            return text;
        }

        private static void CollectText(DBObjectCollection objects, List<string> output, int depth)
        {
            foreach (DBObject item in objects)
            {
                var text = item as DBText;
                if (text != null) { output.Add(text.TextString); continue; }
                var mtext = item as MText;
                if (mtext != null) { output.Add(mtext.Contents); continue; }
                var entity = item as Entity;
                if (entity != null && depth < 2)
                {
                    var nested = new DBObjectCollection();
                    try { entity.Explode(nested); CollectText(nested, output, depth + 1); }
                    catch { }
                    finally { foreach (DBObject child in nested) child.Dispose(); }
                }
            }
        }

        private static string TryReadComProperty(object instance, string name)
        {
            try
            {
                var value = instance.GetType().InvokeMember(name, BindingFlags.GetProperty, null, instance, null, CultureInfo.CurrentCulture);
                if (value == null) return null;
                var text = Convert.ToString(value, CultureInfo.CurrentCulture);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch { return null; }
        }

        /// <summary>尝试只读调用候选方法（无参数或带空字符串参数），仅探测可调用性，不修改对象。</summary>
        private static string TryInvokeComMethod(object instance, string name)
        {
            foreach (var arguments in new object[][] { new object[0], new object[] { string.Empty } })
            {
                try
                {
                    var value = instance.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, instance, arguments, CultureInfo.CurrentCulture);
                    if (value == null) return "null";
                    var text = Convert.ToString(value, CultureInfo.CurrentCulture);
                    return string.IsNullOrWhiteSpace(text) ? "（空）" : text;
                }
                catch { }
            }
            return null;
        }

        private static string SafeHandle(DBObject value) { try { return value.Handle.ToString(); } catch { return "未知"; } }
        private static string DxfName(DBObject value) { try { return (value == null ? string.Empty : value.GetRXClass().DxfName ?? string.Empty).ToUpperInvariant(); } catch { return string.Empty; } }
        private static string FormatPoint(Autodesk.AutoCAD.Geometry.Point3d point) { return point.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + point.Y.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string Truncate(string value) { return value.Length <= 200 ? value : value.Substring(0, 200) + "…"; }
    }
}
