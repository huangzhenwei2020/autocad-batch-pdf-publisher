using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadFloorOpeningReviewWindow : Window
    {
        private sealed class WallRow
        {
            public CadBuildingProbeEntity Source { get; set; }
            public bool Include { get; set; } = true;
            public int Number { get; set; }
            public double? Thickness => Source.CandidateThickness;
            public double? Height => CadFloorModelGeneration.Number(Source,"Height");
            public string Status => Source.StraightHorizontalLineVerified ? "直墙定位已核验" : "定位不支持，须排除或重新核对";
        }
        public CadFloorOpeningReviewWindow(CadFloorPlanCapture capture,
            Func<Window,CadFloorOpeningItem,CadOpeningPlacement> pickPlacement = null)
        {
            Title = capture.Floor.Storey.Name + " · 墙与门窗登记核对"; Width = 1140; Height = 680;
            MinWidth = 720; MinHeight = 440; FontSize = 14; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock { Text = "本层读取天正墙 " + capture.Floor.WallCandidates.Count + " 面、洞口 " + capture.Openings.Count + " 个。\n按编号和尺寸生成默认门窗，具体类型、分格及开启方式在“门窗立面”中调整。未读到编号时按尺寸自动编号。\n离地可留空：门默认 0、窗默认 900 mm，并按墙高调整。未识别到洞口位置时可补充定位。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            var table = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, ItemsSource = capture.Openings };
            table.Columns.Add(new DataGridCheckBoxColumn { Header = "生成", Binding = new Binding("Include"), Width = 65 });
            foreach (var c in new[] { new[] { "门窗编号（可留空）", "Code" }, new[] { "宽 mm", "Width" }, new[] { "高 mm", "Height" }, new[] { "离地 mm（空=默认）", "Sill" }, new[] { "平面位置", "PlacementStatus" } })
                table.Columns.Add(new DataGridTextColumn { Header = c[0], Binding = new Binding(c[1]), IsReadOnly = c[1] == "SourceHandle" || c[1] == "CodeSource" || c[1] == "PlacementStatus", MinWidth = 85, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            table.Columns.Insert(3, new DataGridTextColumn { Header = "生成默认做法", Binding = new Binding("DefaultDescription"), IsReadOnly=true, Width = 180 });
            table.SelectedIndex=capture.Openings.Count>0 ? 0 : -1;
            var tabs=new TabControl(); tabs.Items.Add(new TabItem { Header="门窗洞口",Content=table });
            var wallRows=capture.Floor.WallCandidates.Select((w,i)=>new WallRow { Number=i+1,Source=w,Include=!(capture.ExcludedWallHandles??new List<string>()).Contains(w.Handle) }).ToList();
            var walls=new DataGrid { AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,ItemsSource=wallRows };
            walls.Columns.Add(new DataGridCheckBoxColumn { Header="生成",Binding=new Binding("Include"),Width=65 });
            foreach(var c in new[] { new[] { "墙序号","Number" },new[] { "墙厚（CAD单位）","Thickness" },new[] { "墙高（CAD单位）","Height" },new[] { "定位状态","Status" } })
                walls.Columns.Add(new DataGridTextColumn { Header=c[0],Binding=new Binding(c[1]),IsReadOnly=true,Width=new DataGridLength(1,DataGridLengthUnitType.Star) });
            tabs.Items.Add(new TabItem { Header="墙",Content=walls }); Grid.SetRow(tabs,1); root.Children.Add(tabs);
            var footer=new StackPanel();
            var bulk=new WrapPanel { Margin=new Thickness(0,10,0,0) };
            var sill=new TextBox { Width=95,Margin=new Thickness(0,0,8,0) };
            bulk.Children.Add(new TextBlock { Text="所选洞口离地 mm（可选）",VerticalAlignment=VerticalAlignment.Center }); bulk.Children.Add(sill);
            var fill=new Button { Content="填入所选洞口",Padding=new Thickness(8,5,8,5) };
            fill.Click+=(s,e)=> {
                if (!table.CommitEdit(DataGridEditingUnit.Cell,true) || !table.CommitEdit(DataGridEditingUnit.Row,true)) return;
                double value=0; var hasSill=!string.IsNullOrWhiteSpace(sill.Text);
                if(hasSill && (!double.TryParse(sill.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out value) || double.IsNaN(value) || double.IsInfinity(value) || value<0)) {
                    MessageBox.Show(this,"离地高度须为大于或等于 0 的数值。","请核对"); return; }
                var selected=table.SelectedItems.Cast<CadFloorOpeningItem>().ToList();
                foreach(var item in selected) { if(hasSill)item.Sill=value; }
                table.Items.Refresh(); foreach(var item in selected)table.SelectedItems.Add(item);
            }; bulk.Children.Add(fill);
            if(pickPlacement!=null) { var pick=new Button { Content="补充所选洞口定位",Padding=new Thickness(8,5,8,5),Margin=new Thickness(8,0,0,0) };
                pick.Click+=(s,e)=> { var item=table.SelectedItem as CadFloorOpeningItem; if(item==null)return;
                    try { var location=pickPlacement(this,item); if(location!=null) { item.Placement=location; table.Items.Refresh(); table.SelectedItem=item; } }
                    catch(Exception ex) { MessageBox.Show(this,ex.Message,"定位未更新"); } }; bulk.Children.Add(pick); }
            footer.Children.Add(bulk);
            footer.Children.Add(new TextBlock { Text="生成墙高以建筑模型的楼层设置为准，修改层高后墙高自动跟随。CAD 墙高仅供参考。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0) });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var save = new Button { Content = "登记本层", Padding = new Thickness(14, 8, 14, 8) };
            save.Click += (s, e) => { try { if (!table.CommitEdit(DataGridEditingUnit.Cell, true) || !table.CommitEdit(DataGridEditingUnit.Row, true) || !walls.CommitEdit(DataGridEditingUnit.Cell,true) || !walls.CommitEdit(DataGridEditingUnit.Row,true)) throw new InvalidOperationException("输入格式有误，请先修正标红单元格。");
                capture.ExcludedWallHandles=wallRows.Where(w=>!w.Include).Select(w=>w.Source.Handle).ToList();
                capture.ValidateSchedule(); DialogResult = true; } catch (Exception ex) { MessageBox.Show(this, ex.Message, "请核对门窗"); } }; actions.Children.Add(save);
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0) }; actions.Children.Add(cancel);
            footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        }
    }
}
