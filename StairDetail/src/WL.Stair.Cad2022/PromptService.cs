using Autodesk.AutoCAD.EditorInput;
using WL.Stair.Core.Domain;

namespace WL.Stair.Cad2022
{
    internal sealed class PromptService
    {
        private readonly Editor _editor;

        public PromptService(Editor editor)
        {
            _editor = editor;
        }

        public bool TryGetDefinition(out StairDefinition definition)
        {
            definition = null;

            double floorHeight;
            double flightWidth;
            double stairwellWidth;
            double landingDepth;
            double treadDepth;
            double flightSlabThickness;
            double landingSlabThickness;
            double floorSlabThickness;
            int totalRisers;

            if (!TryGetPositiveDouble("\n结构层高 <3000>: ", 3000.0, out floorHeight)
                || !TryGetPositiveDouble("\n梯段宽度 <1100>: ", 1100.0, out flightWidth)
                || !TryGetNonNegativeDouble("\n梯井宽度 <200>: ", 200.0, out stairwellWidth)
                || !TryGetPositiveDouble("\n平台深度 <1200>: ", 1200.0, out landingDepth)
                || !TryGetPositiveDouble("\n踏步宽度 <280>: ", 280.0, out treadDepth)
                || !TryGetPositiveDouble("\n梯板厚度 <120>: ", 120.0, out flightSlabThickness)
                || !TryGetPositiveDouble("\n平台板厚度 <120>: ", 120.0, out landingSlabThickness)
                || !TryGetPositiveDouble("\n楼板厚度 <120>: ", 120.0, out floorSlabThickness)
                || !TryGetNonNegativeInteger("\n总踢面数，输入 0 自动计算 <0>: ", 0, out totalRisers))
            {
                return false;
            }

            definition = new StairDefinition(
                floorHeight,
                flightWidth,
                stairwellWidth,
                landingDepth,
                treadDepth)
            {
                FlightSlabThickness = flightSlabThickness,
                LandingSlabThickness = landingSlabThickness,
                FloorSlabThickness = floorSlabThickness
            };

            if (totalRisers > 0)
            {
                definition.TotalRiserCount = totalRisers;
            }

            return true;
        }

        private bool TryGetPositiveDouble(string message, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions(message)
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };

            var result = _editor.GetDouble(options);
            value = result.Status == PromptStatus.None ? defaultValue : result.Value;
            return result.Status == PromptStatus.OK || result.Status == PromptStatus.None;
        }

        private bool TryGetNonNegativeDouble(string message, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions(message)
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };

            var result = _editor.GetDouble(options);
            value = result.Status == PromptStatus.None ? defaultValue : result.Value;
            return result.Status == PromptStatus.OK || result.Status == PromptStatus.None;
        }

        private bool TryGetNonNegativeInteger(string message, int defaultValue, out int value)
        {
            var options = new PromptIntegerOptions(message)
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };

            var result = _editor.GetInteger(options);
            value = result.Status == PromptStatus.None ? defaultValue : result.Value;
            return result.Status == PromptStatus.OK || result.Status == PromptStatus.None;
        }
    }
}
