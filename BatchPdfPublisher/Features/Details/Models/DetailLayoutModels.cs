using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using System.Collections.Generic;

namespace BatchPdfPublisher.Models
{
    internal sealed class DetailLayoutItem
    {
        public string Name;
        public string ScaleText;
        public bool AddIndexNumber;
        public bool IsCachedPlan;
        public string CacheRelativePath;
        public double CacheLayoutOffsetX;
        public double CacheLayoutOffsetY;
        public readonly List<ObjectId> ObjectIds = new List<ObjectId>();
        public readonly List<DetailPreviewPrimitive> Preview = new List<DetailPreviewPrimitive>();
        public Point3d MinPoint;
        public Point3d MaxPoint;
        public double Width { get { return MaxPoint.X - MinPoint.X; } }
        public double Height { get { return MaxPoint.Y - MinPoint.Y; } }
        public override string ToString()
        {
            return (IsCachedPlan ? "[小平面] " : string.Empty) + Name + "  "
                + (string.IsNullOrWhiteSpace(ScaleText) ? "比例未识别" : ScaleText);
        }
    }

    internal enum DetailPreviewPrimitiveKind { Line, Ellipse, Text, Box }
    internal sealed class DetailPreviewPrimitive
    {
        public DetailPreviewPrimitiveKind Kind;
        public double X1, Y1, X2, Y2;
        public string Text;
    }

    internal sealed class DetailLayoutOptions
    {
        public bool HasExplicitRange;
        public double LeftMargin = 40d;
        public double RightMargin = 80d;
        public double TopMargin = 20d;
        public double BottomMargin = 20d;
        public double ItemGap = 5d;
        public double PageGap = 30d;
        public bool DeleteSources;
    }

    internal sealed class DetailLayoutSlot
    {
        public DetailLayoutItem Item;
        public int Page;
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public double CellX;
        public double CellY;
        public double CellWidth;
        public double CellHeight;
    }

    internal sealed class DetailLayoutPlan
    {
        public FrameDefinition Frame;
        public int Scale;
        public int PageCount;
        public double PageWidth;
        public double PageHeight;
        public double ContentLeft;
        public double ContentRight;
        public double ContentBottom;
        public double ContentTop;
        public int Columns;
        public int Rows;
        public readonly List<double> ColumnWidths = new List<double>();
        public readonly List<double> RowHeights = new List<double>();
        public readonly List<DetailLayoutSlot> Slots = new List<DetailLayoutSlot>();
    }

    internal sealed class DetailLayoutFrameAnchor
    {
        public ObjectId ReferenceId;
        public Point3d Origin;
        public string FrameRegistrationId;
        public string FrameBlockName;
        public int Scale;
    }
}
