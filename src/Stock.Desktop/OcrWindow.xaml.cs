using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Stock.Core;

namespace Stock.Desktop;

public partial class OcrWindow : Window
{
    public ObservableCollection<OcrReviewRow> Rows { get; }=[];
    private readonly DraftViewModel draft;
    private readonly ImageSession image;
    private OcrResponse? response;
    private CancellationTokenSource? cancellation;
    private bool selectingColumn,cropMode;
    private Point? cropOrigin;
    private bool busy;
    public OcrWindow(Window owner,DraftViewModel draft,string directory)
    {
        InitializeComponent();Owner=owner;this.draft=draft;image=new(Path.Combine(directory,"photo-"+Guid.NewGuid().ToString("N")));DataContext=this;
        Closed+=(_,_)=>{cancellation?.Cancel();image.Dispose();};
    }
    private void Choose(object sender,RoutedEventArgs e) {var d=new OpenFileDialog{Filter="货单照片|*.jpg;*.jpeg;*.png"};if(d.ShowDialog(this)==true)Load(d.FileName);}
    public void Load(string path)=>Ui.Try(()=>{if(busy)return;image.Load(path);Rows.Clear();response=null;ColumnPicker.ItemsSource=null;cropMode=false;Zoom.Value=Math.Clamp(520.0/image.Width,.1,1.0);UpdateImage();StatusText.Text="照片已导入。可旋转或框选裁剪，然后点击识别。";});
    private void UpdateImage(){if(image.Width==0)return;PhotoImage.Source=Ui.Bitmap(image.ProcessedPath);ImageHost.Width=image.Width*Zoom.Value;ImageHost.Height=image.Height*Zoom.Value;Overlay.Width=ImageHost.Width;Overlay.Height=ImageHost.Height;Selection.Visibility=RowHighlight.Visibility=Visibility.Collapsed;}
    private void Zoomed(object sender,RoutedPropertyChangedEventArgs<double> e){if(image is not null)UpdateImage();}
    private void Invalidate(){Rows.Clear();response=null;ColumnPicker.ItemsSource=null;UpdateImage();StatusText.Text="照片已处理，请重新识别。";}
    private void RotateLeft(object sender,RoutedEventArgs e)=>Ui.Try(()=>{image.Rotate(false);Invalidate();});
    private void RotateRight(object sender,RoutedEventArgs e)=>Ui.Try(()=>{image.Rotate(true);Invalidate();});
    private void Reset(object sender,RoutedEventArgs e)=>Ui.Try(()=>{image.Reset();Invalidate();});
    private void EnableCrop(object sender,RoutedEventArgs e){cropMode=image.Width>0;StatusText.Text="在左侧照片上按住鼠标框选表格，松开后裁剪。";}
    private void CropStart(object sender,MouseButtonEventArgs e){if(!cropMode||busy)return;cropOrigin=e.GetPosition(ImageHost);ImageHost.CaptureMouse();Selection.Visibility=Visibility.Visible;Selection.Width=Selection.Height=0;Canvas.SetLeft(Selection,cropOrigin.Value.X);Canvas.SetTop(Selection,cropOrigin.Value.Y);}
    private void CropMove(object sender,MouseEventArgs e){if(cropOrigin is not Point origin)return;var now=e.GetPosition(ImageHost);Canvas.SetLeft(Selection,Math.Min(origin.X,now.X));Canvas.SetTop(Selection,Math.Min(origin.Y,now.Y));Selection.Width=Math.Abs(now.X-origin.X);Selection.Height=Math.Abs(now.Y-origin.Y);}
    private void CropEnd(object sender,MouseButtonEventArgs e)
    {
        if(cropOrigin is not Point origin)return;cropOrigin=null;ImageHost.ReleaseMouseCapture();cropMode=false;var end=e.GetPosition(ImageHost);var scale=Zoom.Value;
        Ui.Try(()=>{var left=Math.Clamp((int)(Math.Min(origin.X,end.X)/scale),0,image.Width-1);var top=Math.Clamp((int)(Math.Min(origin.Y,end.Y)/scale),0,image.Height-1);var width=Math.Min(image.Width-left,(int)(Math.Abs(end.X-origin.X)/scale));var height=Math.Min(image.Height-top,(int)(Math.Abs(end.Y-origin.Y)/scale));image.Crop(left,top,width,height);Invalidate();});
    }
    private async void Recognize(object sender,RoutedEventArgs e)
    {
        if(image.Width==0){StatusText.Text="请先选择照片。";return;}if(busy)return;busy=true;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=false;CancelOcrButton.Visibility=Visibility.Visible;
        cancellation=new();StatusText.Text="正在本机识别，最多等待120秒；识别期间可取消。";
        try{response=await new OcrClient().RecognizeAsync(image.ProcessedPath,cancellation.Token);var parsed=InvoiceParser.Parse(response);selectingColumn=true;ColumnPicker.ItemsSource=parsed.QuantityColumns;ColumnPicker.SelectedIndex=parsed.QuantityColumns.Count==1?0:-1;selectingColumn=false;ShowParse(parsed);}
        catch(OperationCanceledException){StatusText.Text="识别已取消，库存未改变。";}
        catch(Exception ex){StatusText.Text=ex.Message+" 可重试或关闭此窗口后手动录入。";Rows.Clear();response=null;}
        finally{busy=false;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=true;CancelOcrButton.Visibility=Visibility.Collapsed;cancellation.Dispose();cancellation=null;}
    }
    private void CancelOcr(object sender,RoutedEventArgs e)=>cancellation?.Cancel();
    private void ShowParse(InvoiceParseResult parsed){Rows.Clear();var catalog=draft.Service.Products();foreach(var row in parsed.Rows)Rows.Add(new(row,catalog));StatusText.Text=parsed.Message;}
    private void ColumnChanged(object sender,SelectionChangedEventArgs e){if(!selectingColumn&&response is not null)ShowParse(InvoiceParser.Parse(response,(ColumnPicker.SelectedItem as QuantityColumn)?.Id));}
    private void NewProduct(object sender,RoutedEventArgs e)=>Ui.Try(()=>
    {if(((Button)sender).Tag is not OcrReviewRow row)return;var created=StockDialogs.Product(this,draft.Service,zeroOnly:true,name:row.Name,spec:row.Spec);if(created is null)return;var products=draft.Service.Products();foreach(var r in Rows)r.RefreshProducts(products,r==row?created:null);});
    private void RemoveRow(object sender,RoutedEventArgs e){if(((Button)sender).Tag is OcrReviewRow row)Rows.Remove(row);}
    private void Locate(object sender,RoutedEventArgs e)
    {if(((Button)sender).Tag is not OcrReviewRow row)return;var box=row.Source.Box;Canvas.SetLeft(RowHighlight,box.Min(p=>p[0])*Zoom.Value);Canvas.SetTop(RowHighlight,box.Min(p=>p[1])*Zoom.Value);RowHighlight.Width=(box.Max(p=>p[0])-box.Min(p=>p[0]))*Zoom.Value;RowHighlight.Height=(box.Max(p=>p[1])-box.Min(p=>p[1]))*Zoom.Value;RowHighlight.Visibility=Visibility.Visible;RowHighlight.BringIntoView();}
    private void AddRows(object sender,RoutedEventArgs e)=>Ui.Try(()=>
    {
        if(busy||Rows.Count==0)throw new BusinessException("请识别照片并核对货品明细。");
        if(Rows.Any(r=>!r.Reviewed||!r.CanReview))throw new BusinessException("每行必须关联货品、填写有效整数数量，并勾选已核对。");
        // Validate every merged quantity before changing the draft, to avoid partial additions.
        var incoming=Rows.GroupBy(r=>r.Product!.Id).Select(g=>(Product:g.First().Product!,Quantity:g.Sum(r=>long.Parse(r.Quantity)))).ToList();
        foreach(var line in incoming){var old=draft.Lines.FirstOrDefault(l=>l.Product.Id==line.Product.Id);if(old is not null&&!IntegerInput.TryParse(old.Quantity,1,out _))throw new BusinessException("请先修正进货清单中的数量。");Rules.Quantity(checked(line.Quantity+(old is null?0:long.Parse(old.Quantity))));}
        // Snapshot processed photo now; later edits must not change this draft's evidence.
        var savedProcessed=Path.Combine(image.DirectoryPath,Guid.NewGuid().ToString("N")+".png");File.Copy(image.ProcessedPath,savedProcessed);
        foreach(var line in incoming)draft.Add(line.Product,line.Quantity);draft.Photos.Add(image.OriginalPath);draft.Photos.Add(savedProcessed);DialogResult=true;
    });
    private void Camera(object sender,RoutedEventArgs e)=>Ui.Try(()=>{var photo=CameraWindow.Capture(this,image.DirectoryPath);if(photo is not null)Load(photo);});
    private void CloseWindow(object sender,RoutedEventArgs e)=>Close();
    private void Dragged(object sender,DragEventArgs e){e.Effects=!busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private void Dropped(object sender,DragEventArgs e){if(e.Data.GetData(DataFormats.FileDrop) is string[] files&&files.Length==1)Load(files[0]);else StatusText.Text="每次请拖入一张照片。";}
}
