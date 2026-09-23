using Avalonia;

using Avalonia.Media;

namespace OpenFan.Linux.App;

/// <summary>
/// One shared mutable accent brush. Every consumer (code-behind fields, AXAML DynamicResource
/// AccentBrush) points at these instances, so AccentTheme.Apply(hex) recolors the whole app live.
/// </summary>
public static class AccentTheme
{
    public const string DefaultHex = "#F0A03C";

    public static SolidColorBrush Accent { get; } = new(Color.Parse(DefaultHex));

    public static void Apply(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return;
        try
        {
            var c = Color.Parse(hex.Trim());
            Accent.Color = c;
            if (Application.Current?.Resources.TryGetValue("AccentBrush", out var res) == true
                && res is SolidColorBrush resourceBrush)
                resourceBrush.Color = c; // AXAML styles bound via DynamicResource follow live
        }
        catch { }
    }

    public static bool IsValidHex(string? hex)
    {
        try { Color.Parse((hex ?? "").Trim()); return true; } catch { return false; }
    }
}
