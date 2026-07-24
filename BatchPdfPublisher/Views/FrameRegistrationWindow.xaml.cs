using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public partial class FrameRegistrationWindow : Window
    {
        private const string DoNotRead = "（不读取）";
        private readonly IDictionary<string, string> _attributeValues;

        public FrameDefinition Definition { get; private set; }

        public FrameRegistrationWindow(string blockName, FrameSizeGuess guess, IDictionary<string, string> attributeValues, FrameDefinition existing = null)
        {
            InitializeComponent();
            _attributeValues = attributeValues ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            BlockNameBox.Text = blockName;
            MeasuredSizeText.Text = guess.MeasuredSize + "；建议 " + guess.PaperSize +
                                    (string.IsNullOrWhiteSpace(guess.Extension) ? string.Empty : "+" + guess.Extension) +
                                    "，" + guess.PaperOrientation + "，打印比例 " + guess.PrintScale + "（均可修改）";

            var selectedTags = existing == null
                ? Enumerable.Empty<string>()
                : new[] { existing.BuildingAttributeTag, existing.SheetNumberAttributeTag, existing.SheetNameAttributeTag, existing.PrintScaleAttributeTag };
            var tags = new[] { DoNotRead }.Concat(_attributeValues.Keys).Concat(selectedTags)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var box in TagBoxes()) box.ItemsSource = tags;

            SelectComboByText(PaperSizeBox, existing?.PaperSize ?? guess.PaperSize);
            PaperSizeBox_SelectionChanged(null, null);
            SelectComboByText(ExtensionBox, string.IsNullOrWhiteSpace(existing?.Extension ?? guess.Extension) ? "无加长" : existing?.Extension ?? guess.Extension);
            SelectComboByText(OrientationBox, existing?.PaperOrientation ?? guess.PaperOrientation);
            NoteBox.Text = existing?.Note ?? string.Empty;

            if (existing == null)
            {
                SelectTag(BuildingTagBox, tags, "楼栋", "BUILDING", "栋号");
                SelectTag(SheetNumberTagBox, tags, "图号", "SHEETNO", "SHEET_NO", "DRAWINGNO");
                SelectTag(SheetNameTagBox, tags, "图名", "SHEETNAME", "SHEET_NAME", "DRAWINGNAME");
                SelectTag(PrintScaleTagBox, tags, "比例", "SCALE", "PRINTSCALE");
                FillValueFromSelectedTag(BuildingTagBox, BuildingValueBox);
                FillValueFromSelectedTag(SheetNumberTagBox, SheetNumberValueBox);
                FillValueFromSelectedTag(SheetNameTagBox, SheetNameValueBox);
                FillValueFromSelectedTag(PrintScaleTagBox, PrintScaleValueBox);
                if (string.IsNullOrWhiteSpace(PrintScaleValueBox.Text)) PrintScaleValueBox.Text = guess.PrintScale;
            }
            else
            {
                SelectStoredTag(BuildingTagBox, existing.BuildingAttributeTag);
                SelectStoredTag(SheetNumberTagBox, existing.SheetNumberAttributeTag);
                SelectStoredTag(SheetNameTagBox, existing.SheetNameAttributeTag);
                SelectStoredTag(PrintScaleTagBox, existing.PrintScaleAttributeTag);
                BuildingValueBox.Text = existing.DefaultBuilding ?? string.Empty;
                SheetNumberValueBox.Text = existing.DefaultSheetNumber ?? string.Empty;
                SheetNameValueBox.Text = existing.DefaultSheetName ?? string.Empty;
                PrintScaleValueBox.Text = string.IsNullOrWhiteSpace(existing.DefaultPrintScale) ? guess.PrintScale : existing.DefaultPrintScale;
                Title = "修改图框登记";
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var paper = ItemText(PaperSizeBox);
            if (string.IsNullOrWhiteSpace(paper))
            {
                MessageBox.Show("请选择纸张规格。", "登记图框", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var extension = ItemText(ExtensionBox);
            Definition = new FrameDefinition
            {
                BlockName = BlockNameBox.Text,
                PaperSize = paper,
                Extension = extension == "无加长" ? string.Empty : extension,
                PaperOrientation = ItemText(OrientationBox),
                Note = NoteBox.Text?.Trim(),
                BuildingAttributeTag = TagOf(BuildingTagBox),
                SheetNumberAttributeTag = TagOf(SheetNumberTagBox),
                SheetNameAttributeTag = TagOf(SheetNameTagBox),
                PrintScaleAttributeTag = TagOf(PrintScaleTagBox),
                DefaultBuilding = BuildingValueBox.Text?.Trim(),
                DefaultSheetNumber = SheetNumberValueBox.Text?.Trim(),
                DefaultSheetName = SheetNameValueBox.Text?.Trim(),
                DefaultPrintScale = PrintScaleValueBox.Text?.Trim()
            };
            DialogResult = true;
        }

        private void PaperSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ExtensionBox == null) return;
            var paper = ItemText(PaperSizeBox);
            var previous = ItemText(ExtensionBox);
            ExtensionBox.Items.Clear();
            ExtensionBox.Items.Add(new ComboBoxItem { Content = "无加长" });
            foreach (var extension in PaperSizeCatalog.GetSupportedExtensions(paper).Where(x => !string.IsNullOrWhiteSpace(x)))
                ExtensionBox.Items.Add(new ComboBoxItem { Content = extension });
            SelectComboByText(ExtensionBox, string.IsNullOrWhiteSpace(previous) ? "无加长" : previous);
        }

        private void TagBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var box = sender as ComboBox;
            if (box == null) return;
            if (box == BuildingTagBox) FillValueFromSelectedTag(box, BuildingValueBox);
            else if (box == SheetNumberTagBox) FillValueFromSelectedTag(box, SheetNumberValueBox);
            else if (box == SheetNameTagBox) FillValueFromSelectedTag(box, SheetNameValueBox);
            else if (box == PrintScaleTagBox) FillValueFromSelectedTag(box, PrintScaleValueBox);
        }

        private void FillValueFromSelectedTag(ComboBox tagBox, TextBox valueBox)
        {
            var tag = TagOf(tagBox);
            if (!string.IsNullOrWhiteSpace(tag) && _attributeValues.TryGetValue(tag, out var value) && !string.IsNullOrWhiteSpace(value))
                valueBox.Text = value.Trim();
        }

        private IEnumerable<ComboBox> TagBoxes()
        {
            return new[] { BuildingTagBox, SheetNumberTagBox, SheetNameTagBox, PrintScaleTagBox };
        }

        private static string ItemText(ComboBox box)
        {
            return (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? box.Text;
        }

        private static string TagOf(ComboBox box)
        {
            var value = box.SelectedItem?.ToString();
            return value == DoNotRead ? string.Empty : value;
        }

        private static void SelectComboByText(ComboBox box, string text)
        {
            foreach (ComboBoxItem item in box.Items)
            {
                if (string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            if (box.IsEditable) box.Text = text;
            else box.SelectedIndex = 0;
        }

        private static void SelectStoredTag(ComboBox box, string tag)
        {
            box.SelectedItem = string.IsNullOrWhiteSpace(tag) ? DoNotRead : tag;
        }

        private static void SelectTag(ComboBox box, IEnumerable<string> tags, params string[] aliases)
        {
            box.SelectedItem = tags.FirstOrDefault(t => aliases.Any(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase))) ?? DoNotRead;
        }
    }
}
