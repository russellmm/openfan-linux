using System.Diagnostics;
using System.Globalization;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

/// <summary>Unprivileged block counters. Rates use a monotonic clock; paused intervals never become artificial samples.</summary>
public sealed class NvmeActivityBackend(Func<IReadOnlyList<NvmeDevice>>? discover = null, string blockRoot = "/sys/class/block", Func<TimeSpan>? clock = null) : ISensorBackend
{
    public static readonly NvmeHealthMetric[] Metrics =
    [
        new("read_rate", "Read rate", "MB/s", HardwareKind.Data, "Decimal MB/s from Linux block sectors read, sampled between visible updates."),
        new("write_rate", "Write rate", "MB/s", HardwareKind.Data, "Decimal MB/s from Linux block sectors written, sampled between visible updates."),
        new("read_activity", "Read activity", "%", HardwareKind.Load, "Sum of read-request milliseconds / elapsed time. Concurrent NVMe requests can exceed 100%; differs from Windows activity counters."),
        new("write_activity", "Write activity", "%", HardwareKind.Load, "Sum of write-request milliseconds / elapsed time. Concurrent NVMe requests can exceed 100%; differs from Windows activity counters."),
        new("total_activity", "Total activity", "%", HardwareKind.Load, "Linux busy milliseconds / elapsed time, capped at 100%. An activity estimate, not percentage of maximum bandwidth."),
        new("read_total", "Read total since device start", "GB", HardwareKind.Counter, "Linux block sectors × 512 bytes, decimal GB. Since boot/device-counter reset; separate from lifetime SMART totals."),
        new("write_total", "Write total since device start", "GB", HardwareKind.Counter, "Linux block sectors × 512 bytes, decimal GB. Since boot/device-counter reset; separate from lifetime SMART totals."),
    ];
    public readonly record struct Counters(ulong ReadSectors, ulong WriteSectors, ulong ReadMs, ulong WriteMs, ulong BusyMs)
    {
        public static Counters? Parse(string? text)
        {
            var fields = text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields is null || fields.Length < 11) return null;
            var numbers = new ulong[5]; int[] indexes = [2, 6, 3, 7, 9];
            for (var i = 0; i < indexes.Length; i++)
                if (!ulong.TryParse(fields[indexes[i]], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
            return new(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4]);
        }
    }
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Dictionary<string, (TimeSpan At, Counters Value)> _previous = [];
    private IReadOnlyList<(NvmeDevice Device, string[] Paths)> _devices = [];
    public string Name => "nvme-activity";
    public bool Available => _devices.Count > 0;
    public static string Id(NvmeDevice d, string metric) => $"nvme-activity:{d.Identity}:io:{metric}";
    public IReadOnlyList<HardwareItem> Discover()
    {
        var devices = discover?.Invoke() ?? NvmeDevices.Discover();
        var next = devices.Select(d =>
        {
            string[] paths;
            try { paths = Directory.GetDirectories(blockRoot, d.Controller + "n*").Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), "^" + d.Controller + @"n[0-9]+$")).Select(p => Path.Combine(p, "stat")).Order(StringComparer.Ordinal).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { paths = []; }
            return (d, paths);
        }).Where(d => d.paths.Length > 0).ToArray();
        foreach (var (device, paths) in next)
        {
            var oldPaths = _devices.FirstOrDefault(d => d.Device.Identity == device.Identity).Paths;
            if (oldPaths is null || !oldPaths.SequenceEqual(paths)) _previous.Remove(device.Identity);
        }
        var identities = next.Select(d => d.d.Identity).ToHashSet();
        foreach (var id in _previous.Keys.Where(id => !identities.Contains(id)).ToArray()) _previous.Remove(id);
        _devices = next;
        return _devices.SelectMany(d => Metrics.Select(m => new HardwareItem
        {
            Id = Id(d.Device, m.Key), Name = m.Name, Unit = m.Unit, Kind = m.Kind, Group = d.Device.Group,
            Backend = Name, CanSet = false, Source = "Linux block I/O", Details = m.Details + " · " + d.Device.Description,
        })).ToArray();
    }
    public static Dictionary<string, double?> Values(Counters current, Counters? previous, double seconds)
    {
        var result = Metrics.ToDictionary(m => m.Key, _ => (double?)null);
        result["read_total"] = current.ReadSectors * 512d / 1e9;
        result["write_total"] = current.WriteSectors * 512d / 1e9;
        if (previous is not { } p || seconds <= 0 || current.ReadSectors < p.ReadSectors || current.WriteSectors < p.WriteSectors ||
            current.ReadMs < p.ReadMs || current.WriteMs < p.WriteMs || current.BusyMs < p.BusyMs) return result;
        result["read_rate"] = (current.ReadSectors - p.ReadSectors) * 512d / seconds / 1e6;
        result["write_rate"] = (current.WriteSectors - p.WriteSectors) * 512d / seconds / 1e6;
        result["read_activity"] = (current.ReadMs - p.ReadMs) / (seconds * 10);
        result["write_activity"] = (current.WriteMs - p.WriteMs) / (seconds * 10);
        result["total_activity"] = Math.Min(100, (current.BusyMs - p.BusyMs) / (seconds * 10));
        return result;
    }
    public void ReadInto(IDictionary<string, double?> readings) => ReadInto(readings, null);
    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        foreach (var (device, paths) in _devices)
        {
            if (!Metrics.Any(m => requested is null || requested.Contains(Id(device, m.Key)))) { _previous.Remove(device.Identity); continue; }
            var counters = paths.Select(p => Counters.Parse(CpuFrequencyMonitor.ReadText(p))).ToArray();
            var now = clock?.Invoke() ?? _watch.Elapsed;
            Dictionary<string, double?> values;
            if (counters.Any(c => c is null)) { _previous.Remove(device.Identity); values = Metrics.ToDictionary(m => m.Key, _ => (double?)null); }
            else
            {
                var sum = new Counters(counters.Aggregate(0UL, (a, c) => a + c!.Value.ReadSectors), counters.Aggregate(0UL, (a, c) => a + c!.Value.WriteSectors),
                    counters.Aggregate(0UL, (a, c) => a + c!.Value.ReadMs), counters.Aggregate(0UL, (a, c) => a + c!.Value.WriteMs), counters.Aggregate(0UL, (a, c) => a + c!.Value.BusyMs));
                values = _previous.TryGetValue(device.Identity, out var p) ? Values(sum, p.Value, (now - p.At).TotalSeconds) : Values(sum, null, 0);
                _previous[device.Identity] = (now, sum);
            }
            foreach (var m in Metrics)
            { var id = Id(device, m.Key); if (requested is null || requested.Contains(id)) readings[id] = values[m.Key]; }
        }
    }
    public void Dispose() { }
}
