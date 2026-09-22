using System.Collections.Generic;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// 内置样例模型：一栋两层小房子（7200×5400，外墙 240 厚，一层 3600 / 二层 3300）。
    ///
    /// 用途是让 P0 的链路**不依赖任何图纸**就能验证：程序里打开它 → 生成四个立面与一个剖面 →
    /// 落图到 CAD 看效果。后面接入"提取图纸"后，这里就只作为回归样例保留。
    /// </summary>
    public static class SampleModelFactory
    {
        public static BuildingModelDocument CreateTwoStoreyHouse()
        {
            var model = new BuildingModelDocument { Name = "样例-两层小房子" };
            model.Storeys.Add(new StoreyModel { Id = "1F", Name = "一层", Elevation = 0d, Height = 3600d });
            model.Storeys.Add(new StoreyModel { Id = "2F", Name = "二层", Elevation = 3600d, Height = 3300d });

            const double width = 7200d;
            const double depth = 5400d;
            const double thickness = 240d;

            foreach (var storey in new[] { "1F", "2F" })
            {
                model.Walls.Add(new WallModel { Id = storey + "-S", StoreyId = storey, X1 = 0d, Y1 = 0d, X2 = width, Y2 = 0d, Thickness = thickness });
                model.Walls.Add(new WallModel { Id = storey + "-N", StoreyId = storey, X1 = 0d, Y1 = depth, X2 = width, Y2 = depth, Thickness = thickness });
                model.Walls.Add(new WallModel { Id = storey + "-W", StoreyId = storey, X1 = 0d, Y1 = 0d, X2 = 0d, Y2 = depth, Thickness = thickness });
                model.Walls.Add(new WallModel { Id = storey + "-E", StoreyId = storey, X1 = width, Y1 = 0d, X2 = width, Y2 = depth, Thickness = thickness });
            }

            // 一层南面：一樘窗 + 一樘门；北面、东面各一樘窗
            model.Openings.Add(Window("1F-S-C1518", "1F-S", "C1518", 2200d, 1500d, 1800d, 900d));
            model.Openings.Add(Door("1F-S-M0921", "1F-S", "M0921", 5400d, 900d, 2100d));
            model.Openings.Add(Window("1F-N-C1215", "1F-N", "C1215", 3000d, 1200d, 1500d, 900d));
            model.Openings.Add(Window("1F-E-C1215", "1F-E", "C1215", 2700d, 1200d, 1500d, 900d));
            // 二层南面一樘窗
            model.Openings.Add(Window("2F-S-C1518", "2F-S", "C1518", 3600d, 1500d, 1800d, 900d));

            var outline = new List<PointModel>
            {
                new PointModel(0d, 0d),
                new PointModel(width, 0d),
                new PointModel(width, depth),
                new PointModel(0d, depth)
            };
            // 二层楼板（板顶 = 二层标高）与屋面板
            model.Slabs.Add(new SlabModel { Id = "SLAB-2F", StoreyId = "2F", TopElevation = 3600d, Thickness = 120d, Outline = Clone(outline) });
            model.Slabs.Add(new SlabModel { Id = "SLAB-ROOF", StoreyId = "2F", TopElevation = 6900d, Thickness = 120d, Outline = Clone(outline) });

            model.Columns.Add(new ColumnModel { Id = "KZ-1", StoreyId = "1F", X = 600d, Y = 600d, Width = 400d, Depth = 400d });

            return model;
        }

        private static OpeningModel Window(string id, string hostWallId, string code, double offset, double width, double height, double sill)
        {
            return new OpeningModel
            {
                Id = id, HostWallId = hostWallId, Code = code, Kind = "窗",
                Offset = offset, Width = width, Height = height, Sill = sill
            };
        }

        private static OpeningModel Door(string id, string hostWallId, string code, double offset, double width, double height)
        {
            return new OpeningModel
            {
                Id = id, HostWallId = hostWallId, Code = code, Kind = "门",
                Offset = offset, Width = width, Height = height, Sill = 0d
            };
        }

        private static List<PointModel> Clone(List<PointModel> source)
        {
            var result = new List<PointModel>();
            foreach (var point in source) result.Add(new PointModel(point.X, point.Y));
            return result;
        }

        /// <summary>P0 的默认出图集合：四个立面 + 一个剖面（切在 X = 3600）。</summary>
        public static List<ViewDefinitionModel> CreateDefaultViews(string name)
        {
            return new List<ViewDefinitionModel>
            {
                Elevation("elev-south", (name ?? "建筑") + " 南立面图", ElevationDirection.South),
                Elevation("elev-north", (name ?? "建筑") + " 北立面图", ElevationDirection.North),
                Elevation("elev-east", (name ?? "建筑") + " 东立面图", ElevationDirection.East),
                Elevation("elev-west", (name ?? "建筑") + " 西立面图", ElevationDirection.West),
                new ViewDefinitionModel
                {
                    Id = "section-1", Title = "1-1 剖面图", Kind = ViewKind.Section, Scale = 50,
                    CutAxis = SectionAxis.CutX, CutPosition = 3600d, ViewSign = 1, ViewDepth = 12000d
                }
            };
        }

        private static ViewDefinitionModel Elevation(string id, string title, ElevationDirection direction)
        {
            return new ViewDefinitionModel
            {
                Id = id, Title = title, Kind = ViewKind.Elevation, Scale = 100, Direction = direction
            };
        }
    }
}
