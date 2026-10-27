using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Assimp;
using Assimp.Configs;

namespace BatchPdfPublisher.BuildingModel.Export;

/// <summary>
/// Static millimetre/Z-up FBX export through AssimpNetter, without an Autodesk SDK.
/// AssimpNetter 6.0.5's bundled native exporter duplicates shared geometry and can
/// discard node metadata. A transport-only pass restores custom properties and
/// native display layers without changing geometry or claiming FBX instancing.
/// </summary>
internal static class BuildingFbxWriter
{
    // Verified in Assimp's FBXExporter::WriteGlobalSettings (centimetres per unit):
    // https://github.com/Saalvage/assimp/blob/69863feef7f7c42c79ac18c00667d2c0bcb50ee7/code/AssetLib/FBX/FBXExporter.cpp
    private const double MillimetreUnitScale = 0.1d;
    private const string ElementIdKey = "WL_ElementId";
    private const string StoreyIdKey = "WL_StoreyId";
    private const string OriginKey = "WL_OriginMillimetres";

    internal static ValidationResult Write(BuildingExportScene scene, string path,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellation.ThrowIfCancellationRequested();
        var cleanupWarnings = new List<string>();
        var output = CreateScene(scene, cancellation, cleanupWarnings);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath)
            + "." + Guid.NewGuid().ToString("N") + ".fbx");
        try
        {
            using (var context = new AssimpContext())
            {
                cancellation.ThrowIfCancellationRequested();
                // FBXExporter enables shared control points only when the export
                // preprocessing explicitly includes JoinIdenticalVertices. Normals
                // remain per polygon corner, so hard corners need not be unwelded.
                // No pretransform, normal generation or cross-element mesh merge.
                if (!context.ExportFile(output, temporaryPath, "fbx", PostProcessSteps.JoinIdenticalVertices))
                    throw new IOException("Assimp could not export the binary FBX file.");
            }
            cancellation.ThrowIfCancellationRequested();
            FbxDisplayLayers.Apply(temporaryPath, scene, output, cancellation);
            var result = ValidateFile(output, scene, temporaryPath, cancellation);
            result.Warnings.InsertRange(0, cleanupWarnings);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            Console.WriteLine(FormattableString.Invariant($"FBX_VALIDATED instances={result.InstanceCount} displayLayers={result.DisplayLayerCount} neutralMeshDefinitions={result.NeutralMeshDefinitionCount} inputMeshParts={result.ExportInputMeshPartCount} readbackMeshParts={result.MeshCount} materials={result.MaterialCount} polygons={result.PolygonCount} triangleEquivalent={result.TriangleCount} weldedControlPoints=True units=mm up=Z sharedMeshes={result.PreservesSharedMeshes} identityMetadata={result.PreservesIdentityMetadata} originMetadata={result.PreservesOriginMetadata} boundsMm={string.Join(",", result.BoundsMillimetres.Select(Number))}"));
            foreach (var warning in result.Warnings) Console.WriteLine("FBX_WARNING " + warning);
            return result;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Throws for incorrect units, axes, geometry, transforms, names, hierarchy or
    /// materials. Reports unsupported metadata/geometry sharing separately. This
    /// readback is an Assimp check, not a native 3ds Max acceptance test.
    /// </summary>
    internal static ValidationResult Validate(BuildingExportScene scene, string path,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellation.ThrowIfCancellationRequested();
        var cleanupWarnings = new List<string>();
        var result = ValidateFile(CreateScene(scene, cancellation, cleanupWarnings), scene, path, cancellation);
        result.Warnings.InsertRange(0, cleanupWarnings);
        return result;
    }

    internal sealed class ValidationResult
    {
        public int InstanceCount { get; internal set; }
        public int DisplayLayerCount { get; internal set; }
        public int NeutralMeshDefinitionCount { get; internal set; }
        public int ExportInputMeshPartCount { get; internal set; }
        /// <summary>Actual Assimp readback meshes; FBX geometry is split by material on import.</summary>
        public int MeshCount { get; internal set; }
        public int MaterialCount { get; internal set; }
        public long TriangleCount { get; internal set; }
        public long PolygonCount { get; internal set; }
        public bool PreservesSharedMeshes { get; internal set; } = true;
        public bool PreservesIdentityMetadata { get; internal set; } = true;
        public bool PreservesOriginMetadata { get; internal set; } = true;
        /// <summary>FBX world-space min XYZ/max XYZ in millimetres, relative to the source origin.</summary>
        public double[] BoundsMillimetres { get; internal set; } = Array.Empty<double>();
        public List<string> Warnings { get; } = new();
    }

    private static Scene CreateScene(BuildingExportScene source, CancellationToken cancellation, List<string>? cleanupWarnings = null)
    {
        var origin = Point(source.OriginMillimetres, "scene origin");
        if (source.Storeys == null || source.Materials == null || source.Meshes == null
            || source.Instances == null || source.Instances.Count == 0)
            throw new ArgumentException("The export scene is incomplete or contains no instances.");

        var scene = new Scene { Name = source.Name ?? "", RootNode = new Node("Root") };
        var presentation = FbxPresentation.For(source);
        var floorNames = presentation.Floors.ToDictionary(f => f.StoreyId, f => f.GroupName, StringComparer.Ordinal);
        var elementNames = presentation.Elements.ToDictionary(e => e.Id, e => e.NodeName, StringComparer.Ordinal);
        Set(scene.Metadata, "UnitScaleFactor", MillimetreUnitScale);
        Set(scene.Metadata, "OriginalUnitScaleFactor", MillimetreUnitScale);
        Set(scene.Metadata, "UpAxis", 2);
        Set(scene.Metadata, "UpAxisSign", 1);
        Set(scene.Metadata, "OriginalUpAxis", 2);
        Set(scene.Metadata, "OriginalUpAxisSign", 1);
        // +X right, -Y front, +Z up: right-handed, as used by Z-up FBX DCCs.
        Set(scene.Metadata, "FrontAxis", 1);
        Set(scene.Metadata, "FrontAxisSign", -1);
        Set(scene.Metadata, "CoordAxis", 0);
        Set(scene.Metadata, "CoordAxisSign", 1);

        // The exporter omits the aiScene root, so the named scene is a child.
        var originText = string.Join(",", new[] { origin.X, origin.Y, origin.Z }.Select(Number));
        var building = new Node(presentation.BuildingName);
        Set(building.Metadata, OriginKey, originText);
        Set(building.Metadata, "WL_Name", source.Name ?? "");
        scene.RootNode.Children.Add(building);

        var storeys = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var floor in source.Storeys)
        {
            cancellation.ThrowIfCancellationRequested();
            if (floor == null) throw new ArgumentException("The scene contains a missing storey.");
            var id = floor.Id ?? "";
            var node = new Node(floorNames[id]);
            Set(node.Metadata, StoreyIdKey, id);
            Set(node.Metadata, "WL_Name", floor.Name ?? "");
            Set(node.Metadata, "WL_ElevationMillimetres", Finite(floor.Elevation));
            // Elevation is an absolute datum, not another instance transform.
            // Instance translations already include their storey placement.
            if (!storeys.TryAdd(id, node)) throw new ArgumentException("Duplicate storey ID: " + id);
            building.Children.Add(node);
        }

        var materialMap = new int[source.Materials.Count];
        var materialKeys = new Dictionary<(string, double, double, double, double), int>();
        for (var i = 0; i < source.Materials.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var material = source.Materials[i]
                ?? throw new ArgumentException("The scene contains a missing material.");
            var key = (material.Name ?? "", Channel(material.Red), Channel(material.Green),
                Channel(material.Blue), Channel(material.Opacity));
            if (!materialKeys.TryGetValue(key, out var index))
            {
                index = scene.Materials.Count;
                materialKeys.Add(key, index);
                scene.Materials.Add(new Material
                {
                    Name = NodeName("Material", material.Name, index.ToString(CultureInfo.InvariantCulture)),
                    ColorDiffuse = new Vector4((float)key.Item2, (float)key.Item3, (float)key.Item4, 1f),
                    Opacity = (float)key.Item5,
                    ShadingMode = ShadingMode.Gouraud
                });
            }
            materialMap[i] = index;
        }

        var meshMap = new Dictionary<int, int[]>();
        var instanceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in source.Instances)
        {
            cancellation.ThrowIfCancellationRequested();
            if (instance == null || string.IsNullOrWhiteSpace(instance.Id)
                || !instanceIds.Add(instance.Id))
                throw new ArgumentException("Every instance must have a unique nonempty ID.");
            if (!storeys.TryGetValue(instance.StoreyId ?? "", out var floor))
                throw new ArgumentException("An instance refers to an unknown storey: " + instance.Id);
            if ((uint)instance.MeshIndex >= (uint)source.Meshes.Count)
                throw new ArgumentException("An instance refers to an unknown mesh: " + instance.Id);
            if (!meshMap.TryGetValue(instance.MeshIndex, out var indices))
            {
                indices = AddMesh(source.Meshes[instance.MeshIndex], scene, materialMap, cancellation, cleanupWarnings,
                    (source.Storeys.First(f=>f.Id==instance.StoreyId).Name ?? instance.StoreyId)+" · "+instance.Name);
                meshMap.Add(instance.MeshIndex, indices);
            }
            var translation = Point(instance.Translation, "instance translation").ToSingle();
            var node = new Node(elementNames[instance.Id])
            {
                // AssimpNetter marshals Matrix4x4 without transposition. Assimp
                // expects translation in M14/M24/M34 (column-vector convention).
                Transform = Matrix4x4.Transpose(Matrix4x4.CreateTranslation(translation))
            };
            node.MeshIndices.AddRange(indices);
            Set(node.Metadata, ElementIdKey, instance.Id);
            Set(node.Metadata, "WL_SourceId", instance.SourceId ?? "");
            Set(node.Metadata, StoreyIdKey, instance.StoreyId ?? "");
            Set(node.Metadata, "WL_Name", instance.Name ?? "");
            Set(node.Metadata, "WL_Kind", instance.Kind ?? "");
            Set(node.Metadata, "WL_MeshId", source.Meshes[instance.MeshIndex].Id ?? "");
            floor.Children.Add(node);
        }
        return scene;
    }

    private static int[] AddMesh(BuildingExportMesh source, Scene target, int[] materialMap,
        CancellationToken cancellation, List<string>? cleanupWarnings, string label)
    {
        if (source == null || source.Vertices == null || source.Triangles == null
            || source.Triangles.Count == 0)
            throw new ArgumentException("The scene contains an incomplete or empty mesh.");
        var points = new D3[source.Vertices.Count];
        for (var i = 0; i < points.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            points[i] = Point(source.Vertices[i], "mesh vertex");
        }
        var normals = new D3[source.Triangles.Count];
        var omitted = new bool[source.Triangles.Count];
        var removedArea = 0d;var totalArea = 0d;var removedCount = 0;
        var retainedMaterials = new HashSet<int>();var authoredMaterials = new HashSet<int>();
        var smoothNormals = new Dictionary<(D3, string), D3>();
        for (var i = 0; i < source.Triangles.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var triangle = source.Triangles[i];
            if (triangle == null || (uint)triangle.A >= (uint)points.Length
                || (uint)triangle.B >= (uint)points.Length || (uint)triangle.C >= (uint)points.Length
                || (uint)triangle.MaterialIndex >= (uint)materialMap.Length)
                throw new ArgumentException("A mesh triangle has an invalid vertex or material index.");
            var normal = D3.Cross(points[triangle.B] - points[triangle.A], points[triangle.C] - points[triangle.A]);
            var area = Length(normal)/2;totalArea += area;
            if(!double.IsFinite(area))throw new ArgumentException("The scene contains non-finite triangle area.");
            authoredMaterials.Add(triangle.MaterialIndex);
            var a=points[triangle.A].ToSingle();var b=points[triangle.B].ToSingle();var c=points[triangle.C].ToSingle();
            if(Vector3.Cross(b-a,c-a).LengthSquared()==0f)
            {
                var longest=Math.Max(Length(points[triangle.B]-points[triangle.A]),
                    Math.Max(Length(points[triangle.C]-points[triangle.A]),Length(points[triangle.C]-points[triangle.B])));
                // Only zero-area transport faces may be omitted. Bound both the
                // original sliver height and its absolute/cumulative surface loss.
                if(area>0.1 || (longest>0 && 2*area/longest>0.01))
                    throw new ArgumentException("构件存在超出自动修复范围的几何精度损失，请检查："+label);
                omitted[i]=true;removedArea+=area;removedCount++;continue;
            }
            retainedMaterials.Add(triangle.MaterialIndex);
            normals[i] = normal.Unit();
            // Compute area-weighted smooth normals before splitting by material,
            // so a material boundary does not introduce an artificial hard edge.
            if (!string.IsNullOrEmpty(triangle.SmoothSurfaceId))
            {
                foreach (var vertex in new[] { triangle.A, triangle.B, triangle.C })
                {
                    var key = (points[vertex], triangle.SmoothSurfaceId);
                    smoothNormals.TryGetValue(key, out var sum);
                    smoothNormals[key] = sum + normal;
                }
            }
        }

        if(removedCount>0)
        {
            if(removedCount==source.Triangles.Count || !authoredMaterials.SetEquals(retainedMaterials)
                || removedArea>Math.Min(1d,totalArea*1e-6))
                throw new ArgumentException("微小面清理将丢失构件或明显几何，未生成不完整文件："+label);
            cleanupWarnings?.Add(FormattableString.Invariant(
                $"FBX 微小面已自动清理：{label}，{removedCount} 个退化面，原面积合计 {removedArea:G6} mm²；构件与材质保留。"));
        }

        var parts = new Dictionary<int, (Mesh Mesh, Dictionary<(D3, Vector3, string), int> Vertices, List<string> Surfaces)>();
        for (var i = 0; i < source.Triangles.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if(omitted[i])continue;
            var triangle = source.Triangles[i];
            var material = materialMap[triangle.MaterialIndex];
            if (!parts.TryGetValue(material, out var part))
            {
                part = (new Mesh(NodeName("Mesh", source.Id, material.ToString(CultureInfo.InvariantCulture)),
                    PrimitiveType.Triangle) { MaterialIndex = material }, new(), new());
                parts.Add(material, part);
            }
            var indices = new int[3];
            var corners = new[] { triangle.A, triangle.B, triangle.C };
            for (var corner = 0; corner < 3; corner++)
            {
                var point = points[corners[corner]];
                var group = triangle.SmoothSurfaceId ?? "";
                var normal = (group.Length == 0 ? normals[i] : smoothNormals[(point, group)].Unit()).ToSingle();
                var key = (point, normal, group);
                if (!part.Vertices.TryGetValue(key, out var index))
                {
                    index = part.Mesh.VertexCount;
                    part.Vertices.Add(key, index);
                    part.Mesh.Vertices.Add(point.ToSingle());
                    part.Mesh.Normals.Add(normal);
                }
                indices[corner] = index;
            }
            // Reject geometry collapsed by the native single-precision ABI.
            if (Vector3.Cross(part.Mesh.Vertices[indices[1]] - part.Mesh.Vertices[indices[0]],
                part.Mesh.Vertices[indices[2]] - part.Mesh.Vertices[indices[0]]).LengthSquared() == 0f)
                throw new ArgumentException(FormattableString.Invariant(
                    $"构件网格存在无法以 FBX 精度表示的三角面，请检查细小几何或分楼层导出。网格={source.Id}，三角面={i}，顶点={triangle.A}/{triangle.B}/{triangle.C}。"));
            part.Mesh.Faces.Add(new Face(indices));
            part.Surfaces.Add(triangle.SmoothSurfaceId ?? "");
        }
        var result = new List<int>();
        foreach (var part in parts.Values)
        {
            FbxPlanarFaces.Merge(part.Mesh, part.Surfaces, cancellation);
            if(target.Materials[part.Mesh.MaterialIndex].Name.StartsWith("WL_Material: wall [",StringComparison.Ordinal)
                &&part.Surfaces.All(string.IsNullOrEmpty))FbxWallGrid.Apply(part.Mesh,cancellation);
            result.Add(target.MeshCount);
            target.Meshes.Add(part.Mesh);
        }
        return result.ToArray();
    }

    private static ValidationResult ValidateFile(Scene expected, BuildingExportScene source,
        string path, CancellationToken cancellation)
    {
        using (var stream = File.OpenRead(path))
        {
            var magic = new byte[23];
            stream.ReadExactly(magic);
            if (!magic.AsSpan().SequenceEqual("Kaydara FBX Binary  \0\x1a\0"u8))
                throw new InvalidDataException("The exporter did not produce binary FBX.");
        }
        using var context = new AssimpContext();
        // Inspect file-space millimetres/Z-up, without importer axis/unit changes.
        context.SetConfig(new FBXIgnoreUpDirectionConfig(true));
        context.SetConfig(new FBXConvertToMetersConfig(false));
        context.SetConfig(new FBXPreservePivotsConfig(false));
        cancellation.ThrowIfCancellationRequested();
        var actual = context.ImportFile(path, PostProcessSteps.None)
            ?? throw new InvalidDataException("Assimp could not read back the exported FBX.");
        cancellation.ThrowIfCancellationRequested();
        foreach (var key in new[] { "UnitScaleFactor", "OriginalUnitScaleFactor", "UpAxis", "UpAxisSign",
            "OriginalUpAxis", "OriginalUpAxisSign", "FrontAxis", "FrontAxisSign", "CoordAxis", "CoordAxisSign" })
        {
            if (!actual.Metadata.TryGetValue(key, out var entry)
                || Math.Abs(Convert.ToDouble(entry.Data, CultureInfo.InvariantCulture)
                    - Convert.ToDouble(expected.Metadata[key].Data, CultureInfo.InvariantCulture)) > 1e-7)
                throw new InvalidDataException("FBX readback changed or omitted units/axes: " + key);
        }

        var actualNodes = Nodes(actual.RootNode).ToLookup(n => n.Name, StringComparer.Ordinal);
        var expectedNodes = Nodes(expected.RootNode).Skip(1).ToArray();
        if (Nodes(actual.RootNode).Count() != expectedNodes.Length + 1)
            throw new InvalidDataException("FBX readback changed the scene hierarchy.");
        var result = new ValidationResult { DisplayLayerCount = FbxDisplayLayers.Validate(path, source, cancellation),
            NeutralMeshDefinitionCount = source.Meshes.Count,
            ExportInputMeshPartCount = expected.MeshCount, MeshCount = actual.MeshCount,
            MaterialCount = actual.MaterialCount };
        var expectedMeshIndices = expected.Meshes.Select((mesh, index) => (mesh, index))
            .ToDictionary(pair => pair.mesh, pair => pair.index);
        var actualMeshIndices = actual.Meshes.Select((mesh, index) => (mesh, index))
            .ToDictionary(pair => pair.mesh, pair => pair.index);
        var shared = new Dictionary<int, int>();
        foreach (var node in expectedNodes)
        {
            cancellation.ThrowIfCancellationRequested();
            var matches = actualNodes[node.Name].ToArray();
            if (matches.Length != 1) throw new InvalidDataException("FBX readback lost a unique node name: " + node.Name);
            var read = matches[0];
            if (node.Parent != expected.RootNode && read.Parent?.Name != node.Parent?.Name)
                throw new InvalidDataException("FBX readback changed the storey/element hierarchy: " + node.Name);
            if (!MatrixNear(node.Transform, read.Transform))
                throw new InvalidDataException("FBX readback changed an instance transform: " + node.Name);
            foreach (var pair in node.Metadata)
            {
                if (!read.Metadata.TryGetValue(pair.Key, out var entry) || !MetadataEqual(entry.Data, pair.Value.Data))
                {
                    if (pair.Key == OriginKey) result.PreservesOriginMetadata = false;
                    else result.PreservesIdentityMetadata = false;
                }
            }
            if (!node.HasMeshes)
            {
                if (read.HasMeshes) throw new InvalidDataException("FBX readback added unexpected geometry.");
                continue;
            }
            result.InstanceCount++;
            var expectedParts = Parts(expected, node);
            var actualParts = Parts(actual, read);
            if (expectedParts.Count != actualParts.Count)
                throw new InvalidDataException("FBX readback changed per-element materials: " + node.Name);
            foreach (var pair in expectedParts)
            {
                if (!actualParts.TryGetValue(pair.Key, out var actualMesh))
                    throw new InvalidDataException("FBX readback lost a material assignment: " + pair.Key);
                var expectedMesh = pair.Value;
                if (expectedMesh.FaceCount != actualMesh.FaceCount)
                    throw new InvalidDataException("FBX readback changed polygon count: " + node.Name);
                CheckGeometry(expectedMesh, actualMesh, cancellation);
                CheckMaterial(expected.Materials[expectedMesh.MaterialIndex], actual.Materials[actualMesh.MaterialIndex]);
                result.TriangleCount += actualMesh.Faces.Sum(face => face.IndexCount - 2L);
                result.PolygonCount += actualMesh.FaceCount;
                var expectedIndex = expectedMeshIndices[expectedMesh];
                var actualIndex = actualMeshIndices[actualMesh];
                if (shared.TryGetValue(expectedIndex, out var firstIndex) && firstIndex != actualIndex)
                    result.PreservesSharedMeshes = false;
                else shared[expectedIndex] = actualIndex;
            }
        }
        if (actual.MaterialCount != expected.Meshes.Select(mesh => mesh.MaterialIndex).Distinct().Count())
            throw new InvalidDataException("FBX readback duplicated or omitted scene materials.");
        result.BoundsMillimetres = CheckWorldBounds(source, actual, actualNodes, cancellation);
        if (!result.PreservesSharedMeshes)
            result.Warnings.Add("The bundled Assimp FBX exporter duplicated shared geometry. Placements are correct, but native FBX geometry instancing is not preserved.");
        if (!result.PreservesIdentityMetadata)
            result.Warnings.Add("The FBX importer did not expose all custom properties. Stable identities remain in FBX Properties70 and the export manifest.");
        if (!result.PreservesOriginMetadata)
            result.Warnings.Add("The FBX importer did not expose origin metadata. The exact source origin remains in FBX Properties70 and the export manifest.");
        return result;
    }

    private static double[] CheckWorldBounds(BuildingExportScene source, Scene actual,
        ILookup<string, Node> actualNodes, CancellationToken cancellation)
    {
        if (!actual.RootNode.Transform.Equals(Matrix4x4.Identity))
            throw new InvalidDataException("FBX readback changed the root transform.");
        var expectedBounds = EmptyBounds();
        var actualBounds = EmptyBounds();
        var meshBounds = new Dictionary<int, double[]>();
        var elementNames = FbxPresentation.For(source).Elements.ToDictionary(e => e.Id, e => e.NodeName, StringComparer.Ordinal);
        foreach (var instance in source.Instances)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!meshBounds.TryGetValue(instance.MeshIndex, out var localBounds))
            {
                localBounds = EmptyBounds();
                var mesh = source.Meshes[instance.MeshIndex];
                foreach (var triangle in mesh.Triangles)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Include(localBounds, Point(mesh.Vertices[triangle.A], "mesh vertex"));
                    Include(localBounds, Point(mesh.Vertices[triangle.B], "mesh vertex"));
                    Include(localBounds, Point(mesh.Vertices[triangle.C], "mesh vertex"));
                }
                meshBounds.Add(instance.MeshIndex, localBounds);
            }

            var node = actualNodes[elementNames[instance.Id]].Single();
            var worldTranslation = new double[3];
            // Every node's linear transform was checked against the authored
            // identity above. Accumulate translations in double precision: a
            // float matrix product could itself hide a small ancestor offset.
            for (var ancestor = node; ancestor != null; ancestor = ancestor.Parent)
            {
                worldTranslation[0] += ancestor.Transform.M14;
                worldTranslation[1] += ancestor.Transform.M24;
                worldTranslation[2] += ancestor.Transform.M34;
            }
            var instanceBounds = EmptyBounds();
            foreach (var meshIndex in node.MeshIndices)
            {
                var mesh = actual.Meshes[meshIndex];
                foreach (var face in mesh.Faces)
                {
                    cancellation.ThrowIfCancellationRequested();
                    foreach (var index in face.Indices)
                    {
                        var p = mesh.Vertices[index];
                        Include(instanceBounds, new D3(p.X + worldTranslation[0],
                            p.Y + worldTranslation[1], p.Z + worldTranslation[2]));
                    }
                }
            }

            for (var axis = 0; axis < 3; axis++)
            {
                // Compare with original doubles, not CreateScene's float copy.
                // Interior instances must pass independently of scene extrema.
                var extent = localBounds[axis + 3] - localBounds[axis];
                var tolerance = Math.Max(0.01d, extent * 2e-7d);
                var translation = instance.Translation[axis];
                var minimum = localBounds[axis] + translation;
                var maximum = localBounds[axis + 3] + translation;
                if (!double.IsFinite(worldTranslation[axis])
                    || !double.IsFinite(instanceBounds[axis]) || !double.IsFinite(instanceBounds[axis + 3])
                    || Math.Abs(worldTranslation[axis] - translation) > tolerance
                    || Math.Abs(instanceBounds[axis] - minimum) > tolerance
                    || Math.Abs(instanceBounds[axis + 3] - maximum) > tolerance
                    || Math.Abs(instanceBounds[axis + 3] - instanceBounds[axis] - extent) > tolerance)
                    throw PrecisionFailure(instance.Id);
                expectedBounds[axis] = Math.Min(expectedBounds[axis], minimum);
                expectedBounds[axis + 3] = Math.Max(expectedBounds[axis + 3], maximum);
                actualBounds[axis] = Math.Min(actualBounds[axis], instanceBounds[axis]);
                actualBounds[axis + 3] = Math.Max(actualBounds[axis + 3], instanceBounds[axis + 3]);
            }
        }
        for (var axis = 0; axis < 3; axis++)
        {
            // Tolerance depends on building extent, not its georeferenced offset.
            // Unrepresentable far-origin coordinates must fail, not silently move.
            var tolerance = Math.Max(0.01d, (expectedBounds[axis + 3] - expectedBounds[axis]) * 2e-7d);
            if (!double.IsFinite(actualBounds[axis]) || !double.IsFinite(actualBounds[axis + 3])
                || Math.Abs(actualBounds[axis] - expectedBounds[axis]) > tolerance
                || Math.Abs(actualBounds[axis + 3] - expectedBounds[axis + 3]) > tolerance
                || Math.Abs((actualBounds[axis + 3] - actualBounds[axis])
                    - (expectedBounds[axis + 3] - expectedBounds[axis])) > tolerance)
                throw PrecisionFailure(source.Name ?? "");
        }
        return actualBounds;
    }

    private static InvalidDataException PrecisionFailure(string id)
        => new("FBX \u5bfc\u51fa\u7cbe\u5ea6\u4e0d\u8db3\uff0c\u6784\u4ef6\u4f4d\u7f6e\u6216\u51e0\u4f55\u4e0e\u539f\u59cb\u6beb\u7c73\u5750\u6807\u4e0d\u4e00\u81f4\uff1a" + id
            + "\u3002\u8bf7\u542f\u7528\u9879\u76ee\u8fd1\u539f\u70b9\u5bfc\u51fa\uff08\u9ed8\u8ba4\u5f00\u542f\uff09\u540e\u91cd\u8bd5\u3002");

    private static double[] EmptyBounds() => new[] { double.PositiveInfinity, double.PositiveInfinity,
        double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };

    private static void Include(double[] bounds, D3 point)
    {
        bounds[0] = Math.Min(bounds[0], point.X); bounds[3] = Math.Max(bounds[3], point.X);
        bounds[1] = Math.Min(bounds[1], point.Y); bounds[4] = Math.Max(bounds[4], point.Y);
        bounds[2] = Math.Min(bounds[2], point.Z); bounds[5] = Math.Max(bounds[5], point.Z);
    }

    private static Dictionary<string, Mesh> Parts(Scene scene, Node node)
    {
        var result = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        foreach (var index in node.MeshIndices)
        {
            if ((uint)index >= (uint)scene.MeshCount) throw new InvalidDataException("Invalid FBX mesh index.");
            var mesh = scene.Meshes[index];
            if ((uint)mesh.MaterialIndex >= (uint)scene.MaterialCount
                || !result.TryAdd(scene.Materials[mesh.MaterialIndex].Name, mesh))
                throw new InvalidDataException("Invalid or duplicate FBX material assignment.");
        }
        return result;
    }

    private static void CheckGeometry(Mesh expected, Mesh actual, CancellationToken cancellation)
    {
        // FBX can reorder/duplicate vertices. Compare oriented polygon corners and
        // authored normals instead of assuming identical indexing after import.
        if (!actual.HasNormals || actual.Normals.Count != actual.VertexCount)
            throw new InvalidDataException("FBX readback lost vertex normals.");
        for (var faceIndex = 0; faceIndex < expected.FaceCount; faceIndex++)
        {
            cancellation.ThrowIfCancellationRequested();
            var a = expected.Faces[faceIndex];
            var b = actual.Faces[faceIndex];
            if (a.IndexCount < 3 || a.IndexCount != b.IndexCount)
                throw new InvalidDataException("FBX readback changed polygon topology.");
            for (var corner = 0; corner < a.IndexCount; corner++)
            {
                var ai = a.Indices[corner];
                var bi = b.Indices[corner];
                if ((uint)bi >= (uint)actual.VertexCount
                    || !VectorNear(expected.Vertices[ai], actual.Vertices[bi])
                    || Vector3.Distance(expected.Normals[ai], actual.Normals[bi]) > 1e-4f)
                    throw new InvalidDataException("FBX readback changed triangle coordinates, winding or normals.");
            }
        }
    }

    private static void CheckMaterial(Material expected, Material actual)
    {
        if (Vector4.Distance(expected.ColorDiffuse, actual.ColorDiffuse) > 1e-4f
            || Math.Abs(expected.Opacity - actual.Opacity) > 1e-4f)
            throw new InvalidDataException("FBX readback changed a material's colour or opacity.");
    }

    private static bool MatrixNear(Matrix4x4 a, Matrix4x4 b)
    {
        var translationsMatch = VectorNear(new Vector3(a.M14, a.M24, a.M34), new Vector3(b.M14, b.M24, b.M34));
        a.M14 = b.M14 = a.M24 = b.M24 = a.M34 = b.M34 = 0f;
        return translationsMatch && a.Equals(b);
    }

    private static bool MetadataEqual(object a, object b)
        => b is double number && a is float or double
            ? Math.Abs(Convert.ToDouble(a, CultureInfo.InvariantCulture) - number) <= Math.Max(1e-7d, Math.Abs(number) * 2e-7d)
            : Equals(a, b);

    private static bool VectorNear(Vector3 a, Vector3 b)
        => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);

    private static bool Near(float a, float b)
        => float.IsFinite(a) && float.IsFinite(b)
            && Math.Abs((double)a - b) <= Math.Max(0.001d, Math.Abs((double)a) * 2e-7d);

    private static IEnumerable<Node> Nodes(Node node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Nodes(child)) yield return descendant;
    }

    private static string NodeName(string kind, string? label, string id)
    {
        var result = "WL_" + kind + ": " + (label ?? "") + " [" + id + "]";
        // aiString has a 1024-byte buffer. Reject truncation/name rewriting rather
        // than silently destroying stable IDs, UTF-8 names or origin precision.
        if (result.IndexOf('\0') >= 0 || result.Contains("$AssimpFbx$", StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(result) > 1000)
            throw new ArgumentException("An FBX node name/ID exceeds Assimp's limits or contains a reserved marker.");
        return result;
    }

    private static void Set(Metadata metadata, string key, string value)
    {
        if (value.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(value) > 1000)
            throw new ArgumentException("An FBX metadata value exceeds Assimp's string limits.");
        metadata.Add(key, new Metadata.Entry(MetaDataType.String, value));
    }

    private static void Set(Metadata metadata, string key, double value)
        => metadata.Add(key, new Metadata.Entry(MetaDataType.Double, value));
    private static void Set(Metadata metadata, string key, int value)
        => metadata.Add(key, new Metadata.Entry(MetaDataType.Int32, value));
    private static double Length(D3 value) => Math.Sqrt(value.X*value.X+value.Y*value.Y+value.Z*value.Z);
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static double Finite(double value) => double.IsFinite(value) ? value
        : throw new ArgumentException("The scene contains a non-finite value.");
    private static double Channel(double value) => Finite(value) >= 0d && value <= 1d ? value
        : throw new ArgumentException("Material colour/opacity must be between zero and one.");

    private static D3 Point(double[] values, string description)
    {
        if (values == null || values.Length != 3) throw new ArgumentException("Invalid " + description + ".");
        return new D3(Finite(values[0]), Finite(values[1]), Finite(values[2]));
    }

    private readonly record struct D3(double X, double Y, double Z)
    {
        public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static D3 Cross(D3 a, D3 b) => new(a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public D3 Unit()
        {
            var length = Math.Sqrt(X * X + Y * Y + Z * Z);
            if (!double.IsFinite(length) || length <= 1e-15)
                throw new ArgumentException("The scene contains a degenerate triangle or undefined smooth normal.");
            return new D3(X / length, Y / length, Z / length);
        }
        public Vector3 ToSingle()
        {
            var result = new Vector3((float)X, (float)Y, (float)Z);
            if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
                throw new ArgumentException("A coordinate exceeds Assimp's floating-point range.");
            return result;
        }
    }
}
