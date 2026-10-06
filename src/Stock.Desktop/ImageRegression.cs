using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using Stock.Core;
using CvRect=OpenCvSharp.Rect;
using Window=System.Windows.Window;

namespace Stock.Desktop;

/// <summary>Regression checks run only through the isolated developer smoke entry point.</summary>
internal static class ImageRegression
{
    internal static async Task RunAsync(Window owner,StockService service,string directory,Action<bool,string> check,Action<Window,string> capture)
    {
        Directory.CreateDirectory(directory);var source=Path.Combine(directory,"numbered-colors.png");
        using(var pixels=new Mat(240,400,MatType.CV_8UC3,new Scalar(0,0,255)))
        {
            Cv2.Rectangle(pixels,new CvRect(100,60,160,120),new Scalar(0,255,0),-1);
            Cv2.PutText(pixels,"OUTSIDE 1",new(5,30),HersheyFonts.HersheySimplex,.6,Scalar.Black,1);
            Cv2.PutText(pixels,"KEEP 2",new(115,110),HersheyFonts.HersheySimplex,.6,Scalar.Black,1);Cv2.ImWrite(source,pixels);
        }
        using var image=new ImageSession(Path.Combine(directory,"session"));image.Load(source);
        var rect=new CvRect(100,60,160,120);var originalHash=image.OriginalHash;var beforeHash=image.ProcessedHash;var metadata=image.Metadata;
        var oldBitmap=Ui.Bitmap(image.ProcessedPath);var preview=image.PreviewCrop(rect);var previewBitmap=Ui.Bitmap(preview);
        check(image.ProcessedHash==beforeHash&&image.Metadata==metadata,"Crop preview does not change image bytes or transformation metadata");
        check(previewBitmap.PixelWidth==160&&previewBitmap.PixelHeight==120&&Green(previewBitmap),"Crop preview contains only selected pixels and keeps aspect ratio");
        image.Crop(rect.X,rect.Y,rect.Width,rect.Height);var cropped=Ui.Bitmap(image.ProcessedPath);
        check(oldBitmap.PixelWidth==400&&cropped.PixelWidth==160&&cropped.PixelHeight==120&&Green(cropped),"Same-path crop refreshes WPF pixels without stretching cached original");
        using(var original=Cv2.ImRead(source))using(var expected=new Mat(original,rect))using(var actual=Cv2.ImRead(image.ProcessedPath))
            check(Cv2.Norm(expected,actual,NormTypes.INF)==0,"Every cropped pixel matches source ROI; outside content is absent");
        var upload=image.ProcessedHash;image.PrepareCloudImage();
        check(upload==image.ProcessedHash&&Green(Ui.Bitmap(image.ProcessedPath)),"Cloud preparation and display use identical small-image bytes");
        using(var client=new HttpClient(new UploadHandler(await File.ReadAllBytesAsync(image.ProcessedPath))))
        {
            try{await new QwenRecognitionService(client,new("test-only",QwenConfiguration.Beijing)).RecognizeAsync(image.ProcessedPath);}
            catch(BusinessException ex){check(ex.Message.Contains("响应结构"),"Request submits exactly the displayed processed bytes without real network access");}
        }
        image.Crop(10,10,80,60);check(Ui.Bitmap(image.ProcessedPath).PixelWidth==80,"Consecutive crops refresh the same image path");
        image.Rotate(true);check(Ui.Bitmap(image.ProcessedPath).PixelWidth==60&&image.Height==80,"Rotation after crop updates display dimensions");
        image.Reset();check(Ui.Bitmap(image.ProcessedPath).PixelWidth==400&&image.OriginalHash==originalHash,"Reset restores original pixels and never modifies the original");
        image.Perspective([new(100,60),new(259,60),new(259,179),new(100,179)]);
        check(image.Width==159&&image.Height==119&&Green(Ui.Bitmap(image.ProcessedPath)),"Perspective correction refreshes the selected region");
        foreach(var scale in new[]{.1,.5,1,2.5})
        {
            check(CropSelection.FromDisplay(100*scale,60*scale,260*scale,180*scale,400*scale,240*scale,400,240)==rect,$"Crop coordinates are exact at zoom {scale}");
            check(CropSelection.FromDisplay(260*scale,180*scale,100*scale,60*scale,400*scale,240*scale,400,240)==rect,$"Reverse crop coordinates are exact at zoom {scale}");
        }
        check(CropSelection.FromDisplay(-25,-30,120,90,200,120,400,240)==new CvRect(0,0,240,180),"Out-of-bounds drag clips both endpoints to the image intersection");
        check(CropSelection.FromDisplay(10.2,10.1,19.4,20.6,400,240,400,240)==new CvRect(10,10,10,11),"Fractional crop edges round outward without losing selected pixels");
        try{CropSelection.FromDisplay(1,1,2,2,400,240,400,240);check(false,"Small crop rejected");}catch(BusinessException){check(true,"Too-small crop is rejected without touching pixels");}

        var id=service.CreateProduct(ProductType.Vehicle,"兼容车","48V/20Ah","白","",0,0,Guid.NewGuid().ToString("N"));var product=service.GetProduct(id);
        var draft=new DraftViewModel(service,DocumentKind.Purchase);var window=new OcrWindow(owner,draft,Path.Combine(directory,"dialog"))
            {Left=-4000,Top=-4000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false};
        try
        {
            window.Load(source);window.Show();window.UpdateLayout();window.Zoom.Value=2.5;window.UpdateLayout();
            window.ImageScroller.ScrollToHorizontalOffset(150);window.ImageScroller.ScrollToVerticalOffset(80);window.UpdateLayout();
            var point=window.ImageHost.TranslatePoint(new System.Windows.Point(250,150),window.ImageScroller);
            var back=window.ImageScroller.TranslatePoint(point,window.ImageHost);
            check(Math.Abs(back.X-250)<.01&&Math.Abs(back.Y-150)<.01&&window.ImageScroller.HorizontalOffset>0,"Scrolled viewport coordinates translate back to image-relative selection");
            var sourceRow=new RecognizedRow("1","兼容车","兼容车","","48V/20Ah","白",ProductType.Vehicle,"成车","2","PC","",[]);
            window.Rows.Add(new(sourceRow,[product]));window.Rows[0].Reviewed=true;window.UploadConfirm.IsChecked=true;
            var row=window.Rows[0];var zoom=window.Zoom.Value;
            void SchedulePreview(bool apply,string file)
            {
                owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>
                {
                    var dialog=Application.Current.Windows.OfType<CropPreviewWindow>().Single();dialog.UpdateLayout();
                    capture(dialog,Path.Combine(directory,file));
                    (apply?dialog.ApplyButton:dialog.CancelButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
            }
            SchedulePreview(false,"crop-preview-cancel.png");window.PreviewAndApplyCrop(rect);
            check(window.Rows.Count==1&&ReferenceEquals(window.Rows[0],row)&&row.Reviewed&&window.UploadConfirm.IsChecked==true&&((BitmapSource)window.PhotoImage.Source).PixelWidth==400,"Cancel in the real preview dialog preserves pixels, reviewed rows and upload consent");
            SchedulePreview(true,"crop-preview-apply.png");window.PreviewAndApplyCrop(rect);window.UpdateLayout();
            check(window.Rows.Count==0&&window.UploadConfirm.IsChecked==false&&window.Zoom.Value==zoom&&Green((BitmapSource)window.PhotoImage.Source),"Apply in the real preview dialog updates pixels and revokes old review and upload consent");
            check(window.ImageScroller.HorizontalOffset==0&&window.ImageScroller.VerticalOffset==0,"Applying crop resets scroll position while keeping zoom");
            capture(window,Path.Combine(directory,"cropped-display.png"));

            const string json="""{"actualQuantityColumn":true,"rows":[{"name":"兼容车","spec":"48V/20Ah","color":"白","type":"Vehicle","rawQuantity":"-1","rawUnit":"PC"}]}""";
            window.ApplyCloudResult(RecognitionParser.Parse(json));var review=window.Rows[0];review.Reviewed=true;
            check(!review.CanReview&&!review.Reviewed&&review.Quantity=="-1","Invalid cloud quantity stays visible and cannot be marked reviewed");
            review.Quantity="2";review.Reviewed=true;check(review.CanReview&&review.Reviewed,"Manual correction unlocks row review without requiring optional fields");
            review.RawUnit="";check(!review.Reviewed&&!review.CanReview,"Missing source unit blocks review until corrected");review.RawUnit="PC";review.Reviewed=true;
            window.ApplyCloudResult(RecognitionParser.Parse(json.Replace("\"actualQuantityColumn\":true,","")));window.Rows[0].Quantity="2";window.Rows[0].Reviewed=true;
            try{window.AddReviewedRows();check(false,"Unconfirmed actual column rejected");}catch(BusinessException ex){check(ex.Message.Contains("实发列")&&draft.Lines.Count==0,"Unconfirmed actual column blocks adding even manually reviewed rows");}
            var retained=window.Rows[0];
            try{window.ApplyCloudResult(new([sourceRow,sourceRow with{MaterialCode=null!}],new Dictionary<ProductType,long?>(),[],true));check(false,"Row-model build should fail");}
            catch(NullReferenceException){check(window.Rows.Count==1&&ReferenceEquals(window.Rows[0],retained),"Failed row construction preserves the entire previous review collection");}
            window.ApplyCloudResult(RecognitionParser.Parse(json));window.Rows[0].Quantity="2";window.Rows[0].Reviewed=true;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
            timer.Tick+=(_,_)=>{timer.Stop();window.AddReviewedRows();};timer.Start();
            // Exercise the actual add action in a modal window so DialogResult can close it.
            window.Hide();window.ShowDialog();
            check(draft.Lines.Count==1&&draft.Lines[0].Quantity=="2"&&service.GetProduct(id).Total==0,"Corrected and reviewed cloud row joins the draft without changing stock");
        }
        finally{window.Close();}
    }
    private static bool Green(BitmapSource bitmap){var bytes=new byte[4];bitmap.CopyPixels(new Int32Rect(0,0,1,1),bytes,4,0);return bytes[0]==0&&bytes[1]==255&&bytes[2]==0;}
    private sealed class UploadHandler(byte[] expected):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var data=body.RootElement.GetProperty("input").GetProperty("messages")[0].GetProperty("content")[0].GetProperty("image").GetString()!;
            if(!Convert.FromBase64String(data[(data.IndexOf(',')+1)..]).SequenceEqual(expected))throw new InvalidOperationException("Uploaded image differs from display");
            return new(HttpStatusCode.OK){Content=new StringContent("{}")};
        }
    }
}
