using Assimp;
using BatchPdfPublisher.BuildingModel;
using N = BatchPdfPublisher.BuildingModel.Export.FbxBinaryDocument.Node;
using P = BatchPdfPublisher.BuildingModel.Export.FbxBinaryDocument.Property;

namespace BatchPdfPublisher.BuildingModel.Export;

internal static class FbxDisplayLayers
{
    internal static void Apply(string path, BuildingExportScene source, Scene expected, CancellationToken cancellation)
    {
        var document = FbxBinaryDocument.Read(path, cancellation);
        var objects = document.Nodes.Single(n => n.Name == "Objects");
        var connections = document.Nodes.Single(n => n.Name == "Connections");
        var models = objects.Children.Where(n => n.Name == "Model").ToDictionary(n => n.ObjectName, StringComparer.Ordinal);
        IEnumerable<Node> Walk(Node node) { yield return node; foreach (var child in node.Children.SelectMany(Walk)) yield return child; }
        foreach (var node in Walk(expected.RootNode).Skip(1))
        {
            var model = models[node.Name];
            var properties = model.Children.SingleOrDefault(n => n.Name == "Properties70");
            if (properties == null) { properties = new N("Properties70"); model.Children.Add(properties); }
            foreach (var property in node.Metadata)
            {
                properties.Children.RemoveAll(n => n.Name == "P" && Equals(n.Properties[0].Value, property.Key));
                properties.Children.Add(property.Value.Data is string text
                    ? new N("P", property.Key, "KString", "", "U", text)
                    : new N("P", property.Key, "double", "Number", "U", Convert.ToDouble(property.Value.Data, System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        if (objects.Children.Any(n => n.Name == "CollectionExclusive")) throw new InvalidDataException("Unexpected existing FBX display layers.");
        var names = FbxPresentation.For(source);
        var next = objects.Children.Max(n => n.Id);
        foreach (var floor in names.Floors)
        {
            var id = checked(++next);
            var layer = new N("CollectionExclusive", id, floor.LayerName + "\0\u0001DisplayLayer", "DisplayLayer");
            var settings = new N("Properties70");
            settings.Children.AddRange(new[] { new N("P", "Color", "ColorRGB", "Color", "", .65d, .7d, .75d),
                new N("P", "Show", "bool", "", "", 1), new N("P", "Freeze", "bool", "", "", 0), new N("P", "LODBox", "bool", "", "", 0),
                new N("P", "WL_StoreyId", "KString", "", "U", floor.StoreyId) });
            layer.Children.Add(settings); objects.Children.Add(layer);
            foreach (var member in names.Elements.Where(e => e.StoreyId == floor.StoreyId).Select(e => e.NodeName).Prepend(floor.GroupName))
                connections.Children.Add(new N("C", "OO", models[member].Id, id));
        }
        var definitions = document.Nodes.Single(n => n.Name == "Definitions");
        var count = definitions.One("Count"); count.Properties[0] = P.Of(checked((int)count.Properties[0].Value! + names.Floors.Count));
        var definition = new N("ObjectType", "CollectionExclusive"); definition.Children.Add(new N("Count", names.Floors.Count));
        var template = new N("PropertyTemplate", "FbxDisplayLayer");
        var defaults = new N("Properties70"); defaults.Children.AddRange(new[] {
            new N("P", "Color", "ColorRGB", "Color", "", .8d, .8d, .8d),new N("P", "Show", "bool", "", "", 1),
            new N("P", "Freeze", "bool", "", "", 0),new N("P", "LODBox", "bool", "", "", 0) });
        template.Children.Add(defaults); definition.Children.Add(template); definitions.Children.Add(definition);
        var updated = path + ".presentation";
        try
        {
            document.Write(updated, cancellation); Validate(updated, source, cancellation);
            File.Move(updated, path, true);
        }
        finally { if (File.Exists(updated)) File.Delete(updated); }
    }

    internal static int Validate(string path, BuildingExportScene source, CancellationToken cancellation)
    {
        var document = FbxBinaryDocument.Read(path, cancellation);
        var objects = document.Nodes.Single(n => n.Name == "Objects").Children;
        var layers = objects.Where(n => n.Name == "CollectionExclusive").ToArray();
        var models = objects.Where(n => n.Name == "Model").ToDictionary(n => n.ObjectName, StringComparer.Ordinal);
        var connections = document.Nodes.Single(n => n.Name == "Connections").Children.Where(n => n.Name == "C").ToArray();
        var names = FbxPresentation.For(source);
        if (layers.Length != names.Floors.Count) throw new InvalidDataException("FBX storey layer count changed.");
        foreach (var floor in names.Floors)
        {
            var layer = layers.Single(n => n.ObjectName == floor.LayerName);
            var properties = layer.One("Properties70").Children.ToDictionary(p => (string)p.Properties[0].Value!, StringComparer.Ordinal);
            if (!Equals(layer.Properties[2].Value, "DisplayLayer")
                || !Equals(layer.Properties[1].Value, floor.LayerName + "\0\u0001DisplayLayer")
                || !Equals(properties["Show"].Properties[4].Value, 1)
                || !Equals(properties["Freeze"].Properties[4].Value, 0)
                || !Equals(properties["WL_StoreyId"].Properties[4].Value, floor.StoreyId))
                throw new InvalidDataException("FBX storey layer settings changed.");
            var members = names.Elements.Where(e => e.StoreyId == floor.StoreyId).Select(e => models[e.NodeName].Id).Append(models[floor.GroupName].Id).ToHashSet();
            var actual = connections.Where(n => n.Properties.Count == 3 && Equals(n.Properties[0].Value, "OO") && Equals(n.Properties[2].Value, layer.Id))
                .Select(n => (long)n.Properties[1].Value!).ToArray();
            if (actual.Length != members.Count || !members.SetEquals(actual)) throw new InvalidDataException("FBX storey layer members changed.");
        }
        foreach (var element in names.Elements)
        {
            var properties = models[element.NodeName].One("Properties70").Children;
            if (!properties.Any(p => Equals(p.Properties[0].Value, "WL_ElementId") && Equals(p.Properties[4].Value, element.Id)))
                throw new InvalidDataException("FBX hidden element identity is missing.");
        }
        return layers.Length;
    }
}
