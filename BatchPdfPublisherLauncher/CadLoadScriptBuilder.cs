using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BatchPdfPublisherLauncher
{
    internal static class CadLoadScriptBuilder
    {
        public static string BuildExpression(IEnumerable<string> assemblies)
        {
            var files = (assemblies ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var directories = files.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // A single Lisp form owns every NETLOAD argument. No subsequent command can
            // become a filename because of an extra CR/LF or a remaining input prompt.
            var trust = "((lambda (rest paths item cut result seen key) "
                + "(while (/= rest \"\") (setq cut (vl-string-search \";\" rest)) "
                + "(if cut (setq item (substr rest 1 cut) rest (substr rest (+ cut 2))) (setq item rest rest \"\")) "
                + "(setq paths (append paths (list item)))) "
                + "(foreach item (append paths '" + List(directories) + ") "
                + "(setq item (vl-string-trim \" \" item) key (strcase (vl-string-right-trim \"/\" (vl-string-translate \"\\\\\" \"/\" item)))) "
                + "(if (and (/= item \"\") (not (member key seen))) "
                + "(setq seen (cons key seen) result (strcat result (if (= result \"\") \"\" \";\") item)))) "
                + "(setvar \"TRUSTEDPATHS\" result)) (getvar \"TRUSTEDPATHS\") nil nil nil \"\" nil nil) ";
            var loads = "(foreach dll '" + List(files) + " "
                + "(if (not failure) (if (findfile dll) (progn (command \"_.NETLOAD\" dll) "
                + "(if (/= 0 (logand 1 (getvar \"CMDACTIVE\"))) (progn (command) (setq failure \"NETLOAD did not finish\")))) "
                + "(setq failure (strcat \"Missing assembly: \" dll))))) ";
            return "(progn (vl-load-com) ((lambda (result dll failure) (setvar \"FILEDIA\" 0) "
                // Keep native commands out of the Visual LISP catch/apply frame.
                // Cold NETLOAD enters the CLR and third-party CAD startup reactors;
                // wrapping that entry in nested VL evaluation can exhaust the host stack.
                + "(setq result (vl-catch-all-apply '(lambda () " + trust + ") nil)) "
                + "(if (not (vl-catch-all-error-p result)) (progn " + loads + "(if (not failure) (command \"BPPSTARTUP\")))) "
                + "(setvar \"FILEDIA\" 1) "
                + "(if (vl-catch-all-error-p result) (princ (strcat \"\\nWanLuo startup failed: \" (vl-catch-all-error-message result))) "
                + "(if failure (princ (strcat \"\\nWanLuo startup failed: \" failure)) "
                + "(princ \"\\nWANLUO_STARTUP_COMMANDS_DONE\")))) nil nil nil) (princ))";
        }

        public static string ForCom(IEnumerable<string> assemblies) => BuildExpression(assemblies) + "\r";
        public static string ForScript(IEnumerable<string> assemblies) => BuildExpression(assemblies) + "\n";

        public static string CreateLoadRequest(IEnumerable<string> assemblies, string directory, bool forCom)
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            var basename = Path.Combine(directory, "WanluoLoader-" + Guid.NewGuid().ToString("N"));
            var ansi = basename + ".ansi.lsp";
            var unicode = basename + ".utf8.lsp";
            var expression = BuildExpression(assemblies) + Environment.NewLine;
            File.WriteAllText(ansi, expression, Encoding.Default);
            File.WriteAllText(unicode, expression, new UTF8Encoding(true));
            // Keep the interactive command short. The legacy Lisp reader and the
            // Unicode reader need different source encodings for Chinese paths.
            var trusted = ";" + directory.Replace('\\', '/').TrimEnd('/') + ";";
            return "(progn (vl-load-com) (if (not (vl-string-search " + Quote(trusted.ToUpperInvariant())
                + " (strcase (strcat \";\" (vl-string-translate \"\\\\\" \"/\" (getvar \"TRUSTEDPATHS\")) \";\")))) "
                + "(setvar \"TRUSTEDPATHS\" (strcat (getvar \"TRUSTEDPATHS\") " + Quote(trusted.TrimEnd(';')) + "))) "
                + "(load (if (= 0 (getvar \"LISPSYS\")) " + Quote(ansi) + " " + Quote(unicode) + ")) (princ))"
                + (forCom ? "\r" : "\n");
        }

        private static string List(IEnumerable<string> values) => "(" + string.Join(" ", values.Select(Quote)) + ")";
        private static string Quote(string value)
        {
            if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("程序集路径含控制字符。");
            return "\"" + value.Replace('\\', '/').Replace("\"", "\\\"") + "\"";
        }

        public static bool HasCompleteReceipt(string content, string expectedAssembly, int expectedPid, DateTime sinceUtc)
        {
            foreach (var line in (content ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(new[] { " | " }, StringSplitOptions.None);
                DateTimeOffset timestamp;
                int pid;
                if (parts.Length != 4 || parts[1] != "BPPSTARTUP" || !parts[2].StartsWith("PID=", StringComparison.Ordinal)
                    || !int.TryParse(parts[2].Substring(4), out pid)
                    || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp)
                    || timestamp.UtcDateTime < sinceUtc || (expectedPid > 0 && pid != expectedPid)) continue;
                if (string.IsNullOrWhiteSpace(expectedAssembly) || string.Equals(parts[3].Replace('\\', '/'),
                    Path.GetFullPath(expectedAssembly).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
