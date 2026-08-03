using System;
using System.Collections.Generic;
using System.Linq;
using CadArchSpec.Domain.Common;
using CadArchSpec.Domain.Review;
using CadArchSpec.Domain.Rules;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.RuleEngine
{
    public sealed class RuleReviewResult
    {
        public string PackageId { get; set; } = string.Empty;
        public string PackageVersion { get; set; } = string.Empty;
        public string PackageDisplayName { get; set; } = string.Empty;
        public string PackageStatus { get; set; } = string.Empty;
        public string PackageVerifiedAt { get; set; } = string.Empty;
        public string ExecutedAt { get; set; } = string.Empty;
        public bool LocalRulesLoaded { get; set; }
        public string ScopeNotice { get; set; } = string.Empty;
        public List<ReviewIssue> Issues { get; set; } = new List<ReviewIssue>();
    }

    public sealed class WorkspaceRuleEvaluator
    {
        public RuleReviewResult Evaluate(RulePackage package, JObject workspace)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            if (!string.Equals(package.Status, "Active", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(package.Status, "Draft", StringComparison.OrdinalIgnoreCase))
            {
                throw new RulePackageException("当前规则包状态不允许执行。");
            }

            var submissionDate = ParseDate((string)workspace["submissionDate"]) ?? DateTime.Today;
            var buildingType = MapBuildingType((string)workspace["buildingType"]);
            var issues = package.Rules
                .Where(rule => IsApplicable(rule, submissionDate, buildingType))
                .Select(rule => EvaluateRule(rule, workspace))
                .Where(issue => issue != null)
                .ToList();

            return new RuleReviewResult
            {
                PackageId = package.PackageId,
                PackageVersion = package.Version,
                PackageDisplayName = package.DisplayName,
                PackageStatus = package.Status,
                PackageVerifiedAt = package.VerifiedAt?.ToString("O") ?? string.Empty,
                ExecutedAt = DateTimeOffset.Now.ToString("O"),
                LocalRulesLoaded = false,
                ScopeNotice = string.Equals(package.Status, "Draft", StringComparison.OrdinalIgnoreCase)
                    ? "当前执行的是待建筑专业人员最终复核的国家基础试运行规则包，且未加载项目所在地地方规则；结果不能替代设计、校审或施工图审查。"
                    : "当前仅执行国家基础完整性规则，未加载项目所在地地方规则；结果不能替代设计、校审或施工图审查。",
                Issues = issues
            };
        }

        private static ReviewIssue EvaluateRule(ReviewRule rule, JObject workspace)
        {
            if (string.Equals(rule.CheckType, "requiredSection", StringComparison.OrdinalIgnoreCase))
            {
                var sections = workspace["sections"] as JArray;
                var section = sections?
                    .OfType<JObject>()
                    .FirstOrDefault(item =>
                        string.Equals((string)item["id"], rule.Target.SectionType, StringComparison.OrdinalIgnoreCase));
                var content = section?["content"]?["content"] as JArray;
                var missing = section == null ||
                    (bool?)section["enabled"] == false ||
                    content == null ||
                    content.Count < 2;
                return missing ? CreateIssue(rule, rule.Target.SectionType, string.Empty, "章节缺失、未启用或没有形成有效正文。") : null;
            }

            if (string.Equals(rule.CheckType, "requiredField", StringComparison.OrdinalIgnoreCase))
            {
                var fields = workspace["fields"] as JArray;
                var field = fields?
                    .OfType<JObject>()
                    .FirstOrDefault(item =>
                        string.Equals((string)item["path"], rule.Target.FieldPath, StringComparison.OrdinalIgnoreCase));
                var allowedStates = (rule.Parameters.TryGetValue("allowedStates", out var states) ? states : "confirmed,overridden")
                    .Split(',')
                    .Select(value => value.Trim())
                    .Where(value => value.Length > 0)
                    .ToList();
                var missing = field == null ||
                    string.IsNullOrWhiteSpace((string)field["value"]) ||
                    !allowedStates.Contains((string)field["state"] ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                return missing ? CreateIssue(rule, string.Empty, rule.Target.FieldPath, "字段为空或尚未处于允许的确认状态。") : null;
            }

            return null;
        }

        private static ReviewIssue CreateIssue(
            ReviewRule rule,
            string targetNodeId,
            string targetFieldPath,
            string evidence)
        {
            var reference = rule.References.FirstOrDefault();
            return new ReviewIssue
            {
                IssueId = Guid.NewGuid(),
                RuleId = rule.RuleId,
                Severity = rule.Severity,
                Title = rule.Title,
                Message = rule.Message,
                StandardCode = reference?.StandardCode ?? string.Empty,
                ClauseReference = reference?.Clause ?? string.Empty,
                TargetNodeId = targetNodeId ?? string.Empty,
                TargetFieldPath = targetFieldPath ?? string.Empty,
                Evidence = evidence,
                SuggestedAction = "请由建筑专业设计人员核实并补充相应内容。",
                RequiresProfessionalConfirmation = rule.RequiresProfessionalConfirmation
            };
        }

        private static bool IsApplicable(
            ReviewRule rule,
            DateTime submissionDate,
            ArchitectureBuildingType buildingType)
        {
            if (rule.EffectiveFrom.HasValue && submissionDate.Date < rule.EffectiveFrom.Value.Date) return false;
            if (rule.EffectiveTo.HasValue && submissionDate.Date > rule.EffectiveTo.Value.Date) return false;
            return rule.BuildingTypes.Count == 0 ||
                rule.BuildingTypes.Contains(ArchitectureBuildingType.Common) ||
                rule.BuildingTypes.Contains(buildingType);
        }

        private static DateTime? ParseDate(string value)
        {
            return DateTime.TryParse(value, out var result) ? result : (DateTime?)null;
        }

        private static ArchitectureBuildingType MapBuildingType(string value)
        {
            switch (value)
            {
                case "住宅建筑": return ArchitectureBuildingType.Residential;
                case "办公建筑": return ArchitectureBuildingType.Office;
                case "交通建筑": return ArchitectureBuildingType.Transportation;
                case "教育建筑": return ArchitectureBuildingType.Education;
                case "商业建筑": return ArchitectureBuildingType.Commercial;
                case "文体建筑": return ArchitectureBuildingType.CultureAndSports;
                case "医疗建筑": return ArchitectureBuildingType.Medical;
                case "工业建筑": return ArchitectureBuildingType.Industrial;
                case "其他建筑": return ArchitectureBuildingType.Other;
                default: return ArchitectureBuildingType.Common;
            }
        }
    }
}
