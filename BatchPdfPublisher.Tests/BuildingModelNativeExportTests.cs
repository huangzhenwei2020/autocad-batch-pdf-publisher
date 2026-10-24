using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using BatchPdfPublisher.BuildingModel;
using BuildingModelStudio.AvaloniaProbe;

internal static class BuildingModelNativeExportTests
{
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 5 && args[0] == "--commit-fixture")
            {
                var source = args[1]; var destination = args[2]; var barrier = args[3];
                File.WriteAllText(barrier + "." + args[4], "ready");
                var clock = Stopwatch.StartNew();
                while (!File.Exists(barrier))
                { if (clock.Elapsed.TotalSeconds > 15) throw new TimeoutException("Commit barrier."); await Task.Delay(10); }
                for (var i = 0; i < 30; i++)
                    await ModelExportService.CommitAsync(source, destination,
                        JsonSerializer.Serialize(new { Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) }), CancellationToken.None);
                return 0;
            }
            BuildingModelSceneExportTests.Run();
            FbxPlanarFacesTests.Run();
            FbxPrecisionCleanupTests.Run();
            FbxDisplayLayerTests.Run();
            await CheckCommitRecoveryAsync();
            if (args.Length == 0) return 0;
            var executable = Path.GetFullPath(args[0]);
            var folder = Path.GetFullPath(".artifacts/model-export-tests/" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var model = SampleModelFactory.CreateTwoStoreyHouse();
            var original = BuildingModelJson.ToJson(model);
            var progress = new Progress<ModelExportProgress>(p => Console.WriteLine(p.Message + " " + p.RunDirectory));
            var fbx = Path.Combine(folder, "测试方案 带空格.fbx");
            var result = await ModelExportService.ExportAsync(model, fbx, ModelExportFormat.Fbx, null, true,
                null, progress, CancellationToken.None, executable);
            var magic = "Kaydara FBX Binary  \0"u8.ToArray();
            Check(File.ReadAllBytes(fbx).Take(magic.Length).SequenceEqual(magic), "Not binary FBX.");
            VerifyManifest(result, original, model);
            Check(result.Polygons < result.Triangles, "FBX did not dissolve coplanar transport triangles.");
            var old = File.ReadAllBytes(fbx); var oldMeta = File.ReadAllBytes(fbx + ".wanluo.json");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await ModelExportService.ExportAsync(model, fbx, ModelExportFormat.Fbx, null, true,
                    null, progress, cancel.Token, executable); throw new Exception("Cancellation ignored."); }
                catch (OperationCanceledException) { }
            }
            Check(old.SequenceEqual(File.ReadAllBytes(fbx)), "Cancellation changed destination.");
            try { await ModelExportService.ExportAsync(model, fbx, ModelExportFormat.Fbx, "no-such-storey", true,
                null, progress, CancellationToken.None, executable); throw new Exception("Bad scope accepted."); }
            catch (IOException) { }
            Check(old.SequenceEqual(File.ReadAllBytes(fbx)) && oldMeta.SequenceEqual(File.ReadAllBytes(fbx + ".wanluo.json")),
                "Failed worker changed previous exports.");
            // An existing read-locked sidecar must make the two-file commit restore the previous model.
            await using (var locked = new FileStream(fbx + ".wanluo.json", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { await ModelExportService.ExportAsync(model, fbx, ModelExportFormat.Fbx, model.Storeys[0].Id,
                    false, null, progress, CancellationToken.None, executable); throw new Exception("Locked manifest replaced."); }
                catch (IOException) { }
            }
            Check(old.SequenceEqual(File.ReadAllBytes(fbx)) && oldMeta.SequenceEqual(File.ReadAllBytes(fbx + ".wanluo.json")),
                "Commit rollback lost old model or manifest.");
            result = await ModelExportService.ExportAsync(model, fbx, ModelExportFormat.Fbx, model.Storeys[0].Id,
                false, null, progress, CancellationToken.None, executable);
            Check(result.Storeys == 1, "Single-storey export contains more floors.");
            VerifyManifest(result, original, model);
            Console.WriteLine("FBX_EXPORT_PROCESS_OK binary roundtrip scope atomicPair rollback cancellation sourceUnchanged " + folder);
            var precise = new BuildingModelDocument { Name = "Far CAD origin" };
            precise.Storeys.Add(new StoreyModel { Id = "F", Height = 3000 });
            foreach (var x in new[] { 1_000_000_200d, 1_000_000_713d, 1_000_001_224d })
                precise.Columns.Add(new ColumnModel { Id = "P" + precise.Columns.Count, StoreyId = "F", X = x, Y = 200, Width = 400, Depth = 400 });
            var precisionPath = Path.Combine(folder, "precision.fbx");
            try { await ModelExportService.ExportAsync(precise, precisionPath, ModelExportFormat.Fbx, null, false,
                null, progress, CancellationToken.None, executable); throw new Exception("Interior far-origin precision loss accepted."); }
            catch (IOException) { }
            Check(!File.Exists(precisionPath), "Inaccurate far-origin output committed.");
            await ModelExportService.ExportAsync(precise, precisionPath, ModelExportFormat.Fbx, null, true,
                null, progress, CancellationToken.None, executable);
            Console.WriteLine("FBX_PRECISION_OK internalInstanceRejected defaultNearOriginPreserved");
            var curved = SampleModelFactory.CreateTwoStoreyHouse();
            curved.Openings[0].OpenIn3D = true; curved.Openings[0].PlanOpenAngle = 47;
            curved.Walls.Add(new WallModel { Id = "curve", StoreyId = curved.Storeys[0].Id,
                X1 = 11000, X2 = 9000, Bulge = 1, Thickness = 200 });
            var curvePath = Path.Combine(folder, "curves-open-pose.fbx");
            var curveOriginal = BuildingModelJson.ToJson(curved);
            result = await ModelExportService.ExportAsync(curved, curvePath, ModelExportFormat.Fbx, null,
                true, null, progress, CancellationToken.None, executable);
            VerifyManifest(result, curveOriginal, curved);
            File.WriteAllText(Path.Combine(folder, "curves-open-pose.scene.json"),
                JsonSerializer.Serialize(BuildingExportSceneBuilder.Build(curved)));
            Console.WriteLine("FBX_CURVED_POSE_OK retainedSurfaces normals scope identities");
            var api = args.Length > 1 ? args[1] : ModelExportService.FindSketchUpApiDirectory();
            if (api == null) Console.WriteLine("SKP_EXPORT_SKIPPED no local SketchUp2026 API");
            else
            {
                var skp = Path.Combine(folder, "方案模型.skp");
                result = await ModelExportService.ExportAsync(model, skp, ModelExportFormat.Skp, null, true,
                    api, progress, CancellationToken.None, executable);
                VerifyManifest(result, original, model);
                Check(result.Bytes > 1000, "Empty SKP model.");
                Console.WriteLine("SKP_EXPORT_PROCESS_OK nativeSaveReadback scope components units " + skp);
            }
            if (args.Contains("--large"))
            {
                for (var i = 3; i <= 50; i++) model.Storeys.Add(new StoreyModel { Id = "STD" + i,
                    Name = i + "层", Height = 3600, Elevation = (i - 1) * 3600,
                    TemplateStoreyId = model.Storeys[0].Id });
                var beforeLarge = BuildingModelJson.ToJson(model);
                result = await ModelExportService.ExportAsync(model, Path.Combine(folder, "50-storeys.fbx"),
                    ModelExportFormat.Fbx, null, true, null, progress, CancellationToken.None, executable);
                Check(result.Storeys == 50, "Large FBX lost standard floors."); VerifyManifest(result, beforeLarge, model);
                if (api != null)
                {
                    result = await ModelExportService.ExportAsync(model, Path.Combine(folder, "50-storeys.skp"),
                        ModelExportFormat.Skp, null, true, api, progress, CancellationToken.None, executable);
                    Check(result.Storeys == 50 && result.Meshes < result.Elements / 2, "Large SKP failed component reuse.");
                    VerifyManifest(result, beforeLarge, model);
                }
                Console.WriteLine("LARGE_EXPORT_OK 50storeys sharedSkpDefinitions boundedWorker " + folder);
            }
            var fixtureOption = Array.IndexOf(args, "--model-fixture");
            if (fixtureOption >= 0)
            {
                var snapshotPath = args[fixtureOption + 1];
                var snapshotBytes = File.ReadAllBytes(snapshotPath);
                var snapshot = BuildingModelJson.FromJson(System.Text.Encoding.UTF8.GetString(snapshotBytes));
                var snapshotJson = BuildingModelJson.ToJson(snapshot);
                File.WriteAllText(Path.Combine(folder, "private-fixture.scene.json"),
                    JsonSerializer.Serialize(BuildingExportSceneBuilder.Build(snapshot)));
                foreach (var format in api == null || args.Contains("--fbx-fixture-only") ? new[] { ModelExportFormat.Fbx }
                    : new[] { ModelExportFormat.Fbx, ModelExportFormat.Skp })
                {
                    result = await ModelExportService.ExportAsync(snapshot,
                        Path.Combine(folder, "private-fixture." + (format == ModelExportFormat.Fbx ? "fbx" : "skp")),
                        format, null, true, api, progress, CancellationToken.None, executable);
                    VerifyManifest(result, snapshotJson, snapshot);
                    if(format==ModelExportFormat.Fbx&&args.Contains("--expect-fbx-cleanup"))
                        Check(result.Warnings.Any(w=>w.StartsWith("FBX 微小面已自动清理：",StringComparison.Ordinal)),"Cleanup warnings missing from result/sidecar.");
                    Check(result.Elements == BuildingExportSceneBuilder.Build(snapshot).Instances.Count,
                        "Private fixture lost elements during native export.");
                }
                Check(snapshotBytes.SequenceEqual(File.ReadAllBytes(snapshotPath)), "Private source snapshot changed.");
                Console.WriteLine("PRIVATE_MODEL_EXPORT_OK sourceUnchanged allElements nativeReadback " + folder);
            }
            Console.WriteLine("MODEL_EXPORT_TESTS_OK " + folder);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task CheckCommitRecoveryAsync()
    {
        var folder = Path.GetFullPath(".artifacts/export-commit-tests/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "race.fbx");
        var a = Path.Combine(folder, "a.bin"); var b = Path.Combine(folder, "b.bin");
        File.WriteAllText(a, new string('A', 10000)); File.WriteAllText(b, new string('B', 10000));
        var barrier = Path.Combine(folder, "start");
        Process Child(string source, string marker)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(BuildingModelNativeExportTests).Assembly.Location);
            foreach (var value in new[] { "--commit-fixture", source, target, barrier, marker }) start.ArgumentList.Add(value);
            return Process.Start(start)!;
        }
        using var first = Child(a, "A"); using var second = Child(b, "B");
        var clock = Stopwatch.StartNew();
        try
        {
            while (!File.Exists(barrier + ".A") || !File.Exists(barrier + ".B"))
            { Check(clock.Elapsed.TotalSeconds < 15 && !first.HasExited && !second.HasExited, "Commit subprocess failed."); await Task.Delay(10); }
            File.WriteAllText(barrier, "start");
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
            Check(first.ExitCode == 0 && second.ExitCode == 0, "Concurrent commit failed.");
            using var meta = JsonDocument.Parse(File.ReadAllText(target + ".wanluo.json"));
            Check(meta.RootElement.GetProperty("Sha256").GetString() == Hash(target), "Concurrent commit mismatched model and metadata.");
        }
        finally { foreach (var p in new[] { first, second }) if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(); } }
        var oldModel = File.ReadAllBytes(target); var oldMeta = File.ReadAllBytes(target + ".wanluo.json");
        var id = Guid.NewGuid().ToString("N"); var temp = Path.Combine(folder, ".wanluo-" + id + ".fbx");
        File.Copy(target, temp + ".backup");
        File.WriteAllText(target, "Interrupted replacement");
        File.WriteAllText(target + ".wanluo.txn.json", JsonSerializer.Serialize(new { Id = id, HadModel = true,
            HadMetadata = true, OldModelHash = Convert.ToHexString(SHA256.HashData(oldModel)),
            OldMetadataHash = Convert.ToHexString(SHA256.HashData(oldMeta)), NewModelHash = Hash(target),
            NewMetadataHash = "unused-not-yet-committed" }));
        ModelExportService.RecoverPendingCommit(target);
        Check(oldModel.SequenceEqual(File.ReadAllBytes(target)) && oldMeta.SequenceEqual(File.ReadAllBytes(target + ".wanluo.json")),
            "Interrupted pair was not recovered.");
        id = Guid.NewGuid().ToString("N"); temp = Path.Combine(folder, ".wanluo-" + id + ".fbx");
        File.Copy(target, temp + ".backup"); File.Copy(target + ".wanluo.json", temp + ".json.backup");
        File.WriteAllText(target, "New model from interrupted completed commit");
        var newBytes = File.ReadAllBytes(target);
        File.WriteAllText(target + ".wanluo.json", "External metadata edit");
        File.WriteAllText(target + ".wanluo.txn.json", JsonSerializer.Serialize(new { Id = id, HadModel = true,
            HadMetadata = true, OldModelHash = Convert.ToHexString(SHA256.HashData(oldModel)),
            OldMetadataHash = Convert.ToHexString(SHA256.HashData(oldMeta)), NewModelHash = Hash(target), NewMetadataHash = "original-new-metadata" }));
        try { ModelExportService.RecoverPendingCommit(target); throw new Exception("External conflict accepted."); }
        catch (IOException) { }
        Check(newBytes.SequenceEqual(File.ReadAllBytes(target)) && File.ReadAllText(target + ".wanluo.json") == "External metadata edit",
            "Conflict preflight changed model before checking metadata.");
        File.WriteAllBytes(target + ".wanluo.json", oldMeta);
        ModelExportService.RecoverPendingCommit(target);
        Check(oldModel.SequenceEqual(File.ReadAllBytes(target)), "Resolved conflict did not recover.");
        Console.WriteLine("EXPORT_COMMIT_OK crossProcessLock 60commits interruptedRecovery conflictPreflight " + folder);
        static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    }

    private static void VerifyManifest(ModelExportResult result, string original, BuildingModelDocument source)
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(result.Path + ".wanluo.json"));
        Check(meta.RootElement.GetProperty("Sha256").GetString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.Path))),
            "Manifest hash does not match model.");
        Check(meta.RootElement.GetProperty("Units").GetString() == "mm", "Manifest units lost.");
        Check(meta.RootElement.GetProperty("Instances").GetArrayLength() == result.Elements, "Instance identities lost.");
        Check(meta.RootElement.GetProperty("SerializedPolygonCount").GetInt32() == result.Polygons
            && meta.RootElement.GetProperty("SerializedTriangleEquivalent").GetInt32() == result.Triangles,
            "Displayed/serialized topology statistics differ.");
        if (Path.GetExtension(result.Path).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
        {
            Check(meta.RootElement.GetProperty("PlanarPartition").GetString() == "ConvexWithCornerCuts",
                "FBX did not report the corner-preserving partition policy.");
            Check(meta.RootElement.GetProperty("DisplayLayerCount").GetInt32() == result.Storeys
                && meta.RootElement.GetProperty("DisplayLayers").GetArrayLength() == result.Storeys
                && meta.RootElement.GetProperty("ObjectNames").GetArrayLength() == result.Elements,
                "FBX floor layers or readable-name mapping missing.");
        }
        Check(BuildingModelJson.ToJson(source) == original, "Export changed source model.");
    }
}
