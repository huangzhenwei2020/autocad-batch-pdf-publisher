using System.IO;
using System.IO.Compression;
using System.Linq;
using CadArchSpec.CadTable;
using Xunit;

namespace CadArchSpec.Stage0.Tests
{
    public sealed class XlsxTableExporterTests
    {
        [Fact]
        public void WritesExcelPackageWithChineseTextAndMergedCells()
        {
            var table = new SpreadsheetTable { Title = "门窗统计表" };
            table.Columns.AddRange(new[] { "编号", "说明" });
            table.ColumnWidthsMillimeters.AddRange(new[] { 20d, 60d });
            table.Rows.Add(new SpreadsheetRow
            {
                Cells =
                {
                    new SpreadsheetCell { Value = "M01", ColumnSpan = 2 },
                    new SpreadsheetCell { RowSpan = 0, ColumnSpan = 0 }
                }
            });
            table.Rows.Add(new SpreadsheetRow
            {
                Cells =
                {
                    new SpreadsheetCell { Value = "M02", Alignment = "left" },
                    new SpreadsheetCell { Value = "第一行\n第二行\n第三行", Alignment = "right" }
                }
            });

            using (var stream = new MemoryStream())
            {
                new XlsxTableExporter().Write(stream, table);
                stream.Position = 0;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                {
                    Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
                    Assert.NotNull(archive.GetEntry("xl/styles.xml"));
                    var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml");
                    Assert.NotNull(worksheet);
                    using (var reader = new StreamReader(worksheet.Open()))
                    {
                        var xml = reader.ReadToEnd();
                        Assert.Contains("第一行", xml);
                        Assert.Contains("第二行", xml);
                        Assert.Contains("A2:B2", xml);
                        Assert.Contains("<c r=\"B2\" s=\"3\" />", xml);
                        Assert.Contains("<c r=\"A3\" s=\"2\"", xml);
                        Assert.Contains("<c r=\"B3\" s=\"4\"", xml);
                        Assert.Contains("autoFilter", xml);
                        Assert.True(xml.IndexOf("<autoFilter", System.StringComparison.Ordinal) <
                            xml.IndexOf("<mergeCells", System.StringComparison.Ordinal));
                        Assert.Contains("width=\"10.8\"", xml);
                        Assert.Contains("width=\"32.4\"", xml);
                        Assert.Contains("r=\"3\" ht=\"48\" customHeight=\"1\"", xml);
                    }
                }
            }
        }

        [Fact]
        public void RejectsTablesWithoutColumns()
        {
            using (var stream = new MemoryStream())
                Assert.Throws<System.InvalidOperationException>(() =>
                    new XlsxTableExporter().Write(stream, new SpreadsheetTable()));
        }

