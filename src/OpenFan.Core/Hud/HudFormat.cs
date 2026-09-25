using System.Globalization;
using OpenFan.Core.Config;

namespace OpenFan.Core.Hud;

/// <summary>
/// Pure formatting/contrast logic for the desktop overlay (HUD). Deliberately free of Avalonia types so
/// it can be unit-tested headless — the window is just a renderer over these decisions.
/// </summary>
public static class HudFormat
{
    /// <summary>
    /// Unit implied by a sensor/source id, so tiles never need per-entry configuration. The backend has
    /// to be considered: on hwmon `:fan:` is a tachometer (RPM), while NVML's `:fan:` is a percentage and
    /// publishes RPM separately as `:tach:` — inferring from the substring alone crossed the two.
    /// </summary>
    public static string UnitFor(string sourceId)
    {
        var id = sourceId.ToLowerInvariant();
        var isNvml = id.StartsWith("nvml:", StringComparison.Ordinal);

        if (id.Contains("power", StringComparison.Ordinal)) return "W";
        if (id.Contains("temp", StringComparison.Ordinal)) return "°C";
        if (id.Contains(":tach:", StringComparison.Ordinal)) return "RPM";
        if (isNvml && id.Contains(":fan:", StringComparison.Ordinal)) return "%";
        if (!isNvml && id.Contains(":fan", StringComparison.Ordinal)) return "RPM";
        return "";
    }

    /// <summary>
    /// Value as shown. Whole numbers for watts/°C/RPM (a HUD tile has no room for 41.273), one decimal
    /// only where the unit is dimensionless-ish and small. Null means "no reading" and renders as an
    /// em dash rather than a fabricated zero.
    /// </summary>
    public static string Value(string sourceId, double? value)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v)) return "—";
        var unit = UnitFor(sourceId);
        var num = unit switch
        {
            "W" or "°C" or "RPM" => v.ToString("0", CultureInfo.InvariantCulture),
            "%" => v.ToString("0", CultureInfo.InvariantCulture),
            _ => v.ToString("0.#", CultureInfo.InvariantCulture),
        };
        return unit.Length == 0 ? num : $"{num} {unit}";
    }

    /// <summary>Short human label for a source id, e.g. "GPU 2 temp" — the tile already shows the value.</summary>
    public static string LabelFor(string sourceId, string? gpuName = null, int? gpuIndex = null)
    {
        if (sourceId.Equals("cpu:power:w", StringComparison.OrdinalIgnoreCase)) return "CPU power";
        if (sourceId.Equals("cpu:pptcap:w", StringComparison.OrdinalIgnoreCase)) return "CPU cap";
        if (sourceId.Equals("cpu:temp:c", StringComparison.OrdinalIgnoreCase)) return "CPU temp";

        if (sourceId.StartsWith("nvml:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = sourceId.Split(':');
            var what = parts.Length > 3 ? parts[3] : "";
            var who = gpuName is { Length: > 0 } n ? Shorten(n)
                  : gpuIndex is int i ? $"GPU {i + 1}"
                  : "GPU";
            return what switch
            {
                "temp" => $"{who} temp",
                "power" => $"{who} power",
                "fan" => $"{who} fan",
                "tach" => $"{who} RPM",
                _ => $"{who} {what}",
            };
        }

        // hwmon ids: chip + label carry the meaning ("nct6799 / temp4").
        var hp = sourceId.Split(':');
        if (hp.Length >= 4 && hp[0].Equals("hwmon", StringComparison.OrdinalIgnoreCase))
        {
            var label = hp.Length > 4 ? hp[4] : "";
            return label.Length > 0 ? $"{hp[1]} {label}" : $"{hp[1]} {hp[3]}";
        }
        return sourceId;
    }

    private static string Shorten(string name)
    {
        var n = name.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("RTX ", "RTX ", StringComparison.OrdinalIgnoreCase);
        return n.Length <= 14 ? n : n[..13].TrimEnd() + "…";
    }
}

/// <summary>Colour helpers for tiles the user colours themselves.</summary>
public static class HudTheme
{
    /// <summary>
    /// Text colour that stays readable on an arbitrary user-chosen tile background: relative luminance
    /// (ITU-R BT.601 as used by WCAG's simpler variant) rather than a naive "is it dark" hue guess.
    /// </summary>
    public static string TextColorFor(string backgroundHex)
    {
        if (!TryParse(backgroundHex, out var r, out var g, out var b)) return "#FFFFFF";
        var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        return luminance > 0.62 ? "#141414" : "#FFFFFF";
    }

    /// <summary>Accepts #rgb or #rrggbb; anything else is false so callers can fall back to a default.</summary>
    public static bool TryParse(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim();
        if (s[0] == '#') s = s[1..];
        if (s.Length == 3) s = new string([s[0], s[0], s[1], s[1], s[2], s[2]]);
        if (s.Length != 6) return false;
        for (var i = 0; i < 6; i++) if (!Uri.IsHexDigit(s[i])) return false;
        r = Convert.ToInt32(s.Substring(0, 2), 16);
        g = Convert.ToInt32(s.Substring(2, 2), 16);
        b = Convert.ToInt32(s.Substring(4, 2), 16);
        return true;
    }

    /// <summary>Default tile colours offered in the picker — chosen to stay legible with either text colour.</summary>
    public static readonly string[] Palette =
    [
        "#F0A03C", // accent amber
        "#4FC3F7", // light blue (VRAM bar)
        "#B388FF", // purple (RAM bar)
        "#7BC97B", // ok green
        "#E5C07B", // warn amber
        "#EF6B6B", // bad red
        "#2C363D", // card border dark
        "#171B1F", // window dark
    ];
}

/// <summary>
/// First-run tiles, so enabling the overlay shows something useful instead of an empty strip. Kept in
/// Core and taking plain data so it is testable; the app feeds it the GPUs it actually found.
/// </summary>
public static class HudDefaults
{
    public sealed record GpuRef(string Uuid, int Index, string Name);

    /// <summary>Rotated so neighbouring tiles differ; with more GPUs than colours a repeat is unavoidable.</summary>
    private static readonly string[] GpuPalette = ["#4FC3F7", "#B388FF", "#7BC97B", "#E5C05B"];

    /// <summary>CPU socket power + package temp, then each GPU's board power and temp.</summary>
    public static List<HudTileSettings> Seed(IReadOnlyList<GpuRef> gpus)
    {
        var tiles = new List<HudTileSettings>
        {
            new() { SourceId = "cpu:power:w", Label = "CPU power", ColorHex = "#F0A03C" },
            new() { SourceId = "cpu:temp:c", Label = "CPU temp", ColorHex = "#EF6B6B" },
        };

        foreach (var g in gpus)
        {
            var who = gpus.Count > 1 ? $"GPU {g.Index + 1}" : "GPU";
            tiles.Add(new HudTileSettings
            {
                SourceId = $"nvml:{g.Uuid}:power:w", Label = $"{who} power",
                ColorHex = GpuPalette[g.Index % GpuPalette.Length],
            });
            tiles.Add(new HudTileSettings
            {
                SourceId = $"nvml:{g.Uuid}:temp:core", Label = $"{who} temp",
                ColorHex = GpuPalette[(g.Index + GpuPalette.Length / 2) % GpuPalette.Length],
            });
        }

        return tiles;
    }
}
