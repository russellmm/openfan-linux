using System.Globalization;
using OpenFan.Core.Platform;

namespace OpenFan.Linux.App;

/// <summary>
/// Applies an explicit window scale before Avalonia starts, and reports what the session is using.
/// </summary>
/// <remarks>
/// Why an explicit pin rather than automatic detection: under GNOME Wayland this app is an X11 client through
/// XWayland, and its size comes from the session's Xft.dpi hint — which login autostart can read before GNOME has
/// written it, giving scale 1.0 and a postage-stamp window that a manual launch does not have. An earlier attempt
/// waited for the hint and fed AVALONIA_SCREEN_SCALE_FACTORS to the toolkit; builds containing it drew an invisible
/// window on the real session (mapped, correctly sized, nothing painted) while rendering fine on Xvfb, so overriding
/// the toolkit's own scaling is treated as suspect. Automatic mode here therefore changes nothing at all — the app
/// behaves exactly as the known-good build — and the fix only engages when the user pins a number in Settings, which
/// also makes the size identical at every boot instead of depending on hint timing.
/// </remarks>
internal static class SessionScale
{
    /// <summary>Sets AVALONIA_SCREEN_SCALE_FACTORS when the user pinned a scale. Automatic does nothing.</summary>
    public static void Apply(double pinnedPercent)
    {
        // An explicit environment variable wins: it is how someone debugging this would override us.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS")))
            return;

        var connectors = pinnedPercent is > 0 and <= 400 ? DisplayConnectors.Detect() : [];
        var factors = UiScale.FactorsFromPercent(pinnedPercent, connectors);
        if (factors is null) return;   // automatic, or a nonsense pin: leave the toolkit entirely alone

        Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", factors);
        Console.Error.WriteLine(connectors.Count > 0
            ? $"openfan: window scale pinned to {pinnedPercent:0.#}% on [{string.Join(", ", connectors)}]"
            : $"openfan: window scale pinned to {pinnedPercent:0.#}% but no displays could be enumerated; " +
              "the toolkit may ignore it (it needs connector names, not a wildcard)");
    }

    /// <summary>
    /// Percentage the session's own Xft.dpi implies, for the Settings page to suggest — 232 dpi → 241.7%.
    /// Null when there is no usable hint, so the page never shows a made-up number.
    /// </summary>
    public static int? SuggestedPercent()
    {
        var factor = UiScale.FactorFromDpi(ReadXftDpi() ?? 0);
        return factor is null ? null : (int)Math.Round(factor.Value * 100);
    }

    /// <summary>The session's Xft.dpi from the X resource database, or null if unavailable.</summary>
    private static double? ReadXftDpi()
    {
        var text = DisplayConnectors.Run("xrdb", new[] { "-query" });
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("Xft.dpi", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (double.TryParse(line[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi))
                return dpi;
        }
        return null;
    }
}
