using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using System;

namespace BatchPdfPublisher.Services
{
    internal static class CloudSyncCadNotificationService
    {
        private static TrayItem _trayItem;
        private static readonly object Sync = new object();
        private static CloudSyncResult _pendingResult;
        private static Exception _pendingFailure;

        public static void Install()
        {
            try
            {
                if (_trayItem != null) return;
                _trayItem = new TrayItem { ToolTipText = "万落建筑云同步", Visible = true, Enabled = true };
                Application.StatusBar.TrayItems.Add(_trayItem);
                Application.Idle += OnIdle;
            }
            catch { _trayItem = null; }
        }

        public static void Remove()
        {
            try { Application.Idle -= OnIdle; } catch { }
            try { if (_trayItem != null) { _trayItem.CloseBubbleWindows(); Application.StatusBar.TrayItems.Remove(_trayItem); _trayItem.Dispose(); } } catch { }
            _trayItem = null;
        }

        public static void Show(CloudSyncResult result, Exception failure)
        {
            lock (Sync) { _pendingResult = result; _pendingFailure = failure; }
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            CloudSyncResult result; Exception failure;
            lock (Sync) { result = _pendingResult; failure = _pendingFailure; _pendingResult = null; _pendingFailure = null; }
            if (result == null && failure == null) return;
            try
            {
                if (_trayItem == null) Install(); if (_trayItem == null) return;
                var warning = failure != null || (result != null && (result.Errors > 0 || result.Conflicts > 0));
                var title = warning ? "云同步需要处理" : "云同步完成";
                var text = failure != null ? failure.GetBaseException().Message : result == null ? "同步任务已结束。" : result.Uploaded == 0 && result.Downloaded == 0 && result.Errors == 0 && result.Conflicts == 0 ? "本机与云端已是最新状态。" : result.Summary;
                _trayItem.CloseBubbleWindows();
                _trayItem.ShowBubbleWindow(new TrayItemBubbleWindow { Title = title, Text = text, Text2 = result == null ? string.Empty : "本机 " + result.LocalFileCount + " 个文件 · 云端 " + result.RemoteFileCount + " 个文件", IconType = warning ? IconType.Warning : IconType.Information });
            }
            catch { }
        }
    }
}
