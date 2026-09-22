using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OpenFan.Linux.App;

public sealed class App : Application
{
    public static FanApp? Hardware { get; private set; }
    public MainWindow? MainWin { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Hardware = new FanApp();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            MainWin = new MainWindow(Hardware);
            lifetime.MainWindow = MainWin;

            // Exit (tray or session end): restore hwmon auto + NVML default, persist (spec §3.2).
            lifetime.Exit += (_, _) =>
            {
                Hardware.RestoreAll();
                Hardware.Save();
                Hardware.Dispose(); // closes helper socket cleanly → helper restores our session
            };

            SetupTray(lifetime);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        // StatusNotifierItem tray; GNOME needs the AppIndicator extension —
        // if unavailable the window just stays visible (spec §12 mitigation).
        var open = new NativeMenuItem("Open");
        open.Click += (_, _) =>
        {
            MainWin?.Show();
            MainWin?.Activate();
        };

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            if (MainWin is not null) MainWin.Exiting = true;
            lifetime.Shutdown();
        };

        TrayIcon.SetIcons(this, new TrayIcons
        {
            new TrayIcon
            {
                Icon = MakeTrayIcon(),
                ToolTipText = "OpenFan",
                Menu = new NativeMenu { open, exit },
            },
        });    }

    private static WindowIcon MakeTrayIcon()
    {
        var wb = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            var px = new byte[32 * 32 * 4];
            for (var i = 0; i < 32 * 32; i++)
            {
                px[i * 4 + 0] = 0x4B; // B
                px[i * 4 + 1] = 0x4B; // G
                px[i * 4 + 2] = 0xE2; // R — accent #E24B4B
                px[i * 4 + 3] = 0xFF;
            }
            Marshal.Copy(px, 0, fb.Address, px.Length);
        }

        using var ms = new MemoryStream();
        wb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
