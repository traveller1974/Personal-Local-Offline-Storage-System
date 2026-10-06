using System.IO;
using System.Windows;
using System.Windows.Controls;
using Stock.Core;

namespace Stock.Desktop;

public partial class DraftWindow : Window
{
    public DraftViewModel ViewModel { get; }
    public string? SavedId { get; private set; }
    public string TempDirectory { get; }
    private ProductFilter? productFilter;
    private string productSearch="";
    private int productPage;
    private long productCount;
    public DraftWindow(Window owner,StockService service,DocumentKind kind)
    {
        InitializeComponent();Owner=owner;ViewModel=new(service,kind);DataContext=ViewModel;
        Title=Heading.Text=kind==DocumentKind.Purchase?"编辑厂家进货清单":"编辑出货清单";
        Manufacturer.Visibility=kind==DocumentKind.Purchase?Visibility.Visible:Visibility.Collapsed;
        SaleChannel.Visibility=kind==DocumentKind.Sale?Visibility.Visible:Visibility.Collapsed;
        OcrButton.Visibility=kind==DocumentKind.Purchase?Visibility.Visible:Visibility.Collapsed;
        TempDirectory=Path.Combine(Path.GetDirectoryName(service.DataDirectory)!,"Temp","draft-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDirectory);ReloadProducts();
        Closed+=(_,_)=>{try{Directory.Delete(TempDirectory,true);}catch(IOException){}catch(UnauthorizedAccessException){}};
    }
    public void ReloadProducts() {var batch=ViewModel.Service.ProductPage(productFilter,productSearch,page:productPage);ProductPicker.ItemsSource=batch.Items;productCount=batch.Count;}
    private void FilterProducts(object sender,RoutedEventArgs e)=>Ui.Try(()=>{if(QueryDialogs.Filter(this,ViewModel.Service,productFilter,out var f)){productFilter=f;productPage=0;ReloadProducts();}});
    private void SearchProducts(object sender,RoutedEventArgs e){productSearch=ProductSearch.Text;productPage=0;ReloadProducts();}
    private void PreviousProducts(object sender,RoutedEventArgs e){productPage=Math.Max(0,productPage-1);ReloadProducts();}
    private void NextProducts(object sender,RoutedEventArgs e){if((productPage+1)*100<productCount)productPage++;ReloadProducts();}
    private void ChannelChanged(object sender,SelectionChangedEventArgs e) { if(ViewModel is not null)ViewModel.Channel=SaleChannel.SelectedValue as string??""; }
    private void Add(object sender,RoutedEventArgs e) => Ui.Try(()=>{ if(ProductPicker.SelectedItem is not Product p)throw new BusinessException("请选择已有货品；新增货品请先返回库存首页。"); ViewModel.Add(p,AddQuantity.Number); });
    private void Remove(object sender,RoutedEventArgs e) { if(((Button)sender).Tag is DraftLine line)ViewModel.Lines.Remove(line); }
    private void Cancel(object sender,RoutedEventArgs e) => Close();
    private void Preview(object sender,RoutedEventArgs e) => Ui.Try(()=>
    {
        var impacts=ViewModel.Preview();
        if(Ui.Confirm(this,ViewModel.Kind==DocumentKind.Purchase?"确认入库":"确认出库",$"{Rules.KindName(ViewModel.Kind)} · {ViewModel.Channel}",impacts,ViewModel.CommitAsync,out var id))
        {SavedId=id;DialogResult=true;}
    });
    private void Ocr(object sender,RoutedEventArgs e) => OpenOcr();
    public void OpenOcr()
    {
        Ui.Try(()=>{var dialog=new OcrWindow(this,ViewModel,TempDirectory);dialog.ShowDialog();ReloadProducts();PhotosNotice.Text=$"已附加{ViewModel.Photos.Count}张原图或处理图，仅在最终入库后保存。";});
    }
}
