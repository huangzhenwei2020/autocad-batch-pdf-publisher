using System;
using BatchPdfPublisher.Services;

// 图层直达命令的 AutoLISP 调用文本。
//
// 这段文字是"填了图层快捷键、按下去却弹出 GL 对话框"这个问题的现场：
// 只要目标图层没真正传到 .NET 侧，快捷键就会退化成一次普通 GL。
// 在 CAD 里排查代价很高，所以把规则在这里锁死。
internal static class LayerCommandLispTests
{
    public static void RunAll()
    {
        InvocationIsASinglePrognForm();
        InvocationPassesLayerThroughLispFunction();
        InvocationKeepsSetenvAsFallback();
        InvocationGuardsTheLispFunctionCall();
        LayerNamesAreEscaped();
        Console.WriteLine("PASS LayerCommandLisp");
    }

    private static void InvocationIsASinglePrognForm()
    {
        var lisp = LayerCommandLisp.Build("WL-墙面");
        // 别名是 (defun c:X () <这段> (princ))，菜单宏是 ^C^C<这段>。
        // 菜单宏里多个散装 LISP 形式会被命令行拆开执行，必须是一个形式。
        Assert(lisp.StartsWith("(progn ", StringComparison.Ordinal), "should be a single progn form");
        Assert(lisp.EndsWith(")", StringComparison.Ordinal), "form should be closed");
        Assert(Balance(lisp), "parentheses should balance");
        Assert(!lisp.Contains("\n") && !lisp.Contains("\r"), "must stay on one line");
        Console.WriteLine("PASS LayerLispIsASinglePrognForm");
    }

    private static void InvocationPassesLayerThroughLispFunction()
    {
        var lisp = LayerCommandLisp.Build("WL-门窗-门");
        // 主通道：同进程的 LispFunction 调用，不经过环境变量。
        Assert(lisp.Contains("(" + LayerCommandLisp.SetFunctionName + " \"WL-门窗-门\")"),
            "the target layer must be passed to the LispFunction");
        Assert(lisp.Contains("(command \"GL\")"), "GL must still be invoked");
        // 传值必须排在调用 GL 之前，否则 GL 已经弹完对话框了。
        Assert(lisp.IndexOf(LayerCommandLisp.SetFunctionName, StringComparison.Ordinal)
               < lisp.IndexOf("(command \"GL\")", StringComparison.Ordinal),
            "the layer must be handed over before GL runs");
        Console.WriteLine("PASS LayerLispPassesLayerThroughLispFunction");
    }

    private static void InvocationKeepsSetenvAsFallback()
    {
        var lisp = LayerCommandLisp.Build("WL-地面");
        Assert(lisp.Contains("(setenv \"" + LayerCommandLisp.EnvironmentVariable + "\" \"WL-地面\")"),
            "the setenv channel should stay as a fallback");
        Console.WriteLine("PASS LayerLispKeepsSetenvAsFallback");
    }

    private static void InvocationGuardsTheLispFunctionCall()
    {
        var lisp = LayerCommandLisp.Build("WL-地面");
        // 万一 LispFunction 没注册（名字被同名 LISP 函数占了之类），
        // 裸调用会让整条别名报错中断，连 setenv 回退和 GL 都跑不到。
        // (if WLSETLAYER ...) 在 AutoLISP 里是安全的：未绑定的符号求值为 nil。
        Assert(lisp.Contains("(if " + LayerCommandLisp.SetFunctionName + " (" + LayerCommandLisp.SetFunctionName),
            "the LispFunction call must be guarded so an unregistered function cannot abort the alias");
        Console.WriteLine("PASS LayerLispGuardsTheLispFunctionCall");
    }

    private static void LayerNamesAreEscaped()
    {
        var lisp = LayerCommandLisp.Build("A\"B\\C");
        Assert(lisp.Contains("\"A\\\"B\\\\C\""), "quotes and backslashes should be escaped for AutoLISP");
        Assert(Balance(lisp), "escaping must not break the parentheses");
        Console.WriteLine("PASS LayerLispEscapesLayerNames");
    }

    private static bool Balance(string text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && inString) { i++; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (depth < 0) return false;
        }
        return depth == 0 && !inString;
    }

    private static void Assert(bool condition, string message)
    {
        if (condition) return;
        throw new InvalidOperationException("LayerCommandLispTests failed: " + message);
    }
}
