using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using WL.Stair.Core.Calculation;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Geometry;

namespace WL.Stair.Cad2022
{
    internal sealed class StairSettingsForm : Form
    {
        private readonly NumericUpDown _floorCount = CreateNumber(2, 30, 3, 1);
        private readonly NumericUpDown _floorHeight = CreateNumber(2200, 6000, 3000, 10);
        private readonly NumericUpDown _flightWidth = CreateNumber(700, 3000, 1150, 10);
        private readonly NumericUpDown _stairwellWidth = CreateNumber(0, 2000, 100, 10);
        private readonly NumericUpDown _treadDepth = CreateNumber(200, 400, 280, 5);
        private readonly NumericUpDown _floorLandingUp = CreateNumber(300, 4000, 1200, 10);
        private readonly NumericUpDown _floorLandingDown = CreateNumber(300, 4000, 1200, 10);
        private readonly NumericUpDown _intermediateLandingUp = CreateNumber(300, 4000, 1200, 10);
        private readonly NumericUpDown _intermediateLandingDown = CreateNumber(300, 4000, 1200, 10);
        private readonly NumericUpDown _flightSlabThickness = CreateNumber(80, 400, 120, 5);
        private readonly NumericUpDown _landingSlabThickness = CreateNumber(80, 400, 120, 5);
        private readonly NumericUpDown _floorSlabThickness = CreateNumber(80, 600, 120, 5);
        private readonly Label _calculationSummary = new Label();
        private readonly PreviewPanel _preview = new PreviewPanel();

        public StairSettingsForm()
        {
            Text = "万落建筑 - 楼梯设置";
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(980, 680);
            ClientSize = new Size(1080, 720);
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.Controls.Add(BuildParameterPanel(), 0, 0);
            layout.Controls.Add(BuildPreviewPanel(), 1, 0);
            Controls.Add(layout);

            foreach (var input in Inputs())
            {
                input.ValueChanged += delegate { RefreshPreview(); };
            }

            RefreshPreview();
        }

        public StairDefinition Definition { get; private set; }

        public int FloorCount { get; private set; }

