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
        /// <summary>一个空模型：只有一层/二层两个楼层，用来从零开始画平面。</summary>
        public static BuildingModelDocument CreateEmptyModel(string name)
        {
            var model = new BuildingModelDocument { Name = string.IsNullOrWhiteSpace(name) ? "新建模型" : name };
            model.Storeys.Add(new StoreyModel { Id = "1F", Name = "一层", Elevation = 0d, Height = 3600d });
            model.Storeys.Add(new StoreyModel { Id = "2F", Name = "二层", Elevation = 3600d, Height = 3300d });
            return model;
        }

        /// <summary>
        /// 演示用门窗类型库：让程序在没有 CAD、也没有 TQLX 导出时也能试放门窗。
        /// 真实项目应当用 CAD 里 <c>TQLX</c> 导出的那份（它带做法/材料/图集）。
        /// </summary>
        public static OpeningTypeLibraryDocument CreateDemoOpeningLibrary()
        {
            var library = new OpeningTypeLibraryDocument
            {
                ProjectName = "演示类型库",
                ExportedAt = System.DateTime.Now.ToString("O")
            };
            library.Types.Add(Type("C1215", "窗", 1200d, 1500d, 900d, "单扇", "右平开", "铝合金", "图集 12J4-1"));
            library.Types.Add(Type("C1518", "窗", 1500d, 1800d, 900d, "双扇等分", "双向推拉", "铝合金", "图集 12J4-1"));
            library.Types.Add(Type("C1824", "窗", 1800d, 2400d, 900d, "三扇等分", "双扇平开", "铝合金", "图集 12J4-1"));
            library.Types.Add(Type("M0921", "门", 900d, 2100d, 0d, "单扇", "左平开", "木质", "图集 12J6"));
            library.Types.Add(Type("M1524", "门", 1500d, 2400d, 0d, "双扇等分", "双扇平开", "玻璃", "图集 12J6"));
            library.Templates.Add(new OpeningTemplateModel { Name = "普通双扇推拉窗", ElevationType = "普通窗", DivisionPreset = "双扇等分", OpeningMode = "双向推拉" });
            library.Templates.Add(new OpeningTemplateModel { Name = "普通单扇平开窗", ElevationType = "普通窗", DivisionPreset = "单扇", OpeningMode = "左平开" });
            library.Templates.Add(new OpeningTemplateModel { Name = "普通单扇门", ElevationType = "普通门", DivisionPreset = "单扇", OpeningMode = "左平开" });
            return library;
        }

        private static OpeningTypeModel Type(string code, string kind, double width, double height, double sill,
            string division, string opening, string material, string atlas)
        {
            return new OpeningTypeModel
            {
                Code = code, Kind = kind, Width = width, Height = height, Sill = sill,
                // ElevationType 与插件里的取值一致（普通门/普通窗）：生成器靠它判断"整樘都是门扇"
                ElevationType = "普通" + kind,
                DivisionPreset = division, OpeningMode = opening,
                // 做法参数按插件里的默认值（外框 50、中挺 50、安装缝 20、门扇内框 N 型 50）
                HasOuterFrame = true, OuterFrameWidth = 50d,
                HasMullion = true, MullionWidth = 50d,
                HasInstallationGap = true, InstallationGap = 20d,
                DoorFrameType = "N型", DoorFrameWidth = 50d,
                Material = material, AtlasName = atlas, Source = "演示类型库"
            };
        }

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
                    // 剖在 X = 2200：正好穿过一层南墙的窗（中心 2200、宽 1500），
                    // 这样样例剖面能看到"洞口处断面断开 + 窗台线/窗顶线"。
                    CutAxis = SectionAxis.CutX, CutPosition = 2200d, ViewSign = 1, ViewDepth = 12000d
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
