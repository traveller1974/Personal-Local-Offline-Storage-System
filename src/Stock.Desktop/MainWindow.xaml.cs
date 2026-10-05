using System.Windows;
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
    private async void Export(object sender,RoutedEventArgs e)
    {
        var dialog=new SaveFileDialog{Filter="Excel 工作簿 (*.xlsx)|*.xlsx",DefaultExt=".xlsx",AddExtension=true,FileName=$"进出货记录_{DateTime.Now:yyyyMMdd}.xlsx",OverwritePrompt=true};
        if(dialog.ShowDialog(this)!=true)return;var button=(Button)sender;button.IsEnabled=false;await Ui.TryAsync(()=>ViewModel.ExportAsync(dialog.FileName));button.IsEnabled=true;
    }
    private void PreviousPage(object sender,RoutedEventArgs e)=>ViewModel.Page--;
    private void NextPage(object sender,RoutedEventArgs e)=>ViewModel.Page++;
    private void Details(object sender,RoutedEventArgs e)=>Ui.Try(()=>{if(((Button)sender).Tag is RecordRow row)OpenDetails(row);});
    private void RecordDoubleClick(object sender,MouseButtonEventArgs e)=>Ui.Try(()=>{if(RecordsGrid.SelectedItem is RecordRow row)OpenDetails(row);});
    private void OpenDetails(RecordRow row){var id=StockDialogs.Document(this,ViewModel.Service,row.Id);if(id is not null)ViewModel.Notice="作废已记账："+ViewModel.Receipt(id);}
    private void Settings(object sender,RoutedEventArgs e)=>Ui.Try(()=>
    {
        var w=Ui.Dialog(this,"设置与本地备份",750,580);var p=new StackPanel{Margin=new(26)};p.Children.Add(Ui.Text("本机数据与备份",true));p.Children.Add(Ui.Text("数据目录：\n"+ViewModel.Service.DataDirectory));p.Children.Add(Ui.Text(ViewModel.RetentionText));p.Children.Add(Ui.Text("备份包含数据库与货单照片。请把备份另存到安全位置；恢复会替换当前本机数据。"));
        var busy=false;w.Closing+=(_,ev)=>{if(busy)ev.Cancel=true;};var backup=new Button{Content="保存备份",Style=(Style)FindResource("Primary")};var restore=new Button{Content="恢复备份"};
        backup.Click+=async(_,_)=>{var d=new SaveFileDialog{Filter="库存备份 (*.stockbackup)|*.stockbackup",DefaultExt=".stockbackup",FileName=$"库存备份_{DateTime.Now:yyyyMMdd_HHmmss}.stockbackup"};if(d.ShowDialog(w)!=true)return;busy=true;backup.IsEnabled=restore.IsEnabled=false;await Ui.TryAsync(async()=>{await ViewModel.BackupAsync(d.FileName);ViewModel.Notice="备份已保存";});busy=false;backup.IsEnabled=restore.IsEnabled=true;};
        restore.Click+=async(_,_)=>{var d=new OpenFileDialog{Filter="库存备份 (*.stockbackup)|*.stockbackup"};if(d.ShowDialog(w)!=true)return;if(MessageBox.Show(w,"恢复会替换当前本机全部库存与历史记录。请确认已保存需要的当前备份。\n是否恢复选中的备份？","确认恢复",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;busy=true;backup.IsEnabled=restore.IsEnabled=false;await Ui.TryAsync(async()=>{await ViewModel.RestoreAsync(d.FileName);ViewModel.Notice="备份恢复成功，五年清理已完成";Tabs.IsEnabled=true;w.Close();});busy=false;backup.IsEnabled=restore.IsEnabled=true;};
        p.Children.Add(Ui.Row(backup,restore));p.Children.Add(Ui.Text("每台电脑独立保存数据，无账号、无同步。卸载默认保留数据。"));p.Children.Add(Ui.Button("关闭",()=>w.Close()));w.Content=p;w.ShowDialog();
    });
}
