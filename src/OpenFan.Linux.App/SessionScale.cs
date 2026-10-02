using System.Globalization;
using System.Threading;
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
/// the toolkit's own scaling is treated as suspect. Automatic mode therefore only waits for the hint and lets Avalonia
/// read it unaided; a pinned number in Settings additionally makes the size identical at every boot, independent of
/// hint timing — which matters because automatic sizing was confirmed to work after an exit-and-restart but not
/// straight from login autostart on the real machine.
/// </remarks>
internal static class SessionScale
{
    /// <summary>How long automatic mode waits for the session's DPI hint before starting anyway.</summary>
    private const int HintWaitMs = 3000;

    /// <summary>Waits for the session hint when automatic; sets AVALONIA_SCREEN_SCALE_FACTORS only when pinned.</summary>
    public static void Apply(double pinnedPercent)
    {
        // An explicit environment variable wins: it is how someone debugging this would override us.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS")))
            return;

        if (pinnedPercent is not (> 0 and <= 400))
        {
            // Automatic: give the compositor a moment to publish its DPI hint, then let Avalonia read it itself.
            // Login autostart routinely beats GNOME to that hint, which is why automatic sizing works after an exit
            // and restart but not from the login session. Waiting changes only timing — nothing here hands the
            // toolkit a scale factor of our own, which is what an earlier version did and what drew an invisible
            // window on the real session.
            if (Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } && ReadXftDpi() is null)
            {
                var deadline = Environment.TickCount64 + HintWaitMs;
                while (Environment.TickCount64 < deadline && ReadXftDpi() is null)
                    Thread.Sleep(200);

                if (ReadXftDpi() is { } dpi)
                    Console.Error.WriteLine(
                        $"openfan: waited for the session scaling hint ({dpi:0} dpi ~ " +
                        $"{UiScale.PercentLabel(UiScale.FactorFromDpi(dpi) ?? 1)}); starting without it opens far too small");
            }

            return;   // never override the toolkit's own scaling in automatic mode
        }

        var connectors = DisplayConnectors.Detect();
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
