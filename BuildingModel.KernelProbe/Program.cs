using BatchPdfPublisher.BuildingModel;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: BuildingModel.KernelProbe <fixture-directory>");
    return 2;
}

var folder = Path.GetFullPath(args[0]);
Directory.CreateDirectory(folder);
var cases = new[]
{
    new { Name = "wall-window-before", Length = 5000d, Thickness = 240d, Offset = 2000d },
    new { Name = "wall-window-after", Length = 6000d, Thickness = 300d, Offset = 2400d }
};

foreach (var item in cases)
{
    var model = new BuildingModelDocument { Name = item.Name };
    model.Storeys.Add(new StoreyModel { Id = "F", Name = "一层", Elevation = 0d, Height = 3000d });
    model.Walls.Add(new WallModel
    {
        Id = "W", StoreyId = "F", X1 = 0d, Y1 = 0d,
        X2 = item.Length, Y2 = 0d, Thickness = item.Thickness
    });
    model.Openings.Add(new OpeningModel
    {
        Id = "O", HostWallId = "W", Kind = "窗", Offset = item.Offset,
        Width = 1200d, Height = 1200d, Sill = 900d
    });
    var path = Path.Combine(folder, item.Name + ".json");
    BuildingModelJson.SaveModel(path, model);
    Console.WriteLine(path);
}

return 0;