        [Fact]
        public void DistributesMergedCellHeightAcrossItsRows()
        {
            var table = new SpreadsheetTable { Title = "防水做法" };
            table.Columns.Add("做法");
            table.ColumnWidthsMillimeters.Add(60d);
            table.Rows.Add(new SpreadsheetRow
            {
                Cells = { new SpreadsheetCell { Value = "第一行\n第二行\n第三行\n第四行", RowSpan = 2 } }
            });
            table.Rows.Add(new SpreadsheetRow
            {
                Cells = { new SpreadsheetCell { RowSpan = 0, ColumnSpan = 0 } }
            });

            using (var stream = new MemoryStream())
            {
                new XlsxTableExporter().Write(stream, table);
                stream.Position = 0;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                using (var reader = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml").Open()))
                {
                    var xml = reader.ReadToEnd();
                    Assert.Contains("r=\"2\" ht=\"31.5\" customHeight=\"1\"", xml);
                    Assert.Contains("r=\"3\" ht=\"31.5\" customHeight=\"1\"", xml);
                }
            }
        }

        [Fact]
        public void PreservesFormulasAndConfiguredColors()
        {
            var table = new SpreadsheetTable
            {
                Title = "计算表",
                BorderColorRgb = 0x112233,
                FillColorRgb = 0xDDEEFF,
                TextColorRgb = 0x445566
            };
            table.Columns.AddRange(new[] { "数量", "合计" });
            table.Rows.Add(new SpreadsheetRow
            {
                Cells =
                {
                    new SpreadsheetCell { Value = "10" },
                    new SpreadsheetCell { Value = "10", Formula = "=SUM(A1:A1)" }
                }
            });

            using (var stream = new MemoryStream())
            {
                new XlsxTableExporter().Write(stream, table);
                stream.Position = 0;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                {
                    using (var reader = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml").Open()))
                        Assert.Contains("<f>SUM(A2:A2)</f>", reader.ReadToEnd());
                    using (var reader = new StreamReader(archive.GetEntry("xl/styles.xml").Open()))
                    {
                        var styles = reader.ReadToEnd();
                        Assert.Contains("FF112233", styles);
                        Assert.Contains("FFDDEEFF", styles);
                        Assert.Contains("FF445566", styles);
                    }
                }
            }
        }

        [Fact]
        public void PreservesIndependentCellColorsAndPadding()
        {
            var table = new SpreadsheetTable { Title = "单元格样式" };
            table.Columns.AddRange(new[] { "A", "B" });
            table.Rows.Add(new SpreadsheetRow
            {
                Cells =
                {
                    new SpreadsheetCell
                    {
                        Value = "左", Alignment = "left", BorderColorRgb = 0xFF0000,
                        TopBorderColorRgb = 0x123456, BottomBorderColorRgb = 0x654321,
                        FillColorRgb = 0xFFFF00, TextColorRgb = 0x0000FF,
                        HorizontalPaddingMillimeters = 2
                    },
                    new SpreadsheetCell
                    {
                        Value = "右", Alignment = "right", BorderColorRgb = 0x00FF00,
                        TextColorRgb = 0xFF00FF, HorizontalPaddingMillimeters = 3
                    }
                }
            });

            using (var stream = new MemoryStream())
            {
                new XlsxTableExporter().Write(stream, table, false);
                stream.Position = 0;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                {
                    string styles;
                    using (var reader = new StreamReader(archive.GetEntry("xl/styles.xml").Open())) styles = reader.ReadToEnd();
                    Assert.Contains("FFFF0000", styles);
                    Assert.Contains("FFFFFF00", styles);
                    Assert.Contains("FF0000FF", styles);
                    Assert.Contains("FF00FF00", styles);
                    Assert.Contains("FFFF00FF", styles);
                    Assert.Contains("FF123456", styles);
                    Assert.Contains("FF654321", styles);
                    Assert.Contains("horizontal=\"left\"", styles);
                    Assert.Contains("horizontal=\"right\"", styles);
                    Assert.Contains("indent=\"2\"", styles);
                    Assert.Contains("indent=\"3\"", styles);
                }
            }
        }

        [Fact]
        public void CanWriteTianzhengImportWorkbookWithoutSyntheticColumnHeader()
        {
            var table = new SpreadsheetTable { Title = "材料表" };
            table.Columns.AddRange(new[] { "A", "B" });
            table.Rows.Add(new SpreadsheetRow
            {
                Cells =
                {
                    new SpreadsheetCell { Value = "材料名称" },
                    new SpreadsheetCell { Value = "规格" }
                }
            });

            using (var stream = new MemoryStream())
            {
                new XlsxTableExporter().Write(stream, table, false);
                stream.Position = 0;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                using (var reader = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml").Open()))
                {
                    var xml = reader.ReadToEnd();
                    Assert.Contains("<c r=\"A1\" s=\"3\"", xml);
                    Assert.Contains("材料名称", xml);
                    Assert.DoesNotContain("autoFilter", xml);
                    Assert.DoesNotContain("ySplit", xml);
                }
            }
        }

    }
}
