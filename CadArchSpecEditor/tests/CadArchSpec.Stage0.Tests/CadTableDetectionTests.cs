using System.Collections.Generic;
using System.Linq;
using CadArchSpec.CadTable;
using Xunit;

namespace CadArchSpec.Stage0.Tests
{
    public sealed class CadTableDetectionTests
    {
        [Fact]
        public void DetectsRegularGridAndAssignsTextByReadingOrder()
        {
            var input = Grid(new[] { 0d, 100d, 200d }, new[] { 0d, 50d, 100d });
            input.TextFragments.Add(Text("表头", 50, 75, CadTextSourceKind.Standard));
            input.TextFragments.Add(Text("天正内容", 150, 25, CadTextSourceKind.TianzhengProperty));

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Equal(4, result.Cells.Count);
            Assert.Equal("表头", result.Cells.Single(cell => cell.RowIndex == 0 && cell.ColumnIndex == 0).Text);
            var tianzheng = result.Cells.Single(cell => cell.RowIndex == 1 && cell.ColumnIndex == 1).TextFragments.Single();
            Assert.Equal(CadTextSourceKind.TianzhengProperty, tianzheng.SourceKind);
            Assert.Empty(result.UnassignedText);
        }

        [Fact]
        public void JoinsSmallBorderGapsWithoutChangingTheInput()
        {
            var input = new CadTableDetectionInput();
            input.Segments.AddRange(new[]
            {
                Segment(0, 0, 49, 0), Segment(51, 0, 100, 0),
                Segment(0, 50, 100, 50), Segment(0, 0, 0, 50), Segment(100, 0, 100, 50)
            });

            var result = new OrthogonalCadTableDetector().Detect(input, new CadTableDetectionOptions { MaximumBorderGap = 2.1 });

            Assert.Single(result.Cells);
            Assert.Equal(5, input.Segments.Count);
        }

        [Fact]
        public void KeepsAmbiguousBoundaryTextForManualReview()
        {
            var input = Grid(new[] { 0d, 100d, 200d }, new[] { 0d, 50d });
            input.TextFragments.Add(Text("跨格文字", 100, 25, CadTextSourceKind.ExplodedClone));

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Single(result.UnassignedText);
            Assert.Contains(result.Warnings, value => value.Contains("人工确认"));
            Assert.Equal(CadTextSourceKind.ExplodedClone, result.UnassignedText[0].SourceKind);
        }

        [Fact]
        public void InfersHorizontalMergedCellFromMissingInternalDivider()
        {
            var input = new CadTableDetectionInput();
            input.Segments.AddRange(new[]
            {
                Segment(0, 0, 200, 0), Segment(0, 50, 200, 50), Segment(0, 100, 200, 100),
                Segment(0, 0, 0, 100), Segment(100, 0, 100, 50), Segment(200, 0, 200, 100)
            });
            input.TextFragments.Add(Text("合并表头", 100, 75, CadTextSourceKind.Standard));

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Equal(3, result.Cells.Count);
            var merged = result.Cells.Single(cell => cell.RowIndex == 0 && cell.ColumnIndex == 0);
            Assert.Equal(2, merged.ColumnSpan);
            Assert.Equal(1, merged.RowSpan);
            Assert.Equal("合并表头", merged.Text);
            Assert.Contains(result.Warnings, warning => warning.Contains("合并单元格"));
        }

        [Fact]
        public void InfersVerticalMergedCellFromMissingInternalDivider()
        {
            var input = new CadTableDetectionInput();
            input.Segments.AddRange(new[]
            {
                Segment(0, 0, 200, 0), Segment(100, 50, 200, 50), Segment(0, 100, 200, 100),
                Segment(0, 0, 0, 100), Segment(100, 0, 100, 100), Segment(200, 0, 200, 100)
            });

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Equal(3, result.Cells.Count);
            var merged = result.Cells.Single(cell => cell.RowIndex == 0 && cell.ColumnIndex == 0);
            Assert.Equal(1, merged.ColumnSpan);
            Assert.Equal(2, merged.RowSpan);
        }

        private static CadTableDetectionInput Grid(IEnumerable<double> xs, IEnumerable<double> ys)
        {
            var x = xs.ToArray();
            var y = ys.ToArray();
            var input = new CadTableDetectionInput();
            foreach (var value in x) input.Segments.Add(Segment(value, y.First(), value, y.Last()));
            foreach (var value in y) input.Segments.Add(Segment(x.First(), value, x.Last(), value));
            return input;
        }

        private static CadTableSegment Segment(double x1, double y1, double x2, double y2)
        {
            return new CadTableSegment { Start = new CadTablePoint(x1, y1), End = new CadTablePoint(x2, y2) };
        }

        private static CadTextFragment Text(string text, double x, double y, CadTextSourceKind source)
        {
            return new CadTextFragment { Text = text, PlainText = text, Center = new CadTablePoint(x, y), SourceKind = source, SourceHandle = "A1" };
        }
    }
}
