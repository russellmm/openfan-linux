using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFan.Core.Config;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool StartAtLogin { get; set; }
    public bool StartMinimized { get; set; }
    public bool ApplyCurves { get; set; }

    /// <summary>Friendly display names for sensors, keyed by sensor id. Empty/absent = hardware name.</summary>
    public Dictionary<string, string> SensorAliases { get; set; } = [];

    /// <summary>GPU power limits in watts keyed by NVML uuid; re-applied at startup via helper.</summary>
    public Dictionary<string, int> GpuPowerLimitsW { get; set; } = [];

    /// <summary>CPU socket power limit (PPT) in watts; null = leave whatever firmware programmed.
    /// Re-applied at startup through the helper — the live HSMP value is volatile by design.</summary>
    public int? CpuPowerLimitW { get; set; }

    /// <summary>Single switch for CPU limit persistence. When true the limit is written to
    /// /etc/hsmp-control/ppt_mw so hsmp-control-apply.service re-asserts it early at boot, and this
    /// app re-asserts it on start as well. When false NOTHING may re-apply it after a boot — the
    /// firmware-programmed value stands, which is what "returns on next boot" promises the user.</summary>
    public bool CpuKeepAfterReboot { get; set; }

    /// <summary>Whether opening the app should overwrite whatever limit firmware programmed.
    /// Re-applying a saved limit while this is false silently defeats the checkbox: the limit would
    /// appear to survive reboot even though the user asked for it not to.</summary>
    [JsonIgnore]   // derived from the two settings above; persisting it would only invite drift
    public bool CpuLimitShouldReassertOnStart => CpuPowerLimitW is not null && CpuKeepAfterReboot;

    // Desktop overlay (HUD): borderless always-on-top strip of sensor tiles, HWiNFO64-style. The panel
    // tray cannot do this — Avalonia's TrayIcon exposes no text/label property, and Ubuntu's appindicator
    // extension renders only the legacy XAyatanaLabel with themed colour, so per-tile background colours
    // are impossible there (STATUS.md §4 records the evidence).
    public bool HudEnabled { get; set; }

    /// <summary>Ordered tiles; list order IS display order, so no separate index field to drift.</summary>
    public List<HudTileSettings> HudTiles { get; set; } = [];

    /// <summary>Tiles per row (1 = single column). The UI clamps this; 0/negative falls back to 1.</summary>
    public int HudColumns { get; set; } = 1;

    /// <summary>Overlay size multiplier (Settings ▸ Tray). Clamped by HudLayout.ClampScale.</summary>
    public double HudScale { get; set; } = 1.0;

    /// <summary>Keep the overlay above ordinary windows. Off lets it behave like a normal window so it can
    /// be covered when you want the desktop in front.</summary>
    public bool HudTopMost { get; set; } = true;

    // Overlay geometry: restored on launch, saved when the user drags it.
    public int? HudX { get; set; }
    public int? HudY { get; set; }

    // Window geometry (Linux app): restored on launch, saved when the window moves/resizes/closes.
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public string AccentColor { get; set; } = AccentHex.Default;
    public string? NamedConfigPath { get; set; }
    public int RefreshMs { get; set; } = 1000;
    /// <summary>Uniform scale for Home cards incl. text (Settings ▸ Card size). 1.0 = native.</summary>
    public double CardScale { get; set; } = 1.0;
    public int StartupDelaySeconds { get; set; }
    public SourceSettings Sources { get; set; } = new();
    public Dictionary<string, string> SensorNicknames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> PowerTargetsWatts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ReapplyPowerTargetsOnStart { get; set; } = true;
    public WindowSettings Window { get; set; } = new();
    public List<ControlSettings> Controls { get; set; } = [];

    /// <summary>User card order (control ids). Ids absent from the list keep their natural position.</summary>
    public List<string> ControlOrder { get; set; } = [];
    public List<CurveSettings> Curves { get; set; } = [];
}

/// <summary>
/// One overlay tile. <paramref name="SourceId"/> is either a normal sensor id ("hwmon:k10temp:0:temp:Tctl",
/// "nvml:&lt;uuid&gt;:temp:core") or a synthetic power id the app resolves itself ("cpu:power:w",
/// "cpu:pptcap:w", "nvml:&lt;uuid&gt;:power:w"), so the HUD can show socket/board power that the curve-facing
/// readings dictionary does not carry.
/// </summary>
public sealed class HudTileSettings
{
    public string SourceId { get; set; } = "";

    /// <summary>User override; empty means derive a short label from the source id.</summary>
    public string? Label { get; set; }

    /// <summary>Tile background. Text colour is derived for contrast (HudTheme.TextColorFor).</summary>
    public string ColorHex { get; set; } = AccentHex.Default;
}

public sealed class SourceSettings
{
    public bool Hwmon { get; set; } = true;

    /// <summary>hwmon chip names (e.g. "it87", "nvme") excluded from the inventory.</summary>
    public List<string> DisabledHwmonChips { get; set; } = [];

    public bool Nvml { get; set; } = true;
}

public sealed class WindowSettings
{
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 900;
    public bool Maximized { get; set; }
}

public sealed class ControlSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string? CurveId { get; set; }
    public string? PairedTachId { get; set; }
    public int MinPercent { get; set; }
    public bool Hidden { get; set; }
    public List<CalibrationSampleDto> Calibration { get; set; } = [];
}

public sealed class CurveSettings
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "flat";
    public string Name { get; set; } = "";
    public double Percent { get; set; }
    public string? SensorId { get; set; }
    public double HysteresisC { get; set; } = 2;
    public double HysteresisS { get; set; } = 1;
    public double MinTempC { get; set; } = 30;
    public double MaxTempC { get; set; } = 100;
    public int MaxSpeedPercent { get; set; } = 100;
    public string Function { get; set; } = "max";
    public List<string> ChildCurveIds { get; set; } = [];
    public List<CurvePointDto> Points { get; set; } = [];
}

public readonly record struct CurvePointDto(double TempC, double Percent);

public readonly record struct CalibrationSampleDto(int Percent, double Rpm, bool Avoid = false);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    /// <summary>$XDG_CONFIG_HOME/openfan/config.json (default ~/.config/openfan/config.json).</summary>
    public static string DefaultPath
    {
        get
        {
            var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrEmpty(xdgConfig)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdgConfig;
            return Path.Combine(root, "openfan", "config.json");
        }
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
            return new AppSettings();
        var json = File.ReadAllText(_path);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        loaded.Sources ??= new SourceSettings();
        loaded.SensorNicknames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        loaded.PowerTargetsWatts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        loaded.AccentColor = AccentHex.Normalize(loaded.AccentColor);
        if (loaded.CardScale is not (>= 0.6 and <= 1.3)) // also catches NaN/legacy 0
            loaded.CardScale = 1.0;
        return loaded;
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Copy(tmp, _path, overwrite: true);
        File.Delete(tmp);
    }
}
