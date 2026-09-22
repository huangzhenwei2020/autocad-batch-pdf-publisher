using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BatchPdfPublisher.Services
{
    internal static class PdfPreviewRenderer
    {
        private const int FpdfBitmapBgra = 4;
        private const int FpdfAnnot = 0x01;
        private const int FpdfLcdText = 0x02;
        private static readonly object Sync = new object();
        private static bool _initialized;
        private static IntPtr _nativeModule;

        public static Bitmap RenderFirstPage(string pdfPath, int maximumWidth, int maximumHeight)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                throw new FileNotFoundException("找不到临时打印预览文件。", pdfPath);
            EnsureInitialized();
            var pdfBytes = ReadCompletedPdf(pdfPath);
            var pdfHandle = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            var document = FPDF_LoadMemDocument(pdfHandle.AddrOfPinnedObject(), pdfBytes.Length, null);
            if (document == IntPtr.Zero)
            {
                pdfHandle.Free();
                throw new InvalidOperationException("无法读取刚生成的打印预览 PDF（PDFium 错误 " + FPDF_GetLastError() + "）。");
            }
            try
            {
                if (FPDF_GetPageCount(document) < 1) throw new InvalidOperationException("打印预览 PDF 没有页面。");
                var page = FPDF_LoadPage(document, 0);
                if (page == IntPtr.Zero) throw new InvalidOperationException("无法读取打印预览第一页。");
                try
                {
                    var pageWidth = Math.Max(1d, FPDF_GetPageWidthF(page));
                    var pageHeight = Math.Max(1d, FPDF_GetPageHeightF(page));
                    var scale = Math.Min(Math.Max(320, maximumWidth) / pageWidth, Math.Max(240, maximumHeight) / pageHeight);
                    scale = Math.Max(.25d, Math.Min(4d, scale));
                    var width = Math.Max(1, (int)Math.Round(pageWidth * scale));
                    var height = Math.Max(1, (int)Math.Round(pageHeight * scale));
                    var buffer = new byte[checked(width * height * 4)];
                    var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    try
                    {
                        var pdfBitmap = FPDFBitmap_CreateEx(width, height, FpdfBitmapBgra, handle.AddrOfPinnedObject(), width * 4);
                        if (pdfBitmap == IntPtr.Zero) throw new InvalidOperationException("无法创建打印预览画布。");
                        try
                        {
                            FPDFBitmap_FillRect(pdfBitmap, 0, 0, width, height, 0xFFFFFFFFu);
                            FPDF_RenderPageBitmap(pdfBitmap, page, 0, 0, width, height, 0, FpdfAnnot | FpdfLcdText);
                            var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                            var bits = output.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                            try { Marshal.Copy(buffer, 0, bits.Scan0, buffer.Length); }
                            finally { output.UnlockBits(bits); }
                            return output;
                        }
                        finally { FPDFBitmap_Destroy(pdfBitmap); }
                    }
                    finally { handle.Free(); }
                }
                finally { FPDF_ClosePage(page); }
            }
            finally
            {
                FPDF_CloseDocument(document);
                pdfHandle.Free();
            }
        }

        private static byte[] ReadCompletedPdf(string pdfPath)
        {
            // AutoCAD writes previews below a localized user-data path. PDFium's
            // path API expects UTF-8, while .NET Framework marshals it with the
            // active ANSI code page. Loading the finished bytes avoids that path
            // conversion and also guarantees the plotter has released the file.
            Exception lastError = null;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    byte[] bytes;
                    using (var stream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bytes = new byte[stream.Length];
                        var offset = 0;
                        while (offset < bytes.Length)
                        {
                            var read = stream.Read(bytes, offset, bytes.Length - offset);
                            if (read <= 0) break;
                            offset += read;
                        }
                        if (offset != bytes.Length) throw new EndOfStreamException("打印预览 PDF 尚未写完。");
                    }
                    if (bytes.Length < 5 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P' ||
                        bytes[2] != (byte)'D' || bytes[3] != (byte)'F' || bytes[4] != (byte)'-')
                        throw new InvalidDataException("生成的打印预览不是有效 PDF。");
                    return bytes;
                }
                catch (IOException exception)
                {
                    lastError = exception;
                    System.Threading.Thread.Sleep(100);
                }
            }
            throw new InvalidOperationException("无法读取刚生成的打印预览 PDF。", lastError);
        }

        private static void EnsureInitialized()
        {
            lock (Sync)
            {
                if (_initialized) return;
                var assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var nativePath = Path.Combine(assemblyFolder, "pdfium.dll");
                _nativeModule = LoadLibrary(nativePath);
                if (_nativeModule == IntPtr.Zero)
                    throw new InvalidOperationException("打印预览组件 pdfium.dll 未安装或无法加载。");
                FPDF_InitLibrary();
                _initialized = true;
            }
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string fileName);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_InitLibrary();
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDF_LoadMemDocument(IntPtr dataBuffer, int size, string password);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint FPDF_GetLastError();
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_ClosePage(IntPtr page);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_CloseDocument(IntPtr document);
    }
}
