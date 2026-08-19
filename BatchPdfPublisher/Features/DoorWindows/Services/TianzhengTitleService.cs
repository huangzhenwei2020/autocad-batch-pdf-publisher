using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using System;
using System.Globalization;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 天正图名标注（TCH_DRAWINGNAME）适配。
    /// 探针结论（2026-08-17，用户真实天正环境）：
    /// - DXF 名 TCH_DRAWINGNAME，ImpEntity，图层 DIM_SYMB；
    /// - COM 可读：Scale、ObjectName/EntityName=TDbDrawingName、Layer、Color、Visible；
    /// - 天正"更新比例"功能证明 Scale/DrawScale/ScaleText 可通过反射 SetProperty 写入
    ///   （见 TianzhengScaleService.Apply，实际使用验证）；
    /// - 爆炸后两段文字：图名 + 比例。
    /// 插入实现：优先克隆图纸中已有的 TCH_DRAWINGNAME 作为模板 → 移动到位 →
    /// 用更新比例已验证的反射 SetProperty 写法写入比例（Scale/ScaleText）并尝试写
    /// 图名文字候选属性；无模板或写入失败时回退"天正图名样式"（图名+粗下划线+比例）。
    /// 注意：任何 COM 交互都只用反射 InvokeMember（经更新比例验证），不做
    /// GetIdsOfNames/ITypeInfo 等未经验证的 IDispatch 调用，避免 CAD 崩溃。
    /// </summary>
    internal static class TianzhengTitleService
    {
        /// <summary>天正图名对象 DXF 名称（探针确认）。</summary>
        public const string DrawingNameDxfName = "TCH_DRAWINGNAME";
        /// <summary>天正图名对象 COM ObjectName（探针确认）。</summary>
        public const string DrawingNameComName = "TDbDrawingName";
        /// <summary>图名文字候选可写属性名（按可能性排序，逐个尝试，写失败静默跳过）。</summary>
        private static readonly string[] TitleTextCandidates = { "TitleText", "NameText", "TextString", "FigName", "FigNameText", "Title", "Name", "Text", "DrawingName" };

        /// <summary>判断实体是否为天正图名标注对象（DXF 名或 COM 对象名匹配）。</summary>
        public static bool IsDrawingName(Entity entity)
        {
            if (entity == null) return false;
            try { if (string.Equals(entity.GetRXClass().DxfName, DrawingNameDxfName, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            try
            {
                var com = entity.AcadObject;
                if (com != null)
                {
                    var objectName = ReadComString(com, "ObjectName");
                    if (string.Equals(objectName, DrawingNameComName, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        private static string ReadComString(object instance, string name)
        {
            try
            {
                var value = instance.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, instance, null, CultureInfo.InvariantCulture);
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch { return string.Empty; }
        }

        /// <summary>在图名中心点插入天正图名样式：图名文字 + 下方粗下划线 + 比例文字。</summary>
        public static void InsertTitle(BlockTableRecord space, Transaction transaction, Point3d center, string title, int drawingScale, ObjectId titleStyle, ObjectId noteStyle, ObjectId layer, DoorWindowElevationMetadata metadata = null)
        {
            if (space == null || transaction == null) return;
            var scale = Math.Max(1, drawingScale);
            var titleHeight = Math.Max(3.5d * scale, 70d);
            var noteHeight = Math.Max(2.5d * scale, 50d);
            var titleText = string.IsNullOrWhiteSpace(title) ? "未编号" : title;

            // 图名文字（居中）。
            var titleMText = new MText
            {
                Contents = titleText,
                Location = center,
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = titleHeight,
                TextStyleId = titleStyle,
                LayerId = layer
            };
            Append(space, transaction, titleMText, metadata);

            // 粗下划线：宽度按图名文字估算（全角≈1.0 字高、半角≈0.55 字高），两端各加一点出头。
            // 用 Polyline + ConstWidth（几何宽度，模型单位）绘制：不受 CAD 线宽显示开关
            // (LWDISPLAY) 影响，屏幕上始终显示为粗线，与天正图名标注的粗下划线一致。
            var lineLength = EstimateTextWidth(titleText, titleHeight) + titleHeight * 0.8d;
            var underlineY = center.Y - titleHeight * 0.72d;
            var underline = new Polyline(2)
            {
                LayerId = layer,
                ColorIndex = 0,
                ConstantWidth = Math.Max(0.5d * scale, 10d)
            };
            underline.AddVertexAt(0, new Point2d(center.X - lineLength / 2d, underlineY), 0d, 0d, 0d);
            underline.AddVertexAt(1, new Point2d(center.X + lineLength / 2d, underlineY), 0d, 0d, 0d);
            Append(space, transaction, underline, metadata);

            // 比例文字（下划线下方，字高约为图名的 70%）。
            var noteText = "1:" + scale.ToString(CultureInfo.InvariantCulture);
            var noteMText = new MText
            {
                Contents = noteText,
                Location = new Point3d(center.X, underlineY - titleHeight * 0.62d, center.Z),
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = noteHeight,
                TextStyleId = noteStyle,
                LayerId = layer
            };
            Append(space, transaction, noteMText, metadata);
        }

        private static void Append(BlockTableRecord space, Transaction transaction, Entity entity, DoorWindowElevationMetadata metadata)
        {
            space.AppendEntity(entity); transaction.AddNewlyCreatedDBObject(entity, true);
            if (metadata != null) DoorWindowElevationMetadataService.Attach(entity, metadata);
        }

        /// <summary>估算字符串显示宽度（模型单位）：全角字符按 1.0 字高、ASCII 按 0.55 字高。</summary>
        private static double EstimateTextWidth(string text, double height)
        {
            if (string.IsNullOrEmpty(text)) return height;
            var width = 0d;
            foreach (var ch in text)
            {
                var code = (int)ch;
                width += code > 0x2E7F || ch == '　' ? height : height * 0.55d;
            }
            return width;
        }

        /// <summary>
        /// 尝试插入真正的天正图名标注对象：克隆图纸中已有的 TCH_DRAWINGNAME 模板 → 按目标宽度
        /// 等比缩放（避免模板图名比门窗宽造成重叠）→ 移到目标点 → 用更新比例已验证的反射
        /// SetProperty 写入比例（Scale/ScaleText/DrawScale）并尝试写图名文字。
        /// 全部步骤 try/catch，任何失败都返回 false，由调用方回退"天正图名样式"。
        /// </summary>
        public static bool TryInsertNativeTitle(Database database, BlockTableRecord space, Transaction transaction, Point3d center, string title, int drawingScale, double targetWidth, DoorWindowElevationMetadata metadata = null)
        {
            if (database == null || space == null || transaction == null) return false;
            try
            {
                var template = FindDrawingNameTemplate(database, transaction);
                if (template == null) return false;
                var clone = (Entity)template.Clone();
                if (clone == null) return false;
                // 模板几何中心与尺寸。
                Point3d templateCenter;
                double templateWidth;
                try
                {
                    var extents = template.GeometricExtents;
                    templateCenter = new Point3d((extents.MinPoint.X + extents.MaxPoint.X) / 2d, (extents.MinPoint.Y + extents.MaxPoint.Y) / 2d, extents.MinPoint.Z);
                    templateWidth = Math.Max(1d, Math.Abs(extents.MaxPoint.X - extents.MinPoint.X));
                }
                catch { templateCenter = center; templateWidth = 1d; }
                // 等比缩放：目标宽度 = 门窗宽度的 80%（模型单位），模板超宽时缩小，避免与相邻门窗打架。
                var target = Math.Max(10d, targetWidth * 0.8d);
                var factor = templateWidth > target ? target / templateWidth : 1d;
                if (factor < 1d && factor > 0.01d)
                    clone.TransformBy(Matrix3d.Scaling(factor, templateCenter));
                clone.TransformBy(Matrix3d.Displacement(center - templateCenter));
                clone.SetDatabaseDefaults();
                Append(space, transaction, clone, metadata);
                var com = clone.AcadObject;
                if (com != null)
                {
                    // 比例（更新比例功能已验证可写）。
                    TrySetCom(com, "Scale", Convert.ToDouble(drawingScale, CultureInfo.InvariantCulture));
                    TrySetCom(com, "DrawScale", true);
                    TrySetCom(com, "ScaleText", "1:" + drawingScale.ToString(CultureInfo.InvariantCulture));
                    // 图名文字：逐个尝试候选属性，写入成功即停。
                    foreach (var property in TitleTextCandidates)
                        if (TrySetCom(com, property, title ?? string.Empty)) break;
                    try { com.GetType().InvokeMember("Update", System.Reflection.BindingFlags.InvokeMethod, null, com, null, CultureInfo.CurrentCulture); } catch { }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>在当前图纸模型空间查找第一个天正图名标注对象作为克隆模板。</summary>
        private static Entity FindDrawingNameTemplate(Database database, Transaction transaction)
        {
            var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false);
            if (space == null) return null;
            foreach (ObjectId id in space)
            {
                var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity != null && IsDrawingName(entity)) return entity;
            }
            return null;
        }

        /// <summary>用反射 SetProperty 写入 COM 属性（更新比例功能 TianzhengScaleService 已验证的写法），失败返回 false。</summary>
        private static bool TrySetCom(object instance, string name, object value)
        {
            if (instance == null) return false;
            try { instance.GetType().InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, instance, new[] { value }, CultureInfo.CurrentCulture); return true; }
            catch { return false; }
        }
    }
}
