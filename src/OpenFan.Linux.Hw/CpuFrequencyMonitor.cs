using System.Globalization;

namespace OpenFan.Linux.Hw;

/// <summary>Read-only, per-logical-CPU clocks. Statistics describe sampled clocks, not effective clocks.</summary>
public sealed class CpuFrequencyMonitor(string cpuRoot = "/sys/devices/system/cpu", string cpuInfoPath = "/proc/cpuinfo")
{
    public sealed record Policy(int Id, IReadOnlyList<int> Cpus, long? MinimumKHz, long? MaximumKHz,
        long? HardwareMaximumKHz, string Driver, string Governor, string Preference, bool? Boost);
    public sealed record Cpu(int Id, bool Online, int? Package, int? Core, int? Thread,
        double? CurrentMHz, double? MinimumMHz, double? MaximumMHz, double? AverageMHz,
        string Source, Policy? FrequencyPolicy);
    private sealed class History
    {
        public double Min = double.PositiveInfinity, Max, Mean;
        public long Count;
        public void Add(double value) { Min = Math.Min(Min, value); Max = Math.Max(Max, value); Mean += (value - Mean) / ++Count; }
    }
    public sealed record CoreReading(string Key, int? Package, int? Core, IReadOnlyList<Cpu> Threads,
        double? CurrentMHz, double? MinimumMHz, double? MaximumMHz, double? AverageMHz);
    private readonly Dictionary<int, History> _history = [];
    private readonly Dictionary<string, History> _coreHistory = [];
    public void ResetStatistics() { _history.Clear(); _coreHistory.Clear(); }

    public IReadOnlyList<CoreReading> ReadCores()
    {
        var result = new List<CoreReading>();
        foreach (var group in Read().GroupBy(c => c.Package is { } package && c.Core is { } core
                     ? $"{package}:{core}" : $"cpu:{c.Id}"))
        {
            var threads = group.OrderBy(c => c.Id).ToArray();
            var current = threads.Select(c => c.CurrentMHz).Max();
            if (!_coreHistory.TryGetValue(group.Key, out var history)) _coreHistory[group.Key] = history = new History();
            if (current is { } mhz) history.Add(mhz);
            result.Add(new CoreReading(group.Key, threads[0].Package, threads[0].Core, threads,
                current, history.Count > 0 ? history.Min : null, history.Count > 0 ? history.Max : null,
                history.Count > 0 ? history.Mean : null));
        }
        var present = result.Select(c => c.Key).ToHashSet();
        foreach (var key in _coreHistory.Keys.Where(k => !present.Contains(k)).ToArray()) _coreHistory.Remove(key);
        return result.OrderBy(c => c.Package).ThenBy(c => c.Core).ThenBy(c => c.Threads[0].Id).ToArray();
    }

    public IReadOnlyList<Cpu> Read()
    {
        var policies = ReadPolicies(cpuRoot);
        var byCpu = new Dictionary<int, Policy>();
        foreach (var policy in policies)
            foreach (var id in policy.Cpus) byCpu[id] = policy;
        Dictionary<int, double>? proc = null;
        var result = new List<Cpu>();
        foreach (var (id, dir) in NumberedDirectories(cpuRoot, "cpu"))
        {
            var online = ReadText(Path.Combine(dir, "online")) != "0";
            byCpu.TryGetValue(id, out var policy);
            var siblings = ParseCpuList(ReadText(Path.Combine(dir, "topology/thread_siblings_list")));
            var index = Array.IndexOf(siblings.ToArray(), id);
            double? mhz = null; var source = "Unavailable";
            if (online)
            {
                foreach (var file in new[] { "cpuinfo_cur_freq", "scaling_cur_freq" })
                {
                    if (ReadPositive(Path.Combine(dir, "cpufreq", file)) is not { } khz) continue;
                    mhz = khz / 1000.0; source = file; break;
                }
                if (mhz is null)
                {
                    proc ??= ReadProcMHz();
                    if (proc.TryGetValue(id, out var value)) { mhz = value; source = "/proc/cpuinfo"; }
                }
            }
            if (!_history.TryGetValue(id, out var history)) _history[id] = history = new History();
            if (mhz is { } clock) history.Add(clock);
            result.Add(new Cpu(id, online, ReadInt(Path.Combine(dir, "topology/physical_package_id")),
                ReadInt(Path.Combine(dir, "topology/core_id")), index >= 0 ? index : null,
                mhz, history.Count > 0 ? history.Min : null, history.Count > 0 ? history.Max : null,
                history.Count > 0 ? history.Mean : null, source, policy));
        }
        // A removed CPU must not inherit old statistics if it is hot-added later.
        var present = result.Select(c => c.Id).ToHashSet();
        foreach (var id in _history.Keys.Where(id => !present.Contains(id)).ToArray()) _history.Remove(id);
        return result;
    }

