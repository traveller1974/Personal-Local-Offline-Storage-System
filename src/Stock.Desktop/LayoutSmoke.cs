using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Stock.Core;

namespace Stock.Desktop;

/// <summary>Layout acceptance with synthetic data in its own database; no network or user settings.</summary>
internal static class LayoutSmoke
{
    internal static async Task RunStandaloneAsync(string output)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();
        void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add(label);}
        try
        {
            await RunAsync(null,output,Check,DesktopSmoke.Capture);
            File.WriteAllText(Path.Combine(output,"layout-results.json"),JsonSerializer.Serialize(new{success=true,checks,realNetworkRequests=0,userDataTouched=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){Environment.ExitCode=1;File.WriteAllText(Path.Combine(output,"layout-results.json"),JsonSerializer.Serialize(new{success=false,checks,error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));}
    }

    internal static async Task RunAsync(Window? owner,string output,Action<bool,string> check,Action<Window,string> capture)
    {
        Directory.CreateDirectory(output);
        var service=new StockService(Path.Combine(output,"run-"+Guid.NewGuid().ToString("N"),"Data"));
        var id=service.CreateProduct(ProductType.Vehicle,"飞驰一代","48V / 20Ah · 350W\n铝合金车架，前后碟刹；12寸轮胎，带后座与置物篮","薄荷绿 / 深灰拼色","FC-001",28,6,Guid.NewGuid().ToString("N"));
        service.CreateProduct(ProductType.Vehicle,"飞驰二代","60V / 32Ah · 500W\n加长座垫，前后液压减震；蓝牙解锁，LED仪表；加强型载物架","天蓝 / 珍珠白","FC-002",19,3,Guid.NewGuid().ToString("N"));
        service.CreateProduct(ProductType.Battery,"动力电池","48V20Ah / 四只一组 / 适配飞驰系列","","DC-048",46,0,Guid.NewGuid().ToString("N"));
        var model=new MainViewModel(service);
        var main=new MainWindow(model){Owner=owner,WindowStartupLocation=WindowStartupLocation.Manual,Left=-4000,Top=-4000,ShowInTaskbar=false};
        var dimensions=new List<object>();
        try
        {
            main.Show();await Idle(main);capture(main,Path.Combine(output,"库存新版.png"));
            check(model.AppVersion.StartsWith("v1.3.0"),"Displayed version follows the actual build version");
            var original=service.GetProduct(id);
            var longSpec=string.Concat(Enumerable.Repeat("48V20Ah型号-加长车架与加强载物架；",24));longSpec=longSpec[..Math.Min(490,longSpec.Length)]+"\n规格末尾标记";
            longSpec=longSpec.PadRight(500,'规')[..500];
            var longProduct=original with{Name=new string('货',120),Spec=longSpec,Color=new string('色',120),MaterialCode=new string('A',120),Warehouse=Rules.MaxQuantity,Store=0};
            model.Products.Insert(0,longProduct);
            for(var i=0;i<96;i++)model.Products.Add(original with{Id=1000+i,Name=$"测试货品{i:D3}"});
            foreach(var scenario in new[]{(1280d,820d,1d),(980d,650d,1d),(1366d,720d,1d),(1280d,680d,1.25),(1280d,680d,1.5),(1600d,940d,1d)})
            {
                main.MinWidth=0;main.MinHeight=0;main.Width=scenario.Item1*scenario.Item3;main.Height=scenario.Item2*scenario.Item3;
                ((FrameworkElement)main.Content).LayoutTransform=new ScaleTransform(scenario.Item3,scenario.Item3);
                await Idle(main);main.InventoryGrid.ScrollIntoView(longProduct);await Idle(main);
                var label=$"{scenario.Item1}x{scenario.Item2} scale {scenario.Item3}";
                CheckText(main.InventoryGrid,longProduct,"规格",check,label);
                main.InventoryGrid.ScrollIntoView(longProduct,main.InventoryGrid.Columns.Single(c=>(string)c.Header=="颜色"));await Idle(main);
                CheckText(main.InventoryGrid,longProduct,"颜色",check,label);
                main.InventoryGrid.ScrollIntoView(longProduct,main.InventoryGrid.Columns.Single(c=>(string)c.Header=="合计"));await Idle(main);
                CheckText(main.InventoryGrid,longProduct,"合计",check,label);
                var operations=main.InventoryGrid.Columns.Single(c=>(string)c.Header=="操作");main.InventoryGrid.ScrollIntoView(longProduct,operations);await Idle(main);
                var buttons=Children<Button>(operations.GetCellContent(longProduct)).ToList();
                check(buttons.Count==2&&buttons.All(b=>b.IsEnabled&&b.ActualWidth>=40&&b.ActualHeight>=30),"Inventory row actions remain visible and enabled at "+label);
                var presenter=operations.GetCellContent(longProduct);
                var actionBounds=buttons.Select(b=>new{label=b.Content,width=b.ActualWidth,right=b.TranslatePoint(new Point(b.ActualWidth,0),presenter).X}).ToList();
                var fit=actionBounds.All(b=>b.right<=presenter.ActualWidth+1);
                if(!fit){capture(main,Path.Combine(output,"operation-layout.png"));File.WriteAllText(Path.Combine(output,"operation-layout.json"),JsonSerializer.Serialize(new{cellWidth=operations.ActualWidth,presenterWidth=presenter.ActualWidth,buttons=actionBounds},new JsonSerializerOptions{WriteIndented=true}));}
                check(fit,"Inventory actions fit inside their cell at "+label);
                check(main.InventoryGrid.ActualHeight>=140,"Inventory retains usable scrolling height at "+label);
                dimensions.Add(new{width=scenario.Item1,height=scenario.Item2,scale=scenario.Item3,gridWidth=main.InventoryGrid.ActualWidth,rowHeight=((DataGridRow)main.InventoryGrid.ItemContainerGenerator.ContainerFromItem(longProduct)).ActualHeight});
            }
            ((FrameworkElement)main.Content).LayoutTransform=Transform.Identity;main.Width=1280;main.Height=820;await Idle(main);
            main.InventoryGrid.ScrollIntoView(longProduct,main.InventoryGrid.Columns[0]);await Idle(main);
            capture(main,Path.Combine(output,"库存长规格.png"));
            check(Children<DataGridRow>(main.InventoryGrid).Count()<model.Products.Count,"Large variable-height rows keep row virtualization");
            var shortRows=model.Products.Where(p=>p!=longProduct).ToList();model.Products.Clear();foreach(var product in shortRows.Take(3))model.Products.Add(product);await Idle(main);
            var narrowSpec=main.InventoryGrid.Columns[1].ActualWidth;main.Width=1600;await Idle(main);
            check(main.InventoryGrid.Columns[1].ActualWidth>narrowSpec+100,"Enlarging the window gives substantial extra width to specifications");
            check(main.InventoryGrid.Columns[0].ActualWidth<=181,"Product name stops consuming extra width");
            var scroll=Children<ScrollViewer>(main.InventoryGrid).First();
            check(scroll.ScrollableWidth<2,"Default inventory columns fit a wide viewport without horizontal scrolling");
            var originalWidth=main.InventoryGrid.Columns[1].Width;main.InventoryGrid.Columns[1].Width=new DataGridLength(350);await Idle(main);
            check(Math.Abs(main.InventoryGrid.Columns[1].ActualWidth-350)<2,"User column resizing remains supported");main.InventoryGrid.Columns[1].Width=originalWidth;
            var draft=new DraftWindow(main,service,DocumentKind.Purchase){WindowStartupLocation=WindowStartupLocation.Manual,Left=-4000,Top=-4000,ShowInTaskbar=false};
            try
            {
                draft.ViewModel.Add(original,5);draft.Show();await Idle(draft);
                CheckText(draft.DraftGrid,draft.ViewModel.Lines.Single(),"规格",check,"purchase draft");
                capture(draft,Path.Combine(output,"进货新版.png"));
            }
            finally{draft.Close();}
            var settings=main.CreateSettingsWindow();settings.WindowStartupLocation=WindowStartupLocation.Manual;settings.Left=-4000;settings.Top=-4000;settings.ShowInTaskbar=false;
            try
            {
                settings.Show();await Idle(settings);capture(settings,Path.Combine(output,"设置新版.png"));
                settings.Height=520;await Idle(settings);
                var close=Children<Button>(settings).Single(b=>Equals(b.Content,"关闭"));
                var point=close.TranslatePoint(new Point(0,close.ActualHeight),settings);
                check(point.Y<=settings.ActualHeight&&close.IsEnabled,"Settings close action remains reachable in a small window");
                check(Children<ScrollViewer>(settings).Any(v=>v.ScrollableHeight>0),"Settings body scrolls independently from bottom actions");
            }
            finally{settings.Close();}
            var colors=new[]{("Ink","Surface"),("Muted","PageBackground"),("Accent","Surface"),("Warning","Surface"),("DangerInk","Surface")};
            foreach(var (foreground,background) in colors)check(Contrast(((SolidColorBrush)Application.Current.FindResource(foreground)).Color,((SolidColorBrush)Application.Current.FindResource(background)).Color)>=4.5,$"{foreground} text meets 4.5:1 contrast on {background}");
            File.WriteAllText(Path.Combine(output,"layout-metrics.json"),JsonSerializer.Serialize(new{dimensions,actualMonitorDpi=VisualTreeHelper.GetDpi(main).PixelsPerInchX,scalingMethod="Actual monitor rendering plus WPF LayoutTransform simulations; simulations are not physical monitor tests",userDataTouched=false},new JsonSerializerOptions{WriteIndented=true}));
            check(service.GetProduct(id).Warehouse==28,"Visual layout checks do not change inventory");
        }
        finally{main.Close();}
    }

    private static void CheckText(DataGrid grid,object item,string header,Action<bool,string> check,string label)
    {
        var column=(DataGridTextColumn)grid.Columns.Single(c=>(string)c.Header==header);
        var text=column.GetCellContent(item) as TextBlock;
        check(text is not null&&text.Text.Length>0,header+" content is realized at "+label);
        var measure=new TextBlock{Text=text!.Text,FontFamily=text.FontFamily,FontSize=text.FontSize,FontWeight=text.FontWeight,TextWrapping=text.TextWrapping};
        measure.Measure(new Size(text.TextWrapping==TextWrapping.Wrap?text.ActualWidth:double.PositiveInfinity,double.PositiveInfinity));
        check(text.ActualHeight+2>=measure.DesiredSize.Height,header+" has enough height for its complete text at "+label);
        check(text.TextWrapping==TextWrapping.Wrap||text.ActualWidth+2>=measure.DesiredSize.Width,header+" has enough width for an unbroken value at "+label);
    }
    private static async Task Idle(DispatcherObject window){await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);if(window is Window w)w.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);}
    private static IEnumerable<T> Children<T>(DependencyObject? node)where T:DependencyObject
    {if(node is null)yield break;for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);if(child is T match)yield return match;foreach(var item in Children<T>(child))yield return item;}}
    private static double Contrast(Color a,Color b)
    {double L(Color c){double Channel(byte n){var v=n/255d;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}return .2126*Channel(c.R)+.7152*Channel(c.G)+.0722*Channel(c.B);}var x=L(a);var y=L(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
}
