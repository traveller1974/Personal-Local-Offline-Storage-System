using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Stock.Core;

namespace Stock.Desktop;

public partial class App : Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => { Ui.Error(args.Exception); args.Handled = true; };
        try
        {
            if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
            { ShutdownMode = ShutdownMode.OnExplicitShutdown; await DesktopSmoke.RunAsync(Path.GetFullPath(e.Args[1])); Shutdown(Environment.ExitCode); return; }
            if(e.Args.Length==2&&e.Args[0]=="--upgrade-test")
            {ShutdownMode=ShutdownMode.OnExplicitShutdown;await DesktopSmoke.UpgradeAsync(Path.GetFullPath(e.Args[1]));Shutdown(Environment.ExitCode);return;}
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalStockManager", "Data");
            instance = new Mutex(true, @"Local\LocalStockManager", out var created);
            if (!created) { MessageBox.Show("库存管理已经打开。请使用现有窗口。", "本地库存管理"); Shutdown(); return; }
            var service = await Task.Run(() => { var s = new StockService(directory); s.Maintain(); return s; });
            MainWindow = new MainWindow(new MainViewModel(service)); MainWindow.Show();
        }
        catch (Exception ex) { Ui.Error(ex); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
