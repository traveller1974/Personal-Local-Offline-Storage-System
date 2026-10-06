using System.IO;
using System.Windows;

namespace Stock.RecognitionLab;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 1 && e.Args[0] == "--recognition-worker" ||
            e.Args.Length == 2 && e.Args[0] == "--worker-replay")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await WorkerHost.RunAsync(e.Args.Length == 2 ? e.Args[1] : null));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await LabSmoke.RunAsync(Path.GetFullPath(e.Args[1])); Shutdown(0); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(e.Args[1]);
                File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
