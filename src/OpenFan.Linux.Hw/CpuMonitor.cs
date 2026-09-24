using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OpenFan.Linux.Hw;

/// <summary>
/// CPU telemetry from sysfs/procfs — every source is optional, missing pieces read as null:
///  • package power + PPT cap: amd_hsmp hwmon chip (TRX50 / Threadripper PRO)
///  • core temperature: the board hwmon's Tctl / Tdie / "CPU Package" sensor
///  • load: busy % from /proc/stat deltas between reads
///  • frequency: highest scaling_cur_freq across cores
/// </summary>
public sealed class CpuMonitor
{
    public sealed record Snapshot(string Model, double? TempC, double? PowerW, double? PptCapW,
                                  double? LoadPct, double? MaxGHz, int Cores);

    private long _idlePrev = -1;
    private long _totalPrev = -1;

    public Snapshot Read()
    {
        var (powerW, capW) = ReadHsmpPower();
        return new Snapshot(ReadModel(), ReadTempC(), powerW, capW, ReadLoadPct(), ReadMaxGHz(), CoreCount());
    }

    private static string ReadModel()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    return line.Split(':', 2)[1].Trim();
        }
        catch { /* unreadable cpuinfo */ }
        return "Processor";
    }

    private static (double? PowerW, double? CapW) ReadHsmpPower()
    {
        foreach (var chip in Directory.EnumerateDirectories("/sys/class/hwmon"))
        {
            string name;
            try { name = File.ReadAllText(Path.Combine(chip, "name")).Trim(); }
            catch { continue; }
            if (!name.Contains("hsmp", StringComparison.OrdinalIgnoreCase))
                continue;

            var w = MicroToWatt(Path.Combine(chip, "power1_input"));      // µW
            var cap = MicroToWatt(Path.Combine(chip, "power1_cap"));      // PPT limit
            return (w, cap);
        }
        return (null, null);
    }

    private static double? MicroToWatt(string path)
    {
        try
        {
            if (double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var uw))
                return uw / 1_000_000.0;
        }
        catch { /* file may not exist on older kernels */ }
        return null;
    }

    private static double? ReadTempC()
    {
        // Prefer, in order: Tctl, Tdie, "CPU Package" — the usual AMD package labels.
        string[] wanted = ["tctl", "tdie", "cpu package"];
        double? fallback = null;
        foreach (var chip in Directory.EnumerateDirectories("/sys/class/hwmon"))
        {
            string[] inputs;
            try { inputs = Directory.GetFiles(chip, "temp*_input"); }
            catch { continue; }
            foreach (var input in inputs)
            {
                var labelPath = input.Replace("_input", "_label");
                string label = "";
                try { if (File.Exists(labelPath)) label = File.ReadAllText(labelPath).Trim(); }
                catch { /* no label */ }
                double? v = MilliToC(input);
                if (v is null)
                    continue;
                var l = label.ToLowerInvariant();
                if (l == wanted[0])
                    return v;
                if (l == wanted[1] && fallback is null)
                    fallback = v;
                else if (l == wanted[2] && fallback is null)
                    fallback = v;
            }
        }
        return fallback;
    }

    private static double? MilliToC(string path)
    {
        try
        {
            if (double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var milli))
                return milli / 1000.0;
        }
        catch { /* unreadable */ }
        return null;
    }

    private double? ReadLoadPct()
    {
        try
        {
            // cpu  user nice system idle iowait irq softirq steal guest guest_nice
            var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .Skip(1).Select(long.Parse).ToArray();
            if (fields.Length < 5)
                return null;
            var idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
            var total = fields.Sum();
            long idleD = idle - _idlePrev, totalD = total - _totalPrev;
            _idlePrev = idle;
            _totalPrev = total;
            if (totalD <= 0)
                return null; // first read has no baseline
            return Math.Clamp((1.0 - (double)idleD / totalD) * 100.0, 0, 100);
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadMaxGHz()
    {
        double? max = null;
        // Bounded walk cpu*/cpufreq/scaling_cur_freq — a recursive scan of /sys/devices/system/cpu
        // would chase symlinked device trees and stall the UI thread for seconds per tick.
        foreach (var cpu in Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*"))
        {
            try
            {
                var f = Path.Combine(cpu, "cpufreq", "scaling_cur_freq");
                if (double.TryParse(File.ReadAllText(f).Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var khz))
                    max = Math.Max(max ?? 0, khz / 1_000_000.0);
            }
            catch { /* core may not expose cpufreq */ }
        }
        return max;
    }

    private static int CoreCount()
    {
        try
        {
            var n = Directory.EnumerateDirectories("/sys/devices/system/cpu")
                             .Count(d => System.Text.RegularExpressions.Regex.IsMatch(d, @"/cpu\d+$"));
            return n > 0 ? n : Environment.ProcessorCount;
        }
        catch { return Environment.ProcessorCount; }
    }
}
