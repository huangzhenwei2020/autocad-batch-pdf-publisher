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
                    new SpreadsheetCell { Value = "M02" },
                    new SpreadsheetCell { Value = "天正文字兼容" }
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
                        Assert.Contains("天正文字兼容", xml);
                        Assert.Contains("A2:B2", xml);
                        Assert.Contains("autoFilter", xml);
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
    }
}
