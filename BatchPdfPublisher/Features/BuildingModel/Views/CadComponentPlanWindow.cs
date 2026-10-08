using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadComponentPlanSettings
    {
        public string BlockHandle, Code, Name, Category, Axis, DoorAssembly, SourceHash;
        public double X, Y, Z, Width, Height, MillimetresPerCadUnit;
        public List<ComponentPlanPart> Parts = new List<ComponentPlanPart>();
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(BlockHandle)) throw new InvalidDataException("请先选择整樘门窗的平面块。");
            if (Axis != "X" && Axis != "Y") throw new InvalidDataException("方向只能选择 X 或 Y。");
            if (Category != "Door" && Category != "Window") throw new InvalidDataException("门窗类别无效。");
            if (new[] { X, Y, Z }.Any(v => double.IsNaN(v) || double.IsInfinity(v))) throw new InvalidDataException("基点坐标必须是有效数字。");
            new ComponentPlanFrame(0, 0, 0, Axis == "X" ? 1 : 0, Axis == "Y" ? 1 : 0, MillimetresPerCadUnit);
            ComponentPlanSymbols.Validate(new ComponentPlanSymbol { SchemaVersion = Category == "Door" ? 3 : 1,
                Code = Code, Name = string.IsNullOrWhiteSpace(Name) ? Code : Name, Category = Category,
                DoorAssembly = Category == "Door" ? DoorAssembly ?? "SingleSwing" : null,
                Parts = Category == "Door" ? new List<ComponentPlanPart>() : null,
                Width = Width, Height = Height, Primitives = { new ComponentPlanPrimitive { Kind = "Line", X2 = 1 } } });
        }
    }
    public sealed class CadComponentPlanBlock { public string Handle, Name; public double[] BasePoint; }
    public sealed class CadComponentPlanWindow : Window
    {
        private static readonly Brush Ink = Brush("#E4EDF5"), Muted = Brush("#95AABD"), Edge = Brush("#344C60"), Accent = Brush("#51BAEF");
        private readonly TabControl _tabs = new TabControl();
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Dictionary<string, Editor> _editors = new Dictionary<string, Editor>();
        private readonly Func<Window, CadComponentPlanBlock> _pickBlock;
        private readonly Func<Window, double[]> _pickBase;
        private readonly Func<Window, CadComponentPlanSettings, string> _export;
        private readonly Func<Window, CadComponentPlanSettings, ComponentPlanSymbol> _inspect;
        private sealed class Editor
        {
            public string Kind;
            public List<ComponentPlanPart> Parts = new List<ComponentPlanPart>();
            public ListBox List = new ListBox();
            public CadComponentPartPreview Preview = new CadComponentPartPreview();
            public TextBlock Current = new TextBlock(), Summary = new TextBlock();
            public ComponentPlanSymbol Plan;
            public string FrameKey;
        }
        public CadComponentPlanWindow(string category, Func<Window, CadComponentPlanBlock> pickBlock,
            Func<Window, double[]> pickBase, Func<Window, CadComponentPlanSettings, string> export,
            string actionCaption = "平面入库", Func<Window, CadComponentPlanSettings, ComponentPlanSymbol> inspect = null)
        {
            _pickBlock = pickBlock; _pickBase = pickBase; _export = export; _inspect = inspect;
            NameScope.SetNameScope(this, new NameScope());
            Title = "图库 · 门窗平面入库"; Width = 1240; Height = 840; MinWidth = 960; MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14; Foreground = Ink;
            Background = Brush("#101923"); UseLayoutRounding = true;
            ApplyTheme();
            var root = new Grid { Margin = new Thickness(16) };
            foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new RowDefinition { Height = height });
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            heading.Children.Add(new TextBlock { Text = "门窗平面入库", FontSize = 22, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text = "选择整樘平面，再标注部件。CAD 与三维沿用同一份部件记录。", Foreground = Muted, Margin = new Thickness(0, 5, 0, 0) }); root.Children.Add(heading);
            _tabs.Background = Background; _tabs.BorderBrush = Edge;
            foreach (var kind in new[] { "Door", "Window" }) _tabs.Items.Add(new TabItem { Header = kind == "Door" ? "装修门" : "窗", Foreground = Ink, Background = Brush("#1C2C3A"), Padding = new Thickness(24, 6, 24, 6), Content = BuildEditor(kind) });
            _tabs.SelectedIndex = category == "Door" ? 0 : 1;
            _tabs.SelectionChanged += (s, e) => { if (e.Source == _tabs) _status.Text = ""; };
            Grid.SetRow(_tabs, 1); root.Children.Add(_tabs); Grid.SetRow(_status, 2); root.Children.Add(_status);
            var actions = new Grid { Margin = new Thickness(0, 10, 0, 0) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.Children.Add(new TextBlock { Text = "资源先命名；项目编号在选型和尺寸确定后生成。", Foreground = Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var save = Tool(actionCaption, "Export", () => Run(() => {
                var settings = ReadSettings(); var editor = _editors[settings.Category];
                if (_inspect != null && (editor.Plan == null || editor.FrameKey != FrameKey(settings))) throw new InvalidDataException("基点、方向或单位已变化，请先刷新平面预览，核对当前部件后入库。");
                var result = _export?.Invoke(this, settings); if (result != null) { _status.Foreground = Brush("#70D2AC"); _status.Text = result; }
            })); save.Background = Brush("#126A9E"); buttons.Children.Add(save);
            var close = Tool("关闭", "Close", Close); close.IsCancel = true; buttons.Children.Add(close); Grid.SetColumn(buttons, 1); actions.Children.Add(buttons); Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;
        }
        private Grid BuildEditor(string kind)
        {
            var editor = new Editor { Kind = kind }; _editors.Add(kind, editor);
            var body = new Grid { Margin = new Thickness(10) }; body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(kind == "Door" ? 205 : 0) }); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            var left = new Grid { Margin = new Thickness(0, 0, 10, 0) }; left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition()); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.Children.Add(new TextBlock { Text = "入库部件", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 6, 0, 10) });
            editor.List.Background = Brush("#172532"); editor.List.Foreground = Ink; editor.List.BorderBrush = Edge; editor.List.SelectionChanged += (_, __) => UpdatePart(editor); Grid.SetRow(editor.List, 1); left.Children.Add(editor.List);
            var add = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; var role = new ComboBox { ItemsSource = ComponentPlanSymbols.PartRoleNames, SelectedIndex = 2, Height = 34 }; RegisterName(kind + "NewRole", role); add.Children.Add(role);
            add.Children.Add(Tool("＋ 添加部件", kind + "AddPart", () => { if (editor.Parts.Count >= 256) { _status.Text = "部件数量已达上限。"; return; } var index = role.SelectedIndex; if (index < 0) return; var part = new ComponentPlanPart { Role = ComponentPlanSymbols.PartRoles[index], Name = ComponentPlanSymbols.PartRoleNames[index] + " " + (editor.Parts.Count + 1) }; editor.Parts.Add(part); RefreshParts(editor, part); }));
            add.Children.Add(Tool("清空当前标注", kind + "ClearPart", () => { var part = SelectedPart(editor); if (part != null) { part.Primitives.Clear(); RefreshParts(editor, part); } })); Grid.SetRow(add, 2); left.Children.Add(add); left.Visibility = kind == "Door" ? Visibility.Visible : Visibility.Collapsed; body.Children.Add(left);
            add.Children.Add(Tool("删除当前部件", kind + "DeletePart", () => Run(DeleteSelectedPart)));
            var center = new Grid { Margin = new Thickness(0, 0, 10, 0) }; center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); center.RowDefinitions.Add(new RowDefinition()); center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            editor.Current.FontSize = 17; editor.Current.FontWeight = FontWeights.SemiBold; editor.Current.Foreground = Accent; editor.Current.Margin = new Thickness(12); center.Children.Add(new Border { Background = Brush("#193A50"), CornerRadius = new CornerRadius(5), Child = editor.Current });
            editor.Preview.AssignRequested = (indices, remove) => Run(() => Assign(editor, indices, remove)); RegisterName(kind + "Preview", editor.Preview); Grid.SetRow(editor.Preview, 1); editor.Preview.Margin = new Thickness(0, 8, 0, 8); center.Children.Add(editor.Preview);
            var help = new StackPanel(); editor.Summary.Foreground = Muted; editor.Summary.TextWrapping = TextWrapping.Wrap; help.Children.Add(editor.Summary); help.Children.Add(new TextBlock { Text = kind == "Door" ? "点击线条标注 · 拖框批量标注 · 右键取消当前部件的标注\n亮色线是当前部件；开启弧请归入“开启示意”。" : "保留原生 CAD 线条、圆弧和毫米尺寸。", Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }); Grid.SetRow(help, 2); center.Children.Add(help); Grid.SetColumn(center, 1); body.Children.Add(center);
            var fields = new StackPanel { Margin = new Thickness(10) }; var block = Input(kind + "Block", "未选择整樘平面"); block.IsReadOnly = true; Field(fields, "整樘平面", block);
            fields.Children.Add(Tool("▣ 选择平面块", kind + "PickBlock", () => Run(() => { var picked = _pickBlock?.Invoke(this); if (picked == null) return; var changed = !string.Equals((string)block.Tag, picked.Handle, StringComparison.Ordinal); block.Tag = picked.Handle; block.Text = picked.Name; if (changed) {
                editor.Plan = null;
                foreach (var part in editor.Parts) part.Primitives.Clear();
                if (picked.BasePoint != null && picked.BasePoint.Length == 3)
                    for (var i = 0; i < 3; i++) ((TextBox)FindName(kind + new[] { "X", "Y", "Z" }[i])).Text = picked.BasePoint[i].ToString("G17", CultureInfo.InvariantCulture);
            } RefreshPreview(kind); })));
            Field(fields, "资源名称", Input(kind + "Name", kind == "Door" ? "单开装修门" : "窗平面"));
            if (kind == "Door") { var assembly = new ComboBox { ItemsSource = ComponentPlanSymbols.DoorAssemblyNames, SelectedIndex = 0, Height = 36 }; RegisterName(kind + "Assembly", assembly); Field(fields, "门扇组合 / 机构", assembly); }
            Field(fields, kind == "Door" ? "参考编号（可不填）" : "门窗编号", Input(kind + "Code", kind == "Door" ? "" : "C1216")); Field(fields, "参考洞口宽 · mm", Input(kind + "Width", kind == "Door" ? "900" : "1200")); Field(fields, "参考洞口高 · mm", Input(kind + "Height", kind == "Door" ? "2100" : "1600"));
            var coords = new Grid(); coords.ColumnDefinitions.Add(new ColumnDefinition()); coords.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); coords.Children.Add(Input(kind + "X", "0"));
            var pick = Tool("拾取", kind + "PickBase", () => Run(() => { var point = _pickBase?.Invoke(this); if (point == null) return; if (point.Length != 3) throw new InvalidDataException("基点坐标无效。"); for (var i = 0; i < 3; i++) ((TextBox)FindName(kind + new[] { "X", "Y", "Z" }[i])).Text = point[i].ToString("G17", CultureInfo.InvariantCulture); if (block.Tag != null) RefreshPreview(kind); })); Grid.SetColumn(pick, 1); coords.Children.Add(pick); Field(fields, "基点 X · CAD 坐标", coords);
            Field(fields, "基点 Y · CAD 坐标", Input(kind + "Y", "0")); Field(fields, "基点 Z · CAD 坐标", Input(kind + "Z", "0"));
            var axes = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var axis in new[] { "X", "Y" }) { var radio = new RadioButton { Content = axis, GroupName = kind + "Axis", IsChecked = axis == "X", Foreground = Ink, Margin = new Thickness(0, 6, 35, 6) }; RegisterName(kind + axis + "Axis", radio); axes.Children.Add(radio); } Field(fields, "沿墙方向（当前 UCS）", axes); Field(fields, "1 CAD 单位 = mm", Input(kind + "Units", "1"));
            fields.Children.Add(Tool("↻ 刷新平面预览", kind + "RefreshPreview", () => Run(() => RefreshPreview(kind))));
            var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Brush("#172532") }; RegisterName(kind + "Fields", scroll); Grid.SetColumn(scroll, 2); body.Children.Add(scroll);
            if (kind == "Door") foreach (var i in new[] { 8, 0, 1, 2, 3, 4, 5, 6 }) editor.Parts.Add(new ComponentPlanPart { Role = ComponentPlanSymbols.PartRoles[i], Name = ComponentPlanSymbols.PartRoleNames[i] }); RefreshParts(editor, editor.Parts.FirstOrDefault()); return body;
        }
        private static ComponentPlanPart SelectedPart(Editor editor) => (editor.List.SelectedItem as ListBoxItem)?.Tag as ComponentPlanPart;
        private void RefreshParts(Editor editor, ComponentPlanPart selected)
        {
            editor.List.Items.Clear(); foreach (var part in editor.Parts) { var row = new Grid { Margin = new Thickness(3), MinHeight = 50 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.Children.Add(new CadComponentPartPreview { Symbol = editor.Plan, PartGroups = editor.Parts, OnlyIndices = part.Primitives, IsHitTestVisible = false, Height = 42, Width = 44, ShowEmptyText = false }); var label = new TextBlock { Text = part.Name + "\n" + (part.Primitives.Count == 0 ? "未标注" : part.Primitives.Count + " 条图元"), TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(label, 1); row.Children.Add(label); var item = new ListBoxItem { Content = row, Tag = part, Foreground = Ink, HorizontalContentAlignment = HorizontalAlignment.Stretch }; editor.List.Items.Add(item); if (ReferenceEquals(part, selected)) editor.List.SelectedItem = item; } UpdatePart(editor);
        }
        private void UpdatePart(Editor editor)
        {
            var part = SelectedPart(editor); editor.Current.Text = editor.Kind == "Door" ? "当前标注 · " + (part?.Name ?? "请选择部件") : "窗平面 · 原生线条"; editor.Preview.Symbol = editor.Plan; editor.Preview.PartGroups = editor.Parts; editor.Preview.Highlight = part?.Primitives ?? new List<int>(); editor.Preview.InvalidateVisual(); var count = editor.Plan?.Primitives.Count ?? 0; var assigned = editor.Parts.Sum(p => p.Primitives.Count); editor.Summary.Text = count == 0 ? "先选择整樘平面块，预览后逐项标注。" : count + " 条图元 · " + assigned + " 条已标注 · " + (count - assigned) + " 条未分组\n未分组图元仍完整保留；本轮资源为固定规格、静态。";
        }
        private void Assign(Editor editor, IList<int> indices, bool remove)
        {
            var part = SelectedPart(editor); if (part == null || editor.Plan == null) return; var conflict = editor.Parts.FirstOrDefault(p => !ReferenceEquals(p, part) && p.Primitives.Intersect(indices).Any()); if (!remove && conflict != null) throw new InvalidDataException("所选线条已归属“" + conflict.Name + "”。先在该部件取消标注，再分配到当前部件。"); foreach (var index in indices) { if (remove) part.Primitives.Remove(index); else if (!part.Primitives.Contains(index)) part.Primitives.Add(index); } RefreshParts(editor, part);
        }
        public void AssignPrimitives(IEnumerable<int> indices, bool remove = false) { var editor = _editors[_tabs.SelectedIndex == 0 ? "Door" : "Window"]; var selection = indices.Distinct().ToArray(); if (editor.Plan == null || selection.Any(i => i < 0 || i >= editor.Plan.Primitives.Count)) throw new InvalidDataException("请在当前平面选择有效线条。"); Assign(editor, selection, remove); }
        public void SelectPart(string role) { var editor = _editors["Door"]; var item = editor.List.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((ComponentPlanPart)i.Tag).Role == role); if (item == null) throw new InvalidDataException("部件不存在。"); editor.List.SelectedItem = item; }
        public void DeleteSelectedPart()
        {
            var editor=_editors["Door"];var part=SelectedPart(editor);if(part==null)return;
            var index=editor.Parts.IndexOf(part);editor.Parts.Remove(part);
            RefreshParts(editor,editor.Parts.Count==0?null:editor.Parts[Math.Min(index,editor.Parts.Count-1)]);
            _status.Text="已删除部件分组，原始线条保留，可重新标注。保存入库后生效。";
        }
        public void RefreshPreview(string kind)
        {
            if (_inspect == null) return; var settings = ReadSettings(kind); var editor = _editors[kind]; var plan = _inspect(this, settings); if (plan == null) return; ComponentPlanSymbols.Validate(plan); if (editor.Plan != null && !editor.Plan.Primitives.Select(p => p.SourcePath).SequenceEqual(plan.Primitives.Select(p => p.SourcePath))) foreach (var part in editor.Parts) part.Primitives.Clear(); editor.Plan = plan; editor.FrameKey = FrameKey(settings); RefreshParts(editor, SelectedPart(editor) ?? editor.Parts.FirstOrDefault());
        }
        private static string FrameKey(CadComponentPlanSettings s) => string.Join("|", s.BlockHandle, s.Axis, s.X.ToString("R", CultureInfo.InvariantCulture), s.Y.ToString("R", CultureInfo.InvariantCulture), s.Z.ToString("R", CultureInfo.InvariantCulture), s.MillimetresPerCadUnit.ToString("R", CultureInfo.InvariantCulture));
        public void SetInitialPlan(ComponentPlanSymbol plan)
        {
            ComponentPlanSymbols.Validate(plan); var kind = plan.Category; _tabs.SelectedIndex = kind == "Door" ? 0 : 1; ((TextBox)FindName(kind + "Code")).Text = plan.Code; ((TextBox)FindName(kind + "Name")).Text = plan.Name; ((TextBox)FindName(kind + "Width")).Text = plan.Width.ToString("G17", CultureInfo.InvariantCulture); ((TextBox)FindName(kind + "Height")).Text = plan.Height.ToString("G17", CultureInfo.InvariantCulture);
            if (kind == "Door" && plan.Parts != null) { ((ComboBox)FindName(kind + "Assembly")).SelectedIndex = Math.Max(0, Array.IndexOf(ComponentPlanSymbols.DoorAssemblies, plan.DoorAssembly)); var editor = _editors[kind]; editor.Plan = plan; editor.Parts = plan.Parts.Select(CopyPart).ToList(); if (editor.Parts.Count == 0) foreach (var i in new[] { 8, 0, 1, 2, 3, 4, 5, 6 }) editor.Parts.Add(new ComponentPlanPart { Role = ComponentPlanSymbols.PartRoles[i], Name = ComponentPlanSymbols.PartRoleNames[i] }); RefreshParts(editor, editor.Parts.FirstOrDefault()); }
        }
        private static ComponentPlanPart CopyPart(ComponentPlanPart p) => new ComponentPlanPart { PartId = p.PartId, Name = p.Name, Role = p.Role, Primitives = p.Primitives.ToList() };
        public CadComponentPlanSettings ReadSettings() => ReadSettings(_tabs.SelectedIndex == 0 ? "Door" : "Window");
        private CadComponentPlanSettings ReadSettings(string kind)
        {
            var result = new CadComponentPlanSettings { Category = kind, BlockHandle = (string)((TextBox)FindName(kind + "Block")).Tag, Code = ((TextBox)FindName(kind + "Code")).Text.Trim(), Name = ((TextBox)FindName(kind + "Name")).Text.Trim(), DoorAssembly = kind == "Door" ? ComponentPlanSymbols.DoorAssemblies[Math.Max(0, ((ComboBox)FindName(kind + "Assembly")).SelectedIndex)] : null, Parts = _editors[kind].Parts.Select(CopyPart).ToList(), SourceHash = _editors[kind].Plan == null ? null : ComponentPlanSymbols.GeometryHash(_editors[kind].Plan), Axis = ((RadioButton)FindName(kind + "XAxis")).IsChecked == true ? "X" : "Y", X = Number(kind + "X", "基点 X"), Y = Number(kind + "Y", "基点 Y"), Z = Number(kind + "Z", "基点 Z"), Width = Number(kind + "Width", "洞口宽"), Height = Number(kind + "Height", "洞口高"), MillimetresPerCadUnit = Number(kind + "Units", "单位倍率") }; result.Validate(); return result;
        }
        private double Number(string name, string label) { double value; if (!double.TryParse(((TextBox)FindName(name)).Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidDataException(label + "必须是有效数字。"); return value; }
        private TextBox Input(string name, string text) { var input = new TextBox { Text = text, Height = 36, Background = Brush("#101923"), Foreground = Ink, BorderBrush = Edge, Padding = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center }; RegisterName(name, input); return input; }
        private Button Tool(string text, string name, Action action)
        {
            var label=text.TrimStart('＋','▣','↻',' ');
            var path=name.EndsWith("PickBlock")?"M3,3 L21,3 21,21 3,21 Z M3,9 L21,9 M9,9 L9,21":name.EndsWith("RefreshPreview")?"M20,8 A8,8 0 1 0 20,16 M20,2 L20,8 14,8":name.EndsWith("AddPart")?"M12,4 L12,20 M4,12 L20,12":name.EndsWith("ClearPart")?"M5,16 L14,5 21,12 12,21 5,16 M4,21 L21,21":name=="Export"?"M12,3 L12,15 M7,10 L12,15 17,10 M4,15 L4,21 20,21 20,15":name=="Close"?"M6,6 L18,18 M18,6 L6,18":"M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M8,12 L11,15 17,9";
            var content=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            if(name.EndsWith("DeletePart"))path="M4,6 L20,6 M9,6 L9,3 15,3 15,6 M6,6 L7,21 17,21 18,6 M10,10 L10,17 M14,10 L14,17";
            content.Children.Add(new Viewbox {Width=16,Height=16,Margin=new Thickness(0,0,6,0),Child=new System.Windows.Shapes.Path {Data=Geometry.Parse(path),Stroke=Ink,StrokeThickness=1.7,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Width=24,Height=24}});
            content.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});
            var button = new Button { Content = content, Height = 36, Background = Brush("#263C4E"), Foreground = Ink, BorderBrush = Edge, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 4, 6, 4) };
            RegisterName(name, button); button.Click += (_, __) => action(); return button;
        }
        private void ApplyTheme()
        {
            Resources.MergedDictionaries.Add(CreateTheme());
        }
        public static ResourceDictionary CreateTheme()
        {
            var styles=(ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Style TargetType='Button'><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
  <Border x:Name='Box' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='4' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
  <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='#51BAEF'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Box' Property='Opacity' Value='.45'/></Trigger></ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType='TabItem'><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TabItem'>
  <Border x:Name='Box' Background='#1C2C3A' BorderBrush='#344C60' BorderThickness='1' Padding='{TemplateBinding Padding}' Margin='0,0,4,0'><ContentPresenter ContentSource='Header' HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
  <ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter TargetName='Box' Property='Background' Value='#126A9E'/><Setter TargetName='Box' Property='BorderBrush' Value='#51BAEF'/></Trigger></ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType='ListBoxItem'><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'>
  <Border x:Name='Box' Background='Transparent' BorderBrush='Transparent' BorderThickness='1' CornerRadius='3' Padding='2'><ContentPresenter HorizontalAlignment='Stretch'/></Border>
  <ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter TargetName='Box' Property='Background' Value='#21475F'/><Setter TargetName='Box' Property='BorderBrush' Value='#51BAEF'/></Trigger><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='#668CA8'/></Trigger></ControlTemplate.Triggers>
 </ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType='ComboBox'><Setter Property='Foreground' Value='#E4EDF5'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'>
  <Grid><ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen,Mode=TwoWay,RelativeSource={RelativeSource TemplatedParent}}'><ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border Background='#101923' BorderBrush='#344C60' BorderThickness='1' CornerRadius='3'/></ControlTemplate></ToggleButton.Template></ToggleButton>
   <ContentPresenter Margin='8,0,26,0' VerticalAlignment='Center' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' IsHitTestVisible='False'/><Path Data='M0,0 L5,5 10,0' Stroke='#95AABD' StrokeThickness='1.5' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,10,0' IsHitTestVisible='False'/>
   <Popup IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'><Border Background='#172532' BorderBrush='#51BAEF' BorderThickness='1' MinWidth='{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}'><ScrollViewer MaxHeight='260'><ItemsPresenter/></ScrollViewer></Border></Popup>
  </Grid></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType='ComboBoxItem'><Setter Property='Foreground' Value='#E4EDF5'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'><Border x:Name='Box' Background='#172532' Padding='10,8'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Box' Property='Background' Value='#21475F'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
</ResourceDictionary>");
            return styles;
        }
        private static void Field(StackPanel p, string caption, FrameworkElement input) { p.Children.Add(new TextBlock { Text = caption, Foreground = Muted, Margin = new Thickness(0, 10, 0, 5) }); p.Children.Add(input); }
        private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        private void Run(Action action) { try { _status.Foreground = Muted; _status.Text = ""; action(); } catch (Exception ex) { _status.Foreground = Brush("#FFB28A"); _status.Text = ex.Message; } }
    }
}
