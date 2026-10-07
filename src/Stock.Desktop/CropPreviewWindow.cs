using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stock.Desktop;

internal sealed class CropPreviewWindow : Window
{
    internal Button ApplyButton { get; }
    internal Button CancelButton { get; }
    internal Image PreviewImage { get; }
    public CropPreviewWindow(Window owner,byte[] bytes,int width,int height)
    {
        Owner=owner;Title="裁剪预览";Width=900;Height=720;MinWidth=550;MinHeight=420;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var layout=new DockPanel{Margin=new Thickness(20)};
        var explanation=Ui.Text($"裁剪结果：{width} × {height} 像素。检查表头、全部货品和合计是否都在图片中，再点击应用裁剪。");
        DockPanel.SetDock(explanation,Dock.Top);layout.Children.Add(explanation);
        ApplyButton=Ui.Button("应用裁剪",()=>DialogResult=true,true);
        CancelButton=Ui.Button("取消",()=>DialogResult=false);CancelButton.IsCancel=true;
        var actions=Ui.Row(CancelButton,ApplyButton);actions.HorizontalAlignment=HorizontalAlignment.Right;
        DockPanel.SetDock(actions,Dock.Bottom);layout.Children.Add(actions);
        PreviewImage=new Image{Source=Ui.Bitmap(bytes),Stretch=Stretch.Uniform};
        layout.Children.Add(new Border{Background=new SolidColorBrush(Color.FromRgb(229,236,232)),Child=PreviewImage});
        Content=layout;
    }
}
