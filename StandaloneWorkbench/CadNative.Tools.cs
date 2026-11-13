using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wanluo.StandaloneWorkbench
{
    internal static partial class CadNative
    {
        internal static CadDialogRequest DialogState(IntPtr session) => JsonSerializer.Deserialize<CadDialogRequest>(Text(wl_dialog_state(session)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr wl_dialog_state(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_dialog_cancel(IntPtr session, ulong document, [MarshalAs(UnmanagedType.LPUTF8Str)] string kind);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_text_submit(IntPtr session, ulong document, int multiline, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, double height, double rotation, double width);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_hatch_submit(IntPtr session, ulong document, [MarshalAs(UnmanagedType.LPUTF8Str)] string pattern, double scale, double angle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_array_submit(IntPtr session, ulong document, ref CadArrayParameters parameters);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_block_submit(IntPtr session, ulong document, int define, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint blockId);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_image_submit(IntPtr session, ulong document, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_corner_settings(IntPtr session, double radius, double first, double second);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_text_height(IntPtr session, double height);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_dimension_settings(IntPtr session, double height, double arrow, double gap, double extension, double offset, int precision, int terminator);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_leader_start(IntPtr session, int multiple, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, double height);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_mline_settings(IntPtr session, double scale, int justify);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_layer_manage(IntPtr session, int action, uint id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int rgb, int weight);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int wl_layout_manage(IntPtr session, int action, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CadArrayParameters
    {
        public int Type, Rows, Columns, Count, RotateItems;
        public double RowSpacing, ColumnSpacing, Rotation, CenterX, CenterY, FillAngle;
    }

    internal sealed class CadDialogRequest
    {
        public ulong DocumentId { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
        public bool Editing { get; set; }
        public double Height { get; set; }
        public double Width { get; set; }
        public double Rotation { get; set; }
        public double Radius { get; set; }
        public double FirstDistance { get; set; }
        public double SecondDistance { get; set; }
        public double TextHeight { get; set; }
        public double MlineScale { get; set; }
        public int MlineJustify { get; set; }
        public double DimHeight { get; set; }
        public double DimArrow { get; set; }
        public double DimGap { get; set; }
        public double DimExtension { get; set; }
        public double DimOffset { get; set; }
        public int DimPrecision { get; set; }
        public int DimTerminator { get; set; }
        public string Pattern { get; set; }
        public double Scale { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int Count { get; set; }
        public double RowSpacing { get; set; }
        public double ColumnSpacing { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double FillAngle { get; set; }
        public List<string> Choices { get; set; } = new List<string>();
        public List<CadBlockChoice> Blocks { get; set; } = new List<CadBlockChoice>();
    }
    internal sealed class CadBlockChoice
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }
}
