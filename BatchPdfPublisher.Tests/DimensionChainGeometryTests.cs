using BatchPdfPublisher.Services;

static class DimensionChainGeometryTests
{
    static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid edit accepted"); }
    static void Main()
    {
        var eps = 1e-6;
        var stations = DimensionChainGeometry.Stations(new[] { 6000d, 2400, 0, 2400 + eps / 2 }, eps);
        Check(stations.SequenceEqual(new[] { 0d, 2400, 6000 }), "split and duplicate station");
        Check(DimensionChainGeometry.MoveStation(stations, 1, 0, eps) == 0, "zero collapse");
        Check(DimensionChainGeometry.MoveStation(stations, 1, 6000, eps) == 6000, "reverse collapse");
        Check(DimensionChainGeometry.Stations(new[] { 0d, .01, 1 }, eps).Length == 3, "small positive dimension rounded to zero must remain");
        Check(DimensionChainGeometry.MoveStation(stations, 0, -300, eps) == -300, "extend first");
        Check(DimensionChainGeometry.Nearest(stations, 5900) == 2, "end selection");
        Reject(() => DimensionChainGeometry.MoveStation(stations, 1, 7000, eps));
        Reject(() => DimensionChainGeometry.MoveStation(stations, 1, double.NaN, eps));
        Reject(() => DimensionChainGeometry.Stations(new[] { double.PositiveInfinity }, eps));
        Console.WriteLine("DIMENSION_CHAIN_CORE_OK split duplicates zeroCollapse positiveRounding reverse endExtension crossing invalid");
    }
}
