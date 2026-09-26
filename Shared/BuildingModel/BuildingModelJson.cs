using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// 模型与视图的 JSON 读写。用 <see cref="DataContractJsonSerializer"/>——
    /// 与本仓库其它配置一致，net48 与 net8 都能用，不引入新依赖。
    ///
    /// 写盘一律"临时文件 + 替换"，避免半个文件把模型写坏（沿用 PublishPlanStore 的做法）。
    /// </summary>
    public static class BuildingModelJson
    {
        private static readonly object Sync = new object();

        public static void SaveModel(string path, BuildingModelDocument model)
        {
            Write(path, typeof(BuildingModelDocument), model);
        }

        /// <summary>序列化成字符串（撤销栈与单元测试用）。</summary>
        public static string ToJson(BuildingModelDocument model)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(BuildingModelDocument)).WriteObject(stream, model);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static BuildingModelDocument FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("模型内容为空。");
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (BuildingModelDocument)new DataContractJsonSerializer(typeof(BuildingModelDocument)).ReadObject(stream);
        }

        public static BuildingModelDocument LoadModel(string path)
        {
            var model = (BuildingModelDocument)Read(path, typeof(BuildingModelDocument));
            if (model == null) throw new InvalidDataException("模型文件内容为空：" + path);
            if (model.SchemaVersion > BuildingModelSchema.Version)
                throw new InvalidDataException("模型文件版本（" + model.SchemaVersion + "）高于本程序支持的版本（"
                    + BuildingModelSchema.Version + "），请升级后再打开：" + path);
            return model;
        }

        public static void SaveView(string path, ViewDocument view)
        {
            Write(path, typeof(ViewDocument), view);
        }

        public static ViewDocument LoadView(string path)
        {
            var view = (ViewDocument)Read(path, typeof(ViewDocument));
            if (view == null) throw new InvalidDataException("视图文件内容为空：" + path);
            if (view.SchemaVersion > BuildingModelSchema.Version)
                throw new InvalidDataException("视图文件版本（" + view.SchemaVersion + "）高于本插件支持的版本（"
                    + BuildingModelSchema.Version + "），请升级插件后再落图：" + path);
            return view;
        }

        public static void SaveImport(string path, DrawingImportDocument document)
        {
            Write(path, typeof(DrawingImportDocument), document);
        }

        public static DrawingImportDocument LoadImport(string path)
        {
            var document = (DrawingImportDocument)Read(path, typeof(DrawingImportDocument));
            if (document == null) throw new InvalidDataException("提取文件内容为空：" + path);
            return document;
        }

        /// <summary>项目里的模型目录：<c>&lt;项目文件夹&gt;\建筑模型\&lt;名称&gt;\</c>。</summary>
        public static string ModelFolder(string projectFolder, string name)
        {
            var safe = SafeName(string.IsNullOrWhiteSpace(name) ? "建筑模型" : name);
            return Path.Combine(projectFolder ?? string.Empty, "建筑模型", safe);
        }

        public static string ModelFilePath(string projectFolder, string name)
        {
            return Path.Combine(ModelFolder(projectFolder, name), "model.json");
        }

        public static string ViewsFolder(string projectFolder, string name)
        {
            return Path.Combine(ModelFolder(projectFolder, name), "views");
        }

        public static string ViewFilePath(string projectFolder, string name, string viewId)
        {
            return Path.Combine(ViewsFolder(projectFolder, name), SafeName(viewId) + ".json");
        }

        public static string ImportFilePath(string projectFolder, string name)
        {
            return Path.Combine(ModelFolder(projectFolder, name), "import.json");
        }

        /// <summary>门窗类型库：放在模型目录下的 <c>openings.json</c>（随项目走）。</summary>
        public static string OpeningLibraryPath(string projectFolder, string name)
        {
            return Path.Combine(ModelFolder(projectFolder, name), "openings.json");
        }

        public static void SaveOpeningLibrary(string path, OpeningTypeLibraryDocument library)
        {
            Write(path, typeof(OpeningTypeLibraryDocument), library);
        }

        public static OpeningTypeLibraryDocument LoadOpeningLibrary(string path)
        {
            var library = (OpeningTypeLibraryDocument)Read(path, typeof(OpeningTypeLibraryDocument));
            if (library == null) throw new InvalidDataException("门窗类型库内容为空：" + path);
            if (library.Types == null) library.Types = new List<OpeningTypeModel>();
            if (library.Templates == null) library.Templates = new List<OpeningTemplateModel>();
            return library;
        }

        private static string SafeName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder();
            foreach (var ch in value ?? string.Empty) builder.Append(invalid.Contains(ch) ? '_' : ch);
            var text = builder.ToString().Trim();
            return text.Length == 0 ? "模型" : text;
        }

        private static void Write(string path, Type type, object value)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空。", nameof(path));
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) throw new IOException("路径目录无效：" + path);
            Directory.CreateDirectory(directory);
            lock (Sync)
            {
                var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(type).WriteObject(stream, value);
                        stream.Flush(true);
                    }
                    if (new FileInfo(temporary).Length == 0) throw new IOException("暂存文件为空：" + path);
                    if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                    else File.Move(temporary, path);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        private static object Read(string path, Type type)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("文件不存在：" + path, path);
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return new DataContractJsonSerializer(type).ReadObject(stream);
        }
    }
}