    public static IReadOnlyList<Policy> ReadPolicies(string root = "/sys/devices/system/cpu") =>
        NumberedDirectories(Path.Combine(root, "cpufreq"), "policy").Select(p =>
        {
            var cpus = ParseCpuList(ReadText(Path.Combine(p.Path, "related_cpus")));
            if (cpus.Count == 0) cpus = ParseCpuList(ReadText(Path.Combine(p.Path, "affected_cpus")));
            return new Policy(p.Id, cpus, ReadPositive(Path.Combine(p.Path, "scaling_min_freq")),
                ReadPositive(Path.Combine(p.Path, "scaling_max_freq")), ReadPositive(Path.Combine(p.Path, "cpuinfo_max_freq")),
                ReadText(Path.Combine(p.Path, "scaling_driver")), ReadText(Path.Combine(p.Path, "scaling_governor")),
                ReadText(Path.Combine(p.Path, "energy_performance_preference")),
                ReadText(Path.Combine(p.Path, "boost")) switch { "1" => true, "0" => false, _ => (bool?)null });
        }).ToArray();

    internal static IReadOnlyList<(int Id, string Path)> NumberedDirectories(string root, string prefix)
    {
        try
        {
            return Directory.EnumerateDirectories(root, prefix + "*")
                .Select(path => (Path: path, Suffix: Path.GetFileName(path)[prefix.Length..]))
                .Where(p => p.Suffix.Length > 0 && p.Suffix.All(char.IsAsciiDigit) && int.TryParse(p.Suffix, out _))
                .Select(p => (Id: int.Parse(p.Suffix, CultureInfo.InvariantCulture), p.Path)).OrderBy(p => p.Id).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static IReadOnlyList<int> ParseCpuList(string text)
    {
        var result = new SortedSet<int>();
        foreach (var part in text.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            if (!int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first < 0 || first > 1048575) continue;
            var last = first;
            if (range.Length > 2 || (range.Length == 2 && !int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out last))
                || last < first || last > 1048575 || last - first > 65535) continue;
            for (var id = first; id <= last; id++) result.Add(id);
        }
        return result.ToArray();
    }

    internal static string ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
    internal static long? ReadPositive(string path) =>
        long.TryParse(ReadText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    private static int? ReadInt(string path) =>
        int.TryParse(ReadText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private Dictionary<int, double> ReadProcMHz()
    {
        var result = new Dictionary<int, double>();
        int? cpu = null;
        try
        {
            foreach (var line in File.ReadLines(cpuInfoPath))
            {
                if (string.IsNullOrWhiteSpace(line)) { cpu = null; continue; }
                var parts = line.Split(':', 2); if (parts.Length != 2) continue;
                if (parts[0].Trim() == "processor") cpu = int.TryParse(parts[1].Trim(), out var id) ? id : null;
                if (parts[0].Trim() == "cpu MHz" && cpu is { } n &&
                    double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz) &&
                    double.IsFinite(mhz) && mhz > 0) result[n] = mhz;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }
}
