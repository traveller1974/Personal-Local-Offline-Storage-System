using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stock.Core;
using Stock.Desktop.Controls;

namespace Stock.Desktop;

/// <summary>Explicit developer test entry point. Always uses a separate test data directory.</summary>
public static class DesktopSmoke
{
    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();var run=Path.Combine(output,"run-"+Guid.NewGuid().ToString("N"),"中文 含空格目录");Directory.CreateDirectory(run);
        MainWindow? main=null;
        void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);checks.Add(label);}
        string Key()=>Guid.NewGuid().ToString("N");
        try
        {
            var service=new StockService(Path.Combine(run,"Data"));service.Maintain();var a=service.CreateProduct("螺丝","M8","件",10,3,Key());var b=service.CreateProduct("螺帽","M10","件",2,0,Key());var model=new MainViewModel(service);
            main=new(model){WindowStartupLocation=WindowStartupLocation.Manual,Left=-4000,Top=-4000};main.Show();main.UpdateLayout();Check(model.Products.Count==2,"WPF main window and SQLite start under Chinese paths");
            var quantity=new QuantityBox();((Button)quantity.FindName("Plus")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(quantity.Value=="2","Quantity plus increments exactly 1");((Button)quantity.FindName("Minus")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(quantity.Value=="1","Quantity minus decrements exactly 1");
            foreach(var value in new[]{"","-1","1.5","1e3","2147483648"}){quantity.Value=value;Check(!quantity.IsValid,"Quantity rejects "+value);}quantity.Minimum=0;quantity.Value="0";Check(!((Button)quantity.FindName("Minus")).IsEnabled,"Zero lower bound disables minus");quantity.Value=int.MaxValue.ToString();Check(!((Button)quantity.FindName("Plus")).IsEnabled,"Maximum disables plus");
            var draft=new DraftViewModel(service,DocumentKind.Purchase);draft.Add(service.Products().Single(p=>p.Id==a),2);draft.Add(service.Products().Single(p=>p.Id==a),3);Check(draft.Lines.Count==1&&draft.Lines[0].Quantity=="5","Draft merges duplicate products");draft.Preview();Check(service.Products().Single(p=>p.Id==a).Warehouse==10,"Preview does not write stock");var buy=await draft.CommitAsync();Check(buy==await draft.CommitAsync(),"Duplicate confirmation produces one document");
            var sale=new DraftViewModel(service,DocumentKind.Sale);sale.Add(service.Products().Single(p=>p.Id==a),4);try{sale.Preview();Check(false,"Missing channel must be rejected");}catch(BusinessException){checks.Add("Sale requires explicit channel");}sale.Channel="零售";await sale.CommitAsync();service.Transfer(a,2,true,Key());Check(service.Products().Single(p=>p.Id==a) is{Warehouse:9,Store:5,Total:14},"Manual workflows produce warehouse9 store5 total14");service.ValidateIntegrity();
            model.Refresh();model.Query();Capture(main,Path.Combine(output,"库存首页.png"));
            var draftWindow=new DraftWindow(main,service,DocumentKind.Purchase);draftWindow.ViewModel.Add(service.Products().Single(p=>p.Id==a),5);draftWindow.Left=-4000;draftWindow.Top=-4000;draftWindow.WindowStartupLocation=WindowStartupLocation.Manual;draftWindow.Show();draftWindow.UpdateLayout();Capture(draftWindow,Path.Combine(output,"进货清单.png"));draftWindow.Close();Check(service.Products().Single(p=>p.Id==a).Warehouse==9,"Closing an unsubmitted window does not write stock");
            var sample=Path.Combine(AppContext.BaseDirectory,"Samples","示例货单.png");Check(File.Exists(sample),"Packaged invoice sample exists");using var images=new ImageSession(Path.Combine(run,"Temp"));images.Load(sample);var width=images.Width;for(var i=0;i<4;i++)images.Rotate(true);Check(images.Width==width,"OpenCV native image rotation works");images.Crop(0,0,images.Width-10,images.Height-10);Check(images.Width==width-10,"Image crop works");
            var result=await new OcrClient().RecognizeAsync(images.ProcessedPath);var parsed=InvoiceParser.Parse(result);Check(parsed.Rows.Count==2&&parsed.Rows[0].RawQuantity=="5","Bundled OCR and positional invoice parser exclude amounts and totals");
            var review=new OcrReviewRow(parsed.Rows[0],service.Products());Check(review.Product?.Id==a&&!review.Reviewed,"Exact matching never marks a row reviewed automatically");review.Reviewed=true;review.Quantity="6";Check(!review.Reviewed,"Editing reviewed quantity resets review");
            var ocrWindow=new OcrWindow(main,new DraftViewModel(service,DocumentKind.Purchase),Path.Combine(run,"OcrDialog"));ocrWindow.Load(sample);foreach(var row in parsed.Rows)ocrWindow.Rows.Add(new(row,service.Products()));ocrWindow.WindowStartupLocation=WindowStartupLocation.Manual;ocrWindow.Left=-4000;ocrWindow.Top=-4000;ocrWindow.Show();ocrWindow.UpdateLayout();Capture(ocrWindow,Path.Combine(output,"照片核对.png"));ocrWindow.Close();
            try{await new OcrClient{Timeout=TimeSpan.FromMilliseconds(1)}.RecognizeAsync(sample);Check(false,"OCR timeout must be reported");}catch(BusinessException ex){Check(ex.Message.Contains("超过"),"OCR timeout is contained and manual UI remains alive");}
            var noWorker=Path.Combine(run,"absent.exe");try{await new OcrClient{ExecutableOverride=noWorker}.RecognizeAsync(sample);Check(false,"Missing worker must be reported");}catch(BusinessException){checks.Add("Missing OCR component is contained");}
            var broken=Path.Combine(run,"BrokenWorker");Directory.CreateDirectory(broken);var brokenExe=Path.Combine(broken,"StockOcr.exe");File.Copy(Path.Combine(AppContext.BaseDirectory,"Ocr","StockOcr.exe"),brokenExe);
            try{await new OcrClient{ExecutableOverride=brokenExe}.RecognizeAsync(sample);Check(false,"Crashed worker must be reported");}catch(BusinessException ex){Check(ex.Message.Contains("意外退出"),"OCR process crash is contained and manual UI remains alive");}
            var export=Path.Combine(run,"导出 含空格.xlsx");await model.ExportAsync(export);Check(File.Exists(export),"Desktop filter exports to Excel under Chinese paths");var backup=Path.Combine(run,"备份.stockbackup");await model.BackupAsync(backup);await model.RestoreAsync(backup);service.ValidateIntegrity();Check(service.Products().Single(p=>p.Id==a).Total==14,"Desktop backup restore preserves balances");
            File.WriteAllText(Path.Combine(output,"desktop-results.json"),JsonSerializer.Serialize(new{success=true,checks,runtime=Environment.Version.ToString(),os=Environment.OSVersion.ToString(),data=run},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(output,"desktop-results.json"),JsonSerializer.Serialize(new{success=false,checks,error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=1;}
        finally{main?.Close();}
    }
    private static void Capture(Window window,string path)
    {
        var content=(FrameworkElement)window.Content;content.UpdateLayout();var width=(int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right);var height=(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom);
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle(window.Background,null,new Rect(0,0,width,height));bitmap.Render(background);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);
    }
}
