using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadArchSpec.Domain.Rules;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace CadArchSpec.RuleEngine
{
    public sealed class RulePackageLoader
    {
        private static readonly HashSet<string> AllowedCheckTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "requiredSection",
            "requiredField",
            "fieldConsistency",
            "standardStatus",
            "tableRequired",
            "formula",
            "dateApplicability"
        };

        private readonly JsonSerializerSettings _settings;

        public RulePackageLoader()
        {
            _settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                DateFormatString = "yyyy-MM-dd",
                MissingMemberHandling = MissingMemberHandling.Error
            };
            _settings.Converters.Add(new StringEnumConverter(new CamelCaseNamingStrategy()));
        }

        public RulePackage LoadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("规则包路径不能为空。", nameof(path));
            return LoadJson(File.ReadAllText(path));
        }

        public RulePackage LoadJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("规则包 JSON 不能为空。", nameof(json));
            var package = JsonConvert.DeserializeObject<RulePackage>(json, _settings);
            if (package == null) throw new RulePackageException("规则包内容为空。");
            var errors = Validate(package);
            if (errors.Count > 0) throw new RulePackageException(string.Join(Environment.NewLine, errors));
            return package;
        }

        public IReadOnlyList<string> Validate(RulePackage package)
        {
            var errors = new List<string>();
            if (package == null) { errors.Add("规则包不能为空。"); return errors; }
            if (package.SchemaVersion != 1) errors.Add("仅支持 schemaVersion=1。");
            if (string.IsNullOrWhiteSpace(package.PackageId)) errors.Add("packageId 不能为空。");
            if (string.IsNullOrWhiteSpace(package.Version)) errors.Add("version 不能为空。");
            if (string.IsNullOrWhiteSpace(package.JurisdictionCode)) errors.Add("jurisdictionCode 不能为空。");

            var duplicateIds = package.Rules
                .Where(rule => rule != null && !string.IsNullOrWhiteSpace(rule.RuleId))
                .GroupBy(rule => rule.RuleId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key);
            foreach (var duplicateId in duplicateIds) errors.Add("规则编号重复：" + duplicateId);

            foreach (var rule in package.Rules.Where(rule => rule != null))
            {
                if (string.IsNullOrWhiteSpace(rule.RuleId)) errors.Add("存在缺少 ruleId 的规则。");
                if (string.IsNullOrWhiteSpace(rule.Title)) errors.Add(rule.RuleId + " 缺少标题。");
                if (!AllowedCheckTypes.Contains(rule.CheckType ?? string.Empty)) errors.Add(rule.RuleId + " 使用了不允许的 checkType：" + rule.CheckType);
                if (string.IsNullOrWhiteSpace(rule.Message)) errors.Add(rule.RuleId + " 缺少问题提示。");
                if (rule.References.Any(reference => string.IsNullOrWhiteSpace(reference.StandardCode)))
                    errors.Add(rule.RuleId + " 存在缺少标准编号的引用。");
            }
            return errors;
        }
    }

    public sealed class RulePackageException : Exception
    {
        public RulePackageException(string message) : base(message) { }
    }
}
