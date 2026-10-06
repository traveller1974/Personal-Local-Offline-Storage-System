using OpenCvSharp;
using Stock.Core;

namespace Stock.Desktop;

internal static class CropSelection
{
    public static Rect FromDisplay(double x1,double y1,double x2,double y2,double displayWidth,double displayHeight,int pixelWidth,int pixelHeight)
    {
        if(!new[]{x1,y1,x2,y2,displayWidth,displayHeight}.All(double.IsFinite)||displayWidth<=0||displayHeight<=0||pixelWidth<=0||pixelHeight<=0)
            throw new BusinessException("图片显示范围无效，请重新选择照片。");
        var left=(int)Math.Floor(Math.Clamp(Math.Min(x1,x2)/displayWidth,0,1)*pixelWidth);
        var top=(int)Math.Floor(Math.Clamp(Math.Min(y1,y2)/displayHeight,0,1)*pixelHeight);
        var right=(int)Math.Ceiling(Math.Clamp(Math.Max(x1,x2)/displayWidth,0,1)*pixelWidth);
        var bottom=(int)Math.Ceiling(Math.Clamp(Math.Max(y1,y2)/displayHeight,0,1)*pixelHeight);
        if(right-left<5||bottom-top<5)throw new BusinessException("框选范围过小，请在照片内选择至少5×5像素的区域。");
        return new(left,top,right-left,bottom-top);
    }
}
