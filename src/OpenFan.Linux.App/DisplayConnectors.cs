using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenFan.Linux.App;

/// <summary>
/// Output connector names as the display server sees them. Needed because Avalonia's scale override is
/// per-connector and ignores wildcards, so a pinned window scale has to name every display.
/// </summary>
internal static class DisplayConnectors
{
    public static IReadOnlyList<string> Detect()
    {
        var fromMutter = Mutter();
        return fromMutter.Count > 0 ? fromMutter : Xrandr();
    }

    /// <summary>GNOME on Wayland: mutter's monitors array lists each connector by name.</summary>
    private static IReadOnlyList<string> Mutter()
    {
        var text = Run("gdbus", new[]
        {
            "call", "--session", "--dest", "org.gnome.Mutter.DisplayConfig",
            "--object-path", "/org/gnome/Mutter/DisplayConfig",
            "--method", "org.gnome.Mutter.DisplayConfig.GetCurrentState",
        });
        if (string.IsNullOrWhiteSpace(text)) return [];

        var tail = text.LastIndexOf("], [", StringComparison.Ordinal);
        if (tail < 0) return [];

        var names = new List<string>();
        foreach (Match m in MutterRx.Matches(text[tail..]))
            if (!names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
        return names;
    }

    // Monitors: 2\n0: +*DP-2 3840/600x2160/340+0+0  DP-2  ...   (X11 desktops without D-Bus)
    private static IReadOnlyList<string> Xrandr()
    {
        var text = Run("xrandr", new[] { "--listmonitors" });
        if (string.IsNullOrWhiteSpace(text)) return [];

        var names = new List<string>();
        foreach (Match m in XrandrRx.Matches(text))
            if (!names.Contains(m.Groups[1].Value)) names.Add(m.Groups[1].Value);
        return names;
    }

    private static readonly Regex MutterRx = new(@"\[\('([A-Za-z0-9\-_]+)'", RegexOptions.CultureInvariant);
    private static readonly Regex XrandrRx = new(@"[+*]([A-Za-z]+-\d+)", RegexOptions.CultureInvariant);

    /// <summary>Runs a helper under a hard time limit; never throws and never meaningfully delays startup.</summary>
    internal static string? Run(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(700))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return stdout.GetAwaiter().GetResult();
        }
        catch { return null; }   // no gdbus / no xrandr / no D-Bus: caller degrades to the wildcard
    }
}
