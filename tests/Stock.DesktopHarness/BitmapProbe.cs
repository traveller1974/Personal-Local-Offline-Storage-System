using System.IO;
using System.Text.Json;
using OpenCvSharp;
using Stock.Desktop;

internal static class BitmapProbe
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var source=Path.Combine(output,"color-grid.png");
        using var pixels=new Mat(120,200,MatType.CV_8UC3,new Scalar(0,0,255));
        Cv2.Rectangle(pixels,new Rect(50,30,80,60),new Scalar(0,255,0),-1);
        Cv2.ImWrite(source,pixels);
        using var session=new ImageSession(Path.Combine(output,"session"));session.Load(source);
        var before=Ui.Bitmap(session.ProcessedPath);session.Crop(50,30,80,60);
        var after=Ui.Bitmap(session.ProcessedPath);
        var bytes=new byte[4];after.CopyPixels(new System.Windows.Int32Rect(0,0,1,1),bytes,4,0);
        File.WriteAllText(Path.Combine(output,"bitmap-probe.json"),JsonSerializer.Serialize(new {
            originalWidth=before.PixelWidth,fileWidth=session.Width,displayWidth=after.PixelWidth,
            displayHeight=after.PixelHeight,displayTopLeftBgr=bytes.Take(3),
            staleBitmapReproduced=after.PixelWidth!=session.Width,
            displayMatchesCrop=after.PixelWidth==80&&after.PixelHeight==60&&bytes[0]==0&&bytes[1]==255&&bytes[2]==0
        },new JsonSerializerOptions{WriteIndented=true}));
        return 0;
    }
}
