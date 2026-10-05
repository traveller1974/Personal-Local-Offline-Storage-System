using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stock.Core;

namespace Stock.Desktop;

public static class Ui
{
    public static Button Button(string label, Action action, bool primary=false)
    { var b=new Button{Content=label};if(primary)b.Style=(Style)Application.Current.FindResource("Primary"); b.Click+=(_,_)=>Try(action);return b; }
    public static void Try(Action action) { try { action(); } catch(Exception ex) { Error(ex); } }
    public static async Task TryAsync(Func<Task> action) { try { await action(); } catch(Exception ex) { Error(ex); } }
    public static void Error(Exception ex)
    {
        var message=ex is BusinessException?ex.Message:ex is IOException or UnauthorizedAccessException?"无法完成操作，请检查文件是否被占用、目录权限和磁盘空间。\n"+ex.Message:"操作未完成，请重试。\n"+ex.Message;
        MessageBox.Show(message,"操作未完成",MessageBoxButton.OK,MessageBoxImage.Warning);
    }
    public static TextBlock Text(string text,bool title=false) => new(){Text=text,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,14),FontSize=title?22:14,FontWeight=title?FontWeights.SemiBold:FontWeights.Normal};
    public static StackPanel Row(params UIElement[] items) { var p=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,0,0,12)};foreach(var item in items)p.Children.Add(item);return p; }
    public static void Field(Panel p,string label,FrameworkElement input) { p.Children.Add(new TextBlock{Text=label,Margin=new(0,6,0,6)});input.Margin=new(0,0,0,10);p.Children.Add(input); }
    public static Window Dialog(Window owner,string title,double width=620,double height=650)
        => new(){Owner=owner,Title=title,Width=width,Height=height,MinWidth=Math.Min(width,550),MinHeight=Math.Min(height,420),WindowStartupLocation=WindowStartupLocation.CenterOwner};
    public static DataGrid Table(object items,params (string Label,string Path,double Width)[] columns)
    {
        var g=new DataGrid{ItemsSource=items as System.Collections.IEnumerable};
        foreach(var (label,path,width) in columns)g.Columns.Add(new DataGridTextColumn{Header=label,Binding=new Binding(path),Width=width<=0?new DataGridLength(1,DataGridLengthUnitType.Star):new(width)});
        return g;
    }
    public static BitmapImage Bitmap(string path)
    {
        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(Path.GetFullPath(path));image.EndInit();image.Freeze();return image;
    }
    public static void Photo(Window owner,string path)
    { var w=Dialog(owner,"货单照片",1000,760);w.Content=new ScrollViewer{Content=new Image{Source=Bitmap(path),Stretch=Stretch.Uniform},HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};w.ShowDialog(); }
    public static bool Confirm(Window owner,string title,string description,IReadOnlyList<StockImpact> impacts,Func<Task<string>> commit,out string? result)
    {
        string? saved=null;var w=Dialog(owner,title,1000,560);var dock=new DockPanel{Margin=new(24)};
        var text=Text(description);DockPanel.SetDock(text,Dock.Top);dock.Children.Add(text);
        var notice=Text("确认前请核对每种货品的规格、数量与库存变化。");DockPanel.SetDock(notice,Dock.Top);dock.Children.Add(notice);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,18,0,0)};
        var back=Button("返回编辑",()=>w.DialogResult=false);var save=new Button{Content=title,Style=(Style)Application.Current.FindResource("Primary")};actions.Children.Add(back);actions.Children.Add(save);DockPanel.SetDock(actions,Dock.Bottom);dock.Children.Add(actions);
        var rows=impacts.Select(i=>new{ i.Product.Name,i.Product.Spec,i.Product.Unit,Quantity=Math.Abs(i.WarehouseDelta),WarehouseBefore=i.Product.Warehouse,WarehouseAfter=i.WarehouseAfter,StoreBefore=i.Product.Store,StoreAfter=i.StoreAfter,TotalBefore=i.Product.Total,TotalAfter=i.TotalAfter }).ToList();
        dock.Children.Add(Table(rows,("货品","Name",0),("规格","Spec",95),("单位","Unit",55),("数量","Quantity",80),("仓库前","WarehouseBefore",90),("仓库后","WarehouseAfter",90),("店面前","StoreBefore",80),("店面后","StoreAfter",80),("合计前","TotalBefore",90),("合计后","TotalAfter",90)));
        var busy=false;w.Closing+=(_,e)=>{if(busy)e.Cancel=true;};
        save.Click+=async (_,_)=>{save.IsEnabled=false;back.IsEnabled=false;busy=true;try { saved=await commit();busy=false;w.DialogResult=true; }catch(Exception ex){Error(ex);}finally{busy=false;save.IsEnabled=true;back.IsEnabled=true;}};
        w.Content=dock;var ok=w.ShowDialog()==true;result=saved;return ok;
    }
}
