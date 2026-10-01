using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadRegisteredOpeningTableWindow : Window
    {
        public IList<CadRegisteredOpeningRow> Rows { get; }
        public CadRegisteredOpeningTableWindow(CadFloorPlanRegistry registry,BuildingModelDocument model,
            Func<Window,CadFloorPlanCapture,CadFloorOpeningItem,CadOpeningPlacement> pickPlacement=null,
            Action<CadFloorPlanRegistry> readPlan=null,bool locations=false)
        {
            Title=locations ? "从登记平面读取门窗位置" : "门窗表 · 编号与洞口尺寸";
            Width=1020;Height=620;MinWidth=760;MinHeight=480;FontSize=14;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Rows=registry.OpeningRows(model);
            var stamp=File.Exists(CadFloorPlanRegistry.FilePath(registry.ModelPath)) ? File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(registry.ModelPath)).Ticks : 0;
            var root=new Grid { Margin=new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            root.Children.Add(new TextBlock { Text="已登记门窗可直接查看和修改，无需重新框选。门洞 1500×2200 自动编号 M1522，窗洞使用 C 前缀。\n同编号、不同尺寸自动加 A、B…后缀；具体做法继续在“门窗立面”设置。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12) });
            var table=new DataGrid { AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,ItemsSource=Rows };
            foreach(var c in new[] { new[] { "楼层","FloorName" },new[] { "门窗编号","Code" },new[] { "洞口宽 mm","Width" },new[] { "洞口高 mm","Height" },new[] { "数量","Quantity" },new[] { "离地 mm（空=默认）","Sill" },new[] { "平面位置","PlacementStatus" } })
                table.Columns.Add(new DataGridTextColumn { Header=c[0],Binding=new Binding(c[1]),IsReadOnly=c[1]=="FloorName" || c[1]=="Quantity" || c[1]=="PlacementStatus",MinWidth=80,Width=new DataGridLength(1,DataGridLengthUnitType.Star) });
            Grid.SetRow(table,1);root.Children.Add(table);
            table.SelectedItem=locations ? Rows.FirstOrDefault(r=>r.Items.Any(o=>r.GetPlacement(o.SourceHandle)==null)) : Rows.FirstOrDefault();
            var footer=new StackPanel { Margin=new Thickness(0,12,0,0) };
            var status=new TextBlock { Foreground=Brushes.Firebrick,TextWrapping=TextWrapping.Wrap };
            var actions=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right };
            Action commit=()=> { if(!table.CommitEdit(DataGridEditingUnit.Cell,true) || !table.CommitEdit(DataGridEditingUnit.Row,true))throw new InvalidOperationException("请修正标红的编号或尺寸输入。"); };
            Action refreshCodes=()=> { CadRegisteredOpeningRow.RefreshCodes(Rows); };
            table.RowEditEnding+=(s,e)=> { if(e.EditAction==DataGridEditAction.Commit)
                Dispatcher.BeginInvoke(new Action(refreshCodes)); };
            var auto=new Button { Content="所选按尺寸编号",Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,0,8,0) };
            auto.Click+=(s,e)=> { try { commit(); foreach(CadRegisteredOpeningRow row in table.SelectedItems) {
                var kind=CadOpeningDefaults.Kind(row.Code,row.Kind); var prefix=kind=="门" ? "M" : kind=="门联窗" ? "MLC" : kind=="洞口" ? "DK" : "C";
                row.Code=CadOpeningDefaults.SizeCode(prefix,row.Width,row.Height); } refreshCodes(); }
                catch(Exception ex) { status.Text=ex.Message; } };actions.Children.Add(auto);
            if(readPlan!=null) {
                var read=new Button { Content="从 CAD 平面自动读取位置",Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,0,8,0) };
                read.Click+=(s,e)=> { try { commit();refreshCodes();var draft=registry.Clone();draft.SaveOpeningRows(Rows,stamp,false);
                    readPlan(draft);registry.ReplaceFrom(draft,stamp);DialogResult=true; }
                    catch(Exception ex) { status.Text=ex.Message; } };actions.Children.Add(read);
            }
            if(pickPlacement!=null) {
                var pick=new Button { Content="补充所选门窗位置",Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,0,8,0) };
                pick.Click+=(s,e)=> { try { commit();var row=table.SelectedItem as CadRegisteredOpeningRow;if(row==null)return;
                    var item=row.Items.FirstOrDefault(o=>row.GetPlacement(o.SourceHandle)==null) ?? row.Items.First();
                    var draft=new CadFloorOpeningItem { SourceHandle=item.SourceHandle,Code=row.Code,Kind=row.Kind,Width=row.Width,Height=row.Height,Sill=row.Sill };
                    var placement=pickPlacement(this,row.Capture,draft);if(placement!=null)row.SetPlacement(item.SourceHandle,placement);
                    status.Text=row.PlacementStatus; } catch(Exception ex) { status.Text=ex.Message; } };actions.Children.Add(pick);
            }
            var save=new Button { Content="保存门窗表",Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,0,8,0) };
            save.Click+=(s,e)=> { try { commit();refreshCodes();registry.SaveOpeningRows(Rows,stamp);DialogResult=true; } catch(Exception ex) { status.Text=ex.Message; } };actions.Children.Add(save);
            actions.Children.Add(new Button { Content="取消",IsCancel=true,Padding=new Thickness(10,7,10,7) });
            footer.Children.Add(status);footer.Children.Add(actions);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
        }
    }
}
