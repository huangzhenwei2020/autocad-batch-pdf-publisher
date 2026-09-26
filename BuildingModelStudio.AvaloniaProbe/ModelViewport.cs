using System.Numerics;
using System.Runtime.InteropServices;
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
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Color;
    }

    private readonly struct PickTriangle
    {
        public readonly Vector3 A, B, C;
        public readonly string ElementId;
        public PickTriangle(Vector3 a, Vector3 b, Vector3 c, string elementId)
        { A = a; B = b; C = c; ElementId = elementId; }
    }

    private sealed class MeshSnapshot
    {
        public readonly Vertex[] Vertices;
        public readonly PickTriangle[] Triangles;
        public MeshSnapshot(Vertex[] vertices, PickTriangle[] triangles)
        { Vertices = vertices; Triangles = triangles; }
    }

    private volatile MeshSnapshot _snapshot;
    private MeshSnapshot? _uploaded;
    private BuildingVolume _volume;
    private string? _selectedId;
    private int _program;
    private int _vertexShader;
    private int _fragmentShader;
    private int _buffer;
    private int _array;
    private float _yaw = 0.4f;
    private float _pitch = -0.2f;
    private Point? _dragStart;
    private Point? _pressStart;
    private bool _dragged;
    private volatile bool _frameRendered;
    public bool FrameRendered => _frameRendered;
    public event Action<string?>? ElementPicked;

    public ModelViewport(BuildingVolume volume)
    {
        _volume = volume;
        _snapshot = BuildSnapshot(volume, null);
    }

    public void SetVolume(BuildingVolume volume)
    {
        _volume = volume;
        _snapshot = BuildSnapshot(volume, _selectedId);
        RequestNextFrameRendering();
    }

    public void SelectElement(string? id)
    {
        _selectedId = id;
        _snapshot = BuildSnapshot(_volume, id);
        RequestNextFrameRendering();
    }

    private static MeshSnapshot BuildSnapshot(BuildingVolume volume, string? selectedId)
    {
        var vertices = new List<Vertex>();
        var triangles = new List<PickTriangle>();
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
            if (face.ElementId == selectedId) color = new Vector3(1f, 0.72f, 0.18f);
            for (var i = 1; i + 1 < face.Points.Count; i++)
            {
                var a = ToVertex(face.Points[0], center, scale, color);
                var b = ToVertex(face.Points[i], center, scale, color);
                var c = ToVertex(face.Points[i + 1], center, scale, color);
                vertices.Add(a); vertices.Add(b); vertices.Add(c);
                triangles.Add(new PickTriangle(a.Position, b.Position, c.Position, face.ElementId));
            }
        }
        return new MeshSnapshot(vertices.ToArray(), triangles.ToArray());
    }

    private static Vertex ToVertex(Point3DModel point, Point3DModel center, float scale, Vector3 color)
        => new()
        {
            Position = new Vector3((float)(point.X - center.X) * scale,
                (float)(point.Z - center.Z) * scale,
                (float)(center.Y - point.Y) * scale),
            Color = color
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
        var vertexSource = ShaderSource(GlVersion, false, @"
            attribute vec3 aPos;
            attribute vec3 aColor;
            varying vec3 vColor;
            uniform mat4 uProjection;
            uniform mat4 uView;
            uniform mat4 uModel;
            void main() {
                vColor = aColor;
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
        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
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
        var model = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0);
        var view = Matrix4x4.CreateLookAt(new Vector3(11, 10, 13), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.8f, (float)width / height, 0.1f, 100f);
        gl.UniformMatrix4fv(gl.GetUniformLocationString(_program, "uModel"), 1, false, &model);
        gl.UniformMatrix4fv(gl.GetUniformLocationString(_program, "uView"), 1, false, &view);
        gl.UniformMatrix4fv(gl.GetUniformLocationString(_program, "uProjection"), 1, false, &projection);
        gl.DrawArrays(GL_TRIANGLES, 0, snapshot.Vertices.Length);
        _frameRendered = gl.GetError() == GL_NO_ERROR && snapshot.Vertices.Length > 0;
    }

    private string? Pick(Point point)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return null;
        var model = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0);
        var view = Matrix4x4.CreateLookAt(new Vector3(11, 10, 13), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.8f,
            (float)(Bounds.Width / Bounds.Height), 0.1f, 100f);
        var transform = model * view * projection;
        string? bestId = null;
        var bestDepth = float.PositiveInfinity;
        foreach (var triangle in _snapshot.Triangles)
        {
            if (string.IsNullOrWhiteSpace(triangle.ElementId)) continue;
            if (!Project(triangle.A, transform, out var a)
                || !Project(triangle.B, transform, out var b)
                || !Project(triangle.C, transform, out var c)) continue;
            var px = (float)(point.X / Bounds.Width * 2 - 1);
            var py = (float)(1 - point.Y / Bounds.Height * 2);
            var denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(denominator) < 1e-9f) continue;
            var wa = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denominator;
            var wb = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denominator;
            var wc = 1f - wa - wb;
            if (wa < -1e-5f || wb < -1e-5f || wc < -1e-5f) continue;
            var depth = wa * a.Z + wb * b.Z + wc * c.Z;
            if (depth < bestDepth)
            {
                bestDepth = depth;
                bestId = triangle.ElementId;
            }
        }
        return bestId;
    }

    internal string? PickAt(Point point) => Pick(point);

    private static bool Project(Vector3 position, Matrix4x4 transform, out Vector3 normalized)
    {
        var clip = Vector4.Transform(new Vector4(position, 1f), transform);
        if (clip.W <= 1e-6f) { normalized = default; return false; }
        normalized = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragStart = e.GetPosition(this);
        _pressStart = _dragStart;
        _dragged = false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } previous || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var now = e.GetPosition(this);
        if (_pressStart is { } start && (Math.Abs(now.X - start.X) > 4 || Math.Abs(now.Y - start.Y) > 4))
            _dragged = true;
        _yaw += (float)(now.X - previous.X) * 0.008f;
        _pitch += (float)(now.Y - previous.Y) * 0.008f;
        _dragStart = now;
        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_dragged && _pressStart != null)
            ElementPicked?.Invoke(Pick(e.GetPosition(this)));
        _dragStart = null;
        _pressStart = null;
        base.OnPointerReleased(e);
    }
}
