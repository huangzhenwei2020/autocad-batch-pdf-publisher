using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Text;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 经典下拉菜单（菜单栏）。
    ///
    /// AutoCAD 从 2010 起把菜单栏默认隐藏了，但**并没有移除**它：AutoCAD 2022～2025
    /// 仍然支持 MENUBAR 系统变量（0 = 隐藏、1 = 显示，初值 0，保存在注册表）。
    /// 所以"新版本 CAD 没有菜单了"其实是"菜单栏没打开"——菜单加载了也看不见。
    /// 本服务做三件事：
    ///   1. 把 MENUBAR 置 1，让菜单栏显示出来；
    ///   2. 按功能分组生成运行时 CUIx 局部菜单（分组用级联子菜单），用 -MENULOAD 加载；
    ///   3. 用 menucmd 把本组弹菜单挂到菜单栏位置 POP16 上。
    ///
    /// 之所以运行时生成而不是随包发 CUIx：功能表就是 FeatureRegistry，
    /// 新功能登记一次就会自动出现在功能区、菜单栏和快捷键三处，不存在两份不同步。
    /// CUIx 的生成规则在 <see cref="CuixPackageBuilder"/>，不依赖 AutoCAD，可离线断言。
    /// </summary>
    public static class MenuService
    {
        private const string MenuGroup = CuixPackageBuilder.MenuGroupName;

        // 菜单要在文档就绪之后才能用 SendStringToExecute 加载。原来的实现挂在
        // Application.Idle 上重试，但只给了 5 次——Idle 每秒能触发几十次，插件
        // 加载时还没有活动文档的话，5 次预算转瞬就用完了，菜单永远不再尝试。
        // 改成按时间节流 + 限定总时长，既不空转也不会过早放弃。
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(90);

        private static EventHandler _idle;
        private static bool _mnuRequested;
        private static int _installAttempts;
        private static DateTime _startedUtc = DateTime.MinValue;
        private static DateTime _lastAttemptUtc = DateTime.MinValue;

        public static void InstallWhenReady()
        {
            ArmRetry();
            Install();
        }

        /// <summary>尝试挂上菜单；文档还没就绪时由 Idle 重试。</summary>
        public static void Install()
        {
            if (_mnuRequested) return;
            _lastAttemptUtc = DateTime.UtcNow;
            _installAttempts++;
            LoadPartialMenu();
        }

        /// <summary>重新加载菜单（快捷键或图层表变更后调用），返回菜单是否已挂上。</summary>
        public static bool RefreshNow()
        {
            // 这里**不再**单独发一行 "-MENUUNLOAD\nBPP\n"。
            // BuildLoadExpression 里已经带了"先卸载再加载"的守卫，而且整段是一个
            // AutoLISP 形式。按行裸发时，只要 -MENUUNLOAD 没把 BPP 这一行吃掉，
            // 残留的 BPP 就会落到命令行上被当成命令执行——而 BPP 正是打开批量打印
            // 面板的命令。用户看到的就是"改完快捷键、关掉设置窗口，BPP 自己打开了"。
            _mnuRequested = false;
            _installAttempts = 0;
            ArmRetry();
            Install();
            TraceLine("菜单已请求重新加载（快捷键或图层表变更）");
            return _mnuRequested;
        }

        public static void Remove()
        {
            Detach();
        }

        public static bool IsMenuBarVisible()
        {
            try { return Convert.ToInt32(Application.GetSystemVariable("MENUBAR")) == 1; }
            catch { return false; }
        }

        /// <summary>
        /// 显示或隐藏经典菜单栏，返回操作后的可见状态。
        /// 从隐藏切到显示时顺带把本插件的下拉菜单补挂上去——菜单栏隐藏期间
        /// 挂菜单可能不生效，用户重新打开时应当能看到万落建筑工具。
        /// </summary>
        public static bool ToggleMenuBar()
        {
            var visible = IsMenuBarVisible();
            SetMenuBarVisible(!visible);
            if (visible) return false;
            _mnuRequested = false;
            _installAttempts = 0;
            ArmRetry();
            Install();
            return true;
        }

        private static void SetMenuBarVisible(bool visible)
        {
            try
            {
                Application.SetSystemVariable("MENUBAR", visible ? 1 : 0);
                TraceLine("MENUBAR 已设为 " + (visible ? 1 : 0));
            }
            catch (Exception exception) { Trace(exception); }
        }

        private static void ArmRetry()
        {
            _startedUtc = DateTime.UtcNow;
            if (_idle != null) return;
            _idle = (sender, args) => PumpRetry();
            Application.Idle += _idle;
        }

        private static void PumpRetry()
        {
            if (_mnuRequested) { Detach(); return; }
            if (_installAttempts >= 1 && DateTime.UtcNow - _startedUtc > RetryWindow) { Detach(); return; }
            if (DateTime.UtcNow - _lastAttemptUtc < RetryInterval) return;
            Install();
        }

        private static void Detach()
        {
            if (_idle == null) return;
            try { Application.Idle -= _idle; } catch { }
            _idle = null;
        }

        private static void LoadPartialMenu()
        {
            if (_mnuRequested) return;
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return; // 还没有图纸，交给 Idle 继续重试
            try
            {
                // 菜单栏默认是隐藏的（MENUBAR 初值 0）。不先把它打开，下面挂上去的
                // 弹菜单也看不见——这正是"菜单明明加载了却找不到"的原因。
                if (!IsMenuBarVisible()) SetMenuBarVisible(true);

                var path = System.IO.Path.Combine(UserDataPaths.TemporaryDirectory, CuixPackageBuilder.PackageFileName);
                CuixPackageBuilder.WritePackage(path, CuixPackageBuilder.BuildParts(
                    FeatureRegistry.All, ShortcutSettingsService.Load(), DateTime.UtcNow));
                // 先卸载同名菜单组再加载，并整段包成一个 AutoLISP 形式（见
                // CuixPackageBuilder.BuildLoadExpression 的说明）。
                document.SendStringToExecute(CuixPackageBuilder.BuildLoadExpression(path), true, false, false);
                _mnuRequested = true;
                TraceLine("菜单已请求加载并挂到菜单栏 P16：" + path + "（第 " + _installAttempts + " 次尝试）");
            }
            catch (Exception exception) { Trace(exception); }
        }

        private static void Trace(Exception exception)
        {
            TraceLine("Menu: " + exception);
        }

        private static void TraceLine(string message)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(UserDataPaths.LogsDirectory, "BatchPdfPublisher.ui.log"),
                    DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
