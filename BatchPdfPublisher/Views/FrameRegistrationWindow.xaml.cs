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
        public FrameDefinition Definition { get; private set; }

        public FrameRegistrationWindow(string blockName, FrameSizeGuess guess, IEnumerable<string> attributeTags)
        {
            InitializeComponent();
            BlockNameBox.Text = blockName;
            MeasuredSizeText.Text = guess.MeasuredSize + "（已智能建议，可手动修改）";
            SelectComboByText(PaperSizeBox, guess.PaperSize);
            SelectComboByText(ExtensionBox, string.IsNullOrWhiteSpace(guess.Extension) ? "无加长" : guess.Extension);
            var tags = new[] { "（不读取）" }.Concat(attributeTags).ToList();
            foreach (var box in new[] { BuildingTagBox, SheetNumberTagBox, SheetNameTagBox, PrintScaleTagBox }) box.ItemsSource = tags;
            SelectTag(BuildingTagBox, tags, "楼栋", "BUILDING", "栋号");
            SelectTag(SheetNumberTagBox, tags, "图号", "SHEETNO", "SHEET_NO", "DRAWINGNO");
            SelectTag(SheetNameTagBox, tags, "图名", "SHEETNAME", "SHEET_NAME", "DRAWINGNAME");
            SelectTag(PrintScaleTagBox, tags, "比例", "SCALE", "PRINTSCALE");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var paper = (PaperSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(paper)) { MessageBox.Show("请选择纸张规格。", "登记图框", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var extension = (ExtensionBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            Definition = new FrameDefinition { BlockName = BlockNameBox.Text, PaperSize = paper, Extension = extension == "无加长" ? string.Empty : extension, BuildingAttributeTag = TagOf(BuildingTagBox), SheetNumberAttributeTag = TagOf(SheetNumberTagBox), SheetNameAttributeTag = TagOf(SheetNameTagBox), PrintScaleAttributeTag = TagOf(PrintScaleTagBox) };
            DialogResult = true;
        }

        private static string TagOf(ComboBox box) => box.SelectedItem?.ToString() == "（不读取）" ? string.Empty : box.SelectedItem?.ToString();
        private static void SelectComboByText(ComboBox box, string text) { foreach (ComboBoxItem item in box.Items) if (item.Content.ToString() == text) { box.SelectedItem = item; return; } box.SelectedIndex = 0; }
        private static void SelectTag(ComboBox box, IEnumerable<string> tags, params string[] aliases) { box.SelectedItem = tags.FirstOrDefault(t => aliases.Any(a => string.Equals(a, t, System.StringComparison.OrdinalIgnoreCase))) ?? "（不读取）"; }
    }
}
