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
            int duplicateTextCount;
            var textFragments = DeduplicateTextFragments(normalizedInput.TextFragments ??
                new List<CadTextFragment>(), options.CoordinateTolerance, out duplicateTextCount);
            if (duplicateTextCount > 0)
                result.Warnings.Add("已忽略 " + duplicateTextCount + " 段内容和位置均重合的重复文字。");

            if (result.ColumnBoundaries.Count < 2 || result.RowBoundaries.Count < 2)
            {
                result.Warnings.Add("没有检测到完整的表格行列边界。");
                result.UnassignedText.AddRange(textFragments);
                return result;
            }

            int splitMultilineCount;
            textFragments = SplitMultilineTextAtHorizontalBorders(textFragments, horizontal, options,
                out splitMultilineCount);
            if (splitMultilineCount > 0)
                result.Warnings.Add("已按 CAD 横向分格线拆分 " + splitMultilineCount + " 段跨格多行文字。");

            DetectCells(result, horizontal, vertical, options);
            var ignoredDecorationCount = PruneUnusedGridLines(result, horizontal, vertical, options);
            if (ignoredDecorationCount > 0)
                result.Warnings.Add("已忽略 " + ignoredDecorationCount + " 条未参与闭合单元格边界的装饰线。");
            PruneDetachedClosedDecorations(result, textFragments, options.CoordinateTolerance);

            AssignText(result, textFragments, options.CoordinateTolerance);
            if (result.Cells.Count == 0) result.Warnings.Add("检测到边界坐标，但没有形成闭合单元格。");
            var mergedCount = result.Cells.Count(cell => cell.RowSpan > 1 || cell.ColumnSpan > 1);
            if (mergedCount > 0) result.Warnings.Add("根据缺失的内部分隔线推断出 " + mergedCount + " 个合并单元格，请在预览中确认。");
            if (result.UnassignedText.Count > 0) result.Warnings.Add("有 " + result.UnassignedText.Count + " 段文字未能归入单元格，需要人工确认。");
            return result;
        }

        private static List<CadTextFragment> SplitMultilineTextAtHorizontalBorders(
            IEnumerable<CadTextFragment> fragments, IList<AxisSegment> horizontal,
            CadTableDetectionOptions options, out int splitCount)
        {
            var result = new List<CadTextFragment>();
            splitCount = 0;
            foreach (var fragment in fragments)
            {
                var visible = VisibleText(fragment).Replace("\r\n", "\n").Replace('\r', '\n');
                var lines = visible.Split(new[] { '\n' }, StringSplitOptions.None);
                if (lines.Length < 2 || !fragment.HasBounds || fragment.Top <= fragment.Bottom)
                {
                    result.Add(fragment);
                    continue;
                }

                var bandHeight = (fragment.Top - fragment.Bottom) / lines.Length;
                var centers = Enumerable.Range(0, lines.Length)
                    .Select(index => fragment.Top - (index + .5d) * bandHeight).ToList();
                var crossesDivider = Enumerable.Range(0, centers.Count - 1).Any(index =>
                    horizontal.Any(border => border.Fixed < centers[index] - options.CoordinateTolerance &&
                        border.Fixed > centers[index + 1] + options.CoordinateTolerance &&
                        border.Start <= fragment.Center.X + options.MaximumBorderGap &&
                        border.End >= fragment.Center.X - options.MaximumBorderGap));
                if (!crossesDivider)
                {
                    result.Add(fragment);
                    continue;
                }

                splitCount++;
                for (var index = 0; index < lines.Length; index++)
                {
                    var top = fragment.Top - index * bandHeight;
                    var bottom = top - bandHeight;
                    result.Add(new CadTextFragment
                    {
                        Text = lines[index],
                        PlainText = lines[index],
                        Center = new CadTablePoint(fragment.Center.X, centers[index]),
                        Width = fragment.Width,
                        Height = Math.Min(Math.Max(.001d, fragment.Height), bandHeight),
                        HasBounds = true,
                        Left = fragment.Left,
                        Bottom = bottom,
                        Right = fragment.Right,
                        Top = top,
                        RotationDegrees = fragment.RotationDegrees,
                        Confidence = fragment.Confidence,
                        SourceKind = fragment.SourceKind,
                        SourceHandle = fragment.SourceHandle,
                        SourceDxfName = fragment.SourceDxfName
                    });
                }
            }
            return result;
        }

        private static int PruneUnusedGridLines(CadTableDetectionResult result,
            IList<AxisSegment> horizontal, IList<AxisSegment> vertical, CadTableDetectionOptions options)
        {
            if (result.Cells.Count == 0) return 0;
            var usedHorizontalCoordinates = result.Cells.SelectMany(cell => new[] { cell.Top, cell.Bottom }).ToList();
            var usedVerticalCoordinates = result.Cells.SelectMany(cell => new[] { cell.Left, cell.Right }).ToList();
            var retainedHorizontal = horizontal.Where(line => usedHorizontalCoordinates.Any(value =>
                Math.Abs(value - line.Fixed) <= options.CoordinateTolerance)).ToList();
            var retainedVertical = vertical.Where(line => usedVerticalCoordinates.Any(value =>
                Math.Abs(value - line.Fixed) <= options.CoordinateTolerance)).ToList();
            var ignoredCount = horizontal.Count - retainedHorizontal.Count + vertical.Count - retainedVertical.Count;
            if (ignoredCount == 0) return 0;

            var refined = new CadTableDetectionResult
            {
                DetectedRotationDegrees = result.DetectedRotationDegrees,
                ColumnBoundaries = Cluster(retainedVertical.Select(item => item.Fixed), options.CoordinateTolerance),
                RowBoundaries = Cluster(retainedHorizontal.Select(item => item.Fixed), options.CoordinateTolerance)
                    .OrderByDescending(value => value).ToList()
            };
            if (refined.ColumnBoundaries.Count < 2 || refined.RowBoundaries.Count < 2) return 0;
            DetectCells(refined, retainedHorizontal, retainedVertical, options);
            if (refined.Cells.Count == 0) return 0;

            result.ColumnBoundaries = refined.ColumnBoundaries;
            result.RowBoundaries = refined.RowBoundaries;
            result.Cells = refined.Cells;
            result.Warnings.RemoveAll(warning => warning.IndexOf("网格区域边界不完整", StringComparison.Ordinal) >= 0);
            result.Warnings.AddRange(refined.Warnings);
            return ignoredCount;
        }

        private static void PruneDetachedClosedDecorations(CadTableDetectionResult result,
            IList<CadTextFragment> textFragments, double tolerance)
        {
            if (result.Cells.Count < 2) return;
            var components = ConnectedCellComponents(result.Cells, tolerance)
                .OrderByDescending(component => component.Count).ToList();
            if (components.Count < 2) return;

            var largest = components[0];
            var hasUniqueDominantTable = largest.Count >= 2 &&
                (components.Count == 1 || largest.Count > components[1].Count);
            if (!hasUniqueDominantTable)
            {
                result.Warnings.Add("检测到 " + components.Count + " 个彼此独立的闭合区域，无法安全判断主表格，已全部保留，请确认。");
                return;
            }

            var removable = components.Skip(1).Where(component => component.Count == 1 &&
                !ComponentContainsText(component, textFragments, tolerance)).ToList();
            if (removable.Count == 0)
            {
                result.Warnings.Add("检测到 " + components.Count + " 个彼此独立的闭合区域；含文字或结构较复杂的区域已保留，请确认。");
                return;
            }

            var removedCells = new HashSet<DetectedCadTableCell>(removable.SelectMany(component => component));
            result.Cells = result.Cells.Where(cell => !removedCells.Contains(cell)).ToList();
            ReindexCellsAndBoundaries(result, tolerance);
            result.Warnings.Add("已忽略 " + removable.Count + " 个不含文字且未连接主表格的闭合装饰框。");

            var remainingComponentCount = ConnectedCellComponents(result.Cells, tolerance).Count;
            if (remainingComponentCount > 1)
                result.Warnings.Add("仍有 " + remainingComponentCount + " 个彼此独立的闭合区域，已保留含文字或结构较复杂的区域，请确认。");
        }

        private static List<List<DetectedCadTableCell>> ConnectedCellComponents(
            IList<DetectedCadTableCell> cells, double tolerance)
        {
            var components = new List<List<DetectedCadTableCell>>();
            var remaining = new HashSet<DetectedCadTableCell>(cells);
            while (remaining.Count > 0)
            {
                var seed = remaining.First();
                remaining.Remove(seed);
                var component = new List<DetectedCadTableCell>();
                var pending = new Queue<DetectedCadTableCell>();
                pending.Enqueue(seed);
                while (pending.Count > 0)
                {
                    var current = pending.Dequeue();
                    component.Add(current);
                    var neighbours = remaining.Where(candidate => CellsShareEdge(current, candidate, tolerance)).ToList();
                    foreach (var neighbour in neighbours)
                    {
                        remaining.Remove(neighbour);
                        pending.Enqueue(neighbour);
                    }
                }
                components.Add(component);
            }
            return components;
        }

        private static bool CellsShareEdge(DetectedCadTableCell first, DetectedCadTableCell second,
            double tolerance)
        {
            var verticalOverlap = Math.Min(first.Top, second.Top) - Math.Max(first.Bottom, second.Bottom);
            var horizontalOverlap = Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left);
            var sharesVerticalEdge = verticalOverlap > tolerance &&
                (Math.Abs(first.Right - second.Left) <= tolerance || Math.Abs(second.Right - first.Left) <= tolerance);
            var sharesHorizontalEdge = horizontalOverlap > tolerance &&
                (Math.Abs(first.Bottom - second.Top) <= tolerance || Math.Abs(second.Bottom - first.Top) <= tolerance);
            return sharesVerticalEdge || sharesHorizontalEdge;
        }

        private static bool ComponentContainsText(IEnumerable<DetectedCadTableCell> component,
            IEnumerable<CadTextFragment> textFragments, double tolerance)
        {
            return component.Any(cell => textFragments.Any(fragment => fragment != null && fragment.Center != null &&
                fragment.Center.X >= cell.Left - tolerance && fragment.Center.X <= cell.Right + tolerance &&
                fragment.Center.Y >= cell.Bottom - tolerance && fragment.Center.Y <= cell.Top + tolerance));
        }

        private static void ReindexCellsAndBoundaries(CadTableDetectionResult result, double tolerance)
        {
            result.ColumnBoundaries = Cluster(result.Cells.SelectMany(cell => new[] { cell.Left, cell.Right }), tolerance);
            result.RowBoundaries = Cluster(result.Cells.SelectMany(cell => new[] { cell.Top, cell.Bottom }), tolerance)
                .OrderByDescending(value => value).ToList();
            foreach (var cell in result.Cells)
            {
                cell.ColumnIndex = ClosestBoundaryIndex(result.ColumnBoundaries, cell.Left);
                cell.RowIndex = ClosestBoundaryIndex(result.RowBoundaries, cell.Top);
                cell.ColumnSpan = Math.Max(1, ClosestBoundaryIndex(result.ColumnBoundaries, cell.Right) - cell.ColumnIndex);
                cell.RowSpan = Math.Max(1, ClosestBoundaryIndex(result.RowBoundaries, cell.Bottom) - cell.RowIndex);
            }
        }

        private static int ClosestBoundaryIndex(IList<double> boundaries, double coordinate)
        {
            var bestIndex = 0;
            var bestDistance = double.MaxValue;
            for (var index = 0; index < boundaries.Count; index++)
            {
                var distance = Math.Abs(boundaries[index] - coordinate);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestIndex = index;
            }
            return bestIndex;
        }

        private static List<CadTextFragment> DeduplicateTextFragments(IEnumerable<CadTextFragment> source,
            double tolerance, out int duplicateCount)
        {
            var retained = new List<CadTextFragment>();
            duplicateCount = 0;
            foreach (var fragment in source.Where(item => item != null && item.Center != null)
                .OrderBy(item => SourcePriority(item.SourceKind)))
            {
                var duplicate = retained.Any(existing => SameVisibleText(existing, fragment) &&
                    SameTextPosition(existing, fragment, tolerance));
                if (duplicate)
                {
                    duplicateCount++;
                    continue;
                }
                retained.Add(fragment);
            }
            return retained;
        }

        private static int SourcePriority(CadTextSourceKind sourceKind)
        {
            switch (sourceKind)
            {
                case CadTextSourceKind.Standard: return 0;
                case CadTextSourceKind.TianzhengProperty: return 1;
                case CadTextSourceKind.ExplodedClone: return 2;
                case CadTextSourceKind.OcrFallback: return 3;
                default: return 4;
            }
        }

        private static bool SameVisibleText(CadTextFragment first, CadTextFragment second)
        {
            return string.Equals(ComparableText(first), ComparableText(second), StringComparison.Ordinal);
        }

        private static string ComparableText(CadTextFragment fragment)
        {
            var value = VisibleText(fragment).Replace("\r\n", "\n").Replace('\r', '\n');
            return string.Join("\n", value.Split(new[] { '\n' }, StringSplitOptions.None)
                .Select(line => line.Trim())).Trim();
        }

        private static bool SameTextPosition(CadTextFragment first, CadTextFragment second, double tolerance)
        {
            if (first.HasBounds && second.HasBounds)
            {
                var intersectionWidth = Math.Max(0d, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
                var intersectionHeight = Math.Max(0d, Math.Min(first.Top, second.Top) - Math.Max(first.Bottom, second.Bottom));
                var firstArea = Math.Max(0d, first.Right - first.Left) * Math.Max(0d, first.Top - first.Bottom);
                var secondArea = Math.Max(0d, second.Right - second.Left) * Math.Max(0d, second.Top - second.Bottom);
                var smallerArea = Math.Min(firstArea, secondArea);
                if (smallerArea > 0d && intersectionWidth * intersectionHeight / smallerArea >= 0.85d) return true;
            }
            var positionTolerance = Math.Max(tolerance, Math.Max(first.Height, second.Height) * 0.25d);
            return Math.Abs(first.Center.X - second.Center.X) <= positionTolerance &&
                Math.Abs(first.Center.Y - second.Center.Y) <= positionTolerance;
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
                var bounds = fragment.HasBounds
                    ? RotateBounds(fragment.Left, fragment.Bottom, fragment.Right, fragment.Top, cosine, sine)
                    : null;
                result.TextFragments.Add(new CadTextFragment
                {
                    Text = fragment.Text,
                    PlainText = fragment.PlainText,
                    Center = Rotate(fragment.Center, cosine, sine),
                    Width = fragment.Width,
                    Height = fragment.Height,
                    HasBounds = bounds != null,
                    Left = bounds == null ? 0d : bounds[0],
                    Bottom = bounds == null ? 0d : bounds[1],
                    Right = bounds == null ? 0d : bounds[2],
                    Top = bounds == null ? 0d : bounds[3],
                    RotationDegrees = fragment.RotationDegrees + degrees,
                    Confidence = fragment.Confidence,
                    SourceKind = fragment.SourceKind,
                    SourceHandle = fragment.SourceHandle,
                    SourceDxfName = fragment.SourceDxfName
                });
            }
            return result;
        }

        private static double[] RotateBounds(double left, double bottom, double right, double top,
            double cosine, double sine)
        {
            var corners = new[]
            {
                Rotate(new CadTablePoint(left, bottom), cosine, sine),
                Rotate(new CadTablePoint(left, top), cosine, sine),
                Rotate(new CadTablePoint(right, bottom), cosine, sine),
                Rotate(new CadTablePoint(right, top), cosine, sine)
            };
            return new[] { corners.Min(point => point.X), corners.Min(point => point.Y),
                corners.Max(point => point.X), corners.Max(point => point.Y) };
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
                var target = FindTargetCell(result.Cells, fragment, tolerance);
                if (target == null)
                {
                    result.UnassignedText.Add(fragment);
                    continue;
                }
                target.TextFragments.Add(fragment);
                if (!string.IsNullOrWhiteSpace(fragment.SourceHandle) &&
                    !target.SourceHandles.Contains(fragment.SourceHandle, StringComparer.OrdinalIgnoreCase))
                    target.SourceHandles.Add(fragment.SourceHandle);
            }

            foreach (var cell in result.Cells)
            {
                cell.TextFragments = cell.TextFragments.OrderByDescending(item => item.Center.Y).ThenBy(item => item.Center.X).ToList();
                cell.Text = CombineTextFragments(cell.TextFragments, tolerance);
            }
        }

        private static DetectedCadTableCell FindTargetCell(IList<DetectedCadTableCell> cells,
            CadTextFragment fragment, double tolerance)
        {
            var exactCenterMatches = cells.Where(cell =>
                fragment.Center.X > cell.Left && fragment.Center.X < cell.Right &&
                fragment.Center.Y > cell.Bottom && fragment.Center.Y < cell.Top).ToList();
            if (exactCenterMatches.Count == 1) return exactCenterMatches[0];

            if (fragment.HasBounds && fragment.Right > fragment.Left && fragment.Top > fragment.Bottom)
            {
                var ranked = cells.Select(cell => new { Cell = cell, Score = OverlapRatio(cell, fragment) })
                    .Where(item => item.Score > 0d).OrderByDescending(item => item.Score).ToList();
                if (ranked.Count > 0)
                {
                    var second = ranked.Count > 1 ? ranked[1].Score : 0d;
                    if (ranked[0].Score >= 0.5d && ranked[0].Score - second >= 0.2d)
                        return ranked[0].Cell;
                }
            }

            var tolerantCenterMatches = cells.Where(cell =>
                fragment.Center.X >= cell.Left - tolerance && fragment.Center.X <= cell.Right + tolerance &&
                fragment.Center.Y >= cell.Bottom - tolerance && fragment.Center.Y <= cell.Top + tolerance).ToList();
            return tolerantCenterMatches.Count == 1 ? tolerantCenterMatches[0] : null;
        }

        private static double OverlapRatio(DetectedCadTableCell cell, CadTextFragment fragment)
        {
            var width = Math.Max(0d, Math.Min(cell.Right, fragment.Right) - Math.Max(cell.Left, fragment.Left));
            var height = Math.Max(0d, Math.Min(cell.Top, fragment.Top) - Math.Max(cell.Bottom, fragment.Bottom));
            var area = (fragment.Right - fragment.Left) * (fragment.Top - fragment.Bottom);
            return area <= 0d ? 0d : width * height / area;
        }

        private static string CombineTextFragments(IList<CadTextFragment> fragments, double tolerance)
        {
            if (fragments == null || fragments.Count == 0) return string.Empty;
            if (fragments.Count == 1) return VisibleText(fragments[0]);
            var lines = new List<List<CadTextFragment>>();
            foreach (var fragment in fragments.OrderByDescending(item => item.Center.Y).ThenBy(item => item.Center.X))
            {
                var line = lines.LastOrDefault();
                var lineY = line == null || line.Count == 0 ? 0d : line.Average(item => item.Center.Y);
                var height = Math.Max(fragment.Height, line == null || line.Count == 0 ? 0d : line.Max(item => item.Height));
                var lineTolerance = Math.Max(tolerance, height * 0.6d);
                if (line == null || Math.Abs(lineY - fragment.Center.Y) > lineTolerance)
                {
                    line = new List<CadTextFragment>();
                    lines.Add(line);
                }
                line.Add(fragment);
            }
            return string.Join(Environment.NewLine, lines.Select(line => string.Concat(line
                .OrderBy(item => item.Center.X).Select(VisibleText))).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string VisibleText(CadTextFragment fragment)
        {
            return (string.IsNullOrWhiteSpace(fragment.PlainText) ? fragment.Text : fragment.PlainText) ?? string.Empty;
        }
    }
}
