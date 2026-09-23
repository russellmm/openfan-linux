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

    // Window geometry (Linux app): restored on launch, saved when the window moves/resizes/closes.
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public string AccentColor { get; set; } = AccentHex.Default;
    public string? NamedConfigPath { get; set; }
    public int RefreshMs { get; set; } = 1000;
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
