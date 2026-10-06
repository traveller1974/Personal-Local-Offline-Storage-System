using System.IO;
using OpenCvSharp;
using Stock.Core;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stock.Desktop;

public sealed class ImageSession : IDisposable
{
    private Mat? image;
    private double[] transform=[1,0,0,0,1,0,0,0,1];
    public string OriginalHash => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(OriginalPath)));
    public string ProcessedHash => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProcessedPath)));
    public string Metadata => JsonSerializer.Serialize(new{originalHash=OriginalHash,processedHash=ProcessedHash,originalToProcessed=transform,width=Width,height=Height});
    public string OriginalPath { get; private set; }="";
    public string ProcessedPath { get; private set; }="";
    public string DirectoryPath { get; }
    public int Width=>image?.Width??0;
    public int Height=>image?.Height??0;
    public ImageSession(string directory) {DirectoryPath=Path.GetFullPath(directory);Directory.CreateDirectory(DirectoryPath);}
    public void Load(string source)
    {
        var extension=Path.GetExtension(source).ToLowerInvariant();if(extension is not(".jpg" or ".jpeg" or ".png"))throw new BusinessException("请选择JPG或PNG照片。");
        if(new FileInfo(source).Length>100*1024*1024)throw new BusinessException("照片超过100MB，请缩小后导入。");
        var candidate=Cv2.ImDecode(File.ReadAllBytes(source),ImreadModes.Color);
        if(candidate.Empty()||(long)candidate.Width*candidate.Height>60_000_000){candidate.Dispose();throw new BusinessException("照片无法读取或超过6000万像素。");}
        OriginalPath=Path.Combine(DirectoryPath,Guid.NewGuid().ToString("N")+extension);File.Copy(source,OriginalPath);
        ProcessedPath=Path.Combine(DirectoryPath,Guid.NewGuid().ToString("N")+".png");image?.Dispose();image=candidate;transform=[1,0,0,0,1,0,0,0,1];Save();
    }
    public void Rotate(bool clockwise)
    {if(image is null)return;Compose(clockwise?[0,-1,Height-1,1,0,0,0,0,1]:[0,1,0,-1,0,Width-1,0,0,1]);var next=new Mat();Cv2.Rotate(image,next,clockwise?RotateFlags.Rotate90Clockwise:RotateFlags.Rotate90Counterclockwise);image.Dispose();image=next;Save();}
    public void Crop(int left,int top,int width,int height)
    {
        if(image is null||width<5||height<5||left<0||top<0||left+width>Width||top+height>Height)throw new BusinessException("裁剪范围无效，请在照片内框选。");
        using var region=new Mat(image,new Rect(left,top,width,height));var next=region.Clone();Compose([1,0,-left,0,1,-top,0,0,1]);image.Dispose();image=next;Save();
    }
    public void Reset() {if(image is null)return;image.Dispose();image=Cv2.ImDecode(File.ReadAllBytes(OriginalPath),ImreadModes.Color);transform=[1,0,0,0,1,0,0,0,1];Save();}
    private void Compose(double[] next)
    {var result=new double[9];for(var r=0;r<3;r++)for(var c=0;c<3;c++)for(var k=0;k<3;k++)result[r*3+c]+=next[r*3+k]*transform[k*3+c];transform=result;}
    public Point2f[]? DetectTable()
    {
        if(image is null)return null;using var gray=new Mat();using var binary=new Mat();using var horizontal=new Mat();using var vertical=new Mat();using var mask=new Mat();
        Cv2.CvtColor(image,gray,ColorConversionCodes.BGR2GRAY);Cv2.AdaptiveThreshold(gray,binary,255,AdaptiveThresholdTypes.GaussianC,ThresholdTypes.BinaryInv,35,12);
        using var hk=Cv2.GetStructuringElement(MorphShapes.Rect,new Size(Math.Max(20,Width/25),1));using var vk=Cv2.GetStructuringElement(MorphShapes.Rect,new Size(1,Math.Max(20,Height/25)));
        Cv2.MorphologyEx(binary,horizontal,MorphTypes.Open,hk);Cv2.MorphologyEx(binary,vertical,MorphTypes.Open,vk);Cv2.BitwiseOr(horizontal,vertical,mask);
        using var bridge=Cv2.GetStructuringElement(MorphShapes.Rect,new Size(7,7));Cv2.MorphologyEx(mask,mask,MorphTypes.Close,bridge);
        Cv2.FindContours(mask,out Point[][] contours,out _,RetrievalModes.External,ContourApproximationModes.ApproxSimple);
        var contour=contours.OrderByDescending(c=>Cv2.ContourArea(c)).FirstOrDefault();if(contour is null||Cv2.ContourArea(contour)<Width*Height*.05)return null;
        var approx=Cv2.ApproxPolyDP(contour,Cv2.ArcLength(contour,true)*.025,true);
        Point2f[] points=approx.Length==4?approx.Select(p=>new Point2f(p.X,p.Y)).ToArray():Cv2.MinAreaRect(contour).Points();
        var topLeft=points.MinBy(p=>p.X+p.Y);var bottomRight=points.MaxBy(p=>p.X+p.Y);var topRight=points.MaxBy(p=>p.X-p.Y);var bottomLeft=points.MinBy(p=>p.X-p.Y);
        return [topLeft,topRight,bottomRight,bottomLeft];
    }
    public void Perspective(Point2f[] corners)
    {
        if(image is null||corners.Length!=4||corners.Any(p=>p.X<0||p.Y<0||p.X>=Width||p.Y>=Height))throw new BusinessException("四角必须在照片内。");
        var contour=corners.Select(p=>new Point((int)p.X,(int)p.Y)).ToArray();if(!Cv2.IsContourConvex(contour)||Math.Abs(Cv2.ContourArea(contour))<100)throw new BusinessException("请按左上、右上、右下、左下顺序选择四角，避免交叉。");
        static double Distance(Point2f a,Point2f b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
        var width=(int)Math.Max(Distance(corners[0],corners[1]),Distance(corners[3],corners[2]));var height=(int)Math.Max(Distance(corners[0],corners[3]),Distance(corners[1],corners[2]));
        if(width<5||height<5)throw new BusinessException("表格范围过小。");
        using var matrix=Cv2.GetPerspectiveTransform(corners,[new(0,0),new(width-1,0),new(width-1,height-1),new(0,height-1)]);
        var values=new double[9];for(var r=0;r<3;r++)for(var c=0;c<3;c++)values[r*3+c]=matrix.At<double>(r,c);Compose(values);
        var next=new Mat();Cv2.WarpPerspective(image,next,matrix,new Size(width,height));image.Dispose();image=next;Save();
    }
    public void PrepareCloudImage()
    {
        if(image is null)throw new BusinessException("请先导入图片。");
        // Follow the official recommended pixel count; retain small text whenever possible.
        if(Width<=10||Height<=10||Math.Max((double)Width/Height,(double)Height/Width)>200)throw new BusinessException("截图两边须大于10像素，宽高比不能超过200:1，请重新框选表格。");
        var scale=Math.Min(1,Math.Sqrt(15680000.0/((long)Width*Height)));
        if(scale<1){var next=new Mat();Cv2.Resize(image,next,new Size((int)(Width*scale),(int)(Height*scale)),0,0,InterpolationFlags.Area);Compose([scale,0,0,0,scale,0,0,0,1]);image.Dispose();image=next;}
        Cv2.ImEncode(".png",image,out var png);if(png.Length<=7*1024*1024&&4L*((png.Length+2L)/3)<=10*1024*1024){ProcessedPath=Path.ChangeExtension(ProcessedPath,".png");File.WriteAllBytes(ProcessedPath,png);return;}
        Cv2.ImEncode(".jpg",image,out var jpg,new ImageEncodingParam(ImwriteFlags.JpegQuality,95));
        if(jpg.Length>7*1024*1024||4L*((jpg.Length+2L)/3)>10*1024*1024)throw new BusinessException("截图编码超过10MB，请缩小范围后重新确认。");
        ProcessedPath=Path.ChangeExtension(ProcessedPath,".jpg");File.WriteAllBytes(ProcessedPath,jpg);
    }
    private void Save(){if(image is not null){ProcessedPath=Path.ChangeExtension(ProcessedPath,".png");Cv2.ImEncode(".png",image,out var bytes);File.WriteAllBytes(ProcessedPath,bytes);}}
    public void Dispose()=>image?.Dispose();
}
