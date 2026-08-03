using System;
using CadArchSpec.Application;
using CadArchSpec.Domain.Projects;
using CadArchSpec.EditorBridge;
using CadArchSpec.LayoutEngine;
using CadArchSpec.RuleEngine;
using CadArchSpec.StandardRegistry;

namespace CadArchSpec.Net48Compatibility
{
    public static class CompatibilityProbe
    {
        public static Type[] SharedTypes
        {
            get
            {
                return new[]
                {
                    typeof(ArchitectureProject),
                    typeof(ArchitectureProjectFactory),
                    typeof(FormulaEvaluator),
                    typeof(PaperLayoutService),
                    typeof(InMemoryStandardRegistry),
                    typeof(JsonModelSerializer)
                };
            }
        }
    }
}
