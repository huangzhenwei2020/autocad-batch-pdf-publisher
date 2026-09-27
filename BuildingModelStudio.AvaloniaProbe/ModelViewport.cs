using System.Numerics;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using BatchPdfPublisher.BuildingModel;
using static Avalonia.OpenGL.GlConsts;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ModelViewport : OpenGlControlBase
{
    private const int GuideLinesPrimitive = 0x0001; // GL_LINES
    [StructLayout(LayoutKind.Sequential)]
    internal struct Vertex
    {
        public Vector3 Position;
        public Vector3 Color;
        public float ElementIndex;
    }

    internal readonly struct PickTriangle
    {
        public readonly Vector3 A, B, C;
        public readonly string ElementId;
        public readonly int DrawIndex;
        public PickTriangle(Vector3 a, Vector3 b, Vector3 c, string elementId, int drawIndex)
        { A = a; B = b; C = c; ElementId = elementId; DrawIndex = drawIndex; }
    }

    internal struct PickNode
    {
        public Vector3 Min, Max;
        public int Start, Count, Left, Right;
    }

    internal sealed class MeshSnapshot
    {
        public readonly Vertex[] Vertices;
        public readonly int TriangleVertexCount;
        public readonly PickTriangle[] Triangles;
        public readonly PickNode[] PickNodes;
        public readonly Dictionary<string, float> ElementIndexes;
        public MeshSnapshot(Vertex[] vertices, int triangleVertexCount,
            PickTriangle[] triangles, Dictionary<string, float> elementIndexes)
        {
            Vertices = vertices;
            TriangleVertexCount = triangleVertexCount;
            Triangles = triangles;
            ElementIndexes = elementIndexes;
            PickNodes = BuildPickTree(triangles);
        }
    }

    internal sealed class PreparedScene
    {
        internal readonly BuildingVolume Volume;
        private readonly MeshSnapshot _mesh;
        internal PreparedScene(BuildingVolume volume, MeshSnapshot mesh)
        { Volume = volume; _mesh = mesh; }
        internal int TriangleCount => _mesh.Triangles.Length;
        internal int VertexCount => _mesh.Vertices.Length;
        internal MeshSnapshot Snapshot => _mesh;
    }

    private volatile MeshSnapshot _snapshot;
    private MeshSnapshot? _uploaded;
    private BuildingVolume _volume;
    private string? _selectedId;
    private float _selectedIndex;
    private int _program;
    private int _vertexShader;
    private int _fragmentShader;
    private int _buffer;
    private int _array;
    private float _yaw = MathF.Atan2(11f, 13f);
    private float _pitch = MathF.Asin(10f / MathF.Sqrt(11f * 11f + 10f * 10f + 13f * 13f));
    private float _distance = 19.72f;
    private Vector3 _target;
    private Point? _dragStart;
    private Point? _pressStart;
    private bool _panning;
    private bool _orbiting;
    private bool _selecting;
    private bool _orbitAllowed;
    private bool _dragged;
    private volatile bool _frameRendered;
    private long _renderedFrameCount;
    private long _lastSynchronizedFrameTicks;
    private int _lastRenderedVertexCount;
    public bool FrameRendered => _frameRendered;
    internal long RenderedFrameCount => Interlocked.Read(ref _renderedFrameCount);
    internal int LastRenderedVertexCount => Volatile.Read(ref _lastRenderedVertexCount);
    internal (float Yaw, float Pitch) CameraAngles => (_yaw, _pitch);
    internal double LastSynchronizedFrameMs
        => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _lastSynchronizedFrameTicks)).TotalMilliseconds;
    internal string GpuRenderer { get; private set; } = "unknown";
    public event Action<string?>? ElementPicked;

    public ModelViewport(BuildingVolume volume) : this(PrepareScene(volume)) { }

    public ModelViewport(PreparedScene scene)
    {
        _volume = scene.Volume;
        _snapshot = scene.Snapshot;
    }

    public static PreparedScene PrepareScene(BuildingVolume volume)
        => new PreparedScene(volume, BuildSnapshot(volume));

    public void SetScene(PreparedScene scene)
    {
        var oldCenter = _volume.Center;
        var oldScale = (float)(10d / Math.Max(1d, _volume.Diagonal));
        var worldX = oldCenter.X + _target.X / oldScale;
        var worldY = oldCenter.Y - _target.Z / oldScale;
        var worldZ = oldCenter.Z + _target.Y / oldScale;
        _volume = scene.Volume;
        var newCenter = _volume.Center;
        var newScale = (float)(10d / Math.Max(1d, _volume.Diagonal));
        _target = new Vector3((float)(worldX - newCenter.X) * newScale,
            (float)(worldZ - newCenter.Z) * newScale,
            (float)(newCenter.Y - worldY) * newScale);
        _distance *= newScale / oldScale;
        _snapshot = scene.Snapshot;
        _selectedIndex = _selectedId != null && _snapshot.ElementIndexes.TryGetValue(_selectedId, out var index)
            ? index : 0f;
        RequestNextFrameRendering();
    }

    public void SelectElement(string? id)
    {
        _selectedId = id;
        _selectedIndex = id != null && _snapshot.ElementIndexes.TryGetValue(id, out var index)
            ? index : 0f;
        RequestNextFrameRendering();
    }

    public void ResetView()
    {
        _yaw = MathF.Atan2(11f, 13f);
        _pitch = MathF.Asin(10f / MathF.Sqrt(11f * 11f + 10f * 10f + 13f * 13f));
        _distance = 19.72f;
        _target = Vector3.Zero;
        RequestNextFrameRendering();
    }

    internal void RotateForBenchmark(float radians)
    {
        _yaw += radians;
        RequestNextFrameRendering();
    }

    internal static Matrix4x4 OrbitView(float yaw, float pitch, float distance, Vector3 target)
    {
        // Turntable orbit: azimuth stays around world up; elevation is around the
        // camera's current right axis. Never rotate the model to move the view.
        var direction = new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));
        return Matrix4x4.CreateLookAt(target + direction * distance, target, Vector3.UnitY);
    }

    private Matrix4x4 CameraView() => OrbitView(_yaw, _pitch, _distance, _target);

    private Matrix4x4 ViewProjection()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.8f,
            (float)(Bounds.Width / Bounds.Height), 0.1f, 100f);
        return CameraView() * projection;
    }

    internal Point? ProjectModelPoint(double x, double y, double z)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return null;
        var center = _volume.Center;
        var scale = (float)(10d / Math.Max(1d, _volume.Diagonal));
        var position = new Vector4((float)(x - center.X) * scale,
            (float)(z - center.Z) * scale, (float)(center.Y - y) * scale, 1f);
        var clip = Vector4.Transform(position, ViewProjection());
        if (clip.W <= 1e-5f) return null;
        return new Point((clip.X / clip.W + 1d) * Bounds.Width / 2d,
            (1d - clip.Y / clip.W) * Bounds.Height / 2d);
    }

    internal bool TryScreenToPlan(Point point, double elevation, out PointModel result)
    {
        result = new PointModel();
        if (Bounds.Width <= 0 || Bounds.Height <= 0
            || !Matrix4x4.Invert(ViewProjection(), out var inverse)) return false;
        var nx = (float)(point.X / Bounds.Width * 2d - 1d);
        var ny = (float)(1d - point.Y / Bounds.Height * 2d);
        if (!Unproject(nx, ny, 0f, inverse, out var near)
            || !Unproject(nx, ny, 1f, inverse, out var far)) return false;
        var center = _volume.Center;
        var scale = (float)(10d / Math.Max(1d, _volume.Diagonal));
        var planeY = (float)(elevation - center.Z) * scale;
        var dy = far.Y - near.Y;
        if (Math.Abs(dy) < 1e-6f) return false;
        var t = (planeY - near.Y) / dy;
        if (t <= 0f) return false;
        var hit = near + t * (far - near);
        result = new PointModel(hit.X / scale + center.X, center.Y - hit.Z / scale);
        return double.IsFinite(result.X) && double.IsFinite(result.Y);
    }

    private static MeshSnapshot BuildSnapshot(BuildingVolume volume)
    {
        var vertices = new List<Vertex>();
        var triangles = new List<PickTriangle>();
        var elementIndexes = new Dictionary<string, float>(StringComparer.Ordinal);
        var center = volume.Center;
        var scale = (float)(10d / Math.Max(1d, volume.Diagonal));
        foreach (var face in volume.Faces)
        {
            if (face.Points.Count < 3) continue;
            var color = face.Kind switch
            {
                "wall" => new Vector3(0.48f, 0.72f, 0.92f),
                "slab" => new Vector3(0.72f, 0.76f, 0.82f),
                "roof" => new Vector3(0.42f, 0.68f, 0.8f),
                _ => new Vector3(0.62f, 0.78f, 0.89f)
            };
            var brightness = Math.Clamp(0.6f + (float)face.NormalZ * 0.2f
                + (float)face.NormalX * 0.12f + (float)face.NormalY * 0.08f, 0.4f, 1f);
            color *= brightness;
            var elementIndex = 0f;
            if (!string.IsNullOrWhiteSpace(face.ElementId))
            {
                if (!elementIndexes.TryGetValue(face.ElementId, out elementIndex))
                    elementIndexes.Add(face.ElementId, elementIndex = elementIndexes.Count + 1);
            }
            for (var i = 1; i + 1 < face.Points.Count; i++)
            {
                var a = ToVertex(face.Points[0], center, scale, color, elementIndex);
                var b = ToVertex(face.Points[i], center, scale, color, elementIndex);
                var c = ToVertex(face.Points[i + 1], center, scale, color, elementIndex);
                vertices.Add(a); vertices.Add(b); vertices.Add(c);
                triangles.Add(new PickTriangle(a.Position, b.Position, c.Position, face.ElementId, triangles.Count));
            }
        }
        var triangleVertexCount = vertices.Count;
        foreach (var line in volume.GuideLines)
        {
            if (line?.Start == null || line.End == null) continue;
            var color = line.IsBuildingAxis ? new Vector3(0.3f, 0.85f, 0.75f)
                : line.IsWallJoint ? new Vector3(0.23f, 0.36f, 0.5f)
                : new Vector3(1f, 0.7f, 0.25f);
            vertices.Add(ToVertex(line.Start, center, scale, color, 0));
            vertices.Add(ToVertex(line.End, center, scale, color, 0));
            if (line.IsBuildingAxis)
            {
                AddGroundAxisLabel(line.Start, line.StartLabel ?? line.Label, vertices, center, scale, color);
                AddGroundAxisLabel(line.End, line.EndLabel ?? line.Label, vertices, center, scale, color);
            }
        }
        return new MeshSnapshot(vertices.ToArray(), triangleVertexCount,
            triangles.ToArray(), elementIndexes);
    }

    private static readonly Dictionary<char, string> AxisFont = new()
    {
        ['0']="01110/10001/10011/10101/11001/10001/01110", ['1']="00100/01100/00100/00100/00100/00100/01110",
        ['2']="01110/10001/00001/00010/00100/01000/11111", ['3']="11110/00001/00001/01110/00001/00001/11110",
        ['4']="00010/00110/01010/10010/11111/00010/00010", ['5']="11111/10000/10000/11110/00001/00001/11110",
        ['6']="01110/10000/10000/11110/10001/10001/01110", ['7']="11111/00001/00010/00100/01000/01000/01000",
        ['8']="01110/10001/10001/01110/10001/10001/01110", ['9']="01110/10001/10001/01111/00001/00001/01110",
        ['A']="01110/10001/10001/11111/10001/10001/10001", ['B']="11110/10001/10001/11110/10001/10001/11110",
        ['C']="01111/10000/10000/10000/10000/10000/01111", ['D']="11110/10001/10001/10001/10001/10001/11110",
        ['E']="11111/10000/10000/11110/10000/10000/11111", ['F']="11111/10000/10000/11110/10000/10000/10000",
        ['G']="01111/10000/10000/10111/10001/10001/01111", ['H']="10001/10001/10001/11111/10001/10001/10001",
        ['I']="01110/00100/00100/00100/00100/00100/01110", ['J']="00111/00010/00010/00010/10010/10010/01100",
        ['K']="10001/10010/10100/11000/10100/10010/10001", ['L']="10000/10000/10000/10000/10000/10000/11111",
        ['M']="10001/11011/10101/10101/10001/10001/10001", ['N']="10001/11001/10101/10011/10001/10001/10001",
        ['O']="01110/10001/10001/10001/10001/10001/01110", ['P']="11110/10001/10001/11110/10000/10000/10000",
        ['Q']="01110/10001/10001/10001/10101/10010/01101", ['R']="11110/10001/10001/11110/10100/10010/10001",
        ['S']="01111/10000/10000/01110/00001/00001/11110", ['T']="11111/00100/00100/00100/00100/00100/00100",
        ['U']="10001/10001/10001/10001/10001/10001/01110", ['V']="10001/10001/10001/10001/10001/01010/00100",
        ['W']="10001/10001/10001/10101/10101/10101/01010", ['X']="10001/10001/01010/00100/01010/10001/10001",
        ['Y']="10001/10001/01010/00100/00100/00100/00100", ['Z']="11111/00001/00010/00100/01000/10000/11111",
        ['-']="00000/00000/00000/11111/00000/00000/00000", ['/']="00001/00001/00010/00100/01000/10000/10000",
        ['\'']="00100/00100/00100/00000/00000/00000/00000", ['?']="01110/10001/00001/00010/00100/00000/00100"
    };

    private static void AddGroundAxisLabel(Point3DModel at, string? label, List<Vertex> vertices,
        Point3DModel center, float scale, Vector3 color)
    {
        if (string.IsNullOrWhiteSpace(label)) return;
        var name = label.Trim().ToUpperInvariant();
        const double radius = 450d;
        var z = at.Z + 2d;
        void Line(double x1, double y1, double x2, double y2)
        {
            vertices.Add(ToVertex(new Point3DModel(x1, y1, z), center, scale, color, 0));
            vertices.Add(ToVertex(new Point3DModel(x2, y2, z), center, scale, color, 0));
        }
        for (var i = 0; i < 32; i++)
        {
            var a = i * Math.PI / 16d;
            var b = (i + 1) * Math.PI / 16d;
            Line(at.X + radius * Math.Cos(a), at.Y + radius * Math.Sin(a),
                at.X + radius * Math.Cos(b), at.Y + radius * Math.Sin(b));
        }
        var cell = Math.Min(92d, 720d / Math.Max(1, name.Length * 6));
        var left = at.X - (name.Length * 6d - 1d) * cell / 2d;
        var top = at.Y + 3d * cell;
        for (var character = 0; character < name.Length; character++)
        {
            var rows = (AxisFont.TryGetValue(name[character], out var bitmap)
                ? bitmap : AxisFont['?']).Split('/');
            for (var row = 0; row < 7; row++)
            for (var column = 0; column < 5; column++)
            {
                if (rows[row][column] != '1') continue;
                var x = left + (character * 6 + column) * cell;
                var y = top - row * cell;
                Line(x, y, x + cell * 0.8d, y);
            }
        }
    }

    private static PickNode[] BuildPickTree(PickTriangle[] triangles)
    {
        if (triangles.Length == 0) return Array.Empty<PickNode>();
        var nodes = new List<PickNode>();
        BuildNode(0, triangles.Length);
        return nodes.ToArray();

        int BuildNode(int start, int count)
        {
            var nodeIndex = nodes.Count;
            nodes.Add(default);
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            for (var i = start; i < start + count; i++)
            {
                var t = triangles[i];
                min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C)));
                max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C)));
            }
            if (count <= 12)
            {
                nodes[nodeIndex] = new PickNode { Min = min, Max = max, Start = start, Count = count };
                return nodeIndex;
            }
            var extent = max - min;
            var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0
                : extent.Y >= extent.Z ? 1 : 2;
            var middle = axis == 0 ? (min.X + max.X) * 1.5f
                : axis == 1 ? (min.Y + max.Y) * 1.5f : (min.Z + max.Z) * 1.5f;
            var low = start;
            var high = start + count - 1;
            while (low <= high)
            {
                if (Centroid(triangles[low], axis) < middle) low++;
                else
                {
                    (triangles[low], triangles[high]) = (triangles[high], triangles[low]);
                    high--;
                }
            }
            var leftCount = low - start;
            if (leftCount == 0 || leftCount == count) leftCount = count / 2;
            var left = BuildNode(start, leftCount);
            var right = BuildNode(start + leftCount, count - leftCount);
            nodes[nodeIndex] = new PickNode { Min = min, Max = max, Start = start, Left = left, Right = right };
            return nodeIndex;
        }
    }

    private static float Centroid(PickTriangle triangle, int axis)
    {
        return axis == 0 ? triangle.A.X + triangle.B.X + triangle.C.X
            : axis == 1 ? triangle.A.Y + triangle.B.Y + triangle.C.Y
            : triangle.A.Z + triangle.B.Z + triangle.C.Z;
    }

    private static Vertex ToVertex(Point3DModel point, Point3DModel center, float scale, Vector3 color,
        float elementIndex)
        => new()
        {
            Position = new Vector3((float)(point.X - center.X) * scale,
                (float)(point.Z - center.Z) * scale,
                (float)(center.Y - point.Y) * scale),
            Color = color,
            ElementIndex = elementIndex
        };

    private static string ShaderSource(GlVersion version, bool fragment, string body)
    {
        var modern = version.Type == GlProfileType.OpenGL && OperatingSystem.IsMacOS();
        var prefix = modern ? "#version 150\n" : version.Type == GlProfileType.OpenGLES
            ? "#version 100\nprecision mediump float;\n" : "#version 120\n";
        if (modern)
        {
            body = body.Replace("attribute", "in");
            body = body.Replace("varying", fragment ? "in" : "out");
            if (fragment) body = body.Replace("gl_FragColor", "outColor");
            if (fragment) prefix += "out vec4 outColor;\n";
        }
        return prefix + body;
    }

    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        GpuRenderer = gl.GetString(GL_VENDOR) + " / " + gl.GetString(GL_RENDERER);
        var vertexSource = ShaderSource(GlVersion, false, @"
            attribute vec3 aPos;
            attribute vec3 aColor;
            attribute float aElement;
            varying vec3 vColor;
            uniform float uSelectedElement;
            uniform mat4 uProjection;
            uniform mat4 uView;
            uniform mat4 uModel;
            void main() {
                vColor = uSelectedElement > 0.5 && abs(aElement - uSelectedElement) < 0.5
                    ? vec3(1.0, 0.72, 0.18) : aColor;
                gl_Position = uProjection * uView * uModel * vec4(aPos, 1.0);
            }");
        var fragmentSource = ShaderSource(GlVersion, true, @"
            varying vec3 vColor;
            void main() { gl_FragColor = vec4(vColor, 1.0); }");
        _vertexShader = gl.CreateShader(GL_VERTEX_SHADER);
        var vertexError = gl.CompileShaderAndGetError(_vertexShader, vertexSource);
        _fragmentShader = gl.CreateShader(GL_FRAGMENT_SHADER);
        var fragmentError = gl.CompileShaderAndGetError(_fragmentShader, fragmentSource);
        _program = gl.CreateProgram();
        gl.AttachShader(_program, _vertexShader);
        gl.AttachShader(_program, _fragmentShader);
        gl.BindAttribLocationString(_program, 0, "aPos");
        gl.BindAttribLocationString(_program, 1, "aColor");
        gl.BindAttribLocationString(_program, 2, "aElement");
        var linkError = gl.LinkProgramAndGetError(_program);
        if (!string.IsNullOrWhiteSpace(vertexError) || !string.IsNullOrWhiteSpace(fragmentError)
            || !string.IsNullOrWhiteSpace(linkError))
            throw new InvalidOperationException($"GPU shader failed: {vertexError} {fragmentError} {linkError}");

        _buffer = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _buffer);
        Upload(gl, _snapshot);
        _array = gl.GenVertexArray();
        gl.BindVertexArray(_array);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, sizeof(Vertex), IntPtr.Zero);
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, sizeof(Vertex), new IntPtr(12));
        gl.VertexAttribPointer(2, 1, GL_FLOAT, 0, sizeof(Vertex), new IntPtr(24));
        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.EnableVertexAttribArray(2);
    }

    private unsafe void Upload(GlInterface gl, MeshSnapshot snapshot)
    {
        gl.BindBuffer(GL_ARRAY_BUFFER, _buffer);
        fixed (Vertex* data = snapshot.Vertices)
            gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(snapshot.Vertices.Length * sizeof(Vertex)),
                new IntPtr(data), GL_STATIC_DRAW);
        _uploaded = snapshot;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        gl.DeleteBuffer(_buffer);
        gl.DeleteVertexArray(_array);
        gl.DeleteProgram(_program);
        gl.DeleteShader(_fragmentShader);
        gl.DeleteShader(_vertexShader);
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        var benchmarkStart = Program.GpuBenchCount > 0 ? Stopwatch.GetTimestamp() : 0L;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(0.08f, 0.12f, 0.17f, 1f);
        gl.ClearDepth(1);
        gl.Enable(GL_DEPTH_TEST);
        gl.DepthFunc(GL_LESS);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        gl.UseProgram(_program);
        gl.BindVertexArray(_array);
        var snapshot = _snapshot;
        if (!ReferenceEquals(snapshot, _uploaded)) Upload(gl, snapshot);
        var model = Matrix4x4.Identity;
        var view = CameraView();
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.8f, (float)width / height, 0.1f, 100f);
        gl.UniformMatrix4fv(gl.GetUniformLocationString(_program, "uModel"), 1, false, &model);
        gl.UniformMatrix4fv(gl.GetUniformLocationString(_program, "uView"), 1, false, &view);
        gl.UniformMatrix4fv(gl.GetUniformLocationString(_program, "uProjection"), 1, false, &projection);
        gl.Uniform1f(gl.GetUniformLocationString(_program, "uSelectedElement"), _selectedIndex);
        gl.DrawArrays(GL_TRIANGLES, 0, snapshot.TriangleVertexCount);
        if (snapshot.Vertices.Length > snapshot.TriangleVertexCount)
        {
            gl.Uniform1f(gl.GetUniformLocationString(_program, "uSelectedElement"), 0f);
            gl.DrawArrays(GuideLinesPrimitive, snapshot.TriangleVertexCount,
                snapshot.Vertices.Length - snapshot.TriangleVertexCount);
        }
        if (benchmarkStart != 0)
        {
            gl.Finish();
            Interlocked.Exchange(ref _lastSynchronizedFrameTicks, Stopwatch.GetTimestamp() - benchmarkStart);
        }
        _frameRendered = gl.GetError() == GL_NO_ERROR && snapshot.Vertices.Length > 0;
        Volatile.Write(ref _lastRenderedVertexCount, snapshot.Vertices.Length);
        Interlocked.Increment(ref _renderedFrameCount);
    }

    private string? Pick(Point point)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return null;
        var model = Matrix4x4.Identity;
        var view = CameraView();
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.8f,
            (float)(Bounds.Width / Bounds.Height), 0.1f, 100f);
        var x = (float)(point.X / Bounds.Width * 2 - 1);
        var y = (float)(1 - point.Y / Bounds.Height * 2);
        return PickNormalized(_snapshot, model * view * projection, x, y);
    }

    private static string? PickNormalized(MeshSnapshot snapshot, Matrix4x4 transform, float x, float y)
    {
        if (snapshot.PickNodes.Length == 0 || !Matrix4x4.Invert(transform, out var inverse)
            || !Unproject(x, y, 0f, inverse, out var origin)
            || !Unproject(x, y, 1f, inverse, out var far)) return null;
        var direction = Vector3.Normalize(far - origin);
        var bestDistance = float.PositiveInfinity;
        var bestDrawIndex = int.MaxValue;
        string? bestId = null;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var node = snapshot.PickNodes[stack.Pop()];
            if (!IntersectsBox(origin, direction, node.Min, node.Max, bestDistance + 1e-5f)) continue;
            if (node.Count == 0)
            {
                stack.Push(node.Left);
                stack.Push(node.Right);
                continue;
            }
            for (var i = node.Start; i < node.Start + node.Count; i++)
            {
                var triangle = snapshot.Triangles[i];
                if (string.IsNullOrWhiteSpace(triangle.ElementId)) continue;
                if (!IntersectsTriangle(origin, direction, triangle, out var distance)
                    || distance > bestDistance + 1e-5f
                    || (Math.Abs(distance - bestDistance) <= 1e-5f && triangle.DrawIndex >= bestDrawIndex)) continue;
                bestDistance = distance;
                bestDrawIndex = triangle.DrawIndex;
                bestId = triangle.ElementId;
            }
        }
        return bestId;
    }

    internal string? PickAt(Point point) => Pick(point);

    internal static string? PickForBenchmark(PreparedScene scene, Matrix4x4 transform, float x, float y)
        => PickNormalized(scene.Snapshot, transform, x, y);

    internal static string? PickBruteRayForCheck(PreparedScene scene, Matrix4x4 transform, float x, float y)
    {
        if (!Matrix4x4.Invert(transform, out var inverse)
            || !Unproject(x, y, 0f, inverse, out var origin)
            || !Unproject(x, y, 1f, inverse, out var far)) return null;
        var direction = Vector3.Normalize(far - origin);
        var bestDistance = float.PositiveInfinity;
        var bestDrawIndex = int.MaxValue;
        string? bestId = null;
        foreach (var triangle in scene.Snapshot.Triangles)
        {
            if (string.IsNullOrWhiteSpace(triangle.ElementId)
                || !IntersectsTriangle(origin, direction, triangle, out var distance)
                || distance > bestDistance + 1e-5f
                || (Math.Abs(distance - bestDistance) <= 1e-5f && triangle.DrawIndex >= bestDrawIndex)) continue;
            bestDistance = distance;
            bestDrawIndex = triangle.DrawIndex;
            bestId = triangle.ElementId;
        }
        return bestId;
    }

    private static bool Project(Vector3 position, Matrix4x4 transform, out Vector3 normalized)
    {
        var clip = Vector4.Transform(new Vector4(position, 1f), transform);
        if (clip.W <= 1e-6f) { normalized = default; return false; }
        normalized = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        return true;
    }

    private static bool Unproject(float x, float y, float z, Matrix4x4 inverse, out Vector3 position)
    {
        var clip = Vector4.Transform(new Vector4(x, y, z, 1f), inverse);
        if (Math.Abs(clip.W) < 1e-7f) { position = default; return false; }
        position = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        return true;
    }

    private static bool IntersectsBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, float limit)
    {
        var near = 0f;
        var far = limit;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            var hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            lo -= 1e-4f;
            hi += 1e-4f;
            if (Math.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi) return false;
                continue;
            }
            var a = (lo - o) / d;
            var b = (hi - o) / d;
            if (a > b) (a, b) = (b, a);
            near = Math.Max(near, a);
            far = Math.Min(far, b);
            if (near > far) return false;
        }
        return true;
    }

    private static bool IntersectsTriangle(Vector3 origin, Vector3 direction, PickTriangle triangle,
        out float distance)
    {
        distance = 0f;
        var edge1 = triangle.B - triangle.A;
        var edge2 = triangle.C - triangle.A;
        var cross = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, cross);
        if (Math.Abs(determinant) < 1e-8f) return false;
        var inverse = 1f / determinant;
        var offset = origin - triangle.A;
        var u = Vector3.Dot(offset, cross) * inverse;
        if (u < -1e-5f || u > 1f + 1e-5f) return false;
        var q = Vector3.Cross(offset, edge1);
        var v = Vector3.Dot(direction, q) * inverse;
        if (v < -1e-5f || u + v > 1f + 1e-5f) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance > 1e-6f;
    }

    public void BeginInteraction(Point point, bool selecting, bool panning,
        bool orbitAllowed = true, bool orbiting = false)
    {
        _panning = panning;
        _orbiting = orbiting && !panning;
        _selecting = selecting && !panning;
        _orbitAllowed = orbitAllowed;
        if (!_panning && !_selecting && !_orbiting) return;
        _dragStart = point;
        _pressStart = _dragStart;
        _dragged = false;
    }

    public void MoveInteraction(Point now, bool leftPressed, bool middleOrRightPressed)
    {
        if (_dragStart is not { } previous) return;
        if ((_panning || _orbiting) ? !middleOrRightPressed : !leftPressed) return;
        if (_pressStart is { } start && (Math.Abs(now.X - start.X) > 4 || Math.Abs(now.Y - start.Y) > 4))
            _dragged = true;
        if (_panning)
        {
            var forward = Vector3.Normalize(_target - (_target + new Vector3(
                MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch),
                MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _distance));
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            var up = Vector3.Normalize(Vector3.Cross(right, forward));
            var unitsPerPixel = 2f * _distance * MathF.Tan(0.4f) / Math.Max(1f, (float)Bounds.Height);
            _target += right * (float)(previous.X - now.X) * unitsPerPixel
                + up * (float)(now.Y - previous.Y) * unitsPerPixel;
        }
        else if (_orbitAllowed)
        {
            _yaw += (float)(now.X - previous.X) * 0.008f;
            _pitch = Math.Clamp(_pitch - (float)(now.Y - previous.Y) * 0.008f,
                -1.45f, 1.45f);
        }
        _dragStart = now;
        RequestNextFrameRendering();
    }

    public void Zoom(double delta)
    {
        _distance = Math.Clamp(_distance * MathF.Pow(0.85f, (float)delta), 2f, 200f);
        RequestNextFrameRendering();
    }

    public void EndInteraction(Point point)
    {
        if (_selecting && !_dragged && _pressStart != null)
            ElementPicked?.Invoke(Pick(point));
        _dragStart = null;
        _pressStart = null;
        _panning = false;
        _orbiting = false;
        _selecting = false;
    }
}
