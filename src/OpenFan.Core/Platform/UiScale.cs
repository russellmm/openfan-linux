namespace OpenFan.Core.Platform;

/// <summary>
/// Scale-factor plumbing for sessions where the toolkit's automatic detection cannot be trusted.
/// Pure logic so it is testable without a display server.
/// </summary>
public static class UiScale
{
    /// <summary>
    /// Value for Avalonia's AVALONIA_SCREEN_SCALE_FACTORS from a pinned percentage, or null when the
    /// user left it on automatic (0) or entered something nonsensical.
    /// </summary>
    /// <param name="connectors">
    /// Output connector names as the display server reports them (DP-2, eDP-1, …). Avalonia's X11 backend
    /// matches this variable per connector and silently ignores a "*" wildcard — measured on GNOME/Wayland,
    /// "*=2.4" changed nothing while "DP-2=2.4;DP-3=2.4" worked — so callers should enumerate displays.
    /// The wildcard is still emitted as a last resort rather than dropping the user's pin entirely.
    /// </param>
    public static string? FactorsFromPercent(double percent, IEnumerable<string>? connectors = null)
    {
        if (percent is not (> 0 and <= 400)) return null;

        var factor = Round(percent / 100.0);
        var names = (connectors ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        return names.Count == 0
            ? $"*={factor}"
            : string.Join(";", names.Select(n => $"{n}={factor}"));
    }

    /// <summary>
    /// Scale factor implied by an Xft.dpi value, or null when it carries no information — unset, zero, or
    /// the 96 dpi default X reports before a HiDPI session has published its real value. That "not yet"
    /// state is what makes a login-autostarted app draw at scale 1 on a HiDPI panel.
    /// </summary>
    public static double? FactorFromDpi(double dpi) => dpi > 96.5 ? Math.Round(dpi / 96.0, 4) : null;

    /// <summary>
    /// Percentage label for a scale factor ("241.7%"). One decimal is kept deliberately: the value is
    /// compared against Xft.dpi/96 when diagnosing size problems, and rounding to whole percent hides it.
    /// </summary>
    public static string PercentLabel(double factor) =>
        factor > 0 ? Math.Round(factor * 100, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%" : "unknown";

    private static string Round(double v) => Math.Round(v, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
