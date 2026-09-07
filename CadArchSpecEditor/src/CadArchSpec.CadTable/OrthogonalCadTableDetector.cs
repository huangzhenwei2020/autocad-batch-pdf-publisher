using System;
using System.Collections.Generic;
using System.Linq;

namespace CadArchSpec.CadTable
{
    public sealed class OrthogonalCadTableDetector
    {
        private sealed class AxisSegment
        {
            public double Fixed { get; set; }
            public double Start { get; set; }
            public double End { get; set; }
            public List<string> SourceHandles { get; set; } = new List<string>();
        }

        public CadTableDetectionResult Detect(CadTableDetectionInput input, CadTableDetectionOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            options = options ?? new CadTableDetectionOptions();
            if (options.CoordinateTolerance <= 0) throw new ArgumentOutOfRangeException(nameof(options.CoordinateTolerance));
            if (options.MaximumBorderGap < 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumBorderGap));

            var rotation = options.DetectOverallRotation ? DetectOverallRotation(input.Segments) : 0d;
            var normalizedInput = Math.Abs(rotation) > options.OrthogonalAngleToleranceDegrees
                ? RotateInput(input, -rotation)
                : input;

            var horizontal = new List<AxisSegment>();
            var vertical = new List<AxisSegment>();
            foreach (var segment in normalizedInput.Segments ?? new List<CadTableSegment>())
            {
                AxisSegment normalized;
                if (TryNormalize(segment, options.OrthogonalAngleToleranceDegrees, out normalized, true)) horizontal.Add(normalized);
                else if (TryNormalize(segment, options.OrthogonalAngleToleranceDegrees, out normalized, false)) vertical.Add(normalized);
            }

            horizontal = Merge(horizontal, options.CoordinateTolerance, options.MaximumBorderGap);
            vertical = Merge(vertical, options.CoordinateTolerance, options.MaximumBorderGap);
            var result = new CadTableDetectionResult
            {
                DetectedRotationDegrees = rotation,
                ColumnBoundaries = Cluster(vertical.Select(item => item.Fixed), options.CoordinateTolerance),
                RowBoundaries = Cluster(horizontal.Select(item => item.Fixed), options.CoordinateTolerance)
                    .OrderByDescending(value => value).ToList()
            };

            if (result.ColumnBoundaries.Count < 2 || result.RowBoundaries.Count < 2)
            {
                result.Warnings.Add("没有检测到完整的表格行列边界。");
                result.UnassignedText.AddRange(normalizedInput.TextFragments ?? new List<CadTextFragment>());
                return result;
            }

            DetectCells(result, horizontal, vertical, options);

            AssignText(result, normalizedInput.TextFragments ?? new List<CadTextFragment>(), options.CoordinateTolerance);
            if (result.Cells.Count == 0) result.Warnings.Add("检测到边界坐标，但没有形成闭合单元格。");
            var mergedCount = result.Cells.Count(cell => cell.RowSpan > 1 || cell.ColumnSpan > 1);
            if (mergedCount > 0) result.Warnings.Add("根据缺失的内部分隔线推断出 " + mergedCount + " 个合并单元格，请在预览中确认。");
            if (result.UnassignedText.Count > 0) result.Warnings.Add("有 " + result.UnassignedText.Count + " 段文字未能归入单元格，需要人工确认。");
            return result;
        }

