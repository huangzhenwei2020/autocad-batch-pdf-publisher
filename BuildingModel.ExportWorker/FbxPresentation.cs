using System.Text;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.BuildingModel.Export;

internal sealed class FbxPresentation
{
    internal sealed record Floor(string StoreyId, string LayerName, string GroupName);
    internal sealed record Element(string Id, string StoreyId, string NodeName);
    internal string BuildingName { get; private set; } = "";
    internal List<Floor> Floors { get; } = new();
    internal List<Element> Elements { get; } = new();

    internal static FbxPresentation For(BuildingExportScene scene)
    {
        var result = new FbxPresentation();
        var nodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Root"] = 2 };
        var layers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["0"] = 2 };
        result.BuildingName = Unique(nodes, Clean(scene.Name, "建筑模型"));
        foreach (var storey in scene.Storeys)
        {
            var layer = Unique(layers, Clean(storey.Name, "楼层 " + (result.Floors.Count + 1).ToString("D2")));
            result.Floors.Add(new(storey.Id, layer, Unique(nodes, layer + " 构件组")));
        }
        var floorNames = result.Floors.ToDictionary(f => f.StoreyId, f => f.LayerName, StringComparer.Ordinal);
        foreach (var element in scene.Instances)
        {
            var label = element.Name == element.Id || element.Name == element.SourceId ? null : element.Name;
            result.Elements.Add(new(element.Id, element.StoreyId,
                Unique(nodes, floorNames[element.StoreyId] + " " + Clean(label, "构件"))));
        }
        return result;
    }

    private static string Clean(string? name, string fallback)
    {
        var text = new string((name ?? "").Normalize(NormalizationForm.FormC).Where(c => !char.IsControl(c)).ToArray()).Trim();
        text = text.Replace("$AssimpFbx$", "_", StringComparison.Ordinal);
        if (text.Length == 0) text = fallback;
        // Leave space for short duplicate suffixes; never truncate a surrogate pair.
        while (Encoding.UTF8.GetByteCount(text) > 180)
            text = text[..(char.IsLowSurrogate(text[^1]) ? text.Length - 2 : text.Length - 1)];
        return text;
    }

    private static string Unique(Dictionary<string, int> used, string label)
    {
        if (used.TryAdd(label, 2)) return label;
        for (var suffix = used[label]; ; suffix++)
        {
            var candidate = label + " " + suffix.ToString("D2");
            if (!used.TryAdd(candidate, 2)) continue;
            used[label] = suffix + 1;
            return candidate;
        }
    }
}
