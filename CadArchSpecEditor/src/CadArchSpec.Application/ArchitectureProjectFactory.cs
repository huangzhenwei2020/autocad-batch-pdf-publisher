using System;
using System.Collections.Generic;
using CadArchSpec.Domain.Common;
using CadArchSpec.Domain.Documents;
using CadArchSpec.Domain.Projects;

namespace CadArchSpec.Application
{
    public sealed class ArchitectureProjectFactory
    {
        public ArchitectureProject Create(string projectName, ArchitectureBuildingType buildingType)
        {
            if (string.IsNullOrWhiteSpace(projectName)) throw new ArgumentException("项目名称不能为空。", nameof(projectName));
            return new ArchitectureProject
            {
                ProjectId = Guid.NewGuid(),
                ProjectName = projectName.Trim(),
                Classification = new ProjectClassification { BuildingType = buildingType }
            };
        }

        public ArchitectureDesignSpecDocument CreateDocument(ArchitectureProject project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var document = new ArchitectureDesignSpecDocument
            {
                DocumentId = Guid.NewGuid(),
                ProjectId = project.ProjectId,
                Name = project.ProjectName + "建筑设计说明"
            };

            foreach (var section in DefaultSections(project.Classification))
                document.Sections.Add(section);
            return document;
        }

        private static IEnumerable<ArchitectureSection> DefaultSections(ProjectClassification classification)
        {
            yield return Section("DesignBasis", "设计依据", RequirementState.Required);
            yield return Section("ProjectOverview", "项目概况", RequirementState.Required);
            yield return Section("TechnicalIndicators", "主要技术经济指标", RequirementState.Required);
            yield return Section("Elevation", "设计标高", RequirementState.Required);
            yield return Section("GeneralLayout", "总平面建筑说明", RequirementState.Required);
            yield return Section("MaterialsAndFinishes", "建筑用料和装修构造", RequirementState.Required);
            yield return Section("DoorsWindowsCurtainWall", "门窗与幕墙", classification.HasCurtainWall ? RequirementState.Required : RequirementState.Conditional);
            yield return Section("Waterproof", "防水设计", RequirementState.Required);
            yield return Section("Elevators", "电梯和自动扶梯", classification.HasElevator ? RequirementState.Required : RequirementState.Conditional);
            yield return Section("Accessibility", "无障碍设计", RequirementState.Required);
            yield return Section("Safety", "建筑安全设计", RequirementState.Required);
            yield return Section("FireSafety", "建筑防火设计", RequirementState.Required);
            yield return Section("EnergyAndGreen", "建筑节能与绿色建筑", RequirementState.Required);
            yield return Section("SpecialistInterfaces", "专项深化设计责任边界", classification.HasSpecialistDesign ? RequirementState.Required : RequirementState.Conditional);
        }

        private static ArchitectureSection Section(string type, string title, RequirementState requirement)
        {
            return new ArchitectureSection
            {
                SectionId = Guid.NewGuid(),
                SectionType = type,
                Title = title,
                RequirementState = requirement,
                ReviewState = ReviewState.NotReviewed
            };
        }
    }
}
