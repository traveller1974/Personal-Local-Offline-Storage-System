using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Stock.Core;

namespace Stock.Desktop;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMinutes(1)};
    private DateOnly maintained=DateOnly.FromDateTime(DateTime.Now);
    private bool maintaining;
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();ViewModel=model;DataContext=model;model.Query();timer.Tick+=async(_,_)=>
        {
            var today=DateOnly.FromDateTime(DateTime.Now);if(today==maintained||OwnedWindows.Count>0||maintaining)return;
            maintaining=true;Tabs.IsEnabled=false;
            try{await Task.Run(()=>ViewModel.Service.Maintain());maintained=today;ViewModel.Refresh();Tabs.IsEnabled=true;}
            catch(Exception ex){ViewModel.Notice="数据维护失败，请在设置中检查备份或重试。";Ui.Error(ex);}
            finally{maintaining=false;}
        };timer.Start();Closed+=(_,_)=>timer.Stop();
    }
    private void CreateProduct(object sender,RoutedEventArgs e)=>Ui.Try(()=>{StockDialogs.Product(this,ViewModel.Service);ViewModel.Refresh();});
    private void EditProduct(object sender,RoutedEventArgs e)=>Ui.Try(()=>{if(((Button)sender).Tag is Product p){StockDialogs.Product(this,ViewModel.Service,p);ViewModel.Refresh();}});
    private void Transfer(object sender,RoutedEventArgs e)=>Ui.Try(()=>{if(((Button)sender).Tag is Product p){var id=StockDialogs.Transfer(this,ViewModel.Service,p);if(id is not null)ViewModel.Notice="调整已记账："+ViewModel.Receipt(id);}});
    private void Refresh(object sender,RoutedEventArgs e)=>Ui.Try(ViewModel.Refresh);
    private void Purchase(object sender,RoutedEventArgs e)=>OpenDraft(DocumentKind.Purchase,false);
    private void PhotoPurchase(object sender,RoutedEventArgs e)=>OpenDraft(DocumentKind.Purchase,true);
    private void Sale(object sender,RoutedEventArgs e)=>OpenDraft(DocumentKind.Sale,false);
    private void OpenDraft(DocumentKind kind,bool photo)
    {
        Ui.Try(()=>{var w=new DraftWindow(this,ViewModel.Service,kind);if(photo)w.Loaded+=(_,_)=>w.OpenOcr();w.ShowDialog();if(w.SavedId is not null)ViewModel.Notice="已记账："+ViewModel.Receipt(w.SavedId);else ViewModel.Refresh();});
    }
    private void Query(object sender,RoutedEventArgs e)=>Ui.Try(ViewModel.Query);
    private void InventoryFilter(object sender,RoutedEventArgs e)=>Ui.Try(()=>{if(QueryDialogs.Filter(this,ViewModel.Service,ViewModel.ProductSelection,out var filter)){ViewModel.ProductSelection=filter;ViewModel.RefreshProducts();}});
    private void RecordFilter(object sender,RoutedEventArgs e)=>Ui.Try(()=>{if(QueryDialogs.Filter(this,ViewModel.Service,ViewModel.RecordSelection,out var filter)){ViewModel.RecordSelection=filter;ViewModel.Query();}});
    private void InventorySummary(object sender,RoutedEventArgs e)=>Ui.Try(()=>QueryDialogs.Summary(this,ViewModel.Service,ViewModel.ProductSelection,ViewModel.Search));
    private void RecordSummary(object sender,RoutedEventArgs e)=>Ui.Try(()=>QueryDialogs.Summary(this,ViewModel.Service,ViewModel.RecordSelection,ViewModel.RecordSearch,ViewModel.Filter()));
    private void InventoryExport(object sender,RoutedEventArgs e)=>Ui.Try(()=>QueryDialogs.Export(this,ViewModel.Service,true,ViewModel.ProductSelection,ViewModel.Search));
    private void PreviousProducts(object sender,RoutedEventArgs e)=>ViewModel.ProductPage--;
    private void NextProducts(object sender,RoutedEventArgs e)=>ViewModel.ProductPage++;
    private void Export(object sender,RoutedEventArgs e)
    {
        Ui.Try(()=>QueryDialogs.Export(this,ViewModel.Service,false,ViewModel.RecordSelection,ViewModel.RecordSearch,ViewModel.Filter()));
    }
    private void PreviousPage(object sender,RoutedEventArgs e)=>ViewModel.Page--;
    private void NextPage(object sender,RoutedEventArgs e)=>ViewModel.Page++;
    private void Details(object sender,RoutedEventArgs e)=>Ui.Try(()=>{if(((Button)sender).Tag is RecordRow row)OpenDetails(row);});
    private void RecordDoubleClick(object sender,MouseButtonEventArgs e)=>Ui.Try(()=>{if(RecordsGrid.SelectedItem is RecordRow row)OpenDetails(row);});
    private void OpenDetails(RecordRow row){var id=StockDialogs.Document(this,ViewModel.Service,row.Id);if(id is not null)ViewModel.Notice="作废已记账："+ViewModel.Receipt(id);}
    private void Settings(object sender,RoutedEventArgs e)=>Ui.Try(()=>CreateSettingsWindow().ShowDialog());
    internal Window CreateSettingsWindow()
    {
        var w=Ui.Dialog(this,"照片识别设置与数据备份",820,850);var root=new DockPanel{Margin=new(22)};var body=new StackPanel();
        StackPanel Section(){var content=new StackPanel();var card=new System.Windows.Controls.Border{Style=(Style)FindResource("Card"),Child=content,Margin=new(0,0,0,14)};body.Children.Add(card);return content;}
        var p=Section();
        var configuration=RecognitionSettings.Load(ViewModel.Service);p.Children.Add(Ui.Text("照片识别",true));
        var provider=new ComboBox{ItemsSource=new[]{"千问（阿里云百炼）","DeepSeek（官方）"},SelectedIndex=configuration.Provider==Stock.Recognition.RecognitionProviderKind.DeepSeek?1:0};
        var keyBox=new PasswordBox{Password=configuration.ApiKey};var endpoint=new TextBox{Text=configuration.Endpoint};var workspace=new TextBox{Text=configuration.Workspace};
        Ui.Field(p,"识别服务",provider);Ui.Field(p,"密钥（API Key，加密保存在这台电脑）",keyBox);Ui.Field(p,"官方服务地址",endpoint);Ui.Field(p,"百炼业务空间（可留空，DeepSeek不用填写）",workspace);
        var selectedProvider=configuration.Provider;
        var sessionConfigurations=new Dictionary<Stock.Recognition.RecognitionProviderKind,Stock.Recognition.RecognitionConfiguration>{{selectedProvider,configuration}};
        provider.SelectionChanged+=(_,_)=>Ui.Try(()=>
        {
            sessionConfigurations[selectedProvider]=new(selectedProvider,keyBox.Password,endpoint.Text.Trim(),workspace.Text.Trim());
            selectedProvider=provider.SelectedIndex==1?Stock.Recognition.RecognitionProviderKind.DeepSeek:Stock.Recognition.RecognitionProviderKind.Qwen;
            var saved=sessionConfigurations.GetValueOrDefault(selectedProvider)??RecognitionSettings.Load(ViewModel.Service,selectedProvider);
            keyBox.Password=saved.ApiKey;endpoint.Text=saved.Endpoint;workspace.Text=saved.Workspace;
        });
        p.Children.Add(Ui.Button("保存识别设置",()=>{RecognitionSettings.Save(ViewModel.Service,new(provider.SelectedIndex==1?Stock.Recognition.RecognitionProviderKind.DeepSeek:Stock.Recognition.RecognitionProviderKind.Qwen,keyBox.Password,endpoint.Text.Trim(),workspace.Text.Trim()));ViewModel.Notice="识别设置已加密保存。";},true));
        p.Children.Add(Ui.Text("点击识别后，会把确认的表格截图发给所选服务。服务按用量收费；手动填写可断网使用。"));
        p=Section();p.Children.Add(Ui.Text("记住核对结果",true));
        var learning=new CheckBox{Content="成功入库后记住人工修正，供以后填写参考",IsChecked=ViewModel.Service.LearningEnabled};
        learning.Click+=(_,_)=>Ui.Try(()=>ViewModel.Service.LearningEnabled=learning.IsChecked==true);p.Children.Add(learning);
        p.Children.Add(Ui.Text("识别相似货单时，最多参考3条相关货品修正。数量不会照抄旧单。"));
        p.Children.Add(Ui.Button("查看或停用核对记录",()=>ShowCorrectionMemory(w)));
        p.Children.Add(Ui.Button("导出核对参考给独立测试程序",()=>
        {
            var dialog=new SaveFileDialog{Filter="核对参考|*.json",FileName="货品核对参考.json"};if(dialog.ShowDialog(w)!=true)return;
            var terms=ViewModel.Service.CorrectionMemory().Where(e=>e.HumanConfirmed&&e.Enabled).Select(e=>e.Original.Name);
            File.WriteAllText(dialog.FileName,System.Text.Json.JsonSerializer.Serialize(ViewModel.Service.RelevantCorrectionExamples(terms),Stock.Recognition.RecognitionJson.Options));
            ViewModel.Notice="核对参考已导出，独立测试程序可以载入。";
        }));
        p.Children.Add(Ui.Button("补记已入库的修正",()=>{ViewModel.Service.RebuildRecognitionMemory();ViewModel.Notice=ViewModel.Service.MemoryNotice??"核对记录已补记。";}));
        p=Section();p.Children.Add(Ui.Text("本机数据与备份",true));p.Children.Add(Ui.Text("数据目录：\n"+ViewModel.Service.DataDirectory));p.Children.Add(Ui.Text(ViewModel.RetentionText));p.Children.Add(Ui.Text("备份包含库存、历史单据、货单照片和核对记录，识别密钥需另行配置。软件升级和首次清理旧记录时，会在旁边的 ProtectionBackups 文件夹保存保护备份，请自行保管。"));
        var busy=false;w.Closing+=(_,ev)=>{if(busy)ev.Cancel=true;};var backup=new Button{Content="保存备份",Style=(Style)FindResource("Primary")};var restore=new Button{Content="恢复备份"};
        backup.Click+=async(_,_)=>{var d=new SaveFileDialog{Filter="库存备份 (*.stockbackup)|*.stockbackup",DefaultExt=".stockbackup",FileName=$"库存备份_{DateTime.Now:yyyyMMdd_HHmmss}.stockbackup"};if(d.ShowDialog(w)!=true)return;busy=true;backup.IsEnabled=restore.IsEnabled=false;await Ui.TryAsync(async()=>{await ViewModel.BackupAsync(d.FileName);ViewModel.Notice="备份已保存";});busy=false;backup.IsEnabled=restore.IsEnabled=true;};
        restore.Click+=async(_,_)=>{var d=new OpenFileDialog{Filter="库存备份 (*.stockbackup)|*.stockbackup"};if(d.ShowDialog(w)!=true)return;if(MessageBox.Show(w,"恢复会替换当前本机全部库存与历史记录。请确认已保存需要的当前备份。\n是否恢复选中的备份？","确认恢复",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;busy=true;backup.IsEnabled=restore.IsEnabled=false;await Ui.TryAsync(async()=>{await ViewModel.RestoreAsync(d.FileName);ViewModel.Notice="备份恢复成功，三年清理已完成";Tabs.IsEnabled=true;w.Close();});busy=false;backup.IsEnabled=restore.IsEnabled=true;};
        p.Children.Add(Ui.Row(backup,restore));p.Children.Add(Ui.Text("每台电脑独立保存数据，无账号、无同步。卸载默认保留数据。"));
        var close=Ui.Row(Ui.Button("关闭",()=>w.Close()));close.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(close,Dock.Bottom);root.Children.Add(close);
        root.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});w.Content=root;return w;
    }
    private void ShowCorrectionMemory(Window owner)
    {
        var window=Ui.Dialog(owner,"以前的核对记录",850,650);var panel=new StackPanel{Margin=new(22)};
        panel.Children.Add(Ui.Text("发现记录有误时，可以停用。停用后不会用来自动填写。",true));
        foreach(var entry in ViewModel.Service.CorrectionMemory().Take(500))
        {
            var enabled=new CheckBox{Content=new TextBlock{Text=entry.Display,TextWrapping=TextWrapping.Wrap},IsChecked=entry.Enabled,Margin=new(0,8,0,8)};
            enabled.Click+=(_,_)=>Ui.Try(()=>ViewModel.Service.SetCorrectionEnabled(entry.DocumentId,entry.RowId,enabled.IsChecked==true));panel.Children.Add(enabled);
        }
        panel.Children.Add(Ui.Button("关闭",()=>window.Close()));window.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};window.ShowDialog();
    }
}
