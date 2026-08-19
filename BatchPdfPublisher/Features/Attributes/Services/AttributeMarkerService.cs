using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace BatchPdfPublisher.Services
{
    public sealed class AttributeMarkerService : IDisposable
    {
        private readonly List<Entity> _drawables = new List<Entity>();
        private readonly IntegerCollection _viewports = new IntegerCollection();

        public void ShowCurrent(Document document, AttributeTarget target)
        {
            Clear();
            if (document == null || target == null) return;
            var width = Math.Max(target.MaxPoint.X - target.MinPoint.X, 1d);
            var height = Math.Max(target.MaxPoint.Y - target.MinPoint.Y, 1d);
            var textHeight = Math.Max(2.5d, Math.Min(width, height) * 0.09d);
            var point = target.AttributePosition;
            var label = new MText
            {
                Contents = (string.IsNullOrWhiteSpace(target.Tag) ? "当前属性" : target.Tag) + "：" + (target.NewValue ?? target.OldValue ?? string.Empty),
                Location = new Point3d(point.X + textHeight * 0.7d, point.Y + textHeight * 0.7d, point.Z),
                Attachment = AttachmentPoint.BottomLeft,
                TextHeight = textHeight,
                ColorIndex = 1,
                BackgroundFill = true,
                UseBackgroundColor = true
            };
            Add(label);
            var circle = new Circle(point, Vector3d.ZAxis, textHeight * 0.75d) { ColorIndex = 1, LineWeight = LineWeight.LineWeight100 };
            Add(circle);
            document.Editor.UpdateScreen();
        }

        public void ShowOrder(Document document, IEnumerable<AttributeTarget> targets)
        {
            Clear();
            if (document == null || targets == null) return;
            var list = new List<AttributeTarget>(targets);
            if (list.Count > 500)
                throw new InvalidOperationException("一次最多显示 500 个临时序号。请减少勾选数量后再试，避免影响 CAD 显示性能。");
            var order = 1;
            foreach (var target in list)
            {
                var width = Math.Max(target.MaxPoint.X - target.MinPoint.X, 1d);
                var height = Math.Max(target.MaxPoint.Y - target.MinPoint.Y, 1d);
                Add(new MText { Contents = order++.ToString(), Location = target.Center, Attachment = AttachmentPoint.MiddleCenter, TextHeight = Math.Max(2d, Math.Min(width, height) * 0.12d), ColorIndex = 1, BackgroundFill = true, UseBackgroundColor = true });
            }
            document.Editor.UpdateScreen();
        }

        public void Clear()
        {
            foreach (var drawable in _drawables) { try { TransientManager.CurrentTransientManager.EraseTransient(drawable, _viewports); } catch { } drawable.Dispose(); }
            _drawables.Clear();
        }

        private void Add(Entity entity)
        {
            try { TransientManager.CurrentTransientManager.AddTransient(entity, TransientDrawingMode.DirectShortTerm, 129, _viewports); _drawables.Add(entity); }
            catch { entity.Dispose(); }
        }

        public void Dispose() { Clear(); }
    }
}
