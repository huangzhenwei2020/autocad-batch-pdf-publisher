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
            for (var index = 0; index < input.Segments.Count; index++) input.Segments[index].SourceHandle = "L" + index;
            input.TextFragments.Add(Text("表头", 50, 75, CadTextSourceKind.Standard));
            input.TextFragments.Add(Text("天正内容", 150, 25, CadTextSourceKind.TianzhengProperty));

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Equal(4, result.Cells.Count);
            Assert.Equal("表头", result.Cells.Single(cell => cell.RowIndex == 0 && cell.ColumnIndex == 0).Text);
            var tianzheng = result.Cells.Single(cell => cell.RowIndex == 1 && cell.ColumnIndex == 1).TextFragments.Single();
            Assert.Equal(CadTextSourceKind.TianzhengProperty, tianzheng.SourceKind);
            var firstCell = result.Cells.Single(cell => cell.RowIndex == 0 && cell.ColumnIndex == 0);
            Assert.Contains("A1", firstCell.SourceHandles);
            Assert.Contains(firstCell.SourceHandles, handle => handle.StartsWith("L"));
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
        public void IgnoresOpenDecorationLinesOutsideTheClosedTable()
        {
            var input = Grid(new[] { 0d, 100d }, new[] { 0d, 50d });
            input.Segments.Add(Segment(220, 80, 300, 80));
            input.Segments.Add(Segment(220, 80, 220, 120));

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Single(result.Cells);
            Assert.Equal(new[] { 0d, 100d }, result.ColumnBoundaries);
            Assert.Equal(new[] { 50d, 0d }, result.RowBoundaries);
            Assert.Contains(result.Warnings, warning => warning.Contains("装饰线"));
        }

        [Fact]
        public void IgnoresShortDecorationLineInsideACellWithoutCreatingRows()
        {
            var input = Grid(new[] { 0d, 100d }, new[] { 0d, 50d });
            input.Segments.Add(Segment(20, 25, 80, 25));

            var result = new OrthogonalCadTableDetector().Detect(input);

            var cell = Assert.Single(result.Cells);
            Assert.Equal(1, cell.RowSpan);
            Assert.Equal(new[] { 50d, 0d }, result.RowBoundaries);
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
        public void AssignsBoundaryTextToCellWithMajorityBoundingBoxOverlap()
        {
            var input = Grid(new[] { 0d, 100d, 200d }, new[] { 0d, 50d });
            var fragment = Text("靠左单元格", 100, 25, CadTextSourceKind.Standard, 5);
            SetBounds(fragment, 70, 20, 110, 30);
            input.TextFragments.Add(fragment);

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Equal("靠左单元格", result.Cells.Single(cell => cell.ColumnIndex == 0).Text);
            Assert.Empty(result.UnassignedText);
        }

        [Fact]
        public void KeepsEvenlySpanningBoundaryTextForManualReview()
        {
            var input = Grid(new[] { 0d, 100d, 200d }, new[] { 0d, 50d });
            var fragment = Text("跨格文字", 100, 25, CadTextSourceKind.Standard, 5);
            SetBounds(fragment, 90, 20, 110, 30);
            input.TextFragments.Add(fragment);

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Single(result.UnassignedText);
        }

        [Fact]
        public void RemovesOnlySpatiallyOverlappingDuplicateText()
        {
            var input = Grid(new[] { 0d, 100d, 200d }, new[] { 0d, 50d });
            var direct = Text("相同做法", 50, 25, CadTextSourceKind.TianzhengProperty, 5);
            direct.SourceHandle = "T1";
            SetBounds(direct, 30, 20, 70, 30);
            var exploded = Text("相同做法", 50.2, 25.1, CadTextSourceKind.ExplodedClone, 5);
            exploded.SourceHandle = "T1";
            SetBounds(exploded, 30.2, 20.1, 70.2, 30.1);
            var otherCell = Text("相同做法", 150, 25, CadTextSourceKind.Standard, 5);
            otherCell.SourceHandle = "T2";
            SetBounds(otherCell, 130, 20, 170, 30);
            input.TextFragments.AddRange(new[] { exploded, direct, otherCell });

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Single(result.Cells.Single(cell => cell.ColumnIndex == 0).TextFragments);
            Assert.Equal(CadTextSourceKind.TianzhengProperty,
                result.Cells.Single(cell => cell.ColumnIndex == 0).TextFragments[0].SourceKind);
            Assert.Single(result.Cells.Single(cell => cell.ColumnIndex == 1).TextFragments);
            Assert.Contains(result.Warnings, warning => warning.Contains("1 段") && warning.Contains("重复文字"));
        }

        [Fact]
        public void KeepsDifferentTextAtTheSamePosition()
        {
            var input = Grid(new[] { 0d, 100d }, new[] { 0d, 50d });
            input.TextFragments.Add(Text("第一段", 50, 30, CadTextSourceKind.Standard, 5));
            input.TextFragments.Add(Text("第二段", 50, 20, CadTextSourceKind.ExplodedClone, 5));

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Equal(2, result.Cells.Single().TextFragments.Count);
            Assert.Equal("第一段" + System.Environment.NewLine + "第二段", result.Cells.Single().Text);
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

        [Fact]
        public void DetectsTableAfterOverallRotationWithoutMutatingSourceCoordinates()
        {
            var input = Grid(new[] { 0d, 100d, 200d }, new[] { 0d, 50d, 100d });
            input.TextFragments.Add(Text("旋转表格", 50, 75, CadTextSourceKind.ExplodedClone));
            var rotated = Rotate(input, 30d);
            var originalX = rotated.Segments[0].Start.X;
            var originalY = rotated.Segments[0].Start.Y;

            var result = new OrthogonalCadTableDetector().Detect(rotated);

            Assert.Equal(4, result.Cells.Count);
            Assert.Equal(30d, result.DetectedRotationDegrees, 6);
            Assert.Equal("旋转表格", result.Cells.Single(cell => cell.RowIndex == 0 && cell.ColumnIndex == 0).Text);
            Assert.Equal(originalX, rotated.Segments[0].Start.X);
            Assert.Equal(originalY, rotated.Segments[0].Start.Y);
        }

        [Fact]
        public void PreservesMTextParagraphsWhileRemovingFormattingCommands()
        {
            var contents = "{\\fSimSun|b0|i0;5厚1:2水泥砂浆抹面压光\\P15厚聚合物水泥防水砂浆\\C1;\\P防水混凝土厚度250，抗渗等级P8}";

            var text = MTextContentNormalizer.Normalize(contents);

            Assert.Equal("5厚1:2水泥砂浆抹面压光" + System.Environment.NewLine +
                "15厚聚合物水泥防水砂浆" + System.Environment.NewLine +
                "防水混凝土厚度250，抗渗等级P8", text);
        }

        [Fact]
        public void RemovesMTextParagraphFormattingWithoutCreatingFakeText()
        {
            var text = MTextContentNormalizer.Normalize("{\\pxqc;第一行\\P第二行}");

            Assert.Equal("第一行" + System.Environment.NewLine + "第二行", text);
        }

        [Fact]
        public void JoinsFragmentsOnTheSameVisualLineBeforeStartingTheNextLine()
        {
            var input = Grid(new[] { 0d, 100d }, new[] { 0d, 100d });
            input.TextFragments.Add(Text("5厚", 20, 75, CadTextSourceKind.ExplodedClone, 5));
            input.TextFragments.Add(Text("1:2水泥砂浆", 45, 74.5, CadTextSourceKind.ExplodedClone, 5));
            input.TextFragments.Add(Text("15厚防水砂浆", 30, 55, CadTextSourceKind.ExplodedClone, 5));

            var result = new OrthogonalCadTableDetector().Detect(input);

            Assert.Equal("5厚1:2水泥砂浆" + System.Environment.NewLine + "15厚防水砂浆",
                result.Cells.Single().Text);
        }

        [Fact]
        public void PreservesCadColumnWidthRatiosForEditorDisplay()
        {
            var widths = CadTableColumnWidthNormalizer.Normalize(new[] { 20d, 60d, 40d });

            Assert.Equal(new[] { 40d, 120d, 80d }, widths);
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

        private static CadTextFragment Text(string text, double x, double y, CadTextSourceKind source, double height = 0d)
        {
            return new CadTextFragment { Text = text, PlainText = text, Center = new CadTablePoint(x, y), Height = height, SourceKind = source, SourceHandle = "A1" };
        }

        private static void SetBounds(CadTextFragment fragment, double left, double bottom, double right, double top)
        {
            fragment.HasBounds = true;
            fragment.Left = left;
            fragment.Bottom = bottom;
            fragment.Right = right;
            fragment.Top = top;
            fragment.Width = right - left;
            fragment.Height = top - bottom;
        }

        private static CadTableDetectionInput Rotate(CadTableDetectionInput input, double degrees)
        {
            var radians = degrees * System.Math.PI / 180d;
            var cosine = System.Math.Cos(radians);
            var sine = System.Math.Sin(radians);
            var result = new CadTableDetectionInput();
            foreach (var segment in input.Segments)
                result.Segments.Add(new CadTableSegment
                {
                    Start = Rotate(segment.Start, cosine, sine),
                    End = Rotate(segment.End, cosine, sine)
                });
            foreach (var text in input.TextFragments)
                result.TextFragments.Add(new CadTextFragment
                {
                    Text = text.Text,
                    PlainText = text.PlainText,
                    Center = Rotate(text.Center, cosine, sine),
                    HasBounds = text.HasBounds,
                    Left = text.Left,
                    Bottom = text.Bottom,
                    Right = text.Right,
                    Top = text.Top,
                    Width = text.Width,
                    Height = text.Height,
                    SourceKind = text.SourceKind,
                    SourceHandle = text.SourceHandle
                });
            return result;
        }

        private static CadTablePoint Rotate(CadTablePoint point, double cosine, double sine)
        {
            return new CadTablePoint(point.X * cosine - point.Y * sine, point.X * sine + point.Y * cosine);
        }
    }
}
