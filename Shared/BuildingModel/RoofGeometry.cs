using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// 坡屋面（双坡）的几何（纯计算、可单元测试）：檐口四点、屋脊两点、坡度与各面法线。
    ///
    /// 约定：檐口矩形 `X..X+Width × Y..Y+Depth`，屋脊在**半跨处**：
    /// - `AlongX = true`：屋脊沿 X（在 Y + Depth/2 处），朝 ±Y 两坡，坡度方向跨度 = Depth/2；
    /// - `AlongX = false`：屋脊沿 Y（在 X + Width/2 处），朝 ±X 两坡，跨度 = Width/2。
    /// 屋脊标高 = 檐口标高 + 跨度 × tan(坡度)。
    /// </summary>
    public sealed class RoofGeometry
    {
        public bool AlongX { get; set; }
        /// <summary>檐口矩形。</summary>
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        /// <summary>檐口标高与屋脊标高。</summary>
        public double EaveElevation { get; set; }
        public double RidgeElevation { get; set; }
        /// <summary>屋脊线（两个端点，平面坐标 + 标高）。</summary>
        public Point3DModel RidgeStart { get; set; }
        public Point3DModel RidgeEnd { get; set; }
        /// <summary>坡度角（度）与坡度方向上的半跨（mm）。</summary>
        public double PitchDegrees { get; set; }
        public double HalfSpan { get; set; }
        /// <summary>坡面斜长（檐口到屋脊的实际长度，mm）。</summary>
        public double SlopeLength { get; set; }
        /// <summary>屋面板厚。</summary>
        public double Thickness { get; set; } = 120d;

        /// <summary>参数的几何是否说得通（尺寸够、坡度在 1°~60°）。</summary>
        public bool IsValid
        {
            get
            {
                return (X1 - X0) > 100d && (Y1 - Y0) > 100d
                    && HalfSpan > 50d && PitchDegrees > 1d && PitchDegrees < 60d;
            }
        }

        /// <summary>檐口四点（逆时针，平面 + 檐口标高）。</summary>
        public List<Point3DModel> EaveCorners()
        {
            return new List<Point3DModel>
            {
                new Point3DModel(X0, Y0, EaveElevation),
                new Point3DModel(X1, Y0, EaveElevation),
                new Point3DModel(X1, Y1, EaveElevation),
                new Point3DModel(X0, Y1, EaveElevation)
            };
        }

        public static RoofGeometry Build(BuildingModelDocument model, RoofModel roof)
        {
            if (roof == null) return null;
            var storey = model == null ? null : model.FindStorey(roof.StoreyId);
            var eave = roof.EaveElevation > 0.5d
                ? roof.EaveElevation
                : (storey == null ? 0d : storey.Elevation + storey.Height);
            var width = Math.Abs(roof.Width) > 1d ? Math.Abs(roof.Width) : 7440d;
            var depth = Math.Abs(roof.Depth) > 1d ? Math.Abs(roof.Depth) : 5640d;
            var pitch = roof.PitchDegrees > 1d && roof.PitchDegrees < 89d ? roof.PitchDegrees : 26.565d;
            var halfSpan = (roof.AlongX ? depth : width) / 2d;
            var rise = halfSpan * Math.Tan(pitch * Math.PI / 180d);

            var geometry = new RoofGeometry
            {
                AlongX = roof.AlongX,
                X0 = roof.X, Y0 = roof.Y, X1 = roof.X + width, Y1 = roof.Y + depth,
                EaveElevation = eave,
                RidgeElevation = eave + rise,
                PitchDegrees = pitch,
                HalfSpan = halfSpan,
                SlopeLength = Math.Sqrt(halfSpan * halfSpan + rise * rise),
                Thickness = roof.Thickness > 20d ? roof.Thickness : 120d
            };

            if (roof.AlongX)
            {
                var y = geometry.Y0 + halfSpan;
                geometry.RidgeStart = new Point3DModel(geometry.X0, y, geometry.RidgeElevation);
                geometry.RidgeEnd = new Point3DModel(geometry.X1, y, geometry.RidgeElevation);
            }
            else
            {
                var x = geometry.X0 + halfSpan;
                geometry.RidgeStart = new Point3DModel(x, geometry.Y0, geometry.RidgeElevation);
                geometry.RidgeEnd = new Point3DModel(x, geometry.Y1, geometry.RidgeElevation);
            }
            return geometry;
        }

        /// <summary>
        /// 屋面实体（体量用）：檐口矩形 + 双坡 → 5 个面（底面 + 两个坡面 + 两端山墙三角）。
        /// </summary>
        public List<VolumeFace> ToFaces(string kind, string storeyId)
        {
            var faces = new List<VolumeFace>();
            if (!IsValid) return faces;
            var eave = EaveElevation;
            var ridge = RidgeElevation;
            var floorZ = eave - Thickness;

            if (AlongX)
            {
                var y = (Y0 + Y1) / 2d;
                // 底面
                faces.Add(new VolumeFace
                {
                    Kind = kind, StoreyId = storeyId, NormalZ = -1d,
                    Points = new List<Point3DModel>
                    {
                        new Point3DModel(X0, Y0, floorZ), new Point3DModel(X0, Y1, floorZ),
                        new Point3DModel(X1, Y1, floorZ), new Point3DModel(X1, Y0, floorZ)
                    }
                });
                // 南坡（朝 −Y 上斜）与北坡
                faces.Add(SlopeFace(kind, storeyId, X0, Y0, eave, X1, Y0, eave, X1, y, ridge, X0, y, ridge));
                faces.Add(SlopeFace(kind, storeyId, X1, Y1, eave, X0, Y1, eave, X0, y, ridge, X1, y, ridge));
                // 两端山墙三角
                faces.Add(new VolumeFace
                {
                    Kind = kind, StoreyId = storeyId, NormalX = 1d,
                    Points = new List<Point3DModel>
                    {
                        new Point3DModel(X1, Y0, eave), new Point3DModel(X1, Y1, eave),
                        new Point3DModel(X1, y, ridge)
                    }
                });
                faces.Add(new VolumeFace
                {
                    Kind = kind, StoreyId = storeyId, NormalX = -1d,
                    Points = new List<Point3DModel>
                    {
                        new Point3DModel(X0, Y1, eave), new Point3DModel(X0, Y0, eave),
                        new Point3DModel(X0, y, ridge)
                    }
                });
            }
            else
            {
                var x = (X0 + X1) / 2d;
                faces.Add(new VolumeFace
                {
                    Kind = kind, StoreyId = storeyId, NormalZ = -1d,
                    Points = new List<Point3DModel>
                    {
                        new Point3DModel(X0, Y0, floorZ), new Point3DModel(X0, Y1, floorZ),
                        new Point3DModel(X1, Y1, floorZ), new Point3DModel(X1, Y0, floorZ)
                    }
                });
                faces.Add(SlopeFace(kind, storeyId, X0, Y1, eave, X0, Y0, eave, x, Y0, ridge, x, Y1, ridge));
                faces.Add(SlopeFace(kind, storeyId, X1, Y0, eave, X1, Y1, eave, x, Y1, ridge, x, Y0, ridge));
                faces.Add(new VolumeFace
                {
                    Kind = kind, StoreyId = storeyId, NormalY = 1d,
                    Points = new List<Point3DModel>
                    {
                        new Point3DModel(X0, Y1, eave), new Point3DModel(X1, Y1, eave),
                        new Point3DModel(x, Y1, ridge)
                    }
                });
                faces.Add(new VolumeFace
                {
                    Kind = kind, StoreyId = storeyId, NormalY = -1d,
                    Points = new List<Point3DModel>
                    {
                        new Point3DModel(X1, Y0, eave), new Point3DModel(X0, Y0, eave),
                        new Point3DModel(x, Y0, ridge)
                    }
                });
            }
            return faces;
        }

        /// <summary>一片坡面：四个点（檐口两点 + 屋脊两点），法线 = (沿脊方向 × 坡向) 归一，再翻成朝上。</summary>
        private static VolumeFace SlopeFace(string kind, string storeyId,
            double eaveX1, double eaveY1, double z1, double eaveX2, double eaveY2, double z2,
            double ridgeX2, double ridgeY2, double ridgeZ2, double ridgeX1, double ridgeY1, double ridgeZ1)
        {
            var points = new List<Point3DModel>
            {
                new Point3DModel(eaveX1, eaveY1, z1), new Point3DModel(eaveX2, eaveY2, z2),
                new Point3DModel(ridgeX2, ridgeY2, ridgeZ2), new Point3DModel(ridgeX1, ridgeY1, ridgeZ1)
            };
            // A = 沿屋脊方向（檐口边），B = 坡向（檐口 → 屋脊）
            var ax = points[1].X - points[0].X;
            var ay = points[1].Y - points[0].Y;
            var az = points[1].Z - points[0].Z;
            var bx = points[3].X - points[0].X;
            var by = points[3].Y - points[0].Y;
            var bz = points[3].Z - points[0].Z;
            var nx = ay * bz - az * by;
            var ny = az * bx - ax * bz;
            var nz = ax * by - ay * bx;
            var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length > 1e-9d) { nx /= length; ny /= length; nz /= length; }
            if (nz < 0d) { nx = -nx; ny = -ny; nz = -nz; }        // 屋面法线一定要朝上
            return new VolumeFace
            {
                Kind = kind, StoreyId = storeyId,
                NormalX = nx, NormalY = ny, NormalZ = nz,
                Points = points
            };
        }
    }
}
