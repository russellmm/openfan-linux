using System.Globalization;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Reads the desired socket power limit (PPT) that the <c>hsmp-control apply</c> boot helper is
/// configured to re-assert. Read-only: this class never writes the limit or the config file.
/// </summary>
/// <remarks>
/// HSMP limits are volatile SMU state, so "persistence" on this platform means re-applying at
/// every boot rather than making a write stick. The single source of truth for that intent is
/// this small config file, written by an administrator (or a future OpenFan UI action) and read
/// by both the root boot helper and here, so the GUI can show desired vs live vs BIOS default.
/// No range policy lives here on purpose: the helper owns the accepted window, and duplicating
/// it in two languages would let them drift. A value the helper rejects simply shows up as
/// desired != live, which is the honest thing to display.
/// </remarks>
public sealed class HsmpLimitConfig(string? configPath = null)
{
    public const string DefaultConfigPath = "/etc/hsmp-control/ppt_mw";

    public string ConfigPath { get; } =
        configPath
        ?? Environment.GetEnvironmentVariable("HSMP_LIMIT_CONFIG")
        ?? DefaultConfigPath;

    /// <summary>The configured limit in milliwatts, or null when nothing is configured (file
    /// absent means "no override wanted", which is not an error) or the contents are not a
    /// positive integer.</summary>
    public int? ReadDesiredMw()
    {
        string text;
        try
        {
            text = File.ReadAllText(ConfigPath);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            return int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mw) && mw > 0
                ? mw
                : null;
        }
        return null;
    }
}
