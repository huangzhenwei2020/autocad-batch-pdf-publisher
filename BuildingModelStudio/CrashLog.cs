using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>
    /// 程序级错误兜底：把没被处理的异常写进日志，再用中文提示一次。
    ///
    /// 目的很实在：用户看到的应该是"出错了 + 日志在哪"，而不是 .NET 那句
    /// "应用程序的组件中发生了未经处理的异常…… System.OverflowException"。
    /// 日志位置：%LOCALAPPDATA%\万落建筑模型\错误日志\建筑模型-错误-*.log
    /// </summary>
    internal static class CrashLog
    {
        public static string LogFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "万落建筑模型", "错误日志");
            }
        }

        public static string Report(Exception exception)
        {
            var text = Describe(exception);
            var path = WriteLog(text);
            if (!Program.Headless)
            {
                try
                {
                    MessageBox.Show(
                        "程序遇到了一个没有预料到的错误，已经记下日志（不影响已经保存的模型/视图）。\r\n\r\n"
                        + text + "\r\n\r\n日志：" + path + "\r\n\r\n可以点「继续」接着用，或把这个日志发给开发者。",
                        "万落建筑模型 · 出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch
                {
                    // 连提示框都弹不出来就算了，日志已经写好
                }
            }
            return path;
        }

        private static string Describe(Exception exception)
        {
            if (exception == null) return "（没有异常信息）";
            var builder = new StringBuilder();
            builder.AppendLine(exception.GetType().FullName + "：" + exception.Message);
            var inner = exception.InnerException;
            var depth = 0;
            while (inner != null && depth++ < 5)
            {
                builder.AppendLine("  ← " + inner.GetType().FullName + "：" + inner.Message);
                inner = inner.InnerException;
            }
            builder.AppendLine();
            builder.AppendLine(exception.StackTrace);
            return builder.ToString().TrimEnd();
        }

        private static string WriteLog(string text)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                var path = Path.Combine(LogFolder,
                    "建筑模型-错误-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  万落建筑模型" + Environment.NewLine
                    + text + Environment.NewLine, Encoding.UTF8);
                return path;
            }
            catch
            {
                return "（日志写入失败：" + LogFolder + "）";
            }
        }
    }
}
