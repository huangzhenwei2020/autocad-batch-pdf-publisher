using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>建模程序里的一张视图/图纸（给"落图选哪张"的清单用）。</summary>
    public sealed class StudioViewEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public ViewKind Kind { get; set; }
        public string FilePath { get; set; }
        public DateTime Modified { get; set; }
        public long SizeBytes { get; set; }
        public string PaperName { get; set; }
        public double PaperWidth { get; set; }
        public double PaperHeight { get; set; }
        /// <summary>是不是"建模程序刚推过来、还没落图"的那几张（清单里带 ★）。</summary>
        public bool Pending { get; set; }
        /// <summary>当前 DWG 已登记的放置状态，由 CAD 宿主读取后填充，不写入模型文件。</summary>
        public bool Placed { get; set; }

        /// <summary>清单里显示的一行（序号由调用方加）。</summary>
        public string Display
        {
            get
            {
                var kind = Kind == ViewKind.Sheet ? "图纸" : Kind == ViewKind.Plan ? "平面"
                    : Kind == ViewKind.Section ? "剖面" : Kind == ViewKind.Schedule ? "门窗表"
                    : Kind == ViewKind.OpeningElevation ? "门窗立面" : Kind == ViewKind.Axonometric ? "轴测" : "立面";
                return (Pending ? "★" : "　") + "[" + kind + "] " + (string.IsNullOrWhiteSpace(Title) ? Id : Title)
                    + "　" + Modified.ToString("MM-dd HH:mm");
            }
        }
    }

    /// <summary>待落图清单里的一条。</summary>
    public sealed class StudioPendingEntry
    {
        public string Id { get; set; }
        public string FilePath { get; set; }
    }

    /// <summary>待落图清单：建模程序"推到 CAD"时写下的、还没落图的图纸/视图。</summary>
    public sealed class StudioPendingList
    {
        public DateTime WrittenAt { get; set; }
        public List<StudioPendingEntry> Entries { get; set; } = new List<StudioPendingEntry>();
    }

    /// <summary>
    /// 插件与建模程序之间的"启动/取件"约定（纯逻辑，不依赖 AutoCAD，可单元测试）：
    ///
    /// 1. <see cref="BuildArguments"/>：CAD 里启动建模程序时带的命令行参数（项目文件夹 + 模型名称）；
    /// 2. <see cref="FindExecutable"/>：按候选路径找 万落建筑模型.exe（发布目录优先，找不到返回 null）；
    /// 3. <see cref="ListViews"/>：列出模型目录下已生成的视图/图纸（落图时按序号选，不用翻文件对话框）。
    /// </summary>
    public static class StudioLaunch
    {
        private static string SessionToken()
        {
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                return process.Id + "|" + process.StartTime.ToUniversalTime().Ticks;
        }
        public static void RegisterSession(string modelPath)
        {
            var file=modelPath+".studio-session.txt"; var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { File.WriteAllText(temp, SessionToken()); if(File.Exists(file))File.Replace(temp,file,null); else File.Move(temp,file); }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }
        public static bool IsCurrentSession(string modelPath)
        {
            try { return File.ReadAllText(modelPath+".studio-session.txt")==SessionToken(); } catch { return false; }
        }
        public static void RemoveSession(string modelPath)
        {
            try { var file = modelPath + ".studio-session.txt";
                if (File.Exists(file) && File.ReadAllText(file) == SessionToken()) File.Delete(file); }
            catch { }
        }
        public static bool HasLiveSession(string modelPath)
        {
            try {
                var parts = File.ReadAllText(modelPath + ".studio-session.txt").Split('|');
                using (var process = System.Diagnostics.Process.GetProcessById(int.Parse(parts[0])))
                    return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == long.Parse(parts[1]);
            } catch { return false; }
        }
        /// <summary>可执行文件名（发布时放在 <c>建筑模型</c> 子目录里）。</summary>
        public const string ExecutableName = "万落建筑模型.exe";
        /// <summary>建模程序在发布目录里的子目录名。</summary>
        public const string RelativeFolder = "建筑模型";
        /// <summary>模型目录（相对项目文件夹）。</summary>
        public const string ModelFolderName = "建筑模型";
        /// <summary>视图文件所在子目录。</summary>
        public const string ViewsFolderName = "views";
        /// <summary>"待落图"标记文件名（放在 views 目录里；用 .txt 免得被当成视图读）。</summary>
        public const string PendingFileName = "待落图.txt";

        public static void RememberActiveModel(string projectFolder, string modelPath)
        {
            if (string.IsNullOrWhiteSpace(projectFolder) || string.IsNullOrWhiteSpace(modelPath)) return;
            var folder = Path.Combine(projectFolder, ModelFolderName);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "当前模型.txt");
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, Path.GetFullPath(modelPath), Encoding.UTF8);
                if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static string ActiveModelPath(string projectFolder, string modelName)
        {
            if (string.IsNullOrWhiteSpace(projectFolder)) return null;
            var file = Path.Combine(projectFolder, ModelFolderName, "当前模型.txt");
            if (File.Exists(file))
            {
                var path = File.ReadAllText(file, Encoding.UTF8).Trim();
                if (!File.Exists(path))
                {
                    // Only recover the same project-local model after moving the project root.
                    var parent = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
                    var modelsRoot = parent == null ? null : Path.GetDirectoryName(parent);
                    var moved = parent == null ? null : Path.Combine(projectFolder, ModelFolderName,
                        Path.GetFileName(parent), Path.GetFileName(path));
                    if (!HasLiveSession(path) && modelsRoot != null
                        && string.Equals(Path.GetFileName(modelsRoot), ModelFolderName, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(modelName)
                        && string.Equals(Path.GetFileName(parent), modelName.Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetFileName(path), "model.json", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(moved))
                    {
                        RememberActiveModel(projectFolder, moved);
                        return Path.GetFullPath(moved);
                    }
                    throw new FileNotFoundException("当前记录的建筑模型文件不存在，请在建筑模型中打开正确的模型并保存。\n记录路径：" + path, path);
                }
                return Path.GetFullPath(path);
            }
            var folder = FindModelFolder(projectFolder, modelName);
            return folder == null ? null : Path.Combine(folder, "model.json");
        }

        /// <summary>待落图标记文件路径：<c>&lt;模型目录&gt;\views\待落图.txt</c>。</summary>
        public static string PendingFilePath(string modelFolder)
        {
            return string.IsNullOrWhiteSpace(modelFolder) ? null : Path.Combine(modelFolder, ViewsFolderName, PendingFileName);
        }

        /// <summary>
        /// 写下"待落图"清单（建模程序点「推到 CAD」时调用）。
        /// 格式是纯文本：第一行带时间戳，后面每行 <c>id|文件路径</c> —— 出问题用记事本就能看。
        /// </summary>
        public static bool WritePending(string modelFolder, IEnumerable<StudioPendingEntry> entries)
        {
            var file = PendingFilePath(modelFolder);
            if (string.IsNullOrWhiteSpace(file)) return false;
            return WritePendingFile(file, entries);
        }

        public static bool WritePendingFile(string file, IEnumerable<StudioPendingEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var builder = new System.Text.StringBuilder();
                builder.AppendLine("# 万落建筑模型 待落图 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (var entry in entries ?? Enumerable.Empty<StudioPendingEntry>())
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath)) continue;
                    builder.AppendLine((entry.Id ?? string.Empty) + "|" + entry.FilePath);
                }
                File.WriteAllText(file, builder.ToString(), System.Text.Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>切换完整视图批次；失败时尽量恢复旧批次，旧目录无法清理时返回其路径。</summary>
        public static string CommitStagedViews(string modelFolder, string stagingFolder)
        {
            if (string.IsNullOrWhiteSpace(modelFolder) || string.IsNullOrWhiteSpace(stagingFolder))
                throw new ArgumentException("模型目录和暂存目录不能为空。");
            var root = Path.GetFullPath(modelFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var stage = Path.GetFullPath(stagingFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pathComparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(stage), root, pathComparison)
                || !Path.GetFileName(stage).StartsWith(".views-staging-", StringComparison.Ordinal)
                || !Directory.Exists(stage) || Directory.GetFiles(stage, "*.json").Length == 0)
                throw new InvalidOperationException("视图暂存目录无效或没有视图，未切换发布批次。");
            return WithViewLock(root, () => CommitStagedViewsCore(root, stage));
        }

        private static string CommitStagedViewsCore(string root, string stage)
        {
            RecoverInterruptedPublishCore(root);
            var views = Path.Combine(root, ViewsFolderName);
            var backup = Path.Combine(root, ".views-backup-" + Guid.NewGuid().ToString("N"));
            var hadOldViews = Directory.Exists(views);
            if (hadOldViews) Directory.Move(views, backup);
            try { Directory.Move(stage, views); }
            catch
            {
                if (hadOldViews && !Directory.Exists(views)) Directory.Move(backup, views);
                throw;
            }
            if (!hadOldViews) return null;
            try { Directory.Delete(backup, true); return null; }
            catch { return backup; }
        }

        /// <summary>上次进程若在旧目录移走后中断，则恢复最近一份完整旧批次。</summary>
        public static bool RecoverInterruptedPublish(string modelFolder)
        {
            if (string.IsNullOrWhiteSpace(modelFolder) || !Directory.Exists(modelFolder)) return false;
            return WithViewLock(modelFolder, () => RecoverInterruptedPublishCore(modelFolder));
        }

        private static bool RecoverInterruptedPublishCore(string modelFolder)
        {
            var views = Path.Combine(modelFolder, ViewsFolderName);
            if (Directory.Exists(views)) return false;
            var backup = Directory.GetDirectories(modelFolder, ".views-backup-*")
                .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
            if (backup == null) return false;
            Directory.Move(backup, views);
            return true;
        }

        private static T WithViewLock<T>(string modelFolder, Func<T> action)
        {
            var path = Path.GetFullPath(modelFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.DirectorySeparatorChar == '\\') path = path.ToUpperInvariant();
            string name;
            using (var sha = SHA256.Create())
                name = "WanLuoViews-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path)))
                    .Replace("-", "").Substring(0, 32);
            using (var gate = new Mutex(false, name))
            {
                try
                {
                    if (!gate.WaitOne(TimeSpan.FromSeconds(15)))
                        throw new IOException("等待该模型的视图发布完成超时。");
                }
                catch (AbandonedMutexException) { /* 上个进程退出，继续检查备份目录。 */ }
                try { return action(); }
                finally { gate.ReleaseMutex(); }
            }
        }

        /// <summary>读"待落图"清单；没有或读不出来返回 null（调用方按"没有待落图"处理）。</summary>
        public static StudioPendingList ReadPending(string modelFolder)
        {
            var file = PendingFilePath(modelFolder);
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
            try
            {
                var list = new StudioPendingList { WrittenAt = File.GetLastWriteTime(file) };
                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    var split = line.IndexOf('|');
                    if (split <= 0 || split >= line.Length - 1) continue;
                    list.Entries.Add(new StudioPendingEntry
                    {
                        Id = line.Substring(0, split).Trim(),
                        FilePath = line.Substring(split + 1).Trim()
                    });
                }
                return list;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>落图完把这一条从"待落图"里去掉；清单空了就把文件删掉。</summary>
        public static void RemovePending(string modelFolder, string viewId)
        {
            var list = ReadPending(modelFolder);
            if (list == null) return;
            var remaining = list.Entries
                .Where(entry => entry != null && !string.Equals(entry.Id, viewId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var file = PendingFilePath(modelFolder);
            try
            {
                if (remaining.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
                WritePending(modelFolder, remaining);
            }
            catch
            {
                // 删不掉就算了，下次落图再清
            }
        }

        /// <summary>
        /// 组装启动参数：<c>--project "&lt;项目文件夹&gt;" --model "&lt;模型名称&gt;"</c>。
        /// 建模程序收到后直接打开这个项目/模型（不再默认打开样例）。
        /// </summary>
        public static string BuildArguments(string projectFolder, string modelName)
        {
            var arguments = string.Empty;
            if (!string.IsNullOrWhiteSpace(projectFolder))
                arguments += "--project \"" + projectFolder.Trim().TrimEnd('\\', '/') + "\" ";
            if (!string.IsNullOrWhiteSpace(modelName))
                arguments += "--model \"" + modelName.Trim() + "\"";
            return arguments.Trim();
        }

        /// <summary>
        /// 按候选顺序找建模程序；返回第一个存在的路径，都没有则 null。
        /// 候选里可以混入空值（调用方拼路径时的可选项）。
        /// </summary>
        public static string FindExecutable(IEnumerable<string> candidates)
        {
            foreach (var candidate in candidates ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    var path = candidate.Trim();
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
                catch
                {
                    // 路径里有非法字符就当这个候选不存在
                }
            }
            return null;
        }

        /// <summary>
        /// 插件发布目录下的建模程序常见位置（给 <see cref="FindExecutable"/> 用）：
        /// 插件 DLL 在 <c>&lt;发布根&gt;\CadApi\R24\</c>，而建模程序在 <c>&lt;发布根&gt;\建筑模型\</c>。
        /// </summary>
        public static IEnumerable<string> DefaultCandidates(string pluginAssemblyFolder)
        {
            var folder = pluginAssemblyFolder ?? string.Empty;
            // CadApi\R24 → 发布根；CadApi\R25 → 发布根
            var cadApi = Directory.GetParent(folder);
            var root = cadApi == null ? null : cadApi.Parent;
            if (root != null)
            {
                yield return Path.Combine(root.FullName, RelativeFolder, ExecutableName);
            }
            yield return Path.Combine(folder, RelativeFolder, ExecutableName);
            yield return Path.Combine(folder, ExecutableName);
        }

        /// <summary>
        /// 找某个项目的模型目录：<c>&lt;项目文件夹&gt;\建筑模型\&lt;模型名称&gt;</c>。
        /// 模型名称空着时，如果只有一个子目录就用它，否则返回 null（让调用方去问用户）。
        /// </summary>
        public static string FindModelFolder(string projectFolder, string modelName)
        {
            if (string.IsNullOrWhiteSpace(projectFolder)) return null;
            var root = Path.Combine(projectFolder.Trim(), ModelFolderName);
            if (!Directory.Exists(root)) return null;
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                var folder = Path.Combine(root, modelName.Trim());
                return Directory.Exists(folder) ? folder : null;
            }
            var children = Directory.GetDirectories(root);
            return children.Length == 1 ? children[0] : null;
        }

        /// <summary>
        /// 列出模型目录里已生成的视图/图纸（按"图纸在前、平面/立面/剖面/门窗表在后"排，
        /// 各类型内按名称）。文件坏了只跳过它自己。
        /// </summary>
        public static List<StudioViewEntry> ListViews(string modelFolder)
        {
            var result = new List<StudioViewEntry>();
            if (string.IsNullOrWhiteSpace(modelFolder)) return result;
            return WithViewLock(modelFolder, () => ListViewsCore(modelFolder));
        }

        private static List<StudioViewEntry> ListViewsCore(string modelFolder)
        {
            var result = new List<StudioViewEntry>();
            if (!Directory.Exists(modelFolder)) return result;
            RecoverInterruptedPublishCore(modelFolder);
            var viewsFolder = Path.Combine(modelFolder, ViewsFolderName);
            if (!Directory.Exists(viewsFolder)) return result;
            var pending = ReadPending(modelFolder);
            var pendingIds = new HashSet<string>(
                (pending == null ? new List<StudioPendingEntry>() : pending.Entries)
                    .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Id))
                    .Select(entry => entry.Id.Trim()),
                StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(viewsFolder, "*.json"))
            {
                try
                {
                    var view = BuildingModelJson.LoadView(file);
                    if (view == null) continue;
                    var info = new FileInfo(file);
                    var id = string.IsNullOrWhiteSpace(view.Id) ? Path.GetFileNameWithoutExtension(file) : view.Id;
                    result.Add(new StudioViewEntry
                    {
                        Id = id,
                        Title = view.Title,
                        Kind = view.Kind,
                        FilePath = file,
                        Modified = info.LastWriteTime,
                        SizeBytes = info.Length,
                        PaperName = view.PaperName,
                        PaperWidth = view.PaperWidth,
                        PaperHeight = view.PaperHeight,
                        Pending = pendingIds.Contains(id)
                    });
                }
                catch
                {
                    // 读不出来的跳过，别因为一个坏文件列不出清单
                }
            }
            // 带 ★（待落图）的排最前，其余按"图纸 → 平面 → 立面 → 剖面 → 门窗表"
            return result
                .OrderByDescending(entry => entry.Pending)
                .ThenBy(entry => Rank(entry.Kind))
                .ThenBy(entry => entry.Title ?? entry.Id, StringComparer.CurrentCulture)
                .ToList();
        }

        private static int Rank(ViewKind kind)
        {
            switch (kind)
            {
                case ViewKind.Sheet: return 0;
                case ViewKind.Plan: return 1;
                case ViewKind.Elevation: return 2;
                case ViewKind.Section: return 3;
                default: return 4;
            }
        }
    }
}
