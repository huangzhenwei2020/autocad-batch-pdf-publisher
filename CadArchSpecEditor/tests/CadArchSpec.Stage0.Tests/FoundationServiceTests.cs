using System;
using CadArchSpec.Application;
using CadArchSpec.Domain.Common;
using CadArchSpec.Domain.Standards;
using CadArchSpec.LayoutEngine;
using CadArchSpec.StandardRegistry;
using Xunit;

namespace CadArchSpec.Stage0.Tests
{
    public sealed class FoundationServiceTests
    {
        [Fact]
        public void FactoryCreatesStructuredOfficeDocument()
        {
            var factory = new ArchitectureProjectFactory();
            var project = factory.Create("测试办公项目", ArchitectureBuildingType.Office);
            project.Classification.HasCurtainWall = true;
            project.Classification.HasElevator = true;
            var document = factory.CreateDocument(project);

            Assert.Equal(14, document.Sections.Count);
            Assert.Contains(document.Sections, section => section.SectionType == "FireSafety");
            Assert.Equal(RequirementState.Required, document.Sections.Find(section => section.SectionType == "Elevators").RequirementState);
        }

        [Fact]
        public void LayoutUsesPaperMillimetersAndExplicitScale()
        {
            var service = new PaperLayoutService();
            var layout = service.CreateDefault("A1", true);

            Assert.Equal(841m, layout.PaperWidthMillimeters);
            Assert.Equal(594m, layout.PaperHeightMillimeters);
            Assert.Equal(350m, service.PaperMillimetersToCadUnits(3.5m, 100m, 1m));
        }

        [Fact]
        public void LayoutSupportsExtendedAndA4Frames()
        {
            var service = new PaperLayoutService();
            var extended = service.CreateDefault("A1+1/4", true);
            var a4 = service.CreateDefault("A4", false);
            Assert.Equal(1051m, extended.PaperWidthMillimeters);
            Assert.Equal(594m, extended.PaperHeightMillimeters);
            Assert.Equal(210m, a4.PaperWidthMillimeters);
            Assert.Equal(297m, a4.PaperHeightMillimeters);
        }

        [Fact]
        public void RegistryDoesNotTreatUnknownStandardAsApplicable()
        {
            var registry = new InMemoryStandardRegistry();
            registry.Replace(new[]
            {
                new StandardReference
                {
                    StandardId = "unknown",
                    Code = "SAMPLE",
                    Name = "未核实标准",
                    JurisdictionCode = "CN",
                    Status = StandardStatus.Unknown
                }
            });

            Assert.Empty(registry.ApplicableOn(new DateTime(2026, 7, 28), "CN"));
        }
    }
}
