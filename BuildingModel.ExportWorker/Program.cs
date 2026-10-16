using System.Security.Cryptography;
using System.Text.Json;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.BuildingModel.Export;

public static class ModelExportWorker
{
    private sealed class Request
    {
        public string Nonce { get; set; } = "";
        public string Format { get; set; } = "";
        public string ModelPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string ReceiptPath { get; set; } = "";
        public string? StoreyId { get; set; }
        public bool NearOrigin { get; set; } = true;
        public string? SketchUpApiDirectory { get; set; }
    }

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] != "--request")
                throw new ArgumentException("Use --request <job.json>.");
            var requestPath = Path.GetFullPath(args[1]);
            var jobRoot = Path.GetDirectoryName(requestPath)!;
            var request = JsonSerializer.Deserialize<Request>(ReadLimited(requestPath, 64 * 1024))
                ?? throw new InvalidDataException("Empty request.");
            if (!Guid.TryParseExact(request.Nonce, "N", out _)) throw new InvalidDataException("Invalid job ID.");
            foreach (var file in new[] { request.ModelPath, request.OutputPath, request.ReceiptPath })
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), jobRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Job files must stay in the job directory.");
            var extension = request.Format switch { "fbx" => ".fbx", "skp" => ".skp", _ => throw new InvalidDataException("Unknown format.") };
            if (!string.Equals(Path.GetExtension(request.OutputPath), extension, StringComparison.OrdinalIgnoreCase)
                || File.Exists(request.OutputPath) || File.Exists(request.ReceiptPath))
                throw new InvalidDataException("Output must be a new file with the selected extension.");
            Console.WriteLine("BUILD_SCENE");
            var model = BuildingModelJson.FromJson(ReadLimited(request.ModelPath, 128 * 1024 * 1024));
            var scene = BuildingExportSceneBuilder.Build(model, request.StoreyId, request.NearOrigin);
            if (scene.TriangleCount > 2_000_000 || scene.Instances.Count > 100_000)
                throw new InvalidDataException("模型超过本轮安全导出限额（200 万面 / 10 万构件），请分楼层导出。");
            Console.WriteLine($"WRITE_{request.Format.ToUpperInvariant()} elements={scene.Instances.Count} meshes={scene.Meshes.Count} triangles={scene.TriangleCount}");
            BuildingFbxWriter.ValidationResult? fbxValidation = null;
            if (request.Format == "fbx") fbxValidation = BuildingFbxWriter.Write(scene, request.OutputPath);
            else BuildingSkpWriter.Write(scene, request.OutputPath, request.SketchUpApiDirectory
                ?? throw new InvalidDataException("请选择 SketchUp 2026 安装目录。"));
            var info = new FileInfo(request.OutputPath);
            if (!info.Exists || info.Length == 0) throw new IOException("Writer did not produce a model.");
            using var output = File.OpenRead(request.OutputPath);
            var hash = Convert.ToHexString(SHA256.HashData(output));
            var result = new { request.Nonce, request.Format, Status = "Completed", Sha256 = hash,
                Storeys = scene.Storeys.Count, Elements = scene.Instances.Count, Meshes = fbxValidation?.MeshCount ?? scene.Meshes.Count,
                Triangles = fbxValidation?.TriangleCount ?? scene.TriangleCount,
                Polygons = fbxValidation?.PolygonCount ?? scene.TriangleCount, Bytes = info.Length,
                Manifest = new { SchemaVersion = 1, Source = "WanLuo", Units = "mm", UpAxis = "Z", scene.Name,
                    scene.OriginMillimetres, scene.Storeys, scene.Instances,
                    MeshIds = scene.Meshes.Select(m => m.Id).ToArray(), scene.Materials,
                    SharedGeometryPreserved = fbxValidation?.PreservesSharedMeshes ?? true,
                    SerializedMeshCount = fbxValidation?.MeshCount ?? scene.Meshes.Count,
                    SourceTriangleCount = scene.TriangleCount,
                    SerializedPolygonCount = fbxValidation?.PolygonCount ?? scene.TriangleCount,
                    SerializedTriangleEquivalent = fbxValidation?.TriangleCount ?? scene.TriangleCount,
                    CoplanarPolygonsMerged = fbxValidation != null,
                    PlanarPartition = fbxValidation != null ? "ConvexWithCornerCuts" : "TransportTriangles",
                    CoincidentControlPointsJoined = fbxValidation != null,
                    Warnings = fbxValidation?.Warnings.ToArray() ?? Array.Empty<string>(),
                    Representation = "StaticCurrentPose", Sha256 = hash } };
            File.WriteAllText(request.ReceiptPath, JsonSerializer.Serialize(result));
            Console.WriteLine("EXPORT_COMPLETE " + request.Nonce);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }

    private static string ReadLimited(string path, long maximum)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new InvalidDataException("Input exceeds the export safety limit.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