        private static double DetectOverallRotation(IEnumerable<CadTableSegment> segments)
        {
            var cosine = 0d;
            var sine = 0d;
            var totalLength = 0d;
            foreach (var segment in segments ?? new List<CadTableSegment>())
            {
                if (segment == null || segment.Start == null || segment.End == null) continue;
                var dx = segment.End.X - segment.Start.X;
                var dy = segment.End.Y - segment.Start.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 0.000001d) continue;
                // Multiplying the angle by four makes horizontal and vertical table
                // edges vote for the same orientation while keeping the sign of skew.
                var angle = Math.Atan2(dy, dx);
                cosine += Math.Cos(angle * 4d) * length;
                sine += Math.Sin(angle * 4d) * length;
                totalLength += length;
            }
            if (totalLength < 0.000001d || Math.Abs(cosine) + Math.Abs(sine) < 0.000001d) return 0d;
            var degrees = Math.Atan2(sine, cosine) * 180d / Math.PI / 4d;
            while (degrees >= 45d) degrees -= 90d;
            while (degrees < -45d) degrees += 90d;
            return degrees;
        }

        private static CadTableDetectionInput RotateInput(CadTableDetectionInput input, double degrees)
        {
            var radians = degrees * Math.PI / 180d;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            var result = new CadTableDetectionInput();
            foreach (var segment in input.Segments ?? new List<CadTableSegment>())
            {
                if (segment == null || segment.Start == null || segment.End == null) continue;
                result.Segments.Add(new CadTableSegment
                {
                    Start = Rotate(segment.Start, cosine, sine),
                    End = Rotate(segment.End, cosine, sine),
                    SourceHandle = segment.SourceHandle,
                    Layer = segment.Layer
                });
            }
            foreach (var fragment in input.TextFragments ?? new List<CadTextFragment>())
            {
                if (fragment == null || fragment.Center == null) continue;
                result.TextFragments.Add(new CadTextFragment
                {
                    Text = fragment.Text,
                    PlainText = fragment.PlainText,
                    Center = Rotate(fragment.Center, cosine, sine),
                    Width = fragment.Width,
                    Height = fragment.Height,
                    RotationDegrees = fragment.RotationDegrees + degrees,
                    Confidence = fragment.Confidence,
                    SourceKind = fragment.SourceKind,
                    SourceHandle = fragment.SourceHandle,
                    SourceDxfName = fragment.SourceDxfName
                });
            }
            return result;
        }

        private static CadTablePoint Rotate(CadTablePoint point, double cosine, double sine)
        {
            return new CadTablePoint(
                point.X * cosine - point.Y * sine,
                point.X * sine + point.Y * cosine);
        }

        private static void DetectCells(CadTableDetectionResult result, IList<AxisSegment> horizontal,
            IList<AxisSegment> vertical, CadTableDetectionOptions options)
        {
            var rowCount = result.RowBoundaries.Count - 1;
            var columnCount = result.ColumnBoundaries.Count - 1;
            var occupied = new bool[rowCount, columnCount];
            var unresolved = 0;
            for (var row = 0; row < rowCount; row++)
            {
                for (var column = 0; column < columnCount; column++)
                {
                    if (occupied[row, column]) continue;
                    DetectedCadTableCell best = null;
                    var bestArea = int.MaxValue;
                    for (var rowSpan = 1; row + rowSpan <= rowCount; rowSpan++)
                    {
                        for (var columnSpan = 1; column + columnSpan <= columnCount; columnSpan++)
                        {
                            var area = rowSpan * columnSpan;
                            if (area >= bestArea || Overlaps(occupied, row, column, rowSpan, columnSpan)) continue;
                            var top = result.RowBoundaries[row];
                            var bottom = result.RowBoundaries[row + rowSpan];
                            var left = result.ColumnBoundaries[column];
                            var right = result.ColumnBoundaries[column + columnSpan];
                            if (!Covers(horizontal, top, left, right, options) ||
                                !Covers(horizontal, bottom, left, right, options) ||
                                !Covers(vertical, left, bottom, top, options) ||
                                !Covers(vertical, right, bottom, top, options)) continue;
                            bestArea = area;
                            best = new DetectedCadTableCell
                            {
                                RowIndex = row,
                                ColumnIndex = column,
                                RowSpan = rowSpan,
                                ColumnSpan = columnSpan,
                                Left = left,
                                Bottom = bottom,
                                Right = right,
                                Top = top
                            };
                            best.SourceHandles = CoveringHandles(horizontal, top, left, right, options)
                                .Concat(CoveringHandles(horizontal, bottom, left, right, options))
                                .Concat(CoveringHandles(vertical, left, bottom, top, options))
                                .Concat(CoveringHandles(vertical, right, bottom, top, options))
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        }
                    }
                    if (best == null)
                    {
                        unresolved++;
                        continue;
                    }
                    result.Cells.Add(best);
                    for (var occupiedRow = row; occupiedRow < row + best.RowSpan; occupiedRow++)
                        for (var occupiedColumn = column; occupiedColumn < column + best.ColumnSpan; occupiedColumn++)
                            occupied[occupiedRow, occupiedColumn] = true;
                }
            }
            if (unresolved > 0)
                result.Warnings.Add("有 " + unresolved + " 个网格区域边界不完整，未自动生成单元格。");
        }

        private static bool Overlaps(bool[,] occupied, int row, int column, int rowSpan, int columnSpan)
        {
            for (var currentRow = row; currentRow < row + rowSpan; currentRow++)
                for (var currentColumn = column; currentColumn < column + columnSpan; currentColumn++)
                    if (occupied[currentRow, currentColumn]) return true;
            return false;
        }

        private static bool TryNormalize(CadTableSegment source, double angleTolerance, out AxisSegment result, bool horizontal)
        {
            result = null;
            if (source == null || source.Start == null || source.End == null) return false;
            var dx = source.End.X - source.Start.X;
            var dy = source.End.Y - source.Start.Y;
            if (Math.Abs(dx) + Math.Abs(dy) < 0.000001d) return false;
            var angle = Math.Abs(Math.Atan2(dy, dx) * 180d / Math.PI);
            if (horizontal)
            {
                var deviation = Math.Min(angle, Math.Abs(180d - angle));
                if (deviation > angleTolerance) return false;
                result = new AxisSegment
                {
                    Fixed = (source.Start.Y + source.End.Y) * 0.5d,
                    Start = Math.Min(source.Start.X, source.End.X),
                    End = Math.Max(source.Start.X, source.End.X),
                    SourceHandles = string.IsNullOrWhiteSpace(source.SourceHandle)
                        ? new List<string>() : new List<string> { source.SourceHandle }
                };
                return true;
            }

            if (Math.Abs(90d - angle) > angleTolerance) return false;
            result = new AxisSegment
            {
                Fixed = (source.Start.X + source.End.X) * 0.5d,
                Start = Math.Min(source.Start.Y, source.End.Y),
                End = Math.Max(source.Start.Y, source.End.Y),
                SourceHandles = string.IsNullOrWhiteSpace(source.SourceHandle)
                    ? new List<string>() : new List<string> { source.SourceHandle }
            };
            return true;
        }

        private static List<AxisSegment> Merge(IEnumerable<AxisSegment> source, double coordinateTolerance, double maximumGap)
        {
            var result = new List<AxisSegment>();
            foreach (var group in GroupByCoordinate(source, coordinateTolerance))
            {
                var fixedCoordinate = group.Average(item => item.Fixed);
                foreach (var item in group.OrderBy(value => value.Start))
                {
                    var existing = result.LastOrDefault(value => Math.Abs(value.Fixed - fixedCoordinate) <= coordinateTolerance && item.Start <= value.End + maximumGap);
                    if (existing == null)
                    {
                        result.Add(new AxisSegment
                        {
                            Fixed = fixedCoordinate,
                            Start = item.Start,
                            End = item.End,
                            SourceHandles = item.SourceHandles.ToList()
                        });
                    }
                    else
                    {
                        existing.Start = Math.Min(existing.Start, item.Start);
                        existing.End = Math.Max(existing.End, item.End);
                        existing.SourceHandles = existing.SourceHandles.Concat(item.SourceHandles)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }
            }
            return result;
        }

        private static List<List<AxisSegment>> GroupByCoordinate(IEnumerable<AxisSegment> source, double tolerance)
        {
            var groups = new List<List<AxisSegment>>();
            foreach (var item in source.OrderBy(value => value.Fixed))
            {
                var group = groups.LastOrDefault();
                if (group == null || Math.Abs(group.Average(value => value.Fixed) - item.Fixed) > tolerance)
                {
                    group = new List<AxisSegment>();
                    groups.Add(group);
                }
                group.Add(item);
            }
            return groups;
        }

        private static List<double> Cluster(IEnumerable<double> source, double tolerance)
        {
            var groups = new List<List<double>>();
            foreach (var value in source.OrderBy(item => item))
            {
                var group = groups.LastOrDefault();
                if (group == null || Math.Abs(group.Average() - value) > tolerance)
                {
                    group = new List<double>();
                    groups.Add(group);
                }
                group.Add(value);
            }
            return groups.Select(group => group.Average()).ToList();
        }

        private static bool Covers(IEnumerable<AxisSegment> source, double fixedCoordinate, double start, double end, CadTableDetectionOptions options)
        {
            return source.Any(item => Math.Abs(item.Fixed - fixedCoordinate) <= options.CoordinateTolerance &&
                item.Start <= start + options.MaximumBorderGap && item.End >= end - options.MaximumBorderGap);
        }

        private static IEnumerable<string> CoveringHandles(IEnumerable<AxisSegment> source,
            double fixedCoordinate, double start, double end, CadTableDetectionOptions options)
        {
            return source.Where(item => Math.Abs(item.Fixed - fixedCoordinate) <= options.CoordinateTolerance &&
                    item.Start <= start + options.MaximumBorderGap && item.End >= end - options.MaximumBorderGap)
                .SelectMany(item => item.SourceHandles);
        }

        private static void AssignText(CadTableDetectionResult result, IEnumerable<CadTextFragment> fragments, double tolerance)
        {
            foreach (var fragment in fragments)
            {
                if (fragment == null || fragment.Center == null) continue;
                var matches = result.Cells.Where(cell =>
                    fragment.Center.X >= cell.Left - tolerance && fragment.Center.X <= cell.Right + tolerance &&
                    fragment.Center.Y >= cell.Bottom - tolerance && fragment.Center.Y <= cell.Top + tolerance).ToList();
                if (matches.Count != 1)
                {
                    result.UnassignedText.Add(fragment);
                    continue;
                }
                matches[0].TextFragments.Add(fragment);
                if (!string.IsNullOrWhiteSpace(fragment.SourceHandle) &&
                    !matches[0].SourceHandles.Contains(fragment.SourceHandle, StringComparer.OrdinalIgnoreCase))
                    matches[0].SourceHandles.Add(fragment.SourceHandle);
            }

            foreach (var cell in result.Cells)
            {
                cell.TextFragments = cell.TextFragments.OrderByDescending(item => item.Center.Y).ThenBy(item => item.Center.X).ToList();
                cell.Text = string.Join(Environment.NewLine, cell.TextFragments.Select(item =>
                    string.IsNullOrWhiteSpace(item.PlainText) ? item.Text : item.PlainText).Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }
    }
}
