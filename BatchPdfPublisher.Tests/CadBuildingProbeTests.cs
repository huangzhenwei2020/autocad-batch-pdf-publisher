using System;
using System.IO;
using System.Linq;
using System.Globalization;
using BatchPdfPublisher.BuildingModel;
using WanLuo.CadInterop;

public sealed class ProbeFixture
{
    public double LeftWidth { get { return 20; } }
    public double RightWidth { get { return 100; } }
    public double Zero { get { return 0; } }
    public double Failing { get { throw new InvalidOperationException("fixture unavailable"); } }
    public double DangerousMethod() { throw new Exception("must not be invoked"); }
}

internal static class CadBuildingProbeTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static CadProbeField Read(object source, string name)
    {
        object value; string error;
        var success = TianzhengReadOnlyAccess.TryReadProperty(source, name, out value, out error);
        return CadBuildingProbeRules.Field(name, success, value, error);
    }

    private static int Main()
    {
        var folder = Path.Combine(Path.GetTempPath(), "WanLuoCadProbeTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new ProbeFixture();
            var wall = new CadBuildingProbeEntity { Handle = "123", Category = CadBuildingProbeRules.Classify("TCH_WALL", "") };
            wall.Fields.Add(Read(source, "LeftWidth")); wall.Fields.Add(Read(source, "RightWidth"));
            CadBuildingProbeRules.DeriveWall(wall);
            Check(wall.CandidateThickness == 120 && wall.CandidateAxisOffset == -40, "偏心墙被居中或方向反了");
            wall.Fields[0] = Read(source, "Missing"); CadBuildingProbeRules.DeriveWall(wall);
            Check(!wall.CandidateThickness.HasValue && !wall.CandidateAxisOffset.HasValue, "缺失墙厚仍然生成了候选数值");
            Check(Read(source, "Zero").Number == 0 && Read(source, "Missing").Status == "不可读", "零值和缺失混淆");
            Check(Read(source, "Failing").Error.Contains("fixture unavailable"), "没有保留底层读取原因");
            Check(Read(source, "DangerousMethod").Status == "不可读", "调用了方法而非只读属性");
            Check(Read(null, "Width").Status == "不可读", "代理/COM缺失误报成功");
            Console.WriteLine("PASS 偏心墙、零值/缺值、属性异常与只读访问");

            Check(CadBuildingProbeRules.Field("Width", true, double.NaN, null).Number == null, "NaN被接受");
            Check(CadBuildingProbeRules.Field("Width", true, double.PositiveInfinity, null).Number == null, "Infinity被接受");
            Check(CadBuildingProbeRules.Field("Width", true, "200", null).Number == null, "字符串被悄悄解释为尺寸");
            Check(CadBuildingProbeRules.Field("Point", true, new[] { 1d, 2d, 3d }, null).Numbers.Count == 3, "坐标数组丢失");
            Check(CadBuildingProbeRules.Field("Point", true, new[] { 1d, double.NaN }, null).Numbers == null, "非法坐标被接受");
            Check(CadBuildingProbeRules.Classify("FAKE_TCH_WALL_BLOCK", "") == "未适配对象", "普通块名称误判为原生墙");
            Check(CadBuildingProbeRules.Classify("TCH_OPENING", "").Contains("门/窗待核验"), "洞口被猜成门或窗");
            Console.WriteLine("PASS 非有限/未知数值不猜测，洞口类别及坐标数组明确");

            var report = new CadBuildingProbeDocument { DrawingPath = "测试.dwg", DbmodBefore = 1, DbmodAfter = 1 };
            report.Entities.Add(wall);
            wall.Fields.Add(Read(source, "Failing"));
            var path = Path.Combine(folder, "probe.json");
            CadBuildingProbeFile.Save(path, report);
            var reloaded = CadBuildingProbeFile.Load(path);
            Check(reloaded.Entities.Count == 1 && reloaded.Entities[0].Fields.Any(f => f.Status == "不可读"), "失败字段保存后丢失");
            Check(!reloaded.Entities[0].CandidateThickness.HasValue, "缺值变成了0");
            var first = File.ReadAllText(path);
            report.CadVersion = "24.1";
            CadBuildingProbeFile.Save(path, report);
            Check(File.ReadAllText(path + ".bak") == first, "覆盖未保留旧文件");
            report.SchemaVersion = 2; CadBuildingProbeFile.Save(path, report);
            var rejected = false;
            try { CadBuildingProbeFile.Load(path); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "未知版本没有拒绝");
            Check(!Directory.GetFiles(folder, "*.tmp").Any(), "遗留了暂存文件");
            Console.WriteLine("PASS 核验协议往返、缺值保留、原子覆盖备份及版本拒绝");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
