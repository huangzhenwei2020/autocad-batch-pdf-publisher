using BatchPdfPublisherLauncher;

internal static class CadLoadScriptTests
{
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--fixture")
        {
            var paths = args.Skip(2).ToArray();
            var good = CadLoadScriptBuilder.ForScript(paths);
            var missing = CadLoadScriptBuilder.ForScript(new[] { Path.Combine(args[1], "missing.dll") });
            var setup = "(setq wlqa-trust (getvar \"TRUSTEDPATHS\") wlqa-filedia (getvar \"FILEDIA\"))\n"
                + "(setvar \"FILEDIA\" 1)\n(setvar \"TRUSTEDPATHS\" \"C:/vendor;C:\\\\VENDOR;C:/other\")\n";
            var check = "(princ (strcat \"\\nCAD_LOAD_NATIVE_OK FILEDIA=\" (itoa (getvar \"FILEDIA\")) \" TRUST=\" (getvar \"TRUSTEDPATHS\")))\n";
            var restore = "(setvar \"TRUSTEDPATHS\" wlqa-trust)\n(setvar \"FILEDIA\" wlqa-filedia)\n"
                + "(princ (strcat \"\\nCAD_LOAD_SETTINGS_RESTORED=\" (if (and (= (getvar \"TRUSTEDPATHS\") wlqa-trust) (= (getvar \"FILEDIA\") wlqa-filedia)) \"YES\" \"NO\")))\n";
            File.WriteAllText(Path.Combine(args[1], "load.scr"), setup + good + missing + check + restore + "_.QUIT\n_Y\n", System.Text.Encoding.UTF8);
            Console.WriteLine("CAD_LOAD_FIXTURE_OK " + args[1]);
            return;
        }
        var pathsToLoad = new[] { @"C:\资源\main.dll", @"C:\资源\MAIN.dll", @"C:\other space\part.dll" };
        var expression = CadLoadScriptBuilder.BuildExpression(pathsToLoad);
        var quoted = false; var escape = false; var depth = 0;
        foreach (var character in expression)
        {
            if (escape) { escape = false; continue; }
            if (quoted && character == '\\') { escape = true; continue; }
            if (character == '"') { quoted = !quoted; continue; }
            if (!quoted && character == '(') depth++;
            if (!quoted && character == ')') { depth--; Require(depth >= 0, "Unbalanced Lisp"); }
        }
        Require(depth == 0 && !quoted && expression.StartsWith("(progn") && expression.EndsWith("(princ))"), "Not one complete Lisp form");
        Require(!expression.Contains('\r') && !expression.Contains('\n') && expression.Contains("(command \"_.NETLOAD\" dll)"), "Raw interactive command stream remained");
        Require(CadLoadScriptBuilder.ForCom(pathsToLoad).EndsWith("\r") && !CadLoadScriptBuilder.ForCom(pathsToLoad).Contains('\n'), "COM must receive one Enter, not CRLF");
        Require(CadLoadScriptBuilder.ForScript(pathsToLoad).EndsWith("\n") && !CadLoadScriptBuilder.ForScript(pathsToLoad).Contains('\r'), "SCR newline mismatch");
        Require(expression.Contains("vl-catch-all-apply") && expression.Contains("(setvar \"FILEDIA\" 1)") && expression.Contains("(command)"), "Error recovery missing");
        Require(expression.Contains("vl-string-translate") && expression.Contains("member key seen"), "Trusted paths are not deduplicated");
        Require(!expression.Contains("MAIN.dll") && expression.Contains("other space/part.dll"), "Assembly deduplication or quoted path lost");
        var start = DateTime.UtcNow.AddMinutes(-1);
        string Receipt(string stage, int pid, string path, DateTime when) => when.ToString("O") + " | " + stage + " | PID=" + pid + " | " + path;
        var main = Path.GetFullPath(pathsToLoad[0]);
        Require(!CadLoadScriptBuilder.HasCompleteReceipt(Receipt("Initialize", 123, main, DateTime.UtcNow), main, 123, start), "Initialization falsely counted as complete");
        Require(!CadLoadScriptBuilder.HasCompleteReceipt(Receipt("BPPSTARTUP", 122, main, DateTime.UtcNow), main, 123, start), "Wrong CAD receipt accepted");
        Require(!CadLoadScriptBuilder.HasCompleteReceipt(Receipt("BPPSTARTUP", 123, main, start.AddSeconds(-1)), main, 123, start), "Old receipt accepted");
        Require(!CadLoadScriptBuilder.HasCompleteReceipt(Receipt("BPPSTARTUP", 123, "C:/wrong/main.dll", DateTime.UtcNow), main, 123, start), "Wrong assembly receipt accepted");
        Require(CadLoadScriptBuilder.HasCompleteReceipt(Receipt("BPPSTARTUP", 123, main, DateTime.UtcNow), main, 123, start), "Completion rejected");
        Require(CadLoadScriptBuilder.HasCompleteReceipt(Receipt("BPPSTARTUP", 123, main, DateTime.UtcNow), null, 0, start), "Bundle receipt rejected");
        Console.WriteLine("CAD_LOAD_SCRIPT_TESTS_OK singleForm oneComEnter scriptNewline paths errorRecovery trustedDedup stage pid assembly freshReceipt bundle");
    }
}
