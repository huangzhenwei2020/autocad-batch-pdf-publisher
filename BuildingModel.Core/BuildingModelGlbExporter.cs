using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using LibTessDotNet;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class GlbExportResult
    {
        public int StoreyCount { get; internal set; }
        public int ElementCount { get; internal set; }
        public int TriangleCount { get; internal set; }
        public long FileBytes { get; internal set; }
    }

    public static class BuildingModelGlbExporter
    {
        public static GlbExportResult Export(BuildingModelDocument source, string path,
            string storeyId = null, CancellationToken cancellation = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".glb",
                StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 .glb 文件。", nameof(path));
            // Freeze the semantic model: export never edits source objects or saved model files.
            var model = BuildingModelJson.FromJson(BuildingModelJson.ToJson(source));
            BuildingElementNames.EnsureWallCodes(model);
            if (storeyId != null && model.FindStorey(storeyId) == null)
                throw new ArgumentException("导出楼层不存在。", nameof(storeyId));
            cancellation.ThrowIfCancellationRequested();
            var volume = BuildingVolumeBuilder.Build(model, storeyId);
            if (volume.Faces.Count == 0) throw new InvalidOperationException("所选范围没有可导出的三维构件。");
            var root = ModelRoot.CreateModel();
            root.Asset.Generator = "WanLuo Building Model / SharpGLTF";
            root.Extras = new JsonObject { ["sourceUnits"] = "mm", ["units"] = "m",
                ["upAxis"] = "Y", ["schemaVersion"] = model.SchemaVersion };
            var building = root.UseScene(0).CreateNode(model.Name ?? "建筑模型");
            var floors = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            var materials = new Dictionary<string, MaterialBuilder>(StringComparer.Ordinal);
            var result = new GlbExportResult();
            foreach (var group in volume.Faces.GroupBy(f => (f.StoreyId, f.ElementId)))
            {
                cancellation.ThrowIfCancellationRequested();
                var floorId = group.Key.StoreyId ?? "";
                if (!floors.TryGetValue(floorId, out var floor))
                {
                    var storey = model.FindStorey(floorId);
                    floor = building.CreateNode(storey?.Name ?? floorId);
                    floor.Extras = new JsonObject { ["storeyId"] = floorId,
                        ["elevationMetres"] = (storey?.Elevation ?? 0) / 1000d,
                        ["templateStoreyId"] = storey?.TemplateStoreyId };
                    floors.Add(floorId, floor);
                }
                if (string.IsNullOrWhiteSpace(group.Key.ElementId))
                    throw new InvalidOperationException("三维构件缺少 ID，无法导出。");
                var sourceId = StandardStoreyLayout.SourceElementId(model, group.Key.ElementId);
                var wall = model.Walls.FirstOrDefault(w => w.Id == sourceId);
                var opening = model.Openings.FirstOrDefault(o => o.Id == sourceId);
                var name = wall != null ? BuildingElementNames.Wall(wall)
                    : opening != null ? BuildingElementNames.Opening(opening) : group.Key.ElementId;
                var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>(name);
                var triangles = 0;
                // Union only coplanar patches belonging to this element. Other walls' opening
                // grid cuts must not survive as edges in the exported mesh.
                foreach (var patches in group.GroupBy(f => (f.Kind,
                    Math.Round(f.NormalX, 6), Math.Round(f.NormalY, 6), Math.Round(f.NormalZ, 6),
                    Math.Round((f.Points[0].X * f.NormalX + f.Points[0].Y * f.NormalY
                        + f.Points[0].Z * f.NormalZ) / 1000d, 6),
                    f.Kind == "wall" ? null : f)))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var face = patches.First();
                    if (!materials.TryGetValue(face.Kind ?? "other", out var material))
                    {
                        material = Material(face.Kind);
                        materials.Add(face.Kind ?? "other", material);
                    }
                    var primitive = mesh.UsePrimitive(material);
                    foreach (var triangle in Triangulate(patches, cancellation))
                    {
                        primitive.AddTriangle(triangle.Item1, triangle.Item2, triangle.Item3);
                        triangles++;
                    }
                }
                if (triangles == 0) throw new InvalidOperationException("构件没有有效三角面：" + group.Key.ElementId);
                root.CreateMeshes(mesh);
                var element = floor.CreateNode(name);
                element.Mesh = root.LogicalMeshes[root.LogicalMeshes.Count - 1];
                element.Extras = new JsonObject { ["elementId"] = group.Key.ElementId,
                    ["sourceElementId"] = StandardStoreyLayout.SourceElementId(model, group.Key.ElementId),
                    ["storeyId"] = floorId, ["kind"] = group.First().Kind };
                result.ElementCount++;
                result.TriangleCount += triangles;
            }
            result.StoreyCount = floors.Count;
            var destination = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(folder);
            var temporary = Path.Combine(folder, ".wanluo-export-" + Guid.NewGuid().ToString("N") + ".glb");
            try
            {
                cancellation.ThrowIfCancellationRequested();
                root.SaveGLB(temporary);
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
                result.FileBytes = new FileInfo(destination).Length;
                return result;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static Vector3 Convert(Point3DModel p)
        {
            var value = new Vector3((float)(p.X / 1000d), (float)(p.Z / 1000d), (float)(-p.Y / 1000d));
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
                throw new InvalidOperationException("模型含无效坐标。");
            return value;
        }

        private static IEnumerable<Tuple<VertexPositionNormal, VertexPositionNormal, VertexPositionNormal>>
            Triangulate(IEnumerable<VolumeFace> patches, CancellationToken cancellation)
        {
            var face = patches.First();
            var normal = new Vector3((float)face.NormalX, (float)face.NormalZ, (float)-face.NormalY);
            if (!float.IsFinite(normal.LengthSquared()) || normal.LengthSquared() < 0.000001f)
                throw new InvalidOperationException("模型含无效法线。");
            normal = Vector3.Normalize(normal);
            var union = new Tess();
            foreach (var patch in patches)
            {
                cancellation.ThrowIfCancellationRequested();
                if (patch.Points == null || patch.Points.Count < 3)
                    throw new InvalidOperationException("模型含不完整的面。");
                var points = patch.Points.Select(Convert).ToArray();
                var area = Vector3.Zero;
                for (var i = 1; i + 1 < points.Length; i++)
                    area += Vector3.Cross(points[i] - points[0], points[i + 1] - points[0]);
                if (Vector3.Dot(area, normal) < 0) Array.Reverse(points);
                union.AddContour(points.Select(p => new ContourVertex
                    { Position = new Vec3 { X = p.X, Y = p.Y, Z = p.Z } }).ToArray());
            }
            union.Tessellate(WindingRule.NonZero, ElementType.BoundaryContours,
                normal: new Vec3 { X = normal.X, Y = normal.Y, Z = normal.Z });
            var tess = new Tess();
            for (var boundary = 0; boundary < union.ElementCount; boundary++)
            {
                var start = union.Elements[boundary * 2];
                var count = union.Elements[boundary * 2 + 1];
                var contour = new List<ContourVertex>();
                for (var i = 0; i < count; i++)
                {
                    Vector3 At(int j)
                    {
                        var p = union.Vertices[start + (j + count) % count].Position;
                        return new Vector3(p.X, p.Y, p.Z);
                    }
                    var incoming = At(i) - At(i - 1);
                    var outgoing = At(i + 1) - At(i);
                    // Remove collinear grid vertices without removing corners or hole boundaries.
                    if (Vector3.Dot(incoming, outgoing) >= 0 &&
                        Vector3.Cross(incoming, outgoing).LengthSquared() <=
                        1e-12f * incoming.LengthSquared() * outgoing.LengthSquared()) continue;
                    contour.Add(union.Vertices[start + i]);
                }
                if (contour.Count >= 3) tess.AddContour(contour.ToArray());
            }
            tess.Tessellate(WindingRule.NonZero, ElementType.Polygons, 3,
                normal: new Vec3 { X = normal.X, Y = normal.Y, Z = normal.Z });
            for (var i = 0; i < tess.ElementCount; i++)
            {
                Vector3 Point(int offset)
                {
                    var v = tess.Vertices[tess.Elements[i * 3 + offset]].Position;
                    return new Vector3(v.X, v.Y, v.Z);
                }
                var a = Point(0); var b = Point(1); var c = Point(2);
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() < 1e-18f) continue;
                if (Vector3.Dot(cross, normal) < 0) { var swap = b; b = c; c = swap; }
                yield return Tuple.Create(new VertexPositionNormal(a, normal),
                    new VertexPositionNormal(b, normal), new VertexPositionNormal(c, normal));
            }
        }

        private static MaterialBuilder Material(string kind)
        {
            var color = kind == "slab" || kind == "bay-cap" || kind == "roof" ? new Vector4(0.6f, 0.65f, 0.7f, 1)
                : kind == "glass" ? new Vector4(0.35f, 0.65f, 0.78f, 0.35f)
                : kind == "wall" ? new Vector4(0.48f, 0.66f, 0.8f, 1)
                : new Vector4(0.65f, 0.7f, 0.75f, 1);
            var material = new MaterialBuilder(kind ?? "other").WithMetallicRoughnessShader()
                .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA, color)
                .WithChannelParam(KnownChannel.MetallicRoughness, KnownProperty.MetallicFactor, 0f)
                .WithChannelParam(KnownChannel.MetallicRoughness, KnownProperty.RoughnessFactor, 0.8f);
            if (kind == "glass") material.WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND).WithDoubleSide(true);
            return material;
        }
    }
}
