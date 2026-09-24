using System.Globalization;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Read-only CPU socket power telemetry provided by the Linux amd_hsmp_hwmon driver.
/// This class never opens /dev/hsmp and never writes to sysfs.
/// </summary>
public sealed class HsmpPowerReader(string hwmonRoot = "/sys/class/hwmon")
{
    public sealed record Reading(string Source, double? PowerW, double? PptCapW);

    public Reading? Read()
    {
        try
        {
            var chip = FindHsmpChipDirectory(hwmonRoot);
            if (chip is null) return null;
            var power = ReadMicrowatts(Path.Combine(chip, "power1_input"));
            var cap = ReadMicrowatts(Path.Combine(chip, "power1_cap"));
            return new Reading("amd_hsmp_hwmon", power, cap);
        }
        catch (IOException) { /* hwmon not mounted or removed during scan */ }
        catch (UnauthorizedAccessException) { /* hwmon inaccessible */ }
        return null;
    }

    /// <summary>Directory of the amd_hsmp_hwmon chip, or null when absent. Shared with
    /// <see cref="CpuPowerControl"/>, which writes that chip's power1_cap attribute.</summary>
    public static string? FindHsmpChipDirectory(string hwmonRoot)
    {
        try
        {
            foreach (var chip in Directory.EnumerateDirectories(hwmonRoot, "hwmon*").OrderBy(p => p, StringComparer.Ordinal))
            {
                string name;
                try { name = File.ReadAllText(Path.Combine(chip, "name")).Trim(); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (string.Equals(name, "amd_hsmp_hwmon", StringComparison.Ordinal))
                    return chip;
            }
        }
        catch (IOException) { /* hwmon not mounted or removed during scan */ }
        return null;
    }

    private static double? ReadMicrowatts(string path)
    {
        try
        {
            if (long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var uw) && uw >= 0)
                return uw / 1_000_000.0;
        }
        catch (IOException) { /* optional attribute absent or device removed */ }
        catch (UnauthorizedAccessException) { /* unreadable attribute */ }
        return null;
    }
}
