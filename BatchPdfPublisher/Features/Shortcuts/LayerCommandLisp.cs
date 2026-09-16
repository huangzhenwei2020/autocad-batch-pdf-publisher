using System;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 图层直达命令的 AutoLISP 调用文本。
    ///
    /// 纯逻辑、不依赖 AutoCAD，因此可以在测试工程里离线断言——这段文字写错，
    /// 在 CAD 里的表现只是"快捷键按下去弹了 GL 对话框"或者"点了没反应"，
    /// 排查代价很高。
    ///
    /// 两个要点：
    ///
    /// 1. **整段包成一个 (progn ...)**。这段文字有两个消费方：快捷键别名
    ///    <c>(defun c:DOO () (progn ...) (princ))</c>，以及 CUIx 菜单宏
    ///    <c>^C^C(progn ...)</c>。菜单宏里多个散装 LISP 形式会被命令行拆开执行。
    ///
    /// 2. **目标图层走 WLSETLAYER（LispFunction），不走环境变量**。
    ///    AutoLISP 的 setenv 写的是 AutoCAD 自己的环境表，不保证同步进 Windows
    ///    进程环境块；而 .NET 的 Environment.GetEnvironmentVariable 读的正是进程
    ///    环境块。实测"填了图层快捷键、按下去却弹 GL 对话框"就是这个断点。
    ///    改成 AutoLISP 直接调用 .NET 的 LispFunction，同一个进程内直接传参，
    ///    不存在同步问题。setenv 作为兼容通道保留，前面套
    ///    <c>(if WLSETLAYER ...)</c> 是为了万一函数没注册（例如被同名 LISP 函数
    ///    占了名字）也不要让整条别名报错中断，至少还能退回环境变量通道。
    /// </summary>
    public static class LayerCommandLisp
    {
        /// <summary>兼容通道用的环境变量名。</summary>
        public const string EnvironmentVariable = "WANLUO_TARGET_LAYER";

        /// <summary>图层暂存函数名。必须与 Commands 上的 [LispFunction] 完全一致。</summary>
        public const string SetFunctionName = "WLSETLAYER";

        /// <summary>图层直达归层命令的内部命令名（所有图层共用一个命令）。</summary>
        public const string Command = "GL";

        public static string Build(string layerName)
        {
            var name = Escape(layerName);
            return "(progn "
                + "(if " + SetFunctionName + " (" + SetFunctionName + " \"" + name + "\")) "
                + "(setenv \"" + EnvironmentVariable + "\" \"" + name + "\") "
                + "(command \"" + Command + "\"))";
        }

        /// <summary>AutoLISP 字符串字面量转义。</summary>
        public static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
