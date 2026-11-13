using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace BatchPdfPublisher.Views
{
    public partial class BppCatalogWindow : BppCadWindow
    {
        private readonly IList<SheetItem> _sheets;
        private readonly Action _done;
        private readonly List<BuildingChoice> _buildings;
        private readonly List<ColumnChoice> _columns;
        private int _page;
        private AcColor _color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
        private bool _ready;
        private static string SettingsPath => UserDataPaths.SettingsFile("catalog.settings", "BatchPdfPublisher.catalog.settings");

        public BppCatalogWindow(Document document, IList<SheetItem> sheets, Action done)
        {
            _sheets = sheets;
            _done = done;
            _buildings = sheets.Select(sheet => sheet.Building ?? "未分组").Distinct().Select(name => new BuildingChoice { Name = name, Included = true }).ToList();
            _columns = new List<ColumnChoice>
            {
                new ColumnChoice("序号", 20),
                new ColumnChoice("图号", 30),
                new ColumnChoice("图名", 70),
                new ColumnChoice("图框", 24),
                new ColumnChoice("比例", 24)
            };
            BindDocument(document);
            InitializeComponent();
            Scale.ItemsSource = new[] { "1:1", "1:2", "1:5", "1:10", "1:20", "1:25", "1:50", "1:75", "1:100", "1:150", "1:200", "1:500" };
            Buildings.ItemsSource = _buildings;
            Columns.ItemsSource = _columns;
            FontName.ItemsSource = FrameCreationService.GetTextStyleNames(document);
            FontName.SelectedIndex = 0;
            LoadSettings();
            ColorName.Text = _color.ToString();
            _ready = true;
            RefreshPreview();
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            if (_ready)
                Buildings.ItemsSource = _buildings.Where(building => building.Name.IndexOf(Search.Text ?? string.Empty, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        }

        private void Building_Click(object sender, RoutedEventArgs e)
        {
            _page = 0;
            RefreshPreview();
        }

        private void All_Click(object sender, RoutedEventArgs e) => SelectBuildings(true);
        private void None_Click(object sender, RoutedEventArgs e) => SelectBuildings(false);
        private void SelectBuildings(bool included)
        {
            foreach (var building in _buildings)
                building.Included = included;
            Buildings.ItemsSource = null;
            Buildings.ItemsSource = _buildings;
            _page = 0;
            RefreshPreview();
        }

        private void Setting_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _page = 0;
            RefreshPreview();
        }

        private List<SheetItem> SelectedSheets()
        {
            var selected = new HashSet<string>(_buildings.Where(building => building.Included).Select(building => building.Name));
            return _sheets.Where(sheet => selected.Contains(sheet.Building ?? "未分组")).ToList();
        }

        private List<Page> Pages()
        {
            var rows = int.TryParse(Rows.Text, out var count) && count > 0 ? Math.Min(count, 1000) : 30;
            var pages = new List<Page>();
            foreach (var group in SelectedSheets().GroupBy(sheet => sheet.Building ?? "未分组"))
            {
                var sheets = group.OrderBy(sheet => sheet.Order).ToList();
                for (var start = 0; start < sheets.Count; start += rows)
                    pages.Add(new Page { Name = group.Key, Sheets = sheets.Skip(start).Take(rows).ToList(), Offset = start });
            }

            return pages;
        }

        private void RefreshPreview()
        {
            if (!_ready)
                return;
            try
            {
                var pages = Pages();
                _page = Math.Max(0, Math.Min(_page, pages.Count - 1));
                var page = pages.Count > 0 ? pages[_page] : null;
                Preview.Columns.Clear();
                var properties = new[]
                {
                    "Number",
                    "SheetNumber",
                    "SheetName",
                    "Frame",
                    "Scale"
                };
                for (var index = 0; index < _columns.Count; index++)
                {
                    var column = _columns[index];
                    if (column.Included)
                        Preview.Columns.Add(new DataGridTextColumn { Header = column.Name, Binding = new Binding(properties[index]), Width = new DataGridLength(ParsePositive(column.Width, "列宽") * 3) });
                }

                Preview.ItemsSource = page?.Sheets.Select((sheet, index) => new { Number = (page.Offset + index + 1).ToString(), SheetNumber = sheet.SheetNumber, SheetName = sheet.SheetName, Frame = sheet.FrameDisplay, Scale = sheet.PrintScale }).ToList();
                PageLabel.Text = page == null ? "没有选中图纸" : page.Name + "   " + (_page + 1) + " / " + pages.Count;
                Status.Text = "已选 " + SelectedSheets().Count + " 张图纸，共 " + pages.Count + " 页目录";
            }
            catch (InvalidOperationException error)
            {
                Status.Text = error.Message;
            }
        }

        private static double ParsePositive(string text, string label)
        {
            if (!double.TryParse(text, out var value) || double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new InvalidOperationException("请输入有效的" + label + "。");
            return value;
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            _page--;
            RefreshPreview();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            _page++;
            RefreshPreview();
        }

        private void Color_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Autodesk.AutoCAD.Windows.ColorDialog
            {
                Color = _color
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _color = dialog.Color;
                ColorName.Text = _color.ToString();
            }
        }

        private void Defaults_Click(object sender, RoutedEventArgs e)
        {
            Rows.Text = "30";
            RowHeight.Text = "7";
            TextHeight.Text = "3.5";
            Scale.Text = "1:1";
            var widths = new[]
            {
                20,
                30,
                70,
                24,
                24
            };
            for (var index = 0; index < _columns.Count; index++)
            {
                _columns[index].Included = true;
                _columns[index].Width = widths[index].ToString();
            }

            Columns.ItemsSource = null;
            Columns.ItemsSource = _columns;
            RefreshPreview();
        }

        private void Insert_Click(object sender, RoutedEventArgs e)
        {
            if (IsCadBusy)
                return;
            try
            {
                var selected = SelectedSheets();
                if (selected.Count == 0 || !_columns.Any(column => column.Included))
                    throw new InvalidOperationException("请选择子项目和目录列。");
                var settings = ReadSettings();
                SaveSettings();
                var inserted = false;
                RunCad(() => inserted = CatalogInsertionService.Insert(SourceDocument, selected, settings), () =>
                {
                    Status.Text = inserted ? "目录已插入" : "已取消插入";
                    if (inserted)
                        _done?.Invoke();
                });
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Title);
            }
        }

        internal CatalogSettings ReadSettings()
        {
            if (!int.TryParse(Rows.Text, out var rows) || rows < 1 || rows > 1000)
                throw new InvalidOperationException("每页行数请输入 1～1000。");
            if (!CatalogSettings.TryParseDrawingScale(Scale.Text, out var scale))
                throw new InvalidOperationException("插入比例请输入 1:1、1:50、1:75、1:100 或正数，例如 100。");
            return new CatalogSettings
            {
                IncludeBuilding = _columns[0].Included,
                IncludeNumber = _columns[1].Included,
                IncludeName = _columns[2].Included,
                IncludePaper = _columns[3].Included,
                IncludeScale = _columns[4].Included,
                ColumnWidths = _columns.Select(column => ParsePositive(column.Width, "列宽")).ToArray(),
                RowsPerPage = rows,
                RowHeight = ParsePositive(RowHeight.Text, "行高"),
                TextHeight = ParsePositive(TextHeight.Text, "文字高度"),
                Scale = scale,
                Font = FontName.Text,
                Color = _color
            };
        }

        private void LoadSettings()
        {
            if (!File.Exists(SettingsPath))
                return;
            var values = File.ReadAllLines(SettingsPath);
            if (values.Length > 0)
                Rows.Text = values[0];
            if (values.Length > 1)
                RowHeight.Text = values[1];
            if (values.Length > 2)
            {
                var widths = values[2].Split(',');
                for (var index = 0; index < _columns.Count && index < widths.Length; index++)
                    _columns[index].Width = widths[index];
            }

            if (values.Length > 3)
                TextHeight.Text = values[3];
            if (values.Length > 4)
                Scale.Text = values[4];
            if (values.Length > 5)
                FontName.Text = values[5];
            if (values.Length > 7 && values[6].IndexOf("ByAci", StringComparison.OrdinalIgnoreCase) >= 0 && short.TryParse(values[7], out var aci))
                _color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);
            if (values.Length > 8)
                for (var index = 0; index < _columns.Count && index < values[8].Length; index++)
                    _columns[index].Included = values[8][index] == '1';
        }

        private void SaveSettings()
        {
            var values = new[]
            {
                Rows.Text,
                RowHeight.Text,
                string.Join(",", _columns.Select(column => column.Width)),
                TextHeight.Text,
                Scale.Text,
                FontName.Text,
                _color.ColorMethod.ToString(),
                _color.ColorIndex.ToString(),
                string.Concat(_columns.Select(column => column.Included ? "1" : "0"))
            };
            var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temporary, values);
                if (File.Exists(SettingsPath))
                    File.Replace(temporary, SettingsPath, null);
                else
                    File.Move(temporary, SettingsPath);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private sealed class BuildingChoice
        {
            public string Name { get; set; }
            public bool Included { get; set; }
        }

        private sealed class ColumnChoice
        {
            public string Name { get; }
            public string Width { get; set; }
            public bool Included { get; set; } = true;

            internal ColumnChoice(string name, double width)
            {
                Name = name;
                Width = width.ToString();
            }
        }

        private sealed class Page
        {
            internal string Name;
            internal List<SheetItem> Sheets;
            internal int Offset;
        }
    }
}
