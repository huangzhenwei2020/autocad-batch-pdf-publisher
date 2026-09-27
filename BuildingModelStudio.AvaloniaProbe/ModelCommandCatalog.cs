using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal enum ModelCommandKind
{ Unknown, Wall, Move, Copy, Mirror, Fillet, Polar, Ortho, Select, GizmoMove, Rotate }

internal static class ModelCommandCatalog
{
    internal static ModelCommandKind Resolve(string? input)
    {
        var command = input?.Trim().ToUpperInvariant();
        return command switch
        {
            "WA" or "WALL" or "画墙" => ModelCommandKind.Wall,
            "M" or "MOVE" or "移动" => ModelCommandKind.Move,
            "CO" or "COPY" or "复制" => ModelCommandKind.Copy,
            "MI" or "MIRROR" or "镜像" => ModelCommandKind.Mirror,
            "F" or "FILLET" or "圆角" => ModelCommandKind.Fillet,
            "POLAR" or "极轴" => ModelCommandKind.Polar,
            "ORTHO" or "正交" => ModelCommandKind.Ortho,
            "Q" or "SELECT" or "选择" => ModelCommandKind.Select,
            "W" => ModelCommandKind.GizmoMove,
            "E" => ModelCommandKind.Rotate,
            _ => ModelCommandKind.Unknown
        };
    }

    internal static bool TryDisplacement(string? input, out double x, out double y)
    {
        x = y = 0;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text[0] == '@') text = text.Substring(1).Trim();
        var parts = text.Split(',');
        return parts.Length == 2
            && double.TryParse(parts[0].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out x)
            && double.TryParse(parts[1].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out y)
            && double.IsFinite(x) && double.IsFinite(y);
    }

    internal static bool TryLength(string? input, out double length)
        => double.TryParse(input?.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out length)
            && double.IsFinite(length) && length >= 10d;

    internal static bool TryPolarLength(string? input, out double length, out double angleDegrees)
    {
        length = angleDegrees = 0;
        var text = input?.Trim().TrimStart('@');
        var parts = text?.Split('<');
        return parts?.Length == 2 && TryLength(parts[0], out length)
            && double.TryParse(parts[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out angleDegrees)
            && double.IsFinite(angleDegrees);
    }
}
