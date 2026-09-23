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
        AccentTheme.Apply(Hardware.Settings.AccentColor); // persisted accent, live from first paint

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

    /// <summary>MainWindow calls this when its header Apply-curves checkbox changes.</summary>
    public static Action? TrayApplySync { get; set; }

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

        var applyItem = new NativeMenuItem("Apply curves") { ToggleType = NativeMenuItemToggleType.CheckBox };
        applyItem.IsChecked = Hardware.Settings.ApplyCurves;
        applyItem.Click += (_, _) =>
        {
            Hardware.Settings.ApplyCurves = applyItem.IsChecked == true;
            Hardware.Save();
            MainWin?.SyncApplyCurvesBox(); // keep the header checkbox honest
        };
        TrayApplySync = () => applyItem.IsChecked = Hardware.Settings.ApplyCurves;

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            if (MainWin is not null) MainWin.Exiting = true;
            lifetime.Shutdown();
        };

        var tray = new TrayIcon
        {
            Icon = MakeTrayIcon(),
            ToolTipText = "OpenFan — fan control",
            Menu = new NativeMenu { open, applyItem, exit },
        };
        TrayIcon.SetIcons(this, new TrayIcons { tray });    }

    private static WindowIcon MakeTrayIcon()
    {
        try // the real fan icon (multi-size .ico incl. 16/32 px frames)
        {
            return new WindowIcon(Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://OpenFan.Linux.App/Assets/OpenFan.ico")));
        }
        catch { /* fall back to the drawn accent square below */ }

        var wb = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            var px = new byte[32 * 32 * 4];
            for (var i = 0; i < 32 * 32; i++)
            {
                px[i * 4 + 0] = 0x3C; // B
                px[i * 4 + 1] = 0xA0; // G
                px[i * 4 + 2] = 0xF0; // R — accent #F0A03C
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
