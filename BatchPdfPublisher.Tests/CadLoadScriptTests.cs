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
        Require(CadStartupGate.DeferLoad("T20 天正建筑 V9.0",3)&&CadStartupGate.DeferLoad("T30 天正建筑",1),"Tianzheng explicit loads must wait outside SCR startup");
        Require(!CadStartupGate.DeferLoad("无天正",3)&&!CadStartupGate.DeferLoad(null,3)&&!CadStartupGate.DeferLoad("T20",0),"Vanilla/bundle startup changed");
        Require(CadStartupGate.MatchesRelease("24.1s (LMS Tech)","24.1")&&CadStartupGate.MatchesRelease("24.1.51.0","24.1")
            &&!CadStartupGate.MatchesRelease("24.10s","24.1")&&!CadStartupGate.MatchesRelease("24.3s","24.1")&&!CadStartupGate.MatchesRelease(null,"24.1"),"COM version suffix or wrong release matching");
        var gate=new CadStartupGate();
        bool Ready(int pid,string document,bool quiet,int seconds)=>gate.Observe(pid,document,quiet,TimeSpan.FromSeconds(seconds));
        Require(!Ready(123,"Drawing1",true,0)&&!Ready(123,"Drawing1",true,8),"First quiet interval was treated as complete startup");
        Require(!Ready(123,"Drawing1",false,12)&&!Ready(123,"Drawing1",true,13)&&!Ready(123,"Drawing1",true,20)&&Ready(123,"Drawing1",true,21),"Busy startup did not restart stability interval");
        Require(!Ready(123,"OtherDrawing",true,22)&&Ready(123,"OtherDrawing",true,30),"Document change did not reset idle interval");
        Require(!Ready(124,"OtherDrawing",true,31)&&!Ready(124,"OtherDrawing",true,39)&&Ready(124,"OtherDrawing",true,46),"Different CAD reused first process readiness");
        Require(!Ready(0,null,false,47)&&!Ready(124,"OtherDrawing",true,48)&&Ready(124,"OtherDrawing",true,56),"Lost COM connection retained idle interval");
        Console.WriteLine("CAD_STARTUP_GATE_OK TianzhengOnly minimumStartup sustainedIdle busy document process lostConnection");
        Console.WriteLine("CAD_LOAD_SCRIPT_TESTS_OK singleForm oneComEnter scriptNewline paths errorRecovery trustedDedup stage pid assembly freshReceipt bundle");
    }
}