        private Control BuildParameterPanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(16),
                ColumnCount = 3,
                RowCount = 16
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18F));

            AddRow(panel, 0, "层数（标高数量）", CreateInteractiveInput(_floorCount), "层");
            AddRow(panel, 1, "层高", CreateInteractiveInput(_floorHeight), "mm");
            AddRow(panel, 2, "梯段宽度", CreateInteractiveInput(_flightWidth), "mm");
            AddRow(panel, 3, "梯井宽度", CreateInteractiveInput(_stairwellWidth), "mm");
            AddRow(panel, 4, "踏步宽度", CreateInteractiveInput(_treadDepth), "mm");
            AddRow(panel, 5, "楼板接向上梯段", CreateInteractiveInput(_floorLandingUp), "mm");
            AddRow(panel, 6, "楼板接向下梯段", CreateInteractiveInput(_floorLandingDown), "mm");
            AddRow(panel, 7, "休息平台接向上梯段", CreateInteractiveInput(_intermediateLandingUp), "mm");
            AddRow(panel, 8, "休息平台接向下梯段", CreateInteractiveInput(_intermediateLandingDown), "mm");
            AddRow(panel, 9, "梯板厚度", CreateInteractiveInput(_flightSlabThickness), "mm");
            AddRow(panel, 10, "休息平台厚度", CreateInteractiveInput(_landingSlabThickness), "mm");
            AddRow(panel, 11, "楼板厚度", CreateInteractiveInput(_floorSlabThickness), "mm");

            _calculationSummary.AutoSize = true;
            _calculationSummary.ForeColor = Color.FromArgb(38, 93, 141);
            _calculationSummary.Margin = new Padding(3, 10, 3, 8);
            panel.Controls.Add(_calculationSummary, 0, 12);
            panel.SetColumnSpan(_calculationSummary, 3);

            var note = new Label
            {
                AutoSize = true,
                ForeColor = Color.DimGray,
                Text = "修改数值时右侧剖面立即更新；踢面数按每层层高自动计算。"
            };
            panel.Controls.Add(note, 0, 13);
            panel.SetColumnSpan(note, 3);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true
            };
            var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 88 };
            var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88 };
            okButton.Click += ValidateBeforeClose;
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            panel.Controls.Add(buttons, 0, 14);
            panel.SetColumnSpan(buttons, 3);

            AcceptButton = okButton;
            CancelButton = cancelButton;
            return panel;
        }

        private Control BuildPreviewPanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 34,
                Font = new Font(Font, FontStyle.Bold),
                Text = "实时剖面预览（实线=剖到，虚线=后方）"
            };
            _preview.Dock = DockStyle.Fill;
            _preview.BackColor = Color.FromArgb(24, 27, 31);
            panel.Controls.Add(_preview);
            panel.Controls.Add(title);
            return panel;
        }

        private void RefreshPreview()
        {
            StairDefinition definition;
            StairCalculationOutcome outcome;
            if (!TryCreateDefinition(out definition, out outcome) || !outcome.IsSuccess)
            {
                _preview.View = null;
                _calculationSummary.Text = "当前参数无法生成楼梯。";
                return;
            }

            _preview.View = new StairGeometryBuilder().BuildMultiFloorSection(
                definition,
                outcome.Result,
                (int)_floorCount.Value);
            _calculationSummary.Text = string.Format(
                CultureInfo.CurrentCulture,
                "自动计算：每层 {0} 个踢面，踢面高 {1:0.0} mm；下段 {2}，上段 {3}",
                outcome.Result.TotalRiserCount,
                outcome.Result.RiserHeight,
                outcome.Result.FirstFlight.RiserCount,
                outcome.Result.SecondFlight.RiserCount);
        }

        private void ValidateBeforeClose(object sender, EventArgs eventArgs)
        {
            StairDefinition definition;
            StairCalculationOutcome outcome;
            if (!TryCreateDefinition(out definition, out outcome) || !outcome.IsSuccess)
            {
                DialogResult = DialogResult.None;
                MessageBox.Show(
                    this,
                    outcome == null ? "参数无效。" : string.Join("\n", outcome.Issues.Select(issue => issue.Message)),
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            Definition = definition;
            FloorCount = (int)_floorCount.Value;
        }

        private bool TryCreateDefinition(out StairDefinition definition, out StairCalculationOutcome outcome)
        {
            definition = new StairDefinition(
                (double)_floorHeight.Value,
                (double)_flightWidth.Value,
                (double)_stairwellWidth.Value,
                (double)_intermediateLandingUp.Value,
                (double)_treadDepth.Value)
            {
                FloorLandingDepthUp = (double)_floorLandingUp.Value,
                FloorLandingDepthDown = (double)_floorLandingDown.Value,
                IntermediateLandingDepthUp = (double)_intermediateLandingUp.Value,
                IntermediateLandingDepthDown = (double)_intermediateLandingDown.Value,
                FlightSlabThickness = (double)_flightSlabThickness.Value,
                LandingSlabThickness = (double)_landingSlabThickness.Value,
                FloorSlabThickness = (double)_floorSlabThickness.Value,
                TotalRiserCount = null
            };
            outcome = new StairCalculator().Calculate(definition);
            return true;
        }

        private IEnumerable<NumericUpDown> Inputs()
        {
            yield return _floorCount;
            yield return _floorHeight;
            yield return _flightWidth;
            yield return _stairwellWidth;
            yield return _treadDepth;
            yield return _floorLandingUp;
            yield return _floorLandingDown;
            yield return _intermediateLandingUp;
            yield return _intermediateLandingDown;
            yield return _flightSlabThickness;
            yield return _landingSlabThickness;
            yield return _floorSlabThickness;
        }

        private static NumericUpDown CreateNumber(decimal minimum, decimal maximum, decimal value, decimal increment)
        {
            return new NumericUpDown
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                Increment = increment,
                DecimalPlaces = 0,
                ThousandsSeparator = true,
                Dock = DockStyle.Fill
            };
        }

        private static Control CreateInteractiveInput(NumericUpDown number)
        {
            var slider = new TrackBar
            {
                Minimum = decimal.ToInt32(number.Minimum),
                Maximum = decimal.ToInt32(number.Maximum),
                Value = decimal.ToInt32(number.Value),
                SmallChange = Math.Max(1, decimal.ToInt32(number.Increment)),
                LargeChange = Math.Max(1, decimal.ToInt32(number.Increment * 5)),
                TickStyle = TickStyle.None,
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            slider.ValueChanged += delegate
            {
                var value = Math.Min(number.Maximum, Math.Max(number.Minimum, slider.Value));
                if (number.Value != value)
                {
                    number.Value = value;
                }
            };
            number.ValueChanged += delegate
            {
                var value = Math.Min(slider.Maximum, Math.Max(slider.Minimum, decimal.ToInt32(number.Value)));
                if (slider.Value != value)
                {
                    slider.Value = value;
                }
            };

            var editor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0)
            };
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 25F));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 23F));
            editor.Controls.Add(number, 0, 0);
            editor.Controls.Add(slider, 0, 1);
            return editor;
        }

        private static void AddRow(
            TableLayoutPanel panel,
            int row,
            string label,
            Control input,
            string unit)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            panel.Controls.Add(new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true }, 0, row);
            panel.Controls.Add(input, 1, row);
            panel.Controls.Add(new Label { Text = unit, Anchor = AnchorStyles.Left, AutoSize = true }, 2, row);
        }

        private sealed class PreviewPanel : Panel
        {
            private DrawingView _view;

            public DrawingView View
            {
                get { return _view; }
                set
                {
                    _view = value;
                    Invalidate();
                }
            }

            public PreviewPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                base.OnPaint(eventArgs);
                if (_view == null || _view.Lines.Count == 0)
                {
                    return;
                }

                var points = _view.Lines.SelectMany(line => new[] { line.Start, line.End }).ToArray();
                var minX = points.Min(point => point.X);
                var maxX = points.Max(point => point.X);
                var minY = points.Min(point => point.Y);
                var maxY = points.Max(point => point.Y);
                var width = Math.Max(1.0, maxX - minX);
                var height = Math.Max(1.0, maxY - minY);
                var scale = Math.Min((ClientSize.Width - 36.0) / width, (ClientSize.Height - 36.0) / height);

                using (var visiblePen = new Pen(Color.WhiteSmoke, 1.5F))
                using (var cutPen = new Pen(Color.OrangeRed, 2.4F))
                using (var hiddenPen = new Pen(Color.Cyan, 1.2F) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                {
                    foreach (var line in _view.Lines)
                    {
                        var pen = line.IsHidden
                            ? hiddenPen
                            : line.Role == StairLineRole.CutBoundary
                                || line.Role == StairLineRole.CutFlightProfile ? cutPen : visiblePen;
                        eventArgs.Graphics.DrawLine(
                            pen,
                            Transform(line.Start, minX, minY, maxY, scale),
                            Transform(line.End, minX, minY, maxY, scale));
                    }
                }
            }

            private static PointF Transform(
                Point2D point,
                double minX,
                double minY,
                double maxY,
                double scale)
            {
                return new PointF(
                    (float)(18.0 + ((point.X - minX) * scale)),
                    (float)(18.0 + ((maxY - point.Y) * scale)));
            }
        }
    }
}
