using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Assimp;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.BuildingModel.Export;

internal static class FbxDisplayLayerTests
{
    internal static void Run()
    {
        void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var before = BuildingModelJson.ToJson(model);
        var source = BuildingExportSceneBuilder.Build(model);
        var names = FbxPresentation.For(source);
        Check(names.Floors.Count == 2 && names.Elements.Count == source.Instances.Count, "Presentation lost identities.");
        Check(names.Elements.All(e => !e.NodeName.Contains(e.Id, StringComparison.Ordinal)), "Object name contains internal identity.");
        var folder = Path.GetFullPath(".artifacts/fbx-layers-tests/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var native = Path.Combine(folder, "native.fbx");
        var output = (Scene)typeof(BuildingFbxWriter).GetMethod("CreateScene", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { source, CancellationToken.None, new List<string>() })!;
        using (var context = new AssimpContext())
            Check(context.ExportFile(output, native, "fbx", PostProcessSteps.JoinIdenticalVertices), "Native fixture export failed.");
        var document = FbxBinaryDocument.Read(native, default);
        var copy = Path.Combine(folder, "copy.fbx"); document.Write(copy, default);
        Check(File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(native)), "Opaque FBX roundtrip changed payloads.");
        string Geometry(FbxBinaryDocument doc)
        {
            using var data = new MemoryStream();
            void Write(FbxBinaryDocument.Node n)
            {
                foreach (var p in n.Properties) data.Write(p.Bytes);
                foreach (var c in n.Children) Write(c);
            }
            foreach (var g in doc.Nodes.Single(n => n.Name == "Objects").Children.Where(n => n.Name == "Geometry")) Write(g);
            return Convert.ToHexString(SHA256.HashData(data.ToArray()));
        }
        var geometry = Geometry(document);
        FbxDisplayLayers.Apply(native, source, output, default);
        Check(Geometry(FbxBinaryDocument.Read(native, default)) == geometry, "Floor layers changed geometry payloads.");
        Check(FbxDisplayLayers.Validate(native, source, default) == 2, "Missing native layers.");
        var hash = SHA256.HashData(File.ReadAllBytes(native));
        var result = BuildingFbxWriter.Validate(source, native);
        Check(result.DisplayLayerCount == 2 && result.PreservesIdentityMetadata && result.PreservesOriginMetadata,
            "Native readback lost layers/custom properties.");
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(native))), "Validation changed the file.");
        var altered = FbxBinaryDocument.Read(native, default);
        var invalidNamespace = altered.Nodes.Single(n => n.Name == "Objects").Children.First(n => n.Name == "CollectionExclusive");
        invalidNamespace.Properties[1] = FbxBinaryDocument.Property.Of(invalidNamespace.ObjectName + "\0\u001displayLayer");
        var badNamespace = Path.Combine(folder, "bad-namespace.fbx"); altered.Write(badNamespace, default);
        try { FbxDisplayLayers.Validate(badNamespace, source, default); throw new Exception("Wrong FBX layer namespace accepted."); }
        catch (InvalidDataException) { }
        altered = FbxBinaryDocument.Read(native, default);
        altered.Nodes.Single(n => n.Name == "Connections").Children.RemoveAll(n => n.Name == "C"
            && Equals(n.Properties[^1].Value, altered.Nodes.Single(n => n.Name == "Objects").Children.First(n => n.Name == "CollectionExclusive").Id));
        var broken = Path.Combine(folder, "broken-members.fbx"); altered.Write(broken, default);
        try { FbxDisplayLayers.Validate(broken, source, default); throw new Exception("Missing layer membership accepted."); }
        catch (InvalidDataException) { }
        var version = File.ReadAllBytes(native); BitConverter.GetBytes(7400).CopyTo(version, 23);
        var wrongVersion = Path.Combine(folder, "wrong-version.fbx"); File.WriteAllBytes(wrongVersion, version);
        try { FbxBinaryDocument.Read(wrongVersion, default); throw new Exception("Wrong FBX version accepted."); }
        catch (InvalidDataException) { }
        var truncated = Path.Combine(folder, "truncated.fbx"); File.WriteAllBytes(truncated, version[..70]);
        try { FbxBinaryDocument.Read(truncated, default); throw new Exception("Truncated FBX accepted."); }
        catch (InvalidDataException) { }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { FbxDisplayLayers.Validate(native, source, cancel.Token); throw new Exception("Layer validation ignored cancellation."); }
        catch (OperationCanceledException) { }
        source.Name = "Root";
        source.Storeys[0].Name = "0"; source.Storeys[1].Name = "0";
        foreach (var i in source.Instances) i.Name = i.Id;
        var awkward = FbxPresentation.For(source);
        Check(awkward.Floors.All(f => f.LayerName != "0") && awkward.Floors.Select(f => f.LayerName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Reserved/duplicate layer names not resolved.");
        Check(awkward.BuildingName != "Root" && awkward.Elements.Select(e => e.NodeName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == source.Instances.Count,
            "Duplicate readable object names not resolved.");
        Check(awkward.Elements.All(e => !e.NodeName.Contains(e.Id, StringComparison.Ordinal)), "Fallback leaks IDs.");
        Check(before == BuildingModelJson.ToJson(model), "Display names mutated source model.");
        Console.WriteLine("FBX_DISPLAY_LAYERS_OK nativeBinary metadata members geometryBytesUnchanged opaqueRoundtrip duplicateNames reservedNames invalidInput cancellation " + folder);
    }
}
