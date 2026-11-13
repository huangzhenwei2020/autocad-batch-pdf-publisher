using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wanluo.StandaloneWorkbench
{
    internal static partial class CadNative
    {
        private const string Library = "WanluoCadKernel";
        static CadNative()
        {
            NativeLibrary.SetDllImportResolver(typeof(CadNative).Assembly, (name, assembly, paths) =>
                name == Library ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "CAD内核", "WanluoCadKernel.dll")) : IntPtr.Zero);
        }
        internal static string Text(IntPtr value) => Marshal.PtrToStringUTF8(value) ?? string.Empty;
        internal static void Check(int result)
        {
            if (result == 0) throw new IOException(Text(wl_error()));
        }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr wl_error();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr wl_create([MarshalAs(UnmanagedType.LPUTF8Str)] string fonts);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void wl_destroy(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_attach(IntPtr session, IntPtr parent);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr wl_window(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void wl_detach(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_new(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_command(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr wl_state(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_activate(IntPtr session, ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_close(IntPtr session, ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_layout(IntPtr session, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_layer(IntPtr session, uint id, int visible, int locked, int current);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_property(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int kind);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_point(IntPtr session, double x, double y);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_world_to_screen(IntPtr session, double x, double y, out int pixelX, out int pixelY);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_busy(IntPtr session, int busy);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_stage_save(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_saved(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr wl_job_create([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_job_run(IntPtr job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr wl_job_error(IntPtr job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern float wl_job_progress(IntPtr job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void wl_job_cancel(IntPtr job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void wl_job_destroy(IntPtr job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_job_accept(IntPtr session, IntPtr job);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_pixels(IntPtr session, out IntPtr bytes, out int width, out int height);
        internal static CadState State(IntPtr session)
        {
            var data = wl_state(session);
            if (data == IntPtr.Zero) Check(0);
            return JsonSerializer.Deserialize<CadState>(Text(data), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
    }

    internal sealed class CadState
    {
        public ulong ActiveId { get; set; }
        public List<CadDrawing> Documents { get; set; } = new List<CadDrawing>();
        public int EntityCount { get; set; }
        public int SelectionCount { get; set; }
        public bool CanUndo { get; set; }
        public bool CanRedo { get; set; }
        public bool Snap { get; set; }
        public bool Ortho { get; set; }
        public bool Polar { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double BuildProgress { get; set; }
        public string Prompt { get; set; }
        public string CursorMode { get; set; }
        public bool PointerInside { get; set; }
        public string SaveNotice { get; set; }
        public string Error { get; set; }
        public List<string> Log { get; set; } = new List<string>();
        public List<CadLayer> Layers { get; set; } = new List<CadLayer>();
        public List<CadLayout> Layouts { get; set; } = new List<CadLayout>();
        public List<CadProperty> Properties { get; set; } = new List<CadProperty>();
    }
    internal sealed class CadDrawing
    {
        public ulong Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public bool Dirty { get; set; }
        public string Label => Name + (Dirty ? " *" : string.Empty);
    }
    internal sealed class CadLayer
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public bool Visible { get; set; }
        public bool Locked { get; set; }
        public bool Current { get; set; }
        public int Rgb { get; set; }
        public int Weight { get; set; }
    }
    internal sealed class CadLayout
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool Current { get; set; }
        public bool Model { get; set; }
    }
    internal sealed class CadProperty
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public bool Editable { get; set; }
        public int Kind { get; set; }
    }
}
