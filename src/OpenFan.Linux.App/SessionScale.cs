using System.Globalization;
using OpenFan.Core.Platform;

namespace OpenFan.Linux.App;

/// <summary>
/// Applies the user's pinned window scale before Avalonia starts.
/// </summary>
/// <remarks>
/// Under GNOME on Wayland this app is an X11 client through XWayland, and its size comes from whichever hint
/// is populated when it connects — normally Xft.dpi, which on this machine encodes monitor scale × text scaling
/// ÷ the XWayland oversampling (232/96 = 2.42 = 1.25 × 1.21 / 0.625). When that hint is missing the window
/// comes up at scale 1, which on a 4K panel is a postage stamp. Deriving the factor from mutter's per-monitor
/// scale instead was tried and rejected: it ignores text scaling *and* the oversampling, so it reproduces the
/// tiny-window bug rather than fixing it. So automatic means "trust Xft.dpi", and Settings offers an explicit
/// pin that survives reboots regardless of what the session publishes.
/// </remarks>
internal static class SessionScale
{
    /// <summary>How long to wait for a session that has not published its scaling hint yet (login autostart).</summary>
    private const int HintWaitMs = 2500;

    /// <summary>Sets AVALONIA_SCREEN_SCALE_FACTORS when needed. Never touches anything else.</summary>
    public static void Apply(double pinnedPercent)
    {
        // An explicit environment variable wins: it is how someone debugging this would override us.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS")))
            return;

        var connectors = DisplayConnectors.Detect();

        if (pinnedPercent is > 0 and <= 400)
        {
            var pinned = UiScale.FactorsFromPercent(pinnedPercent, connectors);
            if (pinned is not null)
            {
                Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", pinned);
                Console.Error.WriteLine(connectors.Count > 0
                    ? $"openfan: window scale pinned to {pinnedPercent:0.#}% on [{string.Join(", ", connectors)}]"
                    : $"openfan: window scale pinned to {pinnedPercent:0.#}% but no displays could be enumerated; " +
                      "the toolkit may ignore it (it needs connector names, not a wildcard)");
            }
            return;
        }

        // Automatic, with one exception. When started from login autostart, GNOME may not have written
        // Xft.dpi into the X resource database yet, and Avalonia then scales at 1.0 — a postage-stamp
        // window on a HiDPI panel, which is exactly what a manual launch minutes later does not have.
        // Plain X11 sessions set the hint before apps start, so only Wayland/XWayland waits.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;

        double? dpi = ReadXftDpi();
        if (UiScale.FactorFromDpi(dpi ?? 0) is not null) return;   // hint already there; Avalonia will read it too

        var deadline = Environment.TickCount64 + HintWaitMs;
        while (Environment.TickCount64 < deadline && UiScale.FactorFromDpi(dpi ?? 0) is null)
        {
            Thread.Sleep(200);
            dpi = ReadXftDpi();
        }

        var factor = UiScale.FactorFromDpi(dpi ?? 0);
        if (factor is null) return;   // session genuinely has no HiDPI scaling: leave detection alone

        var factors = UiScale.FactorsFromPercent(factor.Value * 100.0, connectors);
        if (factors is null) return;

        Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", factors);
        Console.Error.WriteLine(
            $"openfan: session published Xft.dpi {dpi:0.#} after startup; scaling at {factor:0.###}× " +
            $"({factors}) — a window that opens far too small at login is this race");
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
