using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace BatchPdfPublisher.BuildingModel.Export;

/// <summary>
/// Static SKP output, called synchronously on the isolated worker's main thread.
/// Uses only the explicitly selected local installation, never a bundled native SDK.
/// This does not grant SDK redistribution rights; standalone distribution needs SDK licensing.
/// </summary>
internal static class BuildingSkpWriter
{
    private const double MillimetresPerInch = 25.4;
    private static readonly double CurveCosine = Math.Cos(5.1 * Math.PI / 180);

    // Signatures/layouts verified against the official public C API reference:
    // https://extensions.sketchup.com/developers/sketchup_c_api/sketchup/index.html
    // See struct_s_u_{geometry_input_ref,loop_input_ref,material_input,point3_d,
    // transformation,model_ref,entities_ref,component_definition_ref,group_ref}.html.
    // initialize_8h.html: SUGetAPIVersion returns void and two size_t outputs.
    // md__sketch_up__c__a_p_i__release__notes.html: SketchUp 2026.0 = API 14.0.
    internal static void Write(BuildingExportScene scene, string path, string apiDirectory,
        CancellationToken cancellation = default)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(scene);
        path = OutputPath(path);
        if (File.Exists(path)) throw new IOException("SKP output must be a new staged file.");
        cancellation.ThrowIfCancellationRequested();
        var meshes = Prepare(scene, cancellation);
        WithApi(apiDirectory, api =>
        {
            var model = api.Create("SUModelCreate", "SUModelRelease");
            var units = api.Units(model);
            api.SetUnitOption(units, "LengthFormat", 0);
            api.SetUnitOption(units, "LengthUnit", 2);
            api.Check(api.Function<SetText>("SUModelSetName")(model, Text(scene.Name, "Building")));
            var dictionary = api.Dictionary(model, true);
            api.Attribute(dictionary, "SourceUnits", "mm");
            api.Attribute(dictionary, "OriginMillimetres", string.Join(",", scene.OriginMillimetres
                .Select(value => value.ToString("R", CultureInfo.InvariantCulture))));
            var root = api.Child("SUModelGetEntities", model);
            var materials = new SuRef[scene.Materials.Count];
            for (var i = 0; i < materials.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var input = scene.Materials[i];
                var material = api.Create("SUMaterialCreate", "SUMaterialRelease");
                // A deterministic prefix also makes repeated source material names safe.
                api.Check(api.Function<SetText>("SUMaterialSetName")(material,
                    $"{i:D4} {Text(input.Name, "Material")}"));
                var color = new SuColor { Red = Channel(input.Red), Green = Channel(input.Green),
                    Blue = Channel(input.Blue), Alpha = 255 };
                api.Check(api.Function<SetColor>("SUMaterialSetColor")(material, ref color));
                api.Check(api.Function<SetDouble>("SUMaterialSetOpacity")(material, input.Opacity));
                api.Check(api.Function<SetBool>("SUMaterialSetUseOpacity")(material, input.Opacity < 1));
                api.AddOwned("SUModelAddMaterials", model, material);
                materials[i] = material;
            }

            var definitions = new SuRef[meshes.Length];
            for (var i = 0; i < meshes.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var definition = api.Create("SUComponentDefinitionCreate", "SUComponentDefinitionRelease");
                // Definitions must belong to the model before their entities are populated.
                api.AddOwned("SUModelAddComponentDefinitions", model, definition);
                api.Check(api.Function<SetText>("SUComponentDefinitionSetName")(definition,
                    $"{i:D6} {Text(scene.Meshes[i].Id, "Mesh")}"));
                api.Attribute(api.Dictionary(api.Convert("SUComponentDefinitionToEntity", definition)),
                    "MeshId", scene.Meshes[i].Id);
                Fill(api, api.Child("SUComponentDefinitionGetEntities", definition), meshes[i], materials, cancellation);
                definitions[i] = definition;
            }

            var floors = new Dictionary<string, SuRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var storey in scene.Storeys)
            {
                cancellation.ThrowIfCancellationRequested();
                var group = api.CreateGroup();
                api.Check(api.Function<AddRef>("SUEntitiesAddGroup")(root, group));
                api.Adopt(group);
                api.Check(api.Function<SetText>("SUGroupSetName")(group, Text(storey.Name, storey.Id ?? "Storey")));
                var attributes = api.Dictionary(api.Convert("SUGroupToEntity", group));
                api.Attribute(attributes, "StoreyId", storey.Id);
                api.Attribute(attributes, "ElevationMillimetres", storey.Elevation.ToString("R", CultureInfo.InvariantCulture));
                floors.Add(storey.Id ?? "", api.Child("SUGroupGetEntities", group));
            }
            foreach (var input in scene.Instances)
            {
                cancellation.ThrowIfCancellationRequested();
                var instance = api.CreateChild("SUComponentDefinitionCreateInstance", definitions[input.MeshIndex],
                    "SUComponentInstanceRelease");
                var transform = SuTransform.Translation(input.Translation);
                api.Check(api.Function<SetTransform>("SUComponentInstanceSetTransform")(instance, ref transform));
                api.Check(api.Function<SetText>("SUComponentInstanceSetName")(instance, Text(input.Name, input.Id ?? "Element")));
                api.Check(api.Function<AddInstance>("SUEntitiesAddInstance")(floors[input.StoreyId ?? ""], instance, IntPtr.Zero));
                api.Adopt(instance);
                var attributes = api.Dictionary(api.Convert("SUComponentInstanceToEntity", instance));
                api.Attribute(attributes, "Id", input.Id);
                api.Attribute(attributes, "SourceId", input.SourceId);
                api.Attribute(attributes, "StoreyId", input.StoreyId);
                api.Attribute(attributes, "Kind", input.Kind);
            }
            cancellation.ThrowIfCancellationRequested();
            api.Check(api.Function<SetText>("SUModelSaveToFile")(model, path));
            api.Release(model);
            cancellation.ThrowIfCancellationRequested();
            var result = ReadBack(api, scene, meshes, path, cancellation);
            Console.WriteLine(FormattableString.Invariant(
                $"SKP_READBACK_OK storeys={result.Storeys} instances={result.Instances} definitions={result.Definitions} faces={result.Faces} edges={result.Edges} boundsMm=[{string.Join(",", result.BoundsMillimetres.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}]"));
        });
    }

    internal sealed record ValidationResult(int Storeys, int Instances, int Definitions,
        long Faces, long Edges, double[] BoundsMillimetres);

    internal static ValidationResult Validate(BuildingExportScene scene, string path, string apiDirectory,
        CancellationToken cancellation = default)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(scene);
        path = OutputPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("SKP file does not exist.", path);
        cancellation.ThrowIfCancellationRequested();
        var meshes = Prepare(scene, cancellation);
        ValidationResult? result = null;
        WithApi(apiDirectory, api => result = ReadBack(api, scene, meshes, path, cancellation));
        return result!;
    }

    private static void Fill(NativeApi api, SuRef entities, MeshData mesh, SuRef[] materials,
        CancellationToken cancellation)
    {
        var geometry = api.Create("SUGeometryInputCreate", "SUGeometryInputRelease");
        api.Check(api.Function<SetVertices>("SUGeometryInputSetVertices")(geometry,
            (nuint)mesh.Points.Length, mesh.Points));
        foreach (var triangle in mesh.Triangles)
        {
            cancellation.ThrowIfCancellationRequested();
            var loop = api.Create("SULoopInputCreate", "SULoopInputRelease");
            for (var i = 0; i < 3; i++)
                api.Check(api.Function<AddVertex>("SULoopInputAddVertexIndex")(loop, (nuint)triangle.Indices[i]));
            for (var i = 0; i < 3; i++)
            {
                var edge = mesh.Edges[EdgeKey(triangle.Indices[i], triangle.Indices[(i + 1) % 3])];
                api.Check(api.Function<SetEdgeFlag>("SULoopInputEdgeSetHidden")(loop, (nuint)i, edge.Hidden));
                api.Check(api.Function<SetEdgeFlag>("SULoopInputEdgeSetSoft")(loop, (nuint)i, edge.Smooth));
                api.Check(api.Function<SetEdgeFlag>("SULoopInputEdgeSetSmooth")(loop, (nuint)i, edge.Smooth));
            }
            var consumed = loop;
            api.Check(api.Function<AddFace>("SUGeometryInputAddFace")(geometry, ref consumed, out var face));
            // The API deallocates the loop on success, regardless of its returned ptr value.
            api.Adopt(loop);
            var material = new SuMaterialInput { Material = materials[triangle.Material] };
            api.Check(api.Function<SetFaceMaterial>("SUGeometryInputFaceSetFrontMaterial")(geometry, face, ref material));
            api.Check(api.Function<SetFaceMaterial>("SUGeometryInputFaceSetBackMaterial")(geometry, face, ref material));
        }
        // Vertices were welded exactly in Prepare. Do not merge near points across small openings.
        // Only the supplied triangles are submitted; no contour reconstruction or automatic caps.
        api.Check(api.Function<FillEntities>("SUEntitiesFill")(entities, geometry, false));
        api.Release(geometry);
        RequireCount(api.Count("SUEntitiesGetNumFaces", entities), mesh.Triangles.Length, "mesh faces");
        api.Check(api.Function<GetEdgeCount>("SUEntitiesGetNumEdges")(entities, false, out var edges));
        RequireCount(edges, mesh.Edges.Count, "mesh edges (duplicate or collapsed geometry)");
    }

    private sealed record TriangleData(int[] Indices, int Material, string? Surface, SuPoint Normal);
    private sealed class EdgeData
    {
        internal int Count, Direction;
        internal required TriangleData First;
        internal TriangleData? Second;
        internal bool Hidden, Smooth;
    }
    private sealed record MeshData(SuPoint[] Points, TriangleData[] Triangles,
        Dictionary<(int, int), EdgeData> Edges);

    private static MeshData[] Prepare(BuildingExportScene scene, CancellationToken cancellation)
    {
        CheckPoint(scene.OriginMillimetres);
        if (scene.Storeys == null || scene.Materials == null || scene.Meshes == null || scene.Instances == null)
            throw new InvalidDataException("Scene collections cannot be null.");
        if (scene.Instances.Count == 0) throw new InvalidDataException("Scene contains no instances.");
        var floors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var floor in scene.Storeys)
            if (floor == null || !double.IsFinite(floor.Elevation) || !floors.Add(floor.Id ?? ""))
                throw new InvalidDataException("Invalid or duplicate storey.");
        foreach (var material in scene.Materials)
            if (material == null || !Unit(material.Red) || !Unit(material.Green) || !Unit(material.Blue) || !Unit(material.Opacity))
                throw new InvalidDataException("Material channels must be finite values in [0,1].");
        foreach (var instance in scene.Instances)
        {
            cancellation.ThrowIfCancellationRequested();
            if (instance == null || (uint)instance.MeshIndex >= (uint)scene.Meshes.Count || !floors.Contains(instance.StoreyId ?? ""))
                throw new InvalidDataException("Instance refers to a missing mesh or storey.");
            CheckPoint(instance.Translation);
        }

        var result = new MeshData[scene.Meshes.Count];
        for (var m = 0; m < result.Length; m++)
        {
            cancellation.ThrowIfCancellationRequested();
            var mesh = scene.Meshes[m];
            if (mesh?.Vertices == null || mesh.Triangles == null || mesh.Triangles.Count == 0)
                throw new InvalidDataException("Mesh has no triangle geometry.");
            var vertices = new Dictionary<(double, double, double), int>();
            var points = new List<SuPoint>();
            var remap = new int[mesh.Vertices.Count];
            for (var v = 0; v < remap.Length; v++)
            {
                cancellation.ThrowIfCancellationRequested();
                var point = mesh.Vertices[v];
                CheckPoint(point);
                var key = (point[0], point[1], point[2]);
                if (!vertices.TryGetValue(key, out var index))
                {
                    index = points.Count;
                    vertices.Add(key, index);
                    points.Add(new SuPoint(point[0] / MillimetresPerInch, point[1] / MillimetresPerInch, point[2] / MillimetresPerInch));
                }
                remap[v] = index;
            }
            var triangles = new TriangleData[mesh.Triangles.Count];
            var edges = new Dictionary<(int, int), EdgeData>();
            var faces = new HashSet<(int, int, int)>();
            for (var t = 0; t < triangles.Length; t++)
            {
                cancellation.ThrowIfCancellationRequested();
                var input = mesh.Triangles[t];
                if (input == null || (uint)input.A >= (uint)remap.Length || (uint)input.B >= (uint)remap.Length
                    || (uint)input.C >= (uint)remap.Length || (uint)input.MaterialIndex >= (uint)scene.Materials.Count)
                    throw new InvalidDataException($"Invalid triangle index in mesh {m}, triangle {t}.");
                var indices = new[] { remap[input.A], remap[input.B], remap[input.C] };
                var sorted = indices.Order().ToArray();
                if (!faces.Add((sorted[0], sorted[1], sorted[2])))
                    throw new InvalidDataException($"Duplicate triangle in mesh {m}, triangle {t}.");
                var normal = Normal(points[indices[0]], points[indices[1]], points[indices[2]]);
                var triangle = new TriangleData(indices, input.MaterialIndex, input.SmoothSurfaceId, normal);
                triangles[t] = triangle;
                for (var e = 0; e < 3; e++)
                {
                    var a = indices[e]; var b = indices[(e + 1) % 3];
                    var key = EdgeKey(a, b);
                    if (!edges.TryGetValue(key, out var edge))
                        edges.Add(key, edge = new EdgeData { First = triangle, Direction = a < b ? 1 : -1 });
                    else edge.Second = triangle;
                    edge.Count++;
                }
            }
            foreach (var pair in edges)
            {
                var edge = pair.Value;
                if (edge.Count != 2 || edge.Second == null) continue;
                var second = edge.Second;
                var start = Array.IndexOf(second.Indices, pair.Key.Item1);
                var direction = second.Indices[(start + 1) % 3] == pair.Key.Item2 ? 1 : -1;
                if (edge.Direction == direction) continue;
                var dot = Dot(edge.First.Normal, second.Normal);
                edge.Hidden = dot >= 1 - 1e-10;
                // Match Studio's 5.1-degree curve threshold; sharp corners stay hard even
                // if an upstream source accidentally assigns them the same surface ID.
                edge.Smooth = !edge.Hidden && dot > CurveCosine && !string.IsNullOrEmpty(edge.First.Surface)
                    && string.Equals(edge.First.Surface, second.Surface, StringComparison.Ordinal);
            }
            result[m] = new MeshData(points.ToArray(), triangles, edges);
        }
        return result;
    }

    private static ValidationResult ReadBack(NativeApi api, BuildingExportScene scene, MeshData[] meshes,
        string path, CancellationToken cancellation)
    {
        var model = api.LoadModel(path);
        var units = api.Units(model);
        api.RequireUnitOption(units, "LengthFormat", 0);
        api.RequireUnitOption(units, "LengthUnit", 2);
        Console.WriteLine("SKP_READBACK_UNITS LengthUnit=2 LengthFormat=0");
        var root = api.Child("SUModelGetEntities", model);
        RequireCount(api.Count("SUEntitiesGetNumGroups", root), scene.Storeys.Count, "storeys");
        RequireCount(api.Count("SUModelGetNumComponentDefinitions", model), meshes.Length, "component definitions");
        RequireCount(api.Count("SUModelGetNumMaterials", model), scene.Materials.Count, "materials");
        var groups = api.Items("SUEntitiesGetGroups", root, scene.Storeys.Count);
        long faces = 0, edges = 0;
        var instances = 0;
        var usedDefinitions = new HashSet<IntPtr>();
        foreach (var group in groups)
        {
            cancellation.ThrowIfCancellationRequested();
            var entities = api.Child("SUGroupGetEntities", group);
            var count = checked((int)api.Count("SUEntitiesGetNumInstances", entities));
            foreach (var instance in api.Items("SUEntitiesGetInstances", entities, count))
            {
                cancellation.ThrowIfCancellationRequested();
                var definition = api.Child("SUComponentInstanceGetDefinition", instance);
                usedDefinitions.Add(definition.Ptr);
                var geometry = api.Child("SUComponentDefinitionGetEntities", definition);
                faces = checked(faces + (long)api.Count("SUEntitiesGetNumFaces", geometry));
                api.Check(api.Function<GetEdgeCount>("SUEntitiesGetNumEdges")(geometry, false, out var edgeCount));
                edges = checked(edges + (long)edgeCount);
                instances++;
            }
        }
        if (instances != scene.Instances.Count || usedDefinitions.Count != scene.Instances.Select(i => i.MeshIndex).Distinct().Count()
            || faces != scene.Instances.Sum(i => (long)meshes[i.MeshIndex].Triangles.Length)
            || edges != scene.Instances.Sum(i => (long)meshes[i.MeshIndex].Edges.Count))
            throw new InvalidDataException("SKP readback geometry/instance counts differ from the input scene.");
        api.Check(api.Function<GetBounds>("SUEntitiesGetBoundingBox")(root, out var actual));
        var expectedMin = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var expectedMax = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var instance in scene.Instances)
        foreach (var triangle in meshes[instance.MeshIndex].Triangles)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var index in triangle.Indices)
            {
                var point = meshes[instance.MeshIndex].Points[index];
                var xyz = new[] { point.X, point.Y, point.Z };
                for (var axis = 0; axis < 3; axis++)
                {
                    var value = xyz[axis] + instance.Translation[axis] / MillimetresPerInch;
                    if (!double.IsFinite(value)) throw new InvalidDataException("Scene bounds overflow.");
                    expectedMin[axis] = Math.Min(expectedMin[axis], value);
                    expectedMax[axis] = Math.Max(expectedMax[axis], value);
                }
            }
        }
        var bounds = new[] { actual.Min.X, actual.Min.Y, actual.Min.Z, actual.Max.X, actual.Max.Y, actual.Max.Z };
        for (var axis = 0; axis < 6; axis++)
        {
            var expected = axis < 3 ? expectedMin[axis] : expectedMax[axis - 3];
            // Native point tolerance is 0.001 inch. Include floating-point error at large CAD offsets.
            if (!double.IsFinite(bounds[axis]) || Math.Abs(bounds[axis] - expected) > Math.Max(.001, Math.Abs(expected) * 1e-12))
                throw new InvalidDataException($"SKP readback bounds differ on axis {axis}: {bounds[axis]:R} versus {expected:R} inches.");
            bounds[axis] *= MillimetresPerInch;
        }
        api.Release(model);
        return new ValidationResult(groups.Length, instances, meshes.Length, faces, edges, bounds);
    }

    private static void CheckThread()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The installed SketchUp 2026 API requires a Windows x64 worker.");
        if (Environment.CurrentManagedThreadId != 1 || Thread.CurrentThread.IsThreadPoolThread)
            throw new InvalidOperationException("SketchUp C API must run synchronously on the isolated worker's main thread.");
    }
    private static string OutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !string.Equals(Path.GetExtension(path), ".skp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An SKP file path is required.", nameof(path));
        return Path.GetFullPath(path);
    }
    private static bool Unit(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
    private static byte Channel(double value) => checked((byte)Math.Round(value * 255));
    private static string Text(string? value, string fallback = "")
    {
        if (value?.Contains('\0') == true) throw new InvalidDataException("Native strings cannot contain NUL characters.");
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
    private static void CheckPoint(double[] point)
    {
        if (point == null || point.Length != 3 || point.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("Expected three finite double coordinates.");
    }
    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);
    private static double Dot(SuPoint a, SuPoint b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static SuPoint Normal(SuPoint a, SuPoint b, SuPoint c)
    {
        var ab = new SuPoint(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var ac = new SuPoint(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        var n = new SuPoint(ab.Y * ac.Z - ab.Z * ac.Y, ab.Z * ac.X - ab.X * ac.Z, ab.X * ac.Y - ab.Y * ac.X);
        var length = Math.Sqrt(Dot(n, n));
        if (!double.IsFinite(length) || length == 0) throw new InvalidDataException("Degenerate or overflowing triangle.");
        return new SuPoint(n.X / length, n.Y / length, n.Z / length);
    }
    private static void RequireCount(nuint actual, int expected, string label)
    {
        if (actual != (nuint)expected) throw new InvalidDataException($"Unexpected SKP {label}: {actual}, expected {expected}.");
    }

    private static void WithApi(string directory, Action<NativeApi> body)
    {
        var api = new NativeApi(directory);
        Exception? failure = null;
        try { api.Initialize(); body(api); }
        catch (Exception ex) { failure = ex; }
        var cleanup = api.Cleanup();
        if (cleanup.Count != 0)
        {
            if (failure != null) cleanup.Insert(0, failure);
            throw new AggregateException("SketchUp export or native cleanup failed.", cleanup);
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SuRef { internal IntPtr Ptr; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SuPoint(double x, double y, double z)
    {
        internal double X = x, Y = y, Z = z;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SuColor { internal byte Red, Green, Blue, Alpha; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SuBounds { internal SuPoint Min, Max; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SuTransform
    {
        internal double M0, M1, M2, M3, M4, M5, M6, M7, M8, M9, M10, M11, M12, M13, M14, M15;
        internal static SuTransform Translation(double[] point) => new()
        {
            M0 = 1, M5 = 1, M10 = 1, M15 = 1,
            M12 = point[0] / MillimetresPerInch, M13 = point[1] / MillimetresPerInch, M14 = point[2] / MillimetresPerInch
        };
    }
    // SUMaterialInput: size_t, SUPoint2D[4], size_t[4], SUMaterialRef (112 bytes on x64).
    // No UV coordinates are needed for the scene's solid colors.
    [StructLayout(LayoutKind.Sequential)]
    private struct SuMaterialInput
    {
        internal nuint Count;
        internal double U0, V0, U1, V1, U2, V2, U3, V3;
        internal nuint I0, I1, I2, I3;
        internal SuRef Material;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GetVersion(out nuint major, out nuint minor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RefCall(ref SuRef value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetChild(SuRef parent, ref SuRef child);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate SuRef ConvertRef(SuRef value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetText(SuRef value, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetDouble(SuRef value, double number);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetBool(SuRef value, [MarshalAs(UnmanagedType.I1)] bool flag);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetColor(SuRef value, ref SuColor color);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AddRefs(SuRef parent, nuint count, [In] SuRef[] values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AddRef(SuRef parent, SuRef value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AddInstance(SuRef entities, SuRef instance, IntPtr optionalName);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetTransform(SuRef instance, ref SuTransform transform);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetVertices(SuRef geometry, nuint count, [In] SuPoint[] points);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AddVertex(SuRef loop, nuint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetEdgeFlag(SuRef loop, nuint index, [MarshalAs(UnmanagedType.I1)] bool flag);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AddFace(SuRef geometry, ref SuRef loop, out nuint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetFaceMaterial(SuRef geometry, nuint index, ref SuMaterialInput material);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FillEntities(SuRef entities, SuRef geometry, [MarshalAs(UnmanagedType.I1)] bool weld);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetCount(SuRef value, out nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetEdgeCount(SuRef value, [MarshalAs(UnmanagedType.I1)] bool standaloneOnly, out nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetItems(SuRef value, nuint length, [Out] SuRef[] items, out nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetBounds(SuRef entities, out SuBounds bounds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadFile(ref SuRef model, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDictionary(SuRef value, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref SuRef dictionary);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetAttribute(SuRef dictionary, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, SuRef value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetNamedRef(SuRef owner, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref SuRef value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetOptionValue(SuRef provider, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, SuRef value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetInt32(SuRef value, int number);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetInt32(SuRef value, out int number);

    private sealed class NativeApi
    {
        private IntPtr library;
        private bool initialized;
        private readonly Dictionary<string, Delegate> functions = new(StringComparer.Ordinal);
        private readonly Dictionary<IntPtr, (SuRef Value, string Release)> owned = new();

        internal NativeApi(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || directory.Contains('\0') || !Path.IsPathFullyQualified(directory))
                throw new ArgumentException("An explicit absolute SketchUp 2026 API directory is required.", nameof(directory));
            var dll = Path.Combine(Path.GetFullPath(directory), "SketchUpAPI.dll");
            if (!File.Exists(dll)) throw new FileNotFoundException("SketchUpAPI.dll is missing from the selected installation.", dll);
            using (var stream = File.OpenRead(dll))
            using (var pe = new PEReader(stream))
                if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.CorHeader != null
                    || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) == 0)
                    throw new BadImageFormatException("Expected the native Windows x64 SketchUpAPI.dll.", dll);
            var version = FileVersionInfo.GetVersionInfo(dll);
            if (version.FileMajorPart < 26)
                throw new NotSupportedException($"SketchUp 2026 or newer is required; selected file version is {version.FileVersion}.");
            var loaded = GetModuleHandleW("SketchUpAPI.dll");
            if (loaded != IntPtr.Zero)
            {
                var loadedPath = new StringBuilder(32768);
                if (GetModuleFileNameW(loaded, loadedPath, loadedPath.Capacity) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!string.Equals(Path.GetFullPath(loadedPath.ToString()), dll, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different SketchUpAPI.dll is already loaded in this process.");
            }
            // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS.
            // The dependency search includes the selected DLL's directory without changing PATH.
            library = LoadLibraryExW(dll, IntPtr.Zero, 0x00000100 | 0x00001000);
            if (library == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the selected SketchUp API or its installed dependencies.");
        }

        internal T Function<T>(string name) where T : Delegate
        {
            if (!functions.TryGetValue(name, out var function))
            {
                function = Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
                functions.Add(name, function);
            }
            return (T)function;
        }
        internal void Initialize()
        {
            // Resolve termination first, so any initialized API always has a cleanup route.
            _ = Function<VoidCall>("SUTerminate");
            var version = Function<GetVersion>("SUGetAPIVersion");
            Function<VoidCall>("SUInitialize")();
            initialized = true;
            version(out var major, out var minor);
            if (major < 14) throw new NotSupportedException($"SketchUp C API >=14 is required, got {major}.{minor}.");
            Console.WriteLine($"SKP_C_API_VERSION {major}.{minor}");
            if (Marshal.SizeOf<SuRef>() != 8 || Marshal.SizeOf<SuPoint>() != 24 || Marshal.SizeOf<SuTransform>() != 128
                || Marshal.SizeOf<SuMaterialInput>() != 112 || Marshal.SizeOf<SuColor>() != 4)
                throw new InvalidOperationException("SketchUp native ABI layout mismatch.");
        }
        internal void Check(int result, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(result))] string operation = "")
        {
            if (result != 0) throw new InvalidOperationException($"SketchUp C API SUResult={result}: {operation}");
        }
        internal SuRef Create(string create, string release)
        {
            _ = Function<RefCall>(release);
            SuRef value = default;
            Check(Function<RefCall>(create)(ref value));
            Track(value, release);
            return value;
        }
        internal SuRef CreateChild(string create, SuRef parent, string release)
        {
            _ = Function<RefCall>(release);
            var value = Child(create, parent);
            Track(value, release);
            return value;
        }
        internal SuRef CreateGroup()
        {
            _ = Function<RefCall>("SUComponentInstanceRelease");
            var group = Create("SUGroupCreate", "SUComponentInstanceRelease");
            var instance = Convert("SUGroupToComponentInstance", group);
            owned[group.Ptr] = (instance, "SUComponentInstanceRelease");
            return group;
        }
        private void Track(SuRef value, string release)
        {
            Valid(value);
            owned.Add(value.Ptr, (value, release));
        }
        internal void Adopt(SuRef value) => owned.Remove(value.Ptr);
        internal void Release(SuRef value)
        {
            var resource = owned[value.Ptr];
            owned.Remove(value.Ptr);
            Check(Function<RefCall>(resource.Release)(ref resource.Value));
        }
        internal SuRef Child(string function, SuRef parent)
        {
            SuRef child = default;
            Check(Function<GetChild>(function)(parent, ref child));
            Valid(child);
            return child;
        }
        internal SuRef Convert(string function, SuRef value)
        {
            var converted = Function<ConvertRef>(function)(value);
            Valid(converted);
            return converted;
        }
        internal void AddOwned(string function, SuRef parent, SuRef value)
        {
            Check(Function<AddRefs>(function)(parent, 1, new[] { value }));
            Adopt(value);
        }
        internal SuRef Dictionary(SuRef value, bool model = false)
        {
            SuRef dictionary = default;
            Check(Function<GetDictionary>(model ? "SUModelGetAttributeDictionary" : "SUEntityGetAttributeDictionary")
                (value, "WanLuo.BuildingModel", ref dictionary));
            Valid(dictionary);
            return dictionary;
        }
        internal void Attribute(SuRef dictionary, string key, string? text)
        {
            var value = Create("SUTypedValueCreate", "SUTypedValueRelease");
            Check(Function<SetText>("SUTypedValueSetString")(value, Text(text)));
            Check(Function<SetAttribute>("SUAttributeDictionarySetValue")(dictionary, key, value));
            Release(value);
        }
        // Official options_manager.h/options_provider.h and typed_value.h signatures.
        // UnitsOptions uses Int32: LengthFormat=0 (decimal), LengthUnit=2 (mm).
        internal SuRef Units(SuRef model)
        {
            var manager = Child("SUModelGetOptionsManager", model);
            SuRef provider = default;
            Check(Function<GetNamedRef>("SUOptionsManagerGetOptionsProviderByName")(manager, "UnitsOptions", ref provider));
            Valid(provider);
            return provider;
        }
        internal void SetUnitOption(SuRef provider, string key, int number)
        {
            var value = Create("SUTypedValueCreate", "SUTypedValueRelease");
            Check(Function<SetInt32>("SUTypedValueSetInt32")(value, number));
            Check(Function<SetOptionValue>("SUOptionsProviderSetValue")(provider, key, value));
            Release(value);
        }
        internal void RequireUnitOption(SuRef provider, string key, int expected)
        {
            var value = Create("SUTypedValueCreate", "SUTypedValueRelease");
            Check(Function<GetNamedRef>("SUOptionsProviderGetValue")(provider, key, ref value));
            Check(Function<GetInt32>("SUTypedValueGetInt32")(value, out var actual));
            Release(value);
            if (actual != expected)
                throw new InvalidDataException($"SKP readback {key} is {actual}, expected {expected}.");
        }
        internal nuint Count(string function, SuRef value)
        {
            Check(Function<GetCount>(function)(value, out var count));
            return count;
        }
        internal SuRef[] Items(string function, SuRef value, int count)
        {
            if (count == 0) return Array.Empty<SuRef>();
            var items = new SuRef[count];
            Check(Function<GetItems>(function)(value, (nuint)count, items, out var actual));
            RequireCount(actual, count, function);
            foreach (var item in items) Valid(item);
            return items;
        }
        internal SuRef LoadModel(string path)
        {
            _ = Function<RefCall>("SUModelRelease");
            SuRef model = default;
            Check(Function<LoadFile>("SUModelCreateFromFileWithStatus")(ref model, path, out var status));
            Track(model, "SUModelRelease");
            if (status != 0) throw new InvalidDataException($"Unexpected SKP load status: {status}.");
            return model;
        }
        private static void Valid(SuRef value)
        {
            if (value.Ptr == IntPtr.Zero) throw new InvalidOperationException("SketchUp returned an invalid native reference.");
        }
        internal List<Exception> Cleanup()
        {
            var errors = new List<Exception>();
            foreach (var resource in owned.Values.Reverse().ToArray())
            {
                try { var value = resource.Value; Check(Function<RefCall>(resource.Release)(ref value)); }
                catch (Exception ex) { errors.Add(ex); }
            }
            owned.Clear();
            if (initialized)
            {
                try { Function<VoidCall>("SUTerminate")(); }
                catch (Exception ex) { errors.Add(ex); }
                initialized = false;
            }
            if (library != IntPtr.Zero)
            {
                if (!FreeLibrary(library)) errors.Add(new Win32Exception(Marshal.GetLastWin32Error()));
                library = IntPtr.Zero;
            }
            return errors;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int size);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr library);
}
