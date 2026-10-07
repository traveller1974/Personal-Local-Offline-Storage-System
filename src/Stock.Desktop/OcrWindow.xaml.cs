using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Stock.Core;
using OpenCvSharp;
using System.Windows.Media;
using Stock.Desktop.Controls;
using System.Text.Json;
using Recognition = Stock.Recognition;
using Point = System.Windows.Point;
using Window = System.Windows.Window;

namespace Stock.Desktop;

public partial class OcrWindow : Window
{
    public ObservableCollection<OcrReviewRow> Rows { get; }=[];
    public StockService Stock => draft.Service;
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
    private bool choosingProduct;
    private readonly Dictionary<OcrReviewRow,CancellationTokenSource> pendingMatches=[];
    public OcrWindow(Window owner,DraftViewModel draft,string directory)
    {
        InitializeComponent();Owner=owner;this.draft=draft;image=new(Path.Combine(directory,"photo-"+Guid.NewGuid().ToString("N")));DataContext=this;
        Closed+=(_,_)=>{closed=true;generation++;cancellation?.Cancel();CancelMatches();CancelSelection();image.Dispose();};
    }
    private void Choose(object sender,RoutedEventArgs e) {var d=new OpenFileDialog{Filter="货单照片|*.jpg;*.jpeg;*.png"};if(d.ShowDialog(this)==true)Load(d.FileName);}
    public void Load(string path)=>Ui.Try(()=>
    {
        if(busy)return;CancelSelection();image.Load(path);ReplaceRows([]);response=null;cloudResult=null;regions=null;
        ColumnPicker.ItemsSource=null;tableRange=false;UploadConfirm.IsChecked=false;TotalOverride.Text="";UpdateResultSummary();
        Zoom.Value=Math.Clamp(520.0/image.Width,.1,1.0);UpdateImage();ImageScroller.ScrollToHome();SuggestTable();
        StatusText.Text="请确认表格四角并应用，或手动框选。上传范围须包含表头、货品和分区合计，排除表外资料。";
    });
    private void UpdateImage(){if(image.Width==0)return;PhotoImage.Source=Ui.Bitmap(image.ProcessedPath);ImageHost.Width=image.Width*Zoom.Value;ImageHost.Height=image.Height*Zoom.Value;Overlay.Width=ImageHost.Width;Overlay.Height=ImageHost.Height;Selection.Visibility=RowHighlight.Visibility=Visibility.Collapsed;DrawCorners();}
    private void Zoomed(object sender,RoutedPropertyChangedEventArgs<double> e){if(image is not null){CancelSelection();UpdateImage();}}
    private void Invalidate(){generation++;ReplaceRows([]);response=null;cloudResult=null;regions=null;UploadConfirm.IsChecked=false;ColumnPicker.ItemsSource=null;TotalOverride.Text="";UpdateResultSummary();UpdateImage();ImageScroller.ScrollToHome();StatusText.Text="照片已处理，请确认截图范围后重新识别。";}
    private void RotateLeft(object sender,RoutedEventArgs e)=>Ui.Try(()=>{CancelSelection();image.Rotate(false);tableRange=false;Invalidate();SuggestTable();});
    private void RotateRight(object sender,RoutedEventArgs e)=>Ui.Try(()=>{CancelSelection();image.Rotate(true);tableRange=false;Invalidate();SuggestTable();});
    private void Reset(object sender,RoutedEventArgs e)=>Ui.Try(()=>{CancelSelection();image.Reset();tableRange=false;Invalidate();SuggestTable();});
    private void SuggestTable(){corners.Clear();var detected=image.DetectTable();if(detected is not null)corners.AddRange(detected);DrawCorners();}
    private void AutoTable(object sender,RoutedEventArgs e)=>Ui.Try(()=>{CancelSelection();SuggestTable();});
    private void SelectCorners(object sender,RoutedEventArgs e){if(image.Width==0)return;CancelSelection();corners.Clear();cornerMode=true;DrawCorners();StatusText.Text="依次点击左上、右上、右下、左下四角；然后应用四角。";}
    private void ApplyCorners(object sender,RoutedEventArgs e)=>Ui.Try(()=>{image.Perspective(corners.ToArray());CancelSelection();tableRange=true;corners.Clear();Invalidate();DrawCorners();});
    private void DrawCorners(){CornerOutline.Points=new PointCollection(corners.Select(p=>new System.Windows.Point(p.X*Zoom.Value,p.Y*Zoom.Value)));CornerOutline.Visibility=corners.Count>0?Visibility.Visible:Visibility.Collapsed;}
    private void EnableCrop(object sender,RoutedEventArgs e){CancelSelection();cropMode=image.Width>0;corners.Clear();DrawCorners();StatusText.Text="在左侧照片上按住鼠标框选表格，松开后预览，再点击应用裁剪。";}
    private void CancelSelection(){cropOrigin=null;cropMode=cornerMode=false;if(ImageHost.IsMouseCaptured)ImageHost.ReleaseMouseCapture();Selection.Visibility=Visibility.Collapsed;}
    private void CropCaptureLost(object sender,MouseEventArgs e){if(cropOrigin is not null)CancelSelection();}
    private Point BoundPoint(Point p)=>new(Math.Clamp(p.X,0,ImageHost.Width),Math.Clamp(p.Y,0,ImageHost.Height));
    private void CropStart(object sender,MouseButtonEventArgs e)
    {
        if(busy||image.Width==0)return;
        if(cornerMode){var p=e.GetPosition(ImageHost);corners.Add(new((float)Math.Clamp(p.X/ImageHost.Width*image.Width,0,image.Width-1),(float)Math.Clamp(p.Y/ImageHost.Height*image.Height,0,image.Height-1)));if(corners.Count==4)cornerMode=false;DrawCorners();return;}
        if(!cropMode)return;cropOrigin=BoundPoint(e.GetPosition(ImageHost));ImageHost.CaptureMouse();Selection.Visibility=Visibility.Visible;Selection.Width=Selection.Height=0;Canvas.SetLeft(Selection,cropOrigin.Value.X);Canvas.SetTop(Selection,cropOrigin.Value.Y);e.Handled=true;
    }
    private void CropMove(object sender,MouseEventArgs e){if(cropOrigin is not Point origin)return;var now=BoundPoint(e.GetPosition(ImageHost));Canvas.SetLeft(Selection,Math.Min(origin.X,now.X));Canvas.SetTop(Selection,Math.Min(origin.Y,now.Y));Selection.Width=Math.Abs(now.X-origin.X);Selection.Height=Math.Abs(now.Y-origin.Y);}
    private void CropEnd(object sender,MouseButtonEventArgs e)
    {
        if(cropOrigin is not Point origin)return;var end=e.GetPosition(ImageHost);CancelSelection();e.Handled=true;
        Ui.Try(()=>
        {
            var rect=CropSelection.FromDisplay(origin.X,origin.Y,end.X,end.Y,ImageHost.Width,ImageHost.Height,image.Width,image.Height);
            PreviewAndApplyCrop(rect);
        });
    }
    internal void PreviewAndApplyCrop(OpenCvSharp.Rect rect)
    {
        var preview=new CropPreviewWindow(this,image.PreviewCrop(rect),rect.Width,rect.Height);
        if(preview.ShowDialog()!=true){StatusText.Text="已取消裁剪，当前图片与核对内容保留。点击框选裁剪可重新选择。";return;}
        ApplyCrop(rect);
    }
    internal void ApplyCrop(OpenCvSharp.Rect rect){image.Crop(rect.X,rect.Y,rect.Width,rect.Height);tableRange=true;corners.Clear();Invalidate();DrawCorners();}
    private async void Recognize(object sender,RoutedEventArgs e)
    {
        if(image.Width==0){StatusText.Text="请先选择照片。";return;}if(busy)return;
        var offline=Offline.IsChecked==true;
        if(!offline&&(!tableRange||UploadConfirm.IsChecked!=true)){StatusText.Text="请先应用四角或手动裁剪，并确认只上传表格截图。";return;}
        try{if(!offline){image.PrepareCloudImage();UpdateImage();}}catch(Exception ex){StatusText.Text=ex.Message;return;}
        busy=true;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=false;CancelOcrButton.Visibility=Visibility.Visible;
        cancellation=new();var token=cancellation.Token;var requestGeneration=++generation;var preserveManual=Rows.Any(r=>r.HumanEdited||r.Reviewed);
        StatusText.Text=offline?"正在本机识别，最多等待120秒。":"正在识别；可随时取消。填写内容已保留。";
        try
        {
            if(offline)
            {var result=await new OcrClient().RecognizeAsync(image.ProcessedPath,token);if(closed||requestGeneration!=generation||token.IsCancellationRequested)return;if(preserveManual){StatusText.Text="识别已完成，已有人工修改的明细已保留。";return;}var parsed=InvoiceParser.Parse(result);var next=BuildOfflineRows(parsed);selectingColumn=true;ColumnPicker.ItemsSource=parsed.QuantityColumns;ColumnPicker.SelectedIndex=parsed.QuantityColumns.Count==1?0:-1;selectingColumn=false;ReplaceRows(next);response=result;cloudResult=null;TotalOverride.Text="";UpdateResultSummary();StatusText.Text=parsed.Message;}
            else
            {
                var configuration=RecognitionSettings.Load(draft.Service);
                var details=configuration.Provider==Recognition.RecognitionProviderKind.DeepSeek?Recognition.DetailImagePreparation.Prepare(image.ProcessedPath,Path.Combine(image.DirectoryPath,"details")):[];
                var result=await RecognitionSettings.RecognizeAsync(draft.Service,image.ProcessedPath,new(DetailImages:details),token);
                if(closed||requestGeneration!=generation||token.IsCancellationRequested)return;
                if(preserveManual){StatusText.Text="识别已完成，已有人工修改的明细已保留。可以继续填写并加入清单。";return;}
                ApplyCloudResult(result);
            }
        }
        catch(OperationCanceledException){StatusText.Text="识别已取消，库存未改变。";}
        catch(Exception ex){if(!closed)StatusText.Text=ex.Message+" 原有核对明细和草稿已保留，可主动重试或手动录入。";}
        finally{busy=false;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=true;CancelOcrButton.Visibility=Visibility.Collapsed;cancellation.Dispose();cancellation=null;}
    }
    private void CancelOcr(object sender,RoutedEventArgs e){generation++;cancellation?.Cancel();StatusText.Text="识别已取消，迟到响应不会覆盖明细。";}
    private List<OcrReviewRow> BuildOfflineRows(InvoiceParseResult parsed)=>parsed.Rows.Select(row=>new OcrReviewRow(row,draft.Service.ProductPage(search:row.Name).Items)).ToList();
    private void ReplaceRows(IReadOnlyList<OcrReviewRow> next)
    {
        CancelMatches();
        foreach(var row in Rows)row.PropertyChanged-=ReviewRowChanged;
        Rows.Clear();foreach(var row in next){Rows.Add(row);row.PropertyChanged+=ReviewRowChanged;}
    }
    private void ReviewRowChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(sender is not OcrReviewRow row)return;
        if(!choosingProduct&&e.PropertyName is nameof(OcrReviewRow.Name) or nameof(OcrReviewRow.Spec) or nameof(OcrReviewRow.Color) or nameof(OcrReviewRow.MaterialCode) or nameof(OcrReviewRow.Type))
            RefreshMatch(row,true);
        if(e.PropertyName is nameof(OcrReviewRow.Quantity) or nameof(OcrReviewRow.Type) or nameof(OcrReviewRow.Reviewed) or nameof(OcrReviewRow.CanReview))UpdateResultSummary();
    }
    private void UpdateResultSummary()
    {
        ColumnPicker.Visibility=cloudResult is null&&response is not null?Visibility.Visible:Visibility.Collapsed;
        var progress=$"共{Rows.Count}行，已自动填写{Rows.Count(r=>r.AutomaticallyFilled&&!r.HumanEdited)}行，已核对{Rows.Count(r=>r.Reviewed)}行，还有{Rows.Count(r=>!r.CanReview)}行需要补齐。";
        if(cloudResult is null)
        {
            ResultSummaryText.Text=Rows.Count==0?"识别后，在这里检查每行货品和本次进货数量。":progress+" 请对照照片检查。";
            SectionTotalsList.ItemsSource=null;ResultWarningsText.Text="";return;
        }
        ResultSummaryText.Text=progress+(cloudResult.ActualQuantityColumn?" 已找到照片上的本次发货数量列。":" 没有看清照片中的数量，请填写本次进货数量。加入清单时使用你填写的值。");
        var hasUnknown=Rows.Any(r=>r.Type==ProductType.Unknown);
        if(hasUnknown)ResultSummaryText.Text+=" 请先选择每行货品类型，再检查分类合计。";
        ResultWarningsText.Text=string.Join("；",cloudResult.Warnings);
        var types=new[]{ProductType.Vehicle,ProductType.Battery,ProductType.Charger,ProductType.Accessory};
        SectionTotalsList.ItemsSource=types.Select(type=>
        {
            var rows=Rows.Where(r=>r.Type==type).ToList();
            var calculated=!hasUnknown&&rows.All(r=>IntegerInput.TryParse(r.Quantity,0,out _))?rows.Sum(r=>long.Parse(r.Quantity)).ToString():"待核对";
            var stated=cloudResult.SectionTotals.TryGetValue(type,out var total)&&total.HasValue?total.Value.ToString():"未提供";
            var difference=calculated!="待核对"&&total.HasValue?long.Parse(calculated)-total.Value:(long?)null;
            return $"{Rules.TypeName(type)}：照片合计 {stated}；当前明细合计 {calculated} {Rules.Unit(type)}"+
                (difference.HasValue&&difference.Value!=0?$"；相差 {difference.Value:+0;-0} {Rules.Unit(type)}（请检查数量）":"");
        }).ToArray();
    }
    internal void ApplyCloudResult(RecognitionResult result)
    {
        var style=StockService.InvoiceStyle(result);
        choosingProduct=true;
        List<OcrReviewRow> next;
        try { next=result.Rows.Select(source=>
        {
            var match=draft.Service.MatchRecognition(source,style);var row=new OcrReviewRow(source,match.Candidates);
            if(match.Selected is not null)row.ApplyAutomaticProduct(match.Selected,match.Source,result.ActualQuantityColumn&&source.Issues.Count==0&&IntegerInput.TryParse(source.RawQuantity,0,out _));
            return row;
        }).ToList(); } finally{choosingProduct=false;}
        ReplaceRows(next);cloudResult=result;response=null;regions=null;RowHighlight.Visibility=Visibility.Collapsed;ColumnPicker.ItemsSource=null;TotalOverride.Text="";
        UpdateResultSummary();
        TechnicalDetails.Text=$"识别耗时{result.Elapsed.TotalSeconds:F1}秒，输入Token：{result.InputTokens?.ToString()??"未返回"}，输出Token：{result.OutputTokens?.ToString()??"未返回"}。";
        StatusText.Text="识别完成。请检查提示的内容，数量可以直接修改。加入清单后，最后点击“确认入库”。";
    }
    private void ShowParse(InvoiceParseResult parsed){var next=BuildOfflineRows(parsed);ReplaceRows(next);UpdateResultSummary();StatusText.Text=parsed.Message;}
    private void Associate(object sender,RoutedEventArgs e)=>Ui.Try(()=>{if(((Button)sender).Tag is not OcrReviewRow row)return;var p=QueryDialogs.PickProduct(this,draft.Service);if(p is not null)ChooseExistingProduct(row,p);});
    private void ProductSelected(object sender,SelectionChangedEventArgs e)
    {
        if(choosingProduct||sender is not ComboBox {DataContext:OcrReviewRow row,SelectedItem:Product selected} box||selected==row.Product)return;
        Ui.Try(()=>ChooseExistingProduct(row,selected));
        box.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateTarget();
    }
    internal bool ChooseExistingProduct(OcrReviewRow row,Product selected)
    {
        if(!selected.Active||row.Cloud&&!selected.Complete)
        {StatusText.Text=!selected.Active?"这个货品已停用，请选择启用的货品。":"这个货品资料未填完整，请先在库存首页编辑并补全。";return false;}
        if(row.IdentityDifferences(selected).Count>0&&new ProductChoiceWindow(this,row,selected).ShowDialog()!=true)return false;
        ApplyChosenProduct(row,selected);
        return true;
    }
    private void ApplyChosenProduct(OcrReviewRow row,Product selected)
    {
        CancelMatch(row);choosingProduct=true;
        try
        {
            row.Type=selected.Type;row.Name=selected.Name;row.Spec=selected.Spec;row.Color=selected.Color??"";row.MaterialCode=selected.MaterialCode??"";
            row.RefreshProducts(row.Products.Where(x=>x.Id!=selected.Id).Append(selected).ToList(),selected);row.Reviewed=false;
        }
        finally{choosingProduct=false;}
        UpdateResultSummary();
    }
    private void TermPicked(object sender,RoutedEventArgs e)
    {if(sender is ProductTermBox { DataContext: OcrReviewRow row }&&!busy)RefreshMatch(row,false);}
    private async void RefreshMatch(OcrReviewRow row,bool debounce)
    {
        CancelMatch(row);
        if(closed||busy||!Rows.Contains(row))return;
        using var request=new CancellationTokenSource();pendingMatches[row]=request;row.Matching=true;
        var token=request.Token;
        var identity=(row.Type,row.Name,row.Spec,row.Color,row.MaterialCode);
        var currentGeneration=generation;
        try
        {
            if(debounce)await Task.Delay(150,token);
            var products=await Task.Run(()=>draft.Service.MatchRecognitionProducts(identity.Type,identity.Name,identity.Spec,identity.Color,identity.MaterialCode,row.Cloud,token),token);
            if(closed||busy||token.IsCancellationRequested||currentGeneration!=generation||!Rows.Contains(row)||identity!=(row.Type,row.Name,row.Spec,row.Color,row.MaterialCode))return;
            choosingProduct=true;try{row.RefreshProducts(products);}finally{choosingProduct=false;}
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!closed&&!token.IsCancellationRequested)StatusText.Text="暂时无法查找已有货品，请点击“搜索已有货品”。"+ex.Message;}
        finally{if(pendingMatches.TryGetValue(row,out var pending)&&pending==request){pendingMatches.Remove(row);row.Matching=false;}}
    }
    private void CancelMatch(OcrReviewRow row){if(pendingMatches.Remove(row,out var pending))pending.Cancel();row.Matching=false;}
    private void CancelMatches(){foreach(var row in pendingMatches.Keys.ToArray())CancelMatch(row);}
    private void ReviewClicked(object sender,RoutedEventArgs e)
    {
        if(sender is not CheckBox {DataContext:OcrReviewRow row} box)return;
        var requested=box.IsChecked==true;
        row.Reviewed=requested;
        box.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
        if(requested&&!row.Reviewed)
        {StatusText.Text=$"第{Rows.IndexOf(row)+1}行还需处理："+string.Join("；",row.ReviewProblems.Select(p=>p.Message));FocusProblem(row);}
        else StatusText.Text=requested?$"第{Rows.IndexOf(row)+1}行已核对。":$"第{Rows.IndexOf(row)+1}行已取消核对。";
        UpdateResultSummary();
    }
    private void FocusProblem(OcrReviewRow row)
    {
        ReviewRows.UpdateLayout();
        if(ReviewRows.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement card)return;
        var field=row.ReviewProblems.FirstOrDefault()?.Field;
        FrameworkElement? target=field switch
        {
            "Quantity"=>Children<QuantityBox>(card).FirstOrDefault(),
            "Type"=>Children<ComboBox>(card).FirstOrDefault(c=>c.Name=="ProductTypePicker"),
            "Unit"=>Children<CheckBox>(card).FirstOrDefault(c=>c.Name=="ConfirmUnit"),
            "Name" or "Spec" or "Color"=>Children<ProductTermBox>(card).FirstOrDefault(c=>c.TermField==field),
            "MaterialCode"=>Children<TextBox>(card).FirstOrDefault(c=>c.Name=="ProductCode"),
            _=>Children<Button>(card).FirstOrDefault(c=>c.Name=="SearchExistingProduct")
        };
        target??=Children<CheckBox>(card).FirstOrDefault(c=>c.Name=="ReviewedCheck");
        if(target is null)return;
        target.BringIntoView();
        if(target is QuantityBox quantity)((TextBox)quantity.FindName("Input")).Focus();
        else if(target is ProductTermBox term)term.Editor.Focus();else target.Focus();
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var descendant in Children<T>(child))yield return descendant;}
    }
    private void ColumnChanged(object sender,SelectionChangedEventArgs e){if(!selectingColumn&&response is not null){if(Rows.Any(r=>r.HumanEdited||r.Reviewed)){StatusText.Text="已有人工填写的明细已保留，请直接修改本次进货数量。";return;}ShowParse(InvoiceParser.Parse(response,(ColumnPicker.SelectedItem as QuantityColumn)?.Id));}}
    private void NewProduct(object sender,RoutedEventArgs e)=>Ui.Try(()=>
    {if(((Button)sender).Tag is not OcrReviewRow row)return;var created=StockDialogs.Product(this,draft.Service,zeroOnly:true,name:row.Name,spec:row.Spec,type:row.Type,color:row.Color,code:row.MaterialCode);if(created is not null)ApplyCreatedProduct(row,created);});
    internal void ApplyCreatedProduct(OcrReviewRow row,Product created)
    {
        ApplyChosenProduct(row,created);
    }
    private void RemoveRow(object sender,RoutedEventArgs e){if(((Button)sender).Tag is OcrReviewRow row){CancelMatch(row);row.PropertyChanged-=ReviewRowChanged;Rows.Remove(row);UpdateResultSummary();}}
    private async void Locate(object sender,RoutedEventArgs e)
    {
        if(((Button)sender).Tag is not OcrReviewRow row||busy)return;
        if(!row.Cloud){var box=row.Source.Box;if(box.Length==0)return;Highlight(box.Min(p=>p[0]),box.Min(p=>p[1]),box.Max(p=>p[0])-box.Min(p=>p[0]),box.Max(p=>p[1])-box.Min(p=>p[1]));return;}
        if(regions is null)
        {
            if(RecognitionSettings.Load(draft.Service).Provider==Recognition.RecognitionProviderKind.DeepSeek){StatusText.Text="请用左侧图片缩放对照，DeepSeek 暂不提供文字位置。";return;}
            if(MessageBox.Show(this,"文字定位将另发一次付费识别请求，只上传当前确认的截图。同一会话内复用结果。","文字定位",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            busy=true;cancellation=new();var requestGeneration=generation;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=false;CancelOcrButton.Visibility=Visibility.Visible;
            try{var result=await RecognitionSettings.Service(draft.Service).LocateAsync(image.ProcessedPath,cancellation.Token);if(closed||requestGeneration!=generation||cancellation.IsCancellationRequested)return;regions=result;}
            catch(Exception ex){if(!closed)StatusText.Text=ex.Message;return;}finally{busy=false;cancellation.Dispose();cancellation=null;Toolbar.IsEnabled=ReviewPanel.IsEnabled=RecognizeButton.IsEnabled=AddRowsButton.IsEnabled=true;CancelOcrButton.Visibility=Visibility.Collapsed;}
        }
        var targets=regions.Where(r=>Rules.Identity(r.Text)==Rules.Identity(row.CloudSource!.RawName)||Rules.Identity(r.Text)==Rules.Identity(row.Name)).ToList();
        if(targets.Count==1)
        {var r=targets[0];var radians=r.Angle*Math.PI/180;var width=Math.Abs(r.Width*Math.Cos(radians))+Math.Abs(r.Height*Math.Sin(radians));var height=Math.Abs(r.Width*Math.Sin(radians))+Math.Abs(r.Height*Math.Cos(radians));Highlight(r.CenterX-width/2,r.CenterY-height/2,width,height);StatusText.Text="仅匹配一个完整文字区域，请对照核实。";}
        else{RowHighlight.Visibility=Visibility.Collapsed;StatusText.Text=targets.Count==0?"没有明确文字匹配，请使用缩放对照。":$"找到{targets.Count}个同文字候选，请自行核对，未绑定任何一行。候选中心："+string.Join("；",targets.Select(r=>$"({r.CenterX:F0},{r.CenterY:F0})"));}
    }
    private void Highlight(double x,double y,double width,double height){Canvas.SetLeft(RowHighlight,x*Zoom.Value);Canvas.SetTop(RowHighlight,y*Zoom.Value);RowHighlight.Width=width*Zoom.Value;RowHighlight.Height=height*Zoom.Value;RowHighlight.Visibility=Visibility.Visible;RowHighlight.BringIntoView();}
    private void AddRows(object sender,RoutedEventArgs e)=>Ui.Try(AddReviewedRows);
    internal void AddReviewedRows()
    {
        ValidateCurrentForm();
        foreach(var group in Rows.GroupBy(r=>r.Product!.Id))Rules.Quantity(group.Sum(r=>long.Parse(r.Quantity)),true);
        var hash=image.ProcessedHash;if(draft.PhotoHashes.Contains(hash))throw new BusinessException("相同截图已经加入当前草稿，不能重复加入。");
        if((draft.Service.HasPhotoHash(hash)||draft.Service.HasPhotoHash(image.OriginalHash))&&MessageBox.Show(this,"相同照片或截图已用于历史单据，可能重复入库。确认这是另一张业务单据？","重复照片提示",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        // Snapshot processed photo now; later edits must not change this draft's evidence.
        var savedProcessed=Path.Combine(image.DirectoryPath,Guid.NewGuid().ToString("N")+Path.GetExtension(image.ProcessedPath));File.Copy(image.ProcessedPath,savedProcessed);
        var photoOrder=draft.PhotoHashes.Count+1;foreach(var row in Rows)draft.Lines.Add(new(row.Product!,row.Quantity){Reviewed=row.Reviewed,RecognitionEvidence=row.CloudSource,SourceRowId=row.RowId,HumanEdited=row.HumanEdited,FieldSources=row.FieldSources,InvoiceStyle=cloudResult is null?"":StockService.InvoiceStyle(cloudResult),PhotoOrder=photoOrder,OriginalOrder=row.CloudSource?.OriginalOrder??"",Marker=row.Marker,RawUnit=row.RawUnit,RawName=row.CloudSource?.RawName??row.Source.Name,PhotoTotalCorrection=TotalOverride.Text,ReviewNote=string.Join("；",new[]{TotalOverride.Text,row.UnitReviewNote}.Where(n=>n.Length>0))});
        if(cloudResult is not null)draft.PhotoTotals[photoOrder]=cloudResult.SectionTotals;
        draft.PhotoHashes.Add(hash);draft.Photos.Add(image.OriginalPath);draft.Photos.Add(savedProcessed);draft.PhotoMetadata[savedProcessed]=JsonSerializer.Serialize(new{photoOrder,image=JsonDocument.Parse(image.Metadata).RootElement.Clone(),recognition=cloudResult,reviewed=Rows.Select(r=>new{r.Name,r.Spec,r.Color,r.MaterialCode,r.Type,r.Quantity,r.RawUnit,r.SourceRawUnit,r.UnitConfirmed,r.UnitReviewNote,r.Marker}),totalCorrection=TotalOverride.Text});DialogResult=true;
    }
    internal void ValidateCurrentForm()
    {
        if(busy||Rows.Count==0)throw new BusinessException("请先识别照片，再检查每行货品和数量。");
        var unfinished=Rows.Where(r=>!r.CanReview).ToArray();
        if(unfinished.Length>0)
        {
            FocusProblem(unfinished[0]);
            var message=string.Join(Environment.NewLine,unfinished.Select(r=>$"第{Rows.IndexOf(r)+1}行："+string.Join("；",r.ReviewProblems.Select(p=>p.Message))));
            StatusText.Text=message;throw new BusinessException(message);
        }
    }
    private void AddManualRow(object sender,RoutedEventArgs e)
    {
        var row=new OcrReviewRow(new RecognizedRow("","","","","","",ProductType.Unknown,"","","","",[]),[]);
        Rows.Add(row);row.PropertyChanged+=ReviewRowChanged;UpdateResultSummary();
    }
    private void NextProblem(object sender,RoutedEventArgs e)
    {var row=Rows.FirstOrDefault(r=>!r.CanReview);if(row is not null)FocusProblem(row);else StatusText.Text="表单已填完整，可以加入进货清单。";}
    private void Camera(object sender,RoutedEventArgs e)=>Ui.Try(()=>{var photo=CameraWindow.Capture(this,image.DirectoryPath);if(photo is not null)Load(photo);});
    private void CloseWindow(object sender,RoutedEventArgs e)=>Close();
    private void Dragged(object sender,DragEventArgs e){e.Effects=!busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private void Dropped(object sender,DragEventArgs e){if(e.Data.GetData(DataFormats.FileDrop) is string[] files&&files.Length==1)Load(files[0]);else StatusText.Text="每次请拖入一张照片。";}
}
