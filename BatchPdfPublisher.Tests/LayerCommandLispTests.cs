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
        InvocationCapturesPreselectionBeforeRunning();
        InvocationKeepsSetenvAsFallback();
        InvocationGuardsTheLispFunctionCalls();
        LayerNamesAreEscaped();
        LayerFeatureIdsDoNotCollideWithFixedIds();
        Console.WriteLine("PASS LayerCommandLisp");
    }

    /// <summary>
    /// 固定功能里有 <c>layer_assignment</c>（归层 GL）。前缀如果是 "layer_"，
    /// 它就会被当成"图层命令 assignment"，于是归层的快捷键在设置窗口里改不了、
    /// 统计图层命令时还会多出一条假的 "GL→归层"。
    /// </summary>
    private static void LayerFeatureIdsDoNotCollideWithFixedIds()
    {
        string key;
        Assert(!LayerFeatureIds.TryParse("layer_assignment", out key),
            "固定功能 layer_assignment 不能被抓成图层命令");
        foreach (var fixedId in new[]
        {
            "publisher", "frame", "catalog", "attribute_batch", "attribute_definition",
            "architecture_spec", "cad_table_xlsx", "stair_detail", "drafting_standard",
            "layer_assignment", "drawing_scale", "door_window", "detail_layout",
            "line_vision", "room_rename", "shortcut_settings", "menubar", "cloud_sync"
        })
            Assert(!LayerFeatureIds.TryParse(fixedId, out key), "固定功能不能被抓成图层命令：" + fixedId);

        // 反向：真的图层命令要认得出来，且能取回图层键。
        var id = LayerFeatureIds.For("DoorWindowDoor");
        Assert(LayerFeatureIds.TryParse(id, out key) && key == "DoorWindowDoor",
            "图层命令 id 应能还原出图层键");
        Assert(LayerFeatureIds.TryParse("layerrole_assignment", out key) && key == "assignment",
            "图层键本身叫 assignment 时也不该与固定功能混起来");
        Console.WriteLine("PASS LayerFeatureIdsDoNotCollideWithFixedIds");
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

    /// <summary>
    /// 用户在按图层快捷键之前往往已经选好了对象，这时要直接生效、不能再问一次
    /// "选择对象"。预选集要在调用 GL **之前**抓下来，因为 GL 是由 LISP 的
    /// (command "GL") 发起的，不能假定预选集在那条路径上还在。
    /// </summary>
    private static void InvocationCapturesPreselectionBeforeRunning()
    {
        var lisp = LayerCommandLisp.Build("WL-墙面");
        Assert(lisp.Contains("(" + LayerCommandLisp.SelectionFunctionName + " (ssget \"_I\"))"),
            "the implied (pickfirst) selection must be handed over through the LispFunction");
        Assert(lisp.IndexOf("(ssget \"_I\")", StringComparison.Ordinal)
               < lisp.IndexOf("(command \"GL\")", StringComparison.Ordinal),
            "the selection must be captured before GL runs");
        // ssget "_I" 在没有预选时返回 nil，GL 这时才提示用户选择。
        Assert(lisp.Contains("(if " + LayerCommandLisp.SelectionFunctionName),
            "the capture must be guarded so no preselection stays a no-op");
        Console.WriteLine("PASS LayerLispCapturesPreselectionBeforeRunning");
    }

    private static void InvocationGuardsTheLispFunctionCalls()
    {
        var lisp = LayerCommandLisp.Build("WL-地面");
        // 万一 LispFunction 没注册（名字被同名 LISP 函数占了之类），
        // 裸调用会让整条别名报错中断，连 setenv 回退和 GL 都跑不到。
        // (if WLSETLAYER ...) 在 AutoLISP 里是安全的：未绑定的符号求值为 nil。
        Assert(lisp.Contains("(if " + LayerCommandLisp.SetFunctionName + " (" + LayerCommandLisp.SetFunctionName),
            "the LispFunction call must be guarded so an unregistered function cannot abort the alias");
        Assert(lisp.Contains("(if " + LayerCommandLisp.SelectionFunctionName + " (" + LayerCommandLisp.SelectionFunctionName),
            "the selection LispFunction call must be guarded too");
        Console.WriteLine("PASS LayerLispGuardsTheLispFunctionCalls");
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
