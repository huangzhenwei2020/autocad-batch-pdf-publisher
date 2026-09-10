using CadArchSpec.CadTable;
using Xunit;

namespace CadArchSpec.Stage0.Tests
{
    public sealed class SpreadsheetFormulaEngineTests
    {
        [Fact]
        public void RecalculatesReferencesAndCommonFunctions()
        {
            var cells = new[,]
            {
                { "10", "20", "=SUM(A1:B1)" },
                { "=A1", "=AVERAGE(A1:B1)", "=ROUND(C1/7,2)" }
            };

            var result = SpreadsheetFormulaEngine.Calculate(cells);

            Assert.Equal("30", result[0, 2]);
            Assert.Equal("10", result[1, 0]);
            Assert.Equal("15", result[1, 1]);
            Assert.Equal("4.29", result[1, 2]);
        }

        [Fact]
        public void LinkedTextCellTracksItsSource()
        {
            var first = SpreadsheetFormulaEngine.Calculate(new[,] { { "原名称", "=A1" } });
            var second = SpreadsheetFormulaEngine.Calculate(new[,] { { "新名称", "=A1" } });

            Assert.Equal("原名称", first[0, 1]);
            Assert.Equal("新名称", second[0, 1]);
        }

        [Fact]
        public void ReportsCircularReferencesAndDivisionByZero()
        {
            var result = SpreadsheetFormulaEngine.Calculate(new[,]
            {
                { "=B1", "=A1" },
                { "=1/0", "=UNKNOWN(1)" }
            });

            Assert.Equal("#CYCLE!", result[0, 0]);
            Assert.Equal("#CYCLE!", result[0, 1]);
            Assert.Equal("#DIV/0!", result[1, 0]);
            Assert.Equal("#NAME?", result[1, 1]);
        }

        [Fact]
        public void CreatesExcelStyleAddressesBeyondColumnZ()
        {
            Assert.Equal("A1", SpreadsheetFormulaEngine.CellAddress(0, 0));
            Assert.Equal("AA3", SpreadsheetFormulaEngine.CellAddress(2, 26));
        }

        [Fact]
        public void AcceptsAbsoluteCellReferences()
        {
            var result = SpreadsheetFormulaEngine.Calculate(new[,] { { "12", "=$A$1" } });

            Assert.Equal("12", result[0, 1]);
        }
    }
}
