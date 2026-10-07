using BatchPdfPublisher.BuildingModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var root=Path.Combine(Path.GetTempPath(),"wanluo-assets-test-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
void Reject(Action action,string message){try{action();}catch(InvalidDataException){return;}catch(JsonException){return;}throw new Exception(message);}
string Zip(params (string path,byte[] data)[] entries){
    var path=Path.Combine(root,Guid.NewGuid()+".wlopkg");using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))
        foreach(var entry in entries){using var stream=zip.CreateEntry(entry.path).Open();stream.Write(entry.data);}return path;
}
try {
    var library=new ComponentAssetLibrary(Path.Combine(root,"library"));
    Check(library.Load().Assets.Count==0,"New library not empty");
    var type=new OpeningTypeModel {Code="M1525",Kind="门",Width=1500,Height=2500,Sill=0,ThresholdHeight=80,PlanOpenAngle=45,DefaultOpenIn3D=true};
    var asset=library.Save("双扇门",type);type.Width=900;
    var loaded=library.Load().Assets.Single();
    Check(loaded.Manifest.OpeningType.Width==1500&&loaded.Manifest.OpeningType.ThresholdHeight==80&&loaded.Manifest.OpeningType.PlanOpenAngle==45&&loaded.Manifest.OpeningType.DefaultOpenIn3D,"Params roundtrip lost");
    var destination=Path.Combine(root,"export.wlopkg");library.Export(loaded,destination);
    Check(SHA256.HashData(File.ReadAllBytes(destination)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(asset.PackagePath))),"Export hash changed");
    Check(library.Import(destination).PackagePath==asset.PackagePath&&library.Load().Assets.Count==1,"Duplicate import not idempotent");
    var other=new ComponentAssetLibrary(Path.Combine(root,"other-machine"));var transferred=other.Import(destination);
    Check(transferred.Sha256==asset.Sha256&&transferred.Manifest.OpeningType.Code=="M1525","Cross-library import lost identity");
    try{library.Export(asset,Path.Combine(library.Root,"another.wlopkg"));throw new Exception("Export can overwrite immutable library");}catch(IOException){}
    var manifest=loaded.Manifest;var json=JsonSerializer.SerializeToUtf8Bytes(manifest);
    Reject(()=>library.Import(Zip(("../manifest.json",json))),"Traversal accepted");
    Reject(()=>library.Import(Zip(("C:/manifest.json",json))),"Absolute path accepted");
    Reject(()=>library.Import(Zip(("manifest.json",json),("MANIFEST.JSON",json))),"Duplicate accepted");
    Reject(()=>library.Import(Zip(("manifest.json",json),("source/script.py",Encoding.UTF8.GetBytes("print(1)")))),"Executable source accepted");
    Reject(()=>library.Import(Zip(("manifest.json",new byte[ComponentAssetLibrary.MaxManifestBytes+1]))),"Decompression limit ignored");
    manifest.GeometryMode="External";Reject(()=>library.Import(Zip(("manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest)))),"External silently converted to parametric");manifest.GeometryMode="Parametric";
    manifest.Units="m";Reject(()=>library.Import(Zip(("manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest)))),"Wrong units accepted");manifest.Units="mm";
    manifest.OpeningType.Width=1600;Reject(()=>library.Import(Zip(("manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest)))),"Immutable revision overwritten");
    Check(library.Load().Assets.Single().Manifest.OpeningType.Width==1500,"Conflict changed stored params");
    Reject(()=>library.Save("bad",new OpeningTypeModel {Code="BAD",Width=double.NaN,Height=2000}),"NaN accepted");
    Reject(()=>library.Save("bad-angle",new OpeningTypeModel {Code="BAD",Width=900,Height=2100,PlanOpenAngle=190}),"Angle accepted");
    Reject(()=>library.Save("huge-grid",new OpeningTypeModel {Code="BAD",Width=900,Height=2100,DivisionPreset="自定义",CustomColumnRatios=string.Join(",",Enumerable.Repeat("1",40)),CustomRowRatios="1"}),"Grid limit ignored");
    File.WriteAllBytes(Path.Combine(library.Root,"broken.wlopkg"),new byte[]{1,2,3});
    var snapshot=library.Load();Check(snapshot.Assets.Count==1&&snapshot.Errors.Count==1,"Broken package hides good asset");
    var cancelled=new CancellationToken(true);try{library.Save("cancelled",loaded.Manifest.OpeningType,cancelled);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
    Check(!Directory.EnumerateFiles(library.Root,"*.tmp").Any(),"Partial writes left behind");
    Console.WriteLine("COMPONENT_ASSETS_OK roundtrip immutable duplicate exportHash traversal duplicatePath unsupported external units nan quota corruption cancellation");
}finally{Directory.Delete(root,true);}
