using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Stock.Core;
using OpenCvSharp;
using System.Windows.Media;
using System.Text.Json;
using Point = System.Windows.Point;
using Window = System.Windows.Window;

namespace Stock.Desktop;

public partial class OcrWindow : Window
{
    public ObservableCollection<OcrReviewRow> Rows { get; }=[];
    private readonly DraftViewModel draft;
    private readonly ImageSession image;
    private OcrResponse? response;
    private RecognitionResult? cloudResult;
    private IReadOnlyList<TextRegion>? regions;
    private readonly List<Point2f> corners=[];
    private bool cornerMode,tableRange,closed;
    private long generation;
    private CancellationTokenSource? cancellation;
    private bool selectingColumn,cropMode;
    private Point? cropOrigin;
    private bool busy;
    public OcrWindow(Window owner,DraftViewModel draft,string directory)
    {
        InitializeComponent();Owner=owner;this.draft=draft;image=new(Path.Combine(directory,"photo-"+Guid.NewGuid().ToString("N")));DataContext=this;
        Closed+=(_,_)=>{closed=true;generation++;cancellation?.Cancel();image.Dispose();};
    }
    private void Choose(object sender,RoutedEventArgs e) {var d=new OpenFileDialog{Filter="货单照片|*.jpg;*.jpeg;*.png"};if(d.ShowDialog(this)==true)Load(d.FileName);}
    public void Load(string path)=>Ui.Try(()=>{if(busy)return;image.Load(path);Rows.Clear();response=null;cloudResult=null;regions=null;ColumnPicker.ItemsSource=null;cropMode=false;tableRange=false;UploadConfirm.IsChecked=false;Zoom.Value=Math.Clamp(520.0/image.Width,.1,1.0);UpdateImage();SuggestTable();StatusText.Text="请确认表格四角并应用，或手动框选。上传范围须包含表头、货品和分区合计，排除表外资料。";});
    private void UpdateImage(){if(image.Width==0)return;PhotoImage.Source=Ui.Bitmap(image.ProcessedPath);ImageHost.Width=image.Width*Zoom.Value;ImageHost.Height=image.Height*Zoom.Value;Overlay.Width=ImageHost.Width;Overlay.Height=ImageHost.Height;Selection.Visibility=RowHighlight.Visibility=Visibility.Collapsed;DrawCorners();}
    private void Zoomed(object sender,RoutedPropertyChangedEventArgs<double> e){if(image is not null)UpdateImage();}
    private void Invalidate(){generation++;Rows.Clear();response=null;cloudResult=null;regions=null;UploadConfirm.IsChecked=false;ColumnPicker.ItemsSource=null;UpdateImage();StatusText.Text="照片已处理，请确认截图范围后重新识别。";}
    private void RotateLeft(object sender,RoutedEventArgs e)=>Ui.Try(()=>{image.Rotate(false);Invalidate();});
    private void RotateRight(object sender,RoutedEventArgs e)=>Ui.Try(()=>{image.Rotate(true);Invalidate();});
    private void Reset(object sender,RoutedEventArgs e)=>Ui.Try(()=>{image.Reset();tableRange=false;Invalidate();SuggestTable();});
    private void SuggestTable(){corners.Clear();var detected=image.DetectTable();if(detected is not null)corners.AddRange(detected);DrawCorners();}
    private void AutoTable(object sender,RoutedEventArgs e)=>Ui.Try(SuggestTable);
    private void SelectCorners(object sender,RoutedEventArgs e){if(image.Width==0)return;corners.Clear();cornerMode=true;DrawCorners();StatusText.Text="依次点击左上、右上、右下、左下四角；然后应用四角。";}
    private void ApplyCorners(object sender,RoutedEventArgs e)=>Ui.Try(()=>{image.Perspective(corners.ToArray());tableRange=true;cornerMode=false;corners.Clear();Invalidate();DrawCorners();});
    private void DrawCorners(){CornerOutline.Points=new PointCollection(corners.Select(p=>new System.Windows.Point(p.X*Zoom.Value,p.Y*Zoom.Value)));CornerOutline.Visibility=corners.Count>0?Visibility.Visible:Visibility.Collapsed;}
    private void EnableCrop(object sender,RoutedEventArgs e){cropMode=image.Width>0;StatusText.Text="在左侧照片上按住鼠标框选表格，松开后裁剪。";}
    private void CropStart(object sender,MouseButtonEventArgs e){if(busy)return;if(cornerMode){var p=e.GetPosition(ImageHost);corners.Add(new((float)Math.Clamp(p.X/Zoom.Value,0,image.Width-1),(float)Math.Clamp(p.Y/Zoom.Value,0,image.Height-1)));if(corners.Count==4)cornerMode=false;DrawCorners();return;}if(!cropMode)return;cropOrigin=e.GetPosition(ImageHost);ImageHost.CaptureMouse();Selection.Visibility=Visibility.Visible;Selection.Width=Selection.Height=0;Canvas.SetLeft(Selection,cropOrigin.Value.X);Canvas.SetTop(Selection,cropOrigin.Value.Y);}
    private void CropMove(object sender,MouseEventArgs e){if(cropOrigin is not Point origin)return;var now=e.GetPosition(ImageHost);Canvas.SetLeft(Selection,Math.Min(origin.X,now.X));Canvas.SetTop(Selection,Math.Min(origin.Y,now.Y));Selection.Width=Math.Abs(now.X-origin.X);Selection.Height=Math.Abs(now.Y-origin.Y);}
    private void CropEnd(object sender,MouseButtonEventArgs e)
    {
        if(cropOrigin is not Point origin)return;cropOrigin=null;ImageHost.ReleaseMouseCapture();cropMode=false;var end=e.GetPosition(ImageHost);var scale=Zoom.Value;
        Ui.Try(()=>{var left=Math.Clamp((int)(Math.Min(origin.X,end.X)/scale),0,image.Width-1);var top=Math.Clamp((int)(Math.Min(origin.Y,end.Y)/scale),0,image.Height-1);var width=Math.Min(image.Width-left,(int)(Math.Abs(end.X-origin.X)/scale));var height=Math.Min(image.Height-top,(int)(Math.Abs(end.Y-origin.Y)/scale));image.Crop(left,top,width,height);tableRange=true;corners.Clear();Invalidate();DrawCorners();});
    }
    private async void Recognize(object sender,RoutedEventArgs e)
    {
        if(image.Width==0){StatusText.Text="请先选择照片。";return;}if(busy)return;
        var offline=Offline.IsChecked==true;
        if(!offline&&(!tableRange||UploadConfirm.IsChecked!=true)){StatusText.Text="请先应用四角或手动裁剪，并确认只上传表格截图。";return;}
        try{if(!offline){image.PrepareCloudImage();UpdateImage();}}catch(Exception ex){StatusText.Text=ex.Message;return;}
        busy=true;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=false;CancelOcrButton.Visibility=Visibility.Visible;
        cancellation=new();var token=cancellation.Token;var requestGeneration=++generation;StatusText.Text=offline?"正在本机识别，最多等待120秒。":"正在调用百炼 qwen3.5-ocr，最多等待90秒；不会自动重试。";
        try
        {
            if(offline)
            {var result=await new OcrClient().RecognizeAsync(image.ProcessedPath,token);if(closed||requestGeneration!=generation||token.IsCancellationRequested)return;response=result;cloudResult=null;var parsed=InvoiceParser.Parse(result);selectingColumn=true;ColumnPicker.ItemsSource=parsed.QuantityColumns;ColumnPicker.SelectedIndex=parsed.QuantityColumns.Count==1?0:-1;selectingColumn=false;ShowParse(parsed);}
            else
            {var result=await QwenSettings.Service(draft.Service).RecognizeAsync(image.ProcessedPath,token);if(closed||requestGeneration!=generation||token.IsCancellationRequested)return;cloudResult=result;response=null;ColumnPicker.ItemsSource=null;Rows.Clear();foreach(var row in result.Rows)Rows.Add(new(row,draft.Service.ProductPage(new ProductFilter([row.Type],[row.Name],[row.Spec],[row.Color],row.MaterialCode)).Items));StatusText.Text=$"识别耗时{result.Elapsed.TotalSeconds:F1}秒，输入Token：{result.InputTokens?.ToString()??"未返回"}，输出Token：{result.OutputTokens?.ToString()??"未返回"}。全部明细需人工核对。"+string.Join("；",result.Warnings);}
        }
        catch(OperationCanceledException){StatusText.Text="识别已取消，库存未改变。";}
        catch(Exception ex){if(!closed)StatusText.Text=ex.Message+" 原有核对明细和草稿已保留，可主动重试或手动录入。";}
        finally{busy=false;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=true;CancelOcrButton.Visibility=Visibility.Collapsed;cancellation.Dispose();cancellation=null;}
    }
    private void CancelOcr(object sender,RoutedEventArgs e){generation++;cancellation?.Cancel();StatusText.Text="识别已取消，迟到响应不会覆盖明细。";}
    private void ShowParse(InvoiceParseResult parsed){Rows.Clear();foreach(var row in parsed.Rows)Rows.Add(new(row,draft.Service.ProductPage(search:row.Name).Items));StatusText.Text=parsed.Message;}
    private void Associate(object sender,RoutedEventArgs e)=>Ui.Try(()=>{if(((Button)sender).Tag is not OcrReviewRow row)return;var p=QueryDialogs.PickProduct(this,draft.Service);if(p is null)return;row.RefreshProducts(row.Products.Where(x=>x.Id!=p.Id).Append(p).ToList(),p);});
    private void ColumnChanged(object sender,SelectionChangedEventArgs e){if(!selectingColumn&&response is not null)ShowParse(InvoiceParser.Parse(response,(ColumnPicker.SelectedItem as QuantityColumn)?.Id));}
    private void NewProduct(object sender,RoutedEventArgs e)=>Ui.Try(()=>
    {if(((Button)sender).Tag is not OcrReviewRow row)return;var created=StockDialogs.Product(this,draft.Service,zeroOnly:true,name:row.Name,spec:row.Spec,type:row.Type,color:row.Color,code:row.MaterialCode);if(created is null)return;foreach(var r in Rows)r.RefreshProducts(r.Products.Where(x=>x.Id!=created.Id).Append(created).ToList(),r==row?created:null);});
    private void RemoveRow(object sender,RoutedEventArgs e){if(((Button)sender).Tag is OcrReviewRow row)Rows.Remove(row);}
    private async void Locate(object sender,RoutedEventArgs e)
    {
        if(((Button)sender).Tag is not OcrReviewRow row||busy)return;
        if(!row.Cloud){var box=row.Source.Box;if(box.Length==0)return;Highlight(box.Min(p=>p[0]),box.Min(p=>p[1]),box.Max(p=>p[0])-box.Min(p=>p[0]),box.Max(p=>p[1])-box.Min(p=>p[1]));return;}
        if(regions is null)
        {
            if(MessageBox.Show(this,"文字定位将另发一次付费识别请求，只上传当前确认的截图。同一会话内复用结果。","文字定位",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            busy=true;cancellation=new();var requestGeneration=generation;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=false;CancelOcrButton.Visibility=Visibility.Visible;
            try{var result=await QwenSettings.Service(draft.Service).LocateAsync(image.ProcessedPath,cancellation.Token);if(closed||requestGeneration!=generation||cancellation.IsCancellationRequested)return;regions=result;}
            catch(Exception ex){if(!closed)StatusText.Text=ex.Message;return;}finally{busy=false;cancellation.Dispose();cancellation=null;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=true;CancelOcrButton.Visibility=Visibility.Collapsed;}
        }
        var targets=regions.Where(r=>Rules.Identity(r.Text)==Rules.Identity(row.CloudSource!.RawName)||Rules.Identity(r.Text)==Rules.Identity(row.Name)).ToList();
        if(targets.Count==1)
        {var r=targets[0];var radians=r.Angle*Math.PI/180;var width=Math.Abs(r.Width*Math.Cos(radians))+Math.Abs(r.Height*Math.Sin(radians));var height=Math.Abs(r.Width*Math.Sin(radians))+Math.Abs(r.Height*Math.Cos(radians));Highlight(r.CenterX-width/2,r.CenterY-height/2,width,height);StatusText.Text="仅匹配一个完整文字区域，请对照核实。";}
        else{RowHighlight.Visibility=Visibility.Collapsed;StatusText.Text=targets.Count==0?"没有明确文字匹配，请使用缩放对照。":$"找到{targets.Count}个同文字候选，请自行核对，未绑定任何一行。候选中心："+string.Join("；",targets.Select(r=>$"({r.CenterX:F0},{r.CenterY:F0})"));}
    }
    private void Highlight(double x,double y,double width,double height){Canvas.SetLeft(RowHighlight,x*Zoom.Value);Canvas.SetTop(RowHighlight,y*Zoom.Value);RowHighlight.Width=width*Zoom.Value;RowHighlight.Height=height*Zoom.Value;RowHighlight.Visibility=Visibility.Visible;RowHighlight.BringIntoView();}
    private void AddRows(object sender,RoutedEventArgs e)=>Ui.Try(()=>
    {
        if(busy||Rows.Count==0)throw new BusinessException("请识别照片并核对货品明细。");
        if(Rows.Any(r=>!r.Reviewed||!r.CanReview))throw new BusinessException("每行必须关联货品、填写有效整数数量，并勾选已核对。");
        if(cloudResult?.ActualQuantityColumn==false)throw new BusinessException("未识别到实发列，请重新识别或手工录入。");
        var mismatches=cloudResult?.SectionTotals.Where(t=>t.Value.HasValue&&Rows.Where(r=>r.Type==t.Key).Sum(r=>long.Parse(r.Quantity))!=t.Value).ToList();
        if(mismatches?.Count>0&&string.IsNullOrWhiteSpace(TotalOverride.Text))throw new BusinessException("分区实发合计不一致。请修正明细，或明确核对原单合计有误并填写原因。");
        foreach(var group in Rows.GroupBy(r=>r.Product!.Id))Rules.Quantity(group.Sum(r=>long.Parse(r.Quantity)),true);
        var hash=image.ProcessedHash;if(draft.PhotoHashes.Contains(hash))throw new BusinessException("相同截图已经加入当前草稿，不能重复加入。");
        if((draft.Service.HasPhotoHash(hash)||draft.Service.HasPhotoHash(image.OriginalHash))&&MessageBox.Show(this,"相同照片或截图已用于历史单据，可能重复入库。确认这是另一张业务单据？","重复照片提示",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        // Snapshot processed photo now; later edits must not change this draft's evidence.
        var savedProcessed=Path.Combine(image.DirectoryPath,Guid.NewGuid().ToString("N")+Path.GetExtension(image.ProcessedPath));File.Copy(image.ProcessedPath,savedProcessed);
        var photoOrder=draft.PhotoHashes.Count+1;foreach(var row in Rows)draft.Lines.Add(new(row.Product!,row.Quantity){PhotoOrder=photoOrder,OriginalOrder=row.CloudSource?.OriginalOrder??"",Marker=row.Marker,RawUnit=row.RawUnit,RawName=row.CloudSource?.RawName??row.Source.Name,ReviewNote=TotalOverride.Text});
        if(cloudResult is not null)draft.PhotoTotals[photoOrder]=cloudResult.SectionTotals;
        draft.PhotoHashes.Add(hash);draft.Photos.Add(image.OriginalPath);draft.Photos.Add(savedProcessed);draft.PhotoMetadata[savedProcessed]=JsonSerializer.Serialize(new{image=JsonDocument.Parse(image.Metadata).RootElement.Clone(),recognition=cloudResult,reviewed=Rows.Select(r=>new{r.Name,r.Spec,r.Color,r.MaterialCode,r.Type,r.Quantity,r.RawUnit,r.Marker}),totalCorrection=TotalOverride.Text});DialogResult=true;
    });
    private void Camera(object sender,RoutedEventArgs e)=>Ui.Try(()=>{var photo=CameraWindow.Capture(this,image.DirectoryPath);if(photo is not null)Load(photo);});
    private void CloseWindow(object sender,RoutedEventArgs e)=>Close();
    private void Dragged(object sender,DragEventArgs e){e.Effects=!busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private void Dropped(object sender,DragEventArgs e){if(e.Data.GetData(DataFormats.FileDrop) is string[] files&&files.Length==1)Load(files[0]);else StatusText.Text="每次请拖入一张照片。";}
}
