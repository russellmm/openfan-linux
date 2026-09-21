using System.Text.RegularExpressions;

namespace OpenFan.Core.Hardware;

/// <summary>
/// GPU display naming shared with Windows OpenFan (PCI bus in labels, e.g.
/// "RTX PRO 6000 Blackwell WS 11:00.0"). Driver-model helpers (WDDM/TCC/MCDM)
/// are Windows-only and intentionally absent here (spec §4.2).
/// </summary>
public static class GpuFormat
{
    public static string CompactPciBus(string? busId)
    {
        if (string.IsNullOrWhiteSpace(busId))
            return "";
        var s = busId.Trim();
        var parts = s.Split(':');
        if (parts.Length >= 3 && parts[0].All(c => c == '0'))
            return $"{parts[^2]}:{parts[^1]}";
        return s;
    }

    public static string ShortenGpuName(string name)
        => name
            .Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

    public static string GpuLabel(string name, string? pciBusId)
    {
        var shortName = ShortenGpuName(name);
        var bus = CompactPciBus(pciBusId);
        return bus.Length == 0 ? shortName : $"{shortName} {bus}";
    }

    public static bool ShouldAdoptInventoryName(string saved, string inventory)
    {
        if (string.IsNullOrWhiteSpace(saved))
            return true;
        if (saved.Equals(inventory, StringComparison.OrdinalIgnoreCase))
            return false;
        var noBus = PciBusToken.Replace(inventory, "");
        return saved.Equals(noBus, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex PciBusToken =
        new(@"\s+[0-9A-Fa-f]+:[0-9A-Fa-f]+\.[0-9A-Fa-f]+", RegexOptions.CultureInvariant);

    public static string JoinFans(IReadOnlyList<uint> values, string suffix)
    {
        if (values is null || values.Count == 0)
            return "—";
        return string.Join("·", values) + suffix;
    }

    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double n = bytes;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var i = 0;
        while (n >= 1024 && i < units.Length - 1)
        {
            n /= 1024;
            i++;
        }
        return i <= 1 ? $"{n:0} {units[i]}" : $"{n:0.##} {units[i]}";
    }

    public static string Throughput(uint kbPerSec)
    {
        if (kbPerSec >= 1024)
            return $"{kbPerSec / 1024.0:0.##} MiB/s";
        return $"{kbPerSec:0.##} KiB/s";
    }

    public static bool TryParseWatts(string? text, out int watts)
    {
        watts = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return int.TryParse(text.Trim(), out watts) && watts > 0;
    }

    public static int ClampWatts(int watts, double minW, double maxW)
    {
        var lo = (int)Math.Round(minW);
        var hi = (int)Math.Round(maxW);
        if (hi < lo)
            hi = lo;
        return Math.Clamp(watts, lo, hi);
    }
}
