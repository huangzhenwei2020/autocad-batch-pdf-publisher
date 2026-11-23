using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    public partial class BppFrameManagerWindow : Window
    {
        private readonly PublisherViewModel _viewModel;
        internal enum CadAction
        {
            None,
            Pick,
            Create,
            Edit
        }

        internal CadAction RequestedAction { get; private set; }

        public BppFrameManagerWindow(PublisherViewModel viewModel)
        {
            _viewModel = viewModel;
            InitializeComponent();
            RefreshFrames();
        }

        private void RefreshFrames()
        {
            var selected = Frames.SelectedItem;
            Frames.ItemsSource = _viewModel.Frames.Where(frame => string.IsNullOrWhiteSpace(Search.Text) || ((frame.DisplayName ?? string.Empty) + frame.Note).IndexOf(Search.Text, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            Frames.SelectedItem = selected;
            if (Frames.SelectedIndex < 0 && Frames.Items.Count > 0)
                Frames.SelectedIndex = 0;
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            if (Frames != null)
                RefreshFrames();
        }

        private void Frame_Changed(object sender, SelectionChangedEventArgs e)
        {
            var frame = Frames.SelectedItem as FrameDefinition;
            Details.Text = frame == null ? "请选择图框" : frame.BlockName + "\n\n纸张  " + frame.PaperDisplay + "     " + frame.PaperOrientation + "\n默认比例  " + frame.DefaultPrintScale + "\n备注  " + frame.Note;
            Range.Text = FrameLayoutRangeService.Describe(frame);
            Mapping.ItemsSource = frame == null ? null : new[]
            {
                new MappingRow("子项目", frame.BuildingAttributeTag, frame.DefaultBuilding),
                new MappingRow("图号", frame.SheetNumberAttributeTag, frame.DefaultSheetNumber),
                new MappingRow("图名", frame.SheetNameAttributeTag, frame.DefaultSheetName),
                new MappingRow("打印比例", frame.PrintScaleAttributeTag, frame.DefaultPrintScale),
                new MappingRow("序号／页码", frame.PageNumberAttributeTag, frame.DefaultPageNumber)
            };
        }

        private void Pick_Click(object sender, RoutedEventArgs e)
        {
            RequestedAction = CadAction.Pick;
            Close();
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            RequestedAction = CadAction.Create;
            Close();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (!(Frames.SelectedItem is FrameDefinition frame))
                return;
            _viewModel.SelectedFrame = frame;
            RequestedAction = CadAction.Edit;
            Close();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.SaveFrameLibraryCommand.Execute(null);
        }

        private void Company_Click(object sender, RoutedEventArgs e)
        {
            if (!(Frames.SelectedItem is FrameDefinition frame))
            {
                CompanyStatus.Text = "请先选中一项已登记图框。";
                return;
            }

            try
            {
                var dialog = new BppCompanyFrameWindow(frame, _viewModel.Frames.ToList());
                if (BppDialog.Show(dialog, this) == true)
                {
                    var library = dialog.SavedLibrary;
                    var missingTemplates = library.Frames.Count(item => FrameTemplatePaths.ExistingSnapshot(item) == null);
                    CompanyStatus.Text = "已保存到「" + library.Name + "」公司图框库（共 " + library.Frames.Count
                        + " 项）。在「项目管理」中为项目选用。"
                        + (missingTemplates == 0 ? string.Empty : " 库中仍有 " + missingTemplates + " 项历史登记待补齐模板。");
                }
            }
            catch (Exception error)
            {
                CompanyStatus.Text = "公司图框库读取失败：" + error.Message;
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!(Frames.SelectedItem is FrameDefinition frame))
                return;
            if (MessageBox.Show(this, "删除图框登记“" + frame.BlockName + "”？", "图框登记", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            _viewModel.SelectedFrame = frame;
            _viewModel.RemoveFrameCommand.Execute(null);
            RefreshFrames();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private sealed class MappingRow
        {
            public string Name { get; }
            public string Tag { get; }
            public string Value { get; }

            internal MappingRow(string name, string tag, string value)
            {
                Name = name;
                Tag = tag;
                Value = value;
            }
        }
    }
}
