using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed partial class CadFloorRegistrationWindow
    {
        private CadRegistryRecovery _registryRecovery;
        private List<CadRegistryRecoveryRow> _registryRecoveryRows;
        private DataGrid _recoveryTable;
        private sealed class RecoveryTarget
        {
            public string Id {get;set;}
            public string Name {get;set;}
            public override string ToString()=>Name;
        }
        private FrameworkElement BuildRegistryRecoveryPage()
        {
            var body=new Grid {Margin=new Thickness(4)};
            foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})body.RowDefinitions.Add(new RowDefinition {Height=height});
            var intro=new StackPanel();
            intro.Children.Add(new TextBlock {Text="确认 CAD 登记关联",FontSize=23,FontWeight=FontWeights.SemiBold});
            intro.Children.Add(new TextBlock {Text="已有模型："+_model.Name+"。路径移动后，请核对并修改楼层对应关系；另建模型请选择“新建独立模型”。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,12)});
            body.Children.Add(intro);
            var paths=new Grid {Margin=new Thickness(0,0,0,12)};paths.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});paths.ColumnDefinitions.Add(new ColumnDefinition());
            for(var i=0;i<2;i++)
            {
                paths.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
                var label=new TextBlock {Text=i==0?"原登记模型":"当前目标模型",Margin=new Thickness(0,6,12,6),VerticalAlignment=VerticalAlignment.Center};Grid.SetRow(label,i);paths.Children.Add(label);
                var text=new TextBox {Text=i==0?_registryRecovery.RegisteredPath:_path,IsReadOnly=true,Padding=new Thickness(7),Margin=new Thickness(0,2,0,2),
                    Foreground=Foreground,Background=WorkspaceBrush("#222B35"),BorderBrush=WorkspaceBrush("#435160")};Grid.SetRow(text,i);Grid.SetColumn(text,1);paths.Children.Add(text);
            }
            var pathDetails=new Expander {Header=new TextBlock {Text="原登记与当前模型路径",Foreground=Foreground},Content=paths,IsExpanded=true,Margin=new Thickness(0,0,0,8)};
            body.SizeChanged+=(s,e)=>{if(e.HeightChanged)pathDetails.IsExpanded=e.NewSize.Height>=420;};
            Grid.SetRow(pathDetails,1);body.Children.Add(pathDetails);
            var content=new Grid();content.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(3,GridUnitType.Star)});content.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(2,GridUnitType.Star)});
            _recoveryTable=new DataGrid {Name="CadRegistryRecoveryFloors",AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,
                Background=Background,Foreground=Foreground,RowBackground=Background,AlternatingRowBackground=WorkspaceBrush("#222B35"),BorderBrush=WorkspaceBrush("#435160"),HeadersVisibility=DataGridHeadersVisibility.Column,MinHeight=80,ItemsSource=_registryRecoveryRows};
            var headerStyle=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,WorkspaceBrush("#222B35")));headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,Foreground));headerStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8)));
            _recoveryTable.ColumnHeaderStyle=headerStyle;
            _recoveryTable.Columns.Add(new DataGridTextColumn {Header="原登记楼层",Binding=new Binding("SourceName"),IsReadOnly=true,MinWidth=100,Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
            var targets=new List<RecoveryTarget> {new RecoveryTarget {Id=null,Name="请选择对应楼层"},new RecoveryTarget {Id="",Name="暂不关联 · 之后重新登记"}};
            targets.AddRange(_model.Storeys.Where(f=>string.IsNullOrWhiteSpace(f.TemplateStoreyId)).Select(f=>new RecoveryTarget {Id=f.Id,Name=f.Name+"（"+f.Elevation.ToString("0.##")+" mm）"}));
            var factory=new FrameworkElementFactory(typeof(ComboBox));factory.SetValue(ItemsControl.ItemsSourceProperty,targets);factory.SetValue(ComboBox.DisplayMemberPathProperty,"Name");factory.SetValue(ComboBox.SelectedValuePathProperty,"Id");
            factory.SetValue(FrameworkElement.MinHeightProperty,32d);factory.SetBinding(ComboBox.SelectedValueProperty,new Binding("TargetStoreyId") {Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
            _recoveryTable.Columns.Add(new DataGridTemplateColumn {Header="对应当前模型楼层 · 可修改",CellTemplate=new DataTemplate {VisualTree=factory},MinWidth=230,Width=new DataGridLength(2,DataGridLengthUnitType.Star)});
            _recoveryTable.SizeChanged+=(s,e)=>{var usable=Math.Max(330,e.NewSize.Width-24);_recoveryTable.Columns[0].Width=Math.Max(100,usable/3);_recoveryTable.Columns[1].Width=Math.Max(230,usable*2/3);};
            content.Children.Add(_recoveryTable);
            var preview=new CadRegisteredPlanPreview {Margin=new Thickness(12,0,0,0)};
            _recoveryTable.SelectionChanged+=(s,e)=>preview.SetCapture(_registryRecovery?.Preview((_recoveryTable.SelectedItem as CadRegistryRecoveryRow)?.SourceStoreyId));
            _recoveryTable.SelectedIndex=_registryRecoveryRows.Count>0?0:-1;
            Grid.SetColumn(preview,1);content.Children.Add(preview);Grid.SetRow(content,2);body.Children.Add(content);
            var bottom=new StackPanel {Margin=new Thickness(0,12,0,0)};
            bottom.Children.Add(new TextBlock {Text="保留登记：沿用平面、基点、门窗设置和底图。重新登记：先备份旧登记再框选。两者均不修改已有模型构件，之后再由你生成或更新模型。",TextWrapping=TextWrapping.Wrap,Foreground=WorkspaceBrush("#AEB9C8")});
            var actions=new WrapPanel {Margin=new Thickness(0,8,0,0)};
            var associate=WorkspaceTool("check","保留登记并关联当前模型",()=>ResolveRegistryRecovery(false));associate.Name="CadRegistryAssociate";associate.Background=WorkspaceBrush("#087FDD");actions.Children.Add(associate);
            var fresh=WorkspaceTool("square-dashed","备份并重新登记",()=>ResolveRegistryRecovery(true));fresh.Name="CadRegistryStartFresh";actions.Children.Add(fresh);
            actions.Children.Add(WorkspaceTool("folder-open","选择其他模型",OpenWorkspaceFile));
            var create=WorkspaceTool("file-plus","新建独立模型",BeginNewModel);create.Name="CadRegistryCreateNew";actions.Children.Add(create);bottom.Children.Add(actions);
            Grid.SetRow(bottom,3);body.Children.Add(bottom);return body;
        }
        private Task ResolveRegistryRecovery(bool startFresh)
        {
            if(_registryRecovery==null)return Task.CompletedTask;
            _recoveryTable?.CommitEdit(DataGridEditingUnit.Cell,true);_recoveryTable?.CommitEdit(DataGridEditingUnit.Row,true);
            var backup=_registryRecovery.Apply(_registryRecoveryRows,startFresh);
            LoadModel(_path);ShowWorkspacePage(1);
            _workspaceStatus.Text=(startFresh?"旧登记已备份，请逐层拾取基点并框选平面。模型构件未改变。":"登记已关联，可以修改楼层参数、重新框选或生成模型。")+" 备份："+backup;
            return Task.CompletedTask;
        }
    }
}
