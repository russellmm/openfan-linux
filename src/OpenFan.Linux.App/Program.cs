using Avalonia;
using OpenFan.Core.Config;

namespace OpenFan.Linux.App;

internal static class Program
{
    // Single instance: exclusive lock on $XDG_RUNTIME_DIR/openfan.lock (spec §3.2).
    private static FileStream? _instanceLock;

    [STAThread]
    public static int Main(string[] args)
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
        var lockPath = Path.Combine(runtimeDir, "openfan.lock");
        try
        {
            _instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("OpenFan is already running (lock: " + lockPath + ")");
            return 1;
        }

        // Window scale must be decided before Avalonia reads the display, so it is applied here rather than in App.
        try
        {
            SessionScale.Apply(new SettingsStore(FanApp.ActiveConfigPathAtStartup()).Load().UiScalePercent);
        }
        catch { /* unreadable settings: automatic detection */ }

        AppDomain.CurrentDomain.UnhandledException += (_, a) => ErrorLog.Write("Unhandled", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { ErrorLog.Write("Unobserved task", a.Exception); a.SetObserved(); };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _instanceLock.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
