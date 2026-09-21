using Avalonia;

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
