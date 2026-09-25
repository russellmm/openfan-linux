using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenFan.Core.Hud;

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
            SetupHud();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>MainWindow calls this when its header Apply-curves checkbox changes.</summary>
    public static Action? TrayApplySync { get; set; }

    /// <summary>HudWindow's right-click ▸ Configure: bring the main window forward.</summary>
    public static Action? ShowMainWindowRequested { get; set; }

    /// <summary>
    /// Raised when a HUD setting changes *outside* the Tray page (the overlay's own menu), so the page can
    /// re-read settings instead of showing a ticked checkbox for an overlay that was just hidden.
    /// </summary>
    public static Action? HudUiSync { get; set; }

    private HudWindow? _hud;

    /// <summary>
    /// Desktop overlay. Created lazily and refreshed from the existing control-loop tick, so the HUD
    /// and the fan curves always read the same sample rather than two timers drifting apart.
    /// </summary>
    private void SetupHud()
    {
        Hardware!.Ticked += () => _hud?.Refresh();
        ShowMainWindowRequested = () =>
        {
            MainWin?.Show();
            MainWin?.Activate();
        };
        if (Hardware.Settings.HudEnabled) ShowHud(visible: true);
    }

    /// <summary>Tray tab ▸ Overlay checkbox. Persists the choice so the overlay returns next session.</summary>
    public void SetHudVisible(bool visible)
    {
        if (visible && Hardware!.Settings.HudTiles.Count == 0)
        {
            Hardware.Settings.HudTiles = HudDefaults.Seed(
                Hardware.Nvml.SnapshotAll().Select(g => new HudDefaults.GpuRef(g.Uuid, g.Index, g.Name)).ToList());
        }

        Hardware.Settings.HudEnabled = visible;
        Hardware.Save();
        ShowHud(visible: visible);
    }

    /// <summary>Tray-page edits apply immediately — the overlay is its own preview.</summary>
    public void HudRefreshNow() => _hud?.Refresh();

    /// <summary>Applies the always-on-top preference without a rebuild (stacking is a window property).</summary>
    public void HudApplyTopMost()
    {
        if (_hud is not null) _hud.Topmost = Hardware!.Settings.HudTopMost;
    }

    private void ShowHud(bool visible)
    {
        if (!visible)
        {
            _hud?.Hide();
            return;
        }

        if (_hud is null)
        {
            _hud = new HudWindow(Hardware!);
            // Alt+F4 or a WM close destroys it; without this the field would hold a closed window and
            // re-enabling from the Tray page would silently do nothing.
            _hud.Closed += (_, _) => _hud = null;
        }
        if (!_hud.IsVisible) _hud.Show();
        _hud.ApplySavedPosition();   // the WM re-places borderless windows at map time
        _hud.Refresh();
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
