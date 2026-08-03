using System;
using System.IO;
using System.Linq;
using CadArchSpec.Domain.Common;
using CadArchSpec.Domain.Documents;
using CadArchSpec.Domain.Projects;
using CadArchSpec.Domain.Tables;
using CadArchSpec.EditorBridge;
using Xunit;

namespace CadArchSpec.Stage0.Tests
{
    public sealed class JsonRoundTripTests
    {
        private readonly JsonModelSerializer _serializer = new JsonModelSerializer();

        [Fact]
        public void ProjectSampleRoundTripsWithoutLosingIdentityOrFieldState()
        {
            var project = Read<ArchitectureProject>("samples", "projects", "sample-office-project.json");
            var json = _serializer.Serialize(project);
            var restored = _serializer.Deserialize<ArchitectureProject>(json);

            Assert.Equal(project.ProjectId, restored.ProjectId);
            Assert.Equal(project.ProjectName, restored.ProjectName);
            Assert.Equal(project.Building.BuildingHeightMeters.State, restored.Building.BuildingHeightMeters.State);
            Assert.Equal(project.BuildingUnits.Count, restored.BuildingUnits.Count);
            Assert.Equal(project.Standards.Count, restored.Standards.Count);
        }

        [Fact]
        public void DocumentSampleRoundTripsWithStableNodeAndTableIds()
        {
            var document = Read<ArchitectureDesignSpecDocument>("samples", "documents", "sample-architecture-design-spec.json");
            var restored = _serializer.Deserialize<ArchitectureDesignSpecDocument>(_serializer.Serialize(document));

            Assert.Equal(14, restored.Sections.Count);
            Assert.Equal(document.Sections[0].SectionId, restored.Sections[0].SectionId);
            Assert.Equal(document.Sections[0].Content[0].NodeId, restored.Sections[0].Content[0].NodeId);
            Assert.Equal(4, restored.Tables.Count);
            Assert.Equal(document.Tables[0].TableId, restored.Tables[0].TableId);
        }

        [Theory]
        [InlineData("technical-economic-indicators.json", ProfessionalTableType.TechnicalEconomicIndicators)]
        [InlineData("waterproof-design.json", ProfessionalTableType.WaterproofDesign)]
        [InlineData("building-safety-measures.json", ProfessionalTableType.BuildingSafetyMeasures)]
        [InlineData("fire-compartment-summary.json", ProfessionalTableType.FireCompartmentSummary)]
        public void ProfessionalTableSamplesRoundTrip(string fileName, ProfessionalTableType expectedType)
        {
            var table = Read<ArchitectureTable>("samples", "tables", fileName);
            var restored = _serializer.Deserialize<ArchitectureTable>(_serializer.Serialize(table));

            Assert.Equal(expectedType, restored.TableType);
            Assert.NotEmpty(restored.Columns);
            Assert.NotEmpty(restored.Rows);
            Assert.All(restored.Rows, row => Assert.NotEqual(Guid.Empty, row.RowId));
        }

        private T Read<T>(params string[] segments)
        {
            var path = Path.Combine(new[] { AppContext.BaseDirectory }.Concat(segments).ToArray());
            return _serializer.Deserialize<T>(File.ReadAllText(path));
        }
    }
}
