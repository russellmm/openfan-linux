using Avalonia;
using Avalonia.Platform;
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
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

        // OPENFAN_RENDER works around windows that come up with an empty surface — invisible, but still on
        // top of everything and swallowing clicks. Seen under GNOME Wayland + XWayland, where the GLX-backed
        // surface can present nothing at all; Xvfb never shows it because there is no GLX there to begin with.
        //   software  → CPU rendering, no GL context
        //   retained  → GL kept, but Avalonia holds its own framebuffer instead of presenting per-frame
        var mode = Environment.GetEnvironmentVariable("OPENFAN_RENDER")?.Trim().ToLowerInvariant();
        if (mode is "software" or "retained")
        {
            Console.Error.WriteLine($"openfan: rendering workaround = {mode}");
            builder = builder.With(new X11PlatformOptions
            {
                RenderingMode = mode == "software" ? new[] { X11RenderingMode.Software } : null,
                UseRetainedFramebuffer = mode == "retained",
            });
        }

        return builder;
    }
}
