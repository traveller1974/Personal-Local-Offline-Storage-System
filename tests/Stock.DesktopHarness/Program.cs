using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Stock.Core;
using Stock.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var repo=Path.GetFullPath(args[0]);var output=Path.Combine(repo,"artifacts","v1.1","ui-performance");Directory.CreateDirectory(output);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        // Load styles into a plain Application; never run Stock.Desktop.App's real-data startup.
        var document=XDocument.Load(Path.Combine(repo,"src","Stock.Desktop","App.xaml"));XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources=new XElement(ns+"ResourceDictionary",new XAttribute(XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml"),document.Root!.Element(ns+"Application.Resources")!.Elements());
        app.Resources=(ResourceDictionary)XamlReader.Parse(resources.ToString());
        var success=false;MainWindow? window=null;
        Dispatcher.CurrentDispatcher.BeginInvoke(async()=>
        {
            var watch=Stopwatch.StartNew();var ticks=0;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};timer.Tick+=(_,_)=>ticks++;
            try
            {
                using var result=JsonDocument.Parse(File.ReadAllText(Path.Combine(repo,"artifacts","v1.1","performance","results.json")));var data=Path.GetFullPath(result.RootElement.GetProperty("database").GetString()!);
                if(!data.StartsWith(repo+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Performance data must stay in project artifacts");
                var service=await Task.Run(()=>new StockService(Path.GetDirectoryName(data)!));var model=new MainViewModel(service){Start=new DateTime(2026,10,5),End=new DateTime(2026,10,5)};
                window=new MainWindow(model){WindowStartupLocation=WindowStartupLocation.Manual,Left=-4000,Top=-4000,ShowInTaskbar=false};window.Show();
                var filter=new QueryFilter(new(2026,10,5),new(2026,10,5));timer.Start();watch.Restart();
                await Task.Run(()=>ExcelExporter.Export(service,filter,Path.Combine(output,"full.xlsx")));var exportMs=watch.Elapsed.TotalMilliseconds;var fullTicks=ticks;
                if(fullTicks<3||exportMs>=120000)throw new Exception("WPF must keep processing dispatcher events while exporting 200000 rows");
                using var cancellation=new CancellationTokenSource();ticks=0;watch.Restart();var cancelled=Path.Combine(output,"cancelled.xlsx");
                var work=Task.Run(()=>ExcelExporter.Export(service,filter,cancelled,token:cancellation.Token));await Task.Delay(75);var responseMs=watch.Elapsed.TotalMilliseconds;cancellation.Cancel();
                try{await work;throw new Exception("Large export should be cancelled");}catch(OperationCanceledException){}
                if(responseMs>1000||ticks==0||File.Exists(cancelled))throw new Exception("WPF cancellation must respond and leave no final workbook");
                success=true;File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{success=true,rows=200000,exportMs,dispatcherTicksDuringExport=fullTicks,cancelResponseMs=responseMs,cancellationVerified=true,realUserDataTouched=false},new JsonSerializerOptions{WriteIndented=true}));
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{success=false,error=ex.ToString()}));}
            finally{timer.Stop();window?.Close();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
        });
        Dispatcher.Run();return success?0:1;
    }
}
