using System.IO;
using System.Windows;

namespace XStoreManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? Value(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var smoke = args.Contains("--smoke-test");
        var screenshot = Value("--screenshot");
        var dataDir = Value("--data-dir");
        if (smoke && string.IsNullOrEmpty(dataDir)) dataDir = Path.Combine(Path.GetTempPath(), "XStoreManager-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var window = new MainWindow(dataDir, smoke);
            MainWindow = window;
            if (smoke || screenshot != null)
            {
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                window.Loaded += async (_, _) =>
                {
                    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    try
                    {
                        if (screenshot != null) window.Capture(screenshot);
                        if (smoke) window.VerifySmoke(dataDir!);
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        if (dataDir != null) { System.IO.Directory.CreateDirectory(dataDir); File.WriteAllText(Path.Combine(dataDir, "smoke-error.txt"), ex.ToString()); }
                        Shutdown(1);
                    }
                };
            }
            window.Show();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "XStore Manager could not start", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
}
