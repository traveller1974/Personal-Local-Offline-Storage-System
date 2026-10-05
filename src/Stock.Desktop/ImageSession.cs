using System.IO;
using OpenCvSharp;
using Stock.Core;

namespace Stock.Desktop;

public sealed class ImageSession : IDisposable
{
    private Mat? image;
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
        ProcessedPath=Path.Combine(DirectoryPath,Guid.NewGuid().ToString("N")+".png");image?.Dispose();image=candidate;Save();
    }
    public void Rotate(bool clockwise)
    {if(image is null)return;var next=new Mat();Cv2.Rotate(image,next,clockwise?RotateFlags.Rotate90Clockwise:RotateFlags.Rotate90Counterclockwise);image.Dispose();image=next;Save();}
    public void Crop(int left,int top,int width,int height)
    {
        if(image is null||width<5||height<5||left<0||top<0||left+width>Width||top+height>Height)throw new BusinessException("裁剪范围无效，请在照片内框选。");
        using var region=new Mat(image,new Rect(left,top,width,height));var next=region.Clone();image.Dispose();image=next;Save();
    }
    public void Reset() {if(image is null)return;image.Dispose();image=Cv2.ImDecode(File.ReadAllBytes(OriginalPath),ImreadModes.Color);Save();}
    private void Save(){if(image is not null){Cv2.ImEncode(".png",image,out var bytes);File.WriteAllBytes(ProcessedPath,bytes);}}
    public void Dispose()=>image?.Dispose();
}
