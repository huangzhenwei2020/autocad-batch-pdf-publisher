using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using BatchPdfPublisher.BuildingModel;
using HelixToolkit.Geometry;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;

namespace Wanluo.BuildingModelStudio.WpfPilot
{
    /// <summary>
    /// Read-only WPF migration pilot. It deliberately cannot save the model or
    /// publish CAD views; the current studio remains the production editor.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly List<MeshGeometryModel3D> _models = new List<MeshGeometryModel3D>();
        private readonly IEffectsManager _effects = new DefaultEffectsManager();
        internal bool PreviewReady { get; private set; }
        internal string DebugInfo
        {
            get
            {
                var triangles = _models.Sum(model => (model.Geometry as HelixToolkit.SharpDX.MeshGeometry3D)?.Indices?.Count / 3 ?? 0);
                var camera = Scene.Camera as HelixToolkit.Wpf.SharpDX.ProjectionCamera;
                return "models=" + _models.Count + " triangles=" + triangles
                    + " renderHost=" + (Scene.RenderHost == null ? "null" : Scene.RenderHost.GetType().Name)
                    + " camera=" + (camera == null ? "null" : camera.Position + " / " + camera.LookDirection);
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            Scene.EffectsManager = _effects;
            Scene.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
            {
                Position = new Point3D(16, -18, 14),
                LookDirection = new Vector3D(-12, 18, -10),
                UpDirection = new Vector3D(0, 0, 1),
                NearPlaneDistance = 0.01,
                FarPlaneDistance = 10000
            };
            Loaded += (sender, args) =>
            {
                var commandLine = Environment.GetCommandLineArgs();
                if (commandLine.Length > 2 && commandLine[1] == "--model") LoadFile(commandLine[2]);
                else LoadModel(SampleModelFactory.CreateTwoStoreyHouse(), "内置样例");
            };
            Closed += (sender, args) =>
            {
                Scene.Dispose();
                _effects.Dispose();
            };
        }

        private void OpenModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "打开建筑模型（只读）", Filter = "模型 JSON|model.json|JSON 文件|*.json" };
            if (dialog.ShowDialog(this) == true) LoadFile(dialog.FileName);
        }

        private void Sample_Click(object sender, RoutedEventArgs e)
        {
            LoadModel(SampleModelFactory.CreateTwoStoreyHouse(), "内置样例");
        }

        private void Fit_Click(object sender, RoutedEventArgs e)
        {
            Scene.ZoomExtents(0);
        }

        private void LoadFile(string path)
        {
            try { LoadModel(BuildingModelJson.LoadModel(path), path); }
            catch (Exception exception) { Status.Text = "读取失败：" + exception.Message; }
        }

        private void LoadModel(BuildingModelDocument model, string source)
        {
            try
            {
                var volume = BuildingVolumeBuilder.Build(model);
                if (volume.Faces.Count == 0) throw new InvalidOperationException("模型没有可预览的三维面。");
                foreach (var old in _models) Scene.Items.Remove(old);
                _models.Clear();
                foreach (var group in volume.Faces.GroupBy(face => face.Kind ?? "other"))
                {
                    var mesh = new MeshBuilder();
                    foreach (var face in group)
                    {
                        if (face.Points == null || face.Points.Count < 3) continue;
                        var first = ToMetres(face.Points[0]);
                        for (var index = 1; index + 1 < face.Points.Count; index++)
                            mesh.AddTriangle(first, ToMetres(face.Points[index]), ToMetres(face.Points[index + 1]));
                    }
                    var visual = new MeshGeometryModel3D
                    {
                        Geometry = mesh.ToMeshGeometry3D(),
                        Material = MaterialFor(group.Key),
                        CullMode = SharpDX.Direct3D11.CullMode.None
                    };
                    _models.Add(visual);
                    Scene.Items.Add(visual);
                }
                ModelName.Text = model.Name ?? "未命名模型";
                ModelStats.Text = "楼层 " + model.Storeys.Count + " · 墙 " + model.Walls.Count
                    + " · 门窗洞口 " + model.Openings.Count + "\n显示面 " + volume.Faces.Count;
                Status.Text = "只读预览：" + source + "。面数据由现有建筑参数生成，WPF + DirectX 负责绘制。";
                PreviewReady = _models.Count > 0;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Scene.ZoomExtents(0)));
            }
            catch (Exception exception) { PreviewReady = false; Status.Text = "三维预览失败：" + exception.Message; }
        }

        private static Vector3 ToMetres(Point3DModel point)
        {
            return new Vector3((float)(point.X / 1000d), (float)(point.Y / 1000d), (float)(point.Z / 1000d));
        }

        private static PhongMaterial MaterialFor(string kind)
        {
            switch (kind)
            {
                case "wall": return ColorMaterial(0.36f, 0.68f, 0.96f);
                case "slab": return ColorMaterial(0.43f, 0.53f, 0.68f);
                case "column": return ColorMaterial(0.28f, 0.82f, 0.83f);
                case "roof": return ColorMaterial(0.56f, 0.49f, 0.84f);
                default: return ColorMaterial(0.70f, 0.76f, 0.85f);
            }
        }

        private static PhongMaterial ColorMaterial(float red, float green, float blue)
        {
            return new PhongMaterial { DiffuseColor = new HelixToolkit.Maths.Color4(red, green, blue, 1f) };
        }
    }
}
