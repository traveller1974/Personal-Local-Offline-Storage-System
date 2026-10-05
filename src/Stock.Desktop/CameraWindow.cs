using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenCvSharp;
using Window = System.Windows.Window;
using Stock.Core;

namespace Stock.Desktop;

public static class CameraWindow
{
    public static string? Capture(Window owner,string directory)
    {
        string? saved=null;VideoCapture? camera=null;Mat? last=null;var gate=new object();var closed=false;var reading=false;
        var w=Ui.Dialog(owner,"摄像头拍照",900,700);var dock=new DockPanel{Margin=new(20)};var preview=new Image{Stretch=Stretch.Uniform};var text=Ui.Text("正在打开摄像头，请确保已在Windows设置中允许桌面应用使用摄像头。");DockPanel.SetDock(text,Dock.Top);dock.Children.Add(text);
        var shoot=Ui.Button("拍摄照片",()=>{lock(gate){if(last is null||last.Empty())throw new BusinessException("尚未获取摄像头画面。");saved=Path.Combine(directory,Guid.NewGuid().ToString("N")+".png");Cv2.ImEncode(".png",last,out var bytes);File.WriteAllBytes(saved,bytes);}w.DialogResult=true;},true);shoot.IsEnabled=false;
        var actions=Ui.Row(Ui.Button("取消",()=>w.Close()),shoot);DockPanel.SetDock(actions,Dock.Bottom);dock.Children.Add(actions);dock.Children.Add(preview);w.Content=dock;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(120)};
        timer.Tick+=async(_,_)=>
        {
            if(reading||closed)return;reading=true;
            try
            {
                var bytes=await Task.Run(()=>{lock(gate){if(camera is null||closed)return null;using var frame=new Mat();if(!camera.Read(frame)||frame.Empty())return null;last?.Dispose();last=frame.Clone();Cv2.ImEncode(".png",frame,out var result);return result;}});
                if(bytes is not null&&!closed){using var stream=new MemoryStream(bytes);var bitmap=new System.Windows.Media.Imaging.BitmapImage();bitmap.BeginInit();bitmap.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();preview.Source=bitmap;shoot.IsEnabled=true;}
            }
            catch(Exception ex){timer.Stop();text.Text="摄像头读取失败："+ex.Message+"。请关闭窗口后导入照片。";}
            finally{reading=false;}
        };
        w.Loaded+=async(_,_)=>
        {
            try {var opened=await Task.Run(()=>new VideoCapture(0,VideoCaptureAPIs.DSHOW));lock(gate){if(closed){opened.Dispose();return;}camera=opened;}if(!opened.IsOpened()){text.Text="没有可用摄像头，或摄像头权限被拒绝。请关闭此窗口，使用照片导入。";return;}text.Text="让货单充满画面，并保持文字清晰。";timer.Start();}
            catch(Exception ex){text.Text="无法打开摄像头："+ex.Message+"。照片导入和手动进货仍可使用。";}
        };
        w.Closed+=(_,_)=>{closed=true;timer.Stop();Task.Run(()=>{lock(gate){camera?.Dispose();last?.Dispose();}});};w.ShowDialog();return saved;
    }
}
