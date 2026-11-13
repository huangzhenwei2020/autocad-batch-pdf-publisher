using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace BatchPdfPublisher.Views
{
    public partial class BppFrameCreationWindow : BppCadWindow
    {
        private readonly Action _refresh;
        private bool _loading = true;
        private AcColor _color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
        private readonly Dictionary<string, string> _settings;
        private static string SettingsPath => UserDataPaths.SettingsFile("frame-creation.settings", "BatchPdfPublisher.frame-creation.settings");

        public BppFrameCreationWindow(Document document, Action refresh)
        {
            _refresh = refresh;
            BindDocument(document);
            InitializeComponent();
            _settings = BppSettings.Read(SettingsPath);
            Paper.ItemsSource = new[]
            {
                "A0",
                "A1",
                "A2",
                "A3",
                "A4"
            };
            Orientation.ItemsSource = new[]
            {
                "横向",
                "纵向"
            };
            Property.ItemsSource = new[]
            {
                "工程名称",
                "子项目名称",
                "图纸名称",
                "设计编号",
                "设计阶段",
                "图号",
                "序号",
                "纸张",
                "比例",
                "日期",
                "版本"
            };
            FontName.ItemsSource = FrameCreationService.GetTextStyleNames(document);
            InsertScale.ItemsSource = new[]
            {
                "1:1",
                "1:50",
                "1:75",
                "1:100",
                "1:150",
                "1:200"
            };
            Paper.SelectedItem = Value("Paper", "A1");
            RefreshExtensions();
            Extension.Text = Value("Extension", "无加长");
            Orientation.SelectedItem = Value("Orientation", PaperSizeCatalog.DefaultOrientation(Paper.Text));
            Property.Text = Value("Property", "图纸名称");
            FontName.Text = Value("Font", FontName.Items.Cast<string>().FirstOrDefault() ?? "Standard");
            TextHeight.Text = Value("Height", "3.5");
            WidthFactor.Text = Value("WidthFactor", "1");
            Remark.Text = Value("Remark", "自建图框");
            RegisterAfterCreation.IsChecked = Value("Register", "1") == "1";
            InsertScale.Text = Value("InsertScale", "1:100");
            if (Value("ColorMethod", "ByAci") == "ByColor" && byte.TryParse(Value("ColorR", "255"), out var red) && byte.TryParse(Value("ColorG", "255"), out var green) && byte.TryParse(Value("ColorB", "255"), out var blue))
                _color = AcColor.FromRgb(red, green, blue);
            else if (short.TryParse(Value("ColorIndex", "7"), out var index))
                _color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, index);
            ReloadFrames();
            ColorName.Text = _color.ToString();
            _loading = false;
            RefreshPreview();
            Closed += (sender, args) =>
            {
                try
                {
                    SaveSettings();
                }
                catch (System.IO.IOException error)
                {
                    MessageBox.Show(error.Message, "图框参数保存");
                }
                catch (UnauthorizedAccessException error)
                {
                    MessageBox.Show(error.Message, "图框参数保存");
                }
            };
        }

        private string Value(string key, string fallback) => BppSettings.Value(_settings, key, fallback);
        private void RefreshExtensions() => Extension.ItemsSource = new[]
        {
            "无加长"
        }.Concat(PaperSizeCatalog.GetSupportedExtensions(Paper.Text).Where(value => !string.IsNullOrWhiteSpace(value))).ToList();
        private string ExtensionValue => Extension.Text == "无加长" ? string.Empty : Extension.Text;
        private double[] PaperSize => PaperSizeCatalog.GetSize(Paper.Text, ExtensionValue, Orientation.Text);

        private void Paper_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;
            RefreshExtensions();
            Extension.Text = "无加长";
            Orientation.SelectedItem = PaperSizeCatalog.DefaultOrientation(Paper.Text);
            RefreshPreview();
        }

        private void Size_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading)
                Dispatcher.BeginInvoke(new Action(RefreshPreview));
        }

        private void Size_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => RefreshPreview();
        private void RefreshPreview()
        {
            try
            {
                var size = PaperSize;
                PaperPreview.Width = PaperOutline.Width = size[0];
                PaperPreview.Height = PaperOutline.Height = size[1];
                PaperInner.Width = Math.Max(1, size[0] - 35);
                PaperInner.Height = Math.Max(1, size[1] - 20);
                SizeInfo.Text = Paper.Text + (string.IsNullOrWhiteSpace(ExtensionValue) ? string.Empty : "+" + ExtensionValue) + "  " + size[0] + " × " + size[1] + " mm";
            }
            catch (ArgumentException error)
            {
                SizeInfo.Text = error.Message;
            }
        }

        private void ReloadFrames()
        {
            var selected = Frames.SelectedItem as FrameDefinition;
            var frames = new PublishPlanStore().LoadFrames();
            Frames.ItemsSource = frames;
            Frames.SelectedItem = frames.FirstOrDefault(frame => frame.RegistrationId == (selected?.RegistrationId ?? Value("InsertFrameId", string.Empty))) ?? frames.FirstOrDefault(frame => frame.BlockName == Value("InsertFrameBlock", string.Empty)) ?? frames.FirstOrDefault();
        }

        private void Frame_Changed(object sender, SelectionChangedEventArgs e)
        {
            var frame = Frames.SelectedItem as FrameDefinition;
            FrameInfo.Text = frame == null ? "请选择已登记图框" : frame.DisplayName + "\n" + frame.Note + "\n" + FrameLayoutRangeService.Describe(frame);
        }

        private double Positive(string text, string name)
        {
            if (!double.TryParse(text, out var value) || double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new InvalidOperationException("请输入有效的" + name + "。");
            return value;
        }

        private void Execute(Action action)
        {
            try
            {
                SaveSettings();
                RunCad(action, () =>
                {
                    ReloadFrames();
                    _refresh?.Invoke();
                });
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Title);
            }
        }

        private void Border_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var size = PaperSize;
                Execute(() => FrameCreationService.InsertBorder(SourceDocument, size[0], size[1]));
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Title);
            }
        }

        private void Property_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var height = Positive(TextHeight.Text, "文字高度");
                var width = Positive(WidthFactor.Text, "宽度系数");
                var property = Property.Text;
                var font = FontName.Text;
                var color = _color;
                Execute(() => FrameCreationService.InsertCenteredProperty(SourceDocument, property, font, height, width, color));
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Title);
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var size = PaperSize;
                var height = Positive(TextHeight.Text, "文字高度");
                var font = FontName.Text;
                var remark = Remark.Text;
                var register = RegisterAfterCreation.IsChecked == true;
                Execute(() =>
                {
                    var name = FrameCreationService.CreateFrameBlockFromSelection(SourceDocument, string.Empty, size[0], size[1], font, height, remark, out var error, out var paper, out var extension, out var orientation, out var id);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                            throw new InvalidOperationException(error);
                        return;
                    }

                    if (register && !new FrameRegistrationService().RegisterCreated(SourceDocument, id, remark, out var registrationError))
                        throw new InvalidOperationException(registrationError);
                });
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Title);
            }
        }

        private void Insert_Click(object sender, RoutedEventArgs e)
        {
            if (!(Frames.SelectedItem is FrameDefinition frame))
                return;
            try
            {
                var text = InsertScale.Text.Trim().Replace('：', ':').Split(':').Last();
                if (!int.TryParse(text, out var scale) || scale <= 0)
                    throw new InvalidOperationException("请输入有效插入比例。");
                bool prepared;
                string templateError;
                using (SourceDocument.LockDocument())
                    prepared = FrameTemplateStore.TryPrepare(SourceDocument, frame, out templateError);
                if (!prepared)
                {
                    if (MessageBox.Show(this, templateError + "\n\n选择含原图框的 DWG 补齐模板？", "补齐图框模板", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        return;
                    var chooser = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "选择包含“" + frame.BlockName + "”的图纸",
                        Filter = "CAD 图纸|*.dwg"
                    };
                    if (chooser.ShowDialog(this) != true)
                        return;
                    if (!FrameTemplateStore.TryRecover(frame, new[] { chooser.FileName }, out templateError))
                        throw new InvalidOperationException(templateError);
                }

                Execute(() =>
                {
                    if (!FrameCreationService.InsertRegisteredFrame(SourceDocument, frame, scale))
                        return;
                });
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Title);
            }
        }

        private void Range_Click(object sender, RoutedEventArgs e)
        {
            if (Frames.SelectedItem is FrameDefinition frame)
                Execute(() => FrameLayoutRangeService.PromptAndSaveRange(SourceDocument, frame));
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

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void SaveSettings()
        {
            var frame = Frames.SelectedItem as FrameDefinition;
            _settings["Paper"] = Paper.Text;
            _settings["Extension"] = Extension.Text;
            _settings["Orientation"] = Orientation.Text;
            _settings["Property"] = Property.Text;
            _settings["Font"] = FontName.Text;
            _settings["Height"] = TextHeight.Text;
            _settings["WidthFactor"] = WidthFactor.Text;
            _settings["Remark"] = Remark.Text;
            _settings["Register"] = RegisterAfterCreation.IsChecked == true ? "1" : "0";
            _settings["InsertScale"] = InsertScale.Text;
            _settings["InsertFrameId"] = frame?.RegistrationId ?? string.Empty;
            _settings["InsertFrameBlock"] = frame?.BlockName ?? string.Empty;
            _settings["ColorMethod"] = _color.ColorMethod.ToString();
            _settings["ColorIndex"] = _color.ColorIndex.ToString();
            _settings["ColorR"] = _color.Red.ToString();
            _settings["ColorG"] = _color.Green.ToString();
            _settings["ColorB"] = _color.Blue.ToString();
            BppSettings.Save(SettingsPath, _settings);
        }
    }
}
