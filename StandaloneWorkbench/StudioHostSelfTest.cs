using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.StandaloneWorkbench
{
    internal static class StudioHostSelfTest
    {
        internal static async Task RunAsync(MainWindow window, string root, Action<Window, string> capture)
        {
            var host = (StudioHost)window.FindName("StudioEditor");
            var tabs = (TabControl)window.FindName("Workspace");
            var path = window.Session.ModelPath;
            var original = BuildingModelJson.LoadModel(path);
            await window.OpenStudioAsync();
            var state = await host.RequestAsync("status");
            Check(state.Ready && state.Success && GetParent(host.ChildWindow) == host.Handle, "Model editor is not embedded in the workbench");
            var child = host.ChildWindow;
            Check(state.ModelPath == path, "The embedded editor opened a different model");
            tabs.SelectedIndex = 0; await Task.Delay(100);
            Check(host.IsRunning, "2D switching closed the model editor");
            tabs.SelectedIndex = 2; await Task.Delay(100);
            Check(host.ChildWindow == child && GetParent(child) == host.Handle, "Returning from 2D lost the embedded editor");
            foreach (var size in new[] { new Size(1000, 700), new Size(1440, 900) })
            {
                window.Width = size.Width; window.Height = size.Height;
                window.UpdateLayout(); await Task.Delay(120);
                GetClientRect(host.Handle, out var parentRect); GetClientRect(child, out var childRect);
                Check(Math.Abs(parentRect.Right - childRect.Right) <= 2 && Math.Abs(parentRect.Bottom - childRect.Bottom) <= 2,
                    "Embedded model editor did not follow workspace resizing");
            }
            var edit = await host.RequestAsync("self-test");
            Check(edit.Success && edit.Dirty && edit.Message.Contains("STUDIO_EMBED_EDIT_OK"), "Embedded model editing, undo or close cancellation failed");
            capture(window, Path.Combine(root, "studio-workspace.png"));
            Check(BuildingModelJson.LoadModel(path).Walls.Count == original.Walls.Count, "Unapplied model draft changed the file");
            state = await host.RequestAsync("save");
            Check(state.Success && !state.Dirty && BuildingModelJson.LoadModel(path).Walls.Count == original.Walls.Count + 1, "Embedded model save did not persist geometry");
            var closed = await host.CloseAsync();
            Check(closed.Success && !host.IsRunning, "Embedded model did not close normally");
            await window.OpenStudioAsync();
            state = await host.RequestAsync("status");
            Check(state.Ready && state.ModelPath == path && !state.Dirty, "Saved embedded model did not reopen");
            Check((await host.CloseAsync()).Success, "Reopened model did not close");
            tabs.SelectedIndex = 0;
            File.WriteAllText(Path.Combine(root, "studio-host-result.txt"), "STUDIO_HOST_OK childWindow sameModel resize 2D3D edit undo redo cancel save reopen close");
        }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    }
}
