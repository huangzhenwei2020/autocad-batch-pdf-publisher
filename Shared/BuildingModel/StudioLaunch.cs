using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
                    : Kind == ViewKind.Section ? "剖面" : Kind == ViewKind.Schedule ? "门窗表" : "立面";
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
                yield return Path.Combine(root.FullName, RelativeFolder, "万落建筑模型.dll");
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
