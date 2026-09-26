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

    private readonly Vertex[] _vertices;
    private int _program;
    private int _vertexShader;
    private int _fragmentShader;
    private int _buffer;
    private int _array;
    private float _yaw = 0.4f;
    private float _pitch = -0.2f;
    private Point? _dragStart;
    private volatile bool _frameRendered;
    public bool FrameRendered => _frameRendered;

    public ModelViewport(BuildingVolume volume)
    {
        var vertices = new List<Vertex>();
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
            for (var i = 1; i + 1 < face.Points.Count; i++)
            {
                vertices.Add(ToVertex(face.Points[0], center, scale, color));
                vertices.Add(ToVertex(face.Points[i], center, scale, color));
                vertices.Add(ToVertex(face.Points[i + 1], center, scale, color));
            }
        }
        _vertices = vertices.ToArray();
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
        fixed (Vertex* data = _vertices)
            gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(_vertices.Length * sizeof(Vertex)),
                new IntPtr(data), GL_STATIC_DRAW);
        _array = gl.GenVertexArray();
        gl.BindVertexArray(_array);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, sizeof(Vertex), IntPtr.Zero);
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, sizeof(Vertex), new IntPtr(12));
        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
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
        var model = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0);
        var view = Matrix4x4.CreateLookAt(new Vector3(11, 10, 13), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.8f, (float)width / height, 0.1f, 100f);
        gl.UniformMatrix4fv(gl.GetUniformLocationString(_program, "uModel"), 1, false, &model);
        gl.UniformMatrix4fv(gl.GetUniformLocationString(_program, "uView"), 1, false, &view);
        gl.UniformMatrix4fv(gl.GetUniformLocationString(_program, "uProjection"), 1, false, &projection);
        gl.DrawArrays(GL_TRIANGLES, 0, _vertices.Length);
        _frameRendered = gl.GetError() == GL_NO_ERROR && _vertices.Length > 0;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragStart = e.GetPosition(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } previous || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var now = e.GetPosition(this);
        _yaw += (float)(now.X - previous.X) * 0.008f;
        _pitch += (float)(now.Y - previous.Y) * 0.008f;
        _dragStart = now;
        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragStart = null;
        base.OnPointerReleased(e);
    }
}
