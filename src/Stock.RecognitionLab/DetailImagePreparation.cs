using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Stock.Recognition;

// Shared WPF source, also compiled into the desktop application. Segments derive only from the confirmed screenshot.
internal static class DetailImagePreparation
{
    internal static IReadOnlyList<string> Prepare(string image,string directory)
    {
        using var stream=File.OpenRead(image);
        var bitmap=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
        var longImage=bitmap.PixelHeight>=bitmap.PixelWidth*2;
        if(!longImage&&(bitmap.PixelWidth<600||bitmap.PixelHeight<220))return [];
        // Wide tables can also have tiny characters. Shorter full-width views retain column and row order.
        var targetHeight=Math.Max(100,longImage?bitmap.PixelWidth:bitmap.PixelWidth/4);
        var count=Math.Min(8,(int)Math.Ceiling((double)bitmap.PixelHeight/targetHeight));
        if(count<2)return [];
        Directory.CreateDirectory(directory);
        var height=(int)Math.Ceiling((double)bitmap.PixelHeight/count);
        var images=new List<string>();
        for(var y=0;y<bitmap.PixelHeight;y+=height)
        {
            // Small overlaps protect text crossing an edge; model must merge by source position, never by name.
            var top=Math.Max(0,y-30);var bottom=Math.Min(bitmap.PixelHeight,y+height+30);
            var crop=new CroppedBitmap(bitmap,new Int32Rect(0,top,bitmap.PixelWidth,bottom-top));
            var encoder=new JpegBitmapEncoder{QualityLevel=95};encoder.Frames.Add(BitmapFrame.Create(crop));
            var path=Path.Combine(directory,$"detail-{images.Count:D2}.jpg");using var output=File.Create(path);encoder.Save(output);images.Add(path);
        }
        return images;
    }
}
