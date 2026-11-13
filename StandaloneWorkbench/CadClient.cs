using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using BatchPdfPublisher.Services;

namespace Wanluo.StandaloneWorkbench
{
    internal sealed class CadClient : IDisposable
    {
        internal IntPtr Handle { get; private set; }
        private readonly Dictionary<string, string> _stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private IntPtr _job;
        internal bool IsBusy { get; private set; }
        internal float OpenProgress => _job == IntPtr.Zero ? 0 : CadNative.wl_job_progress(_job);

        internal CadClient()
        {
            var fonts = Path.Combine(UserDataPaths.RootDirectory, "CAD字体");
            Directory.CreateDirectory(fonts);
            Handle = CadNative.wl_create(fonts);
            if (Handle == IntPtr.Zero) CadNative.Check(0);
        }
        internal CadState State => CadNative.State(Handle);
        internal void Command(string text) => CadNative.Check(CadNative.wl_command(Handle, text));
        internal void New()
        {
            var previousCount = State.Documents.Count;
            CadNative.Check(CadNative.wl_new(Handle));
            var created = State;
            if (created.ActiveId == 0 || created.Documents.Count != previousCount + 1)
                throw new IOException("新图纸未能创建，请重试。");
        }
        internal void Activate(ulong id) => CadNative.Check(CadNative.wl_activate(Handle, id));
        internal void Close(ulong id) => CadNative.Check(CadNative.wl_close(Handle, id));

        private static string Stamp(string path)
        {
            if (!File.Exists(path)) return null;
            using (var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return Convert.ToHexString(SHA256.HashData(input));
        }

        internal async Task OpenAsync(string path)
        {
            if (IsBusy) throw new InvalidOperationException("请先完成或取消当前文件操作。");
            path = Path.GetFullPath(path);
            var existing = State.Documents.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { Activate(existing.Id); return; }
            if (!File.Exists(path)) throw new FileNotFoundException("找不到图纸。", path);
            IsBusy = true;
            try
            {
                _job = CadNative.wl_job_create(path);
                if (_job == IntPtr.Zero) CadNative.Check(0);
                var job = _job;
                var stamp = await Task.Run(() => Stamp(path));
                var result = await Task.Run(() => CadNative.wl_job_run(job));
                if (result == 0) throw new IOException(CadNative.Text(CadNative.wl_job_error(job)) is string error && error.Length > 0 ? error : "图纸读取已取消或未完成。");
                if (await Task.Run(() => Stamp(path)) != stamp) throw new IOException("图纸在打开时被其他程序修改，请重新打开。");
                CadNative.Check(CadNative.wl_job_accept(Handle, job));
                _stamps[path] = stamp;
            }
            finally
            {
                if (_job != IntPtr.Zero) { CadNative.wl_job_destroy(_job); _job = IntPtr.Zero; }
                IsBusy = false;
            }
        }

        internal void CancelOpen() { if (_job != IntPtr.Zero) CadNative.wl_job_cancel(_job); }

        internal async Task SaveAsync(string path)
        {
            if (IsBusy) throw new InvalidOperationException("请先完成当前文件操作。");
            path = Path.GetFullPath(path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".dwg" && extension != ".dxf") throw new ArgumentException("请选择 DWG 或 DXF 文件。");
            var state = State;
            var document = state.Documents.FirstOrDefault(item => item.Id == state.ActiveId);
            if (document == null) throw new InvalidOperationException("请先新建或打开图纸。");
            var currentPath = string.Equals(document.Path, path, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileNameWithoutExtension(path) + ".wanluo-" + Guid.NewGuid().ToString("N") + extension);
            IsBusy = true;
            CadNative.Check(CadNative.wl_busy(Handle, 1));
            try
            {
                var expected = currentPath && _stamps.TryGetValue(path, out var openedStamp) ? openedStamp : await Task.Run(() => Stamp(path));
                var savedStamp = await Task.Run(() =>
                {
                    var gateName = "Local\\WanluoCadSave-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
                    using (var gate = new Mutex(false, gateName))
                    {
                        var acquired = false;
                        try
                        {
                            try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                            if (!acquired) throw new IOException("另一窗口正在保存此图纸，请稍后重试。");
                            if (Stamp(path) != expected) throw new IOException("图纸已被其他程序修改，请另存当前修改或重新打开。");
                            CadNative.Check(CadNative.wl_stage_save(Handle, temporary));
                            var savedHash = Stamp(temporary);
                            if (Stamp(path) != expected) throw new IOException("图纸在保存期间发生变化，当前修改保留，请另存文件。");
                            if (File.Exists(path)) File.Replace(temporary, path, null);
                            else File.Move(temporary, path);
                            return savedHash;
                        }
                        finally { if (acquired) gate.ReleaseMutex(); }
                    }
                });
                CadNative.Check(CadNative.wl_saved(Handle, path));
                _stamps[path] = savedStamp;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                finally { IsBusy = false; CadNative.Check(CadNative.wl_busy(Handle, 0)); }
            }
        }

        internal void Capture(string path)
        {
            CadNative.Check(CadNative.wl_pixels(Handle, out var pixels, out var width, out var height));
            var bytes = new byte[checked(width * height * 4)];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
        }

        public void Dispose()
        {
            if (IsBusy) throw new InvalidOperationException("请等待文件操作完成后关闭绘图区。");
            if (Handle != IntPtr.Zero) { CadNative.wl_destroy(Handle); Handle = IntPtr.Zero; }
        }
    }
}
