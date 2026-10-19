using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

public sealed record NvmeHealthMetric(string Key, string Name, string Unit, HardwareKind Kind, string Details);

/// <summary>SMART is sampled independently every 30 s; slow admin reads never gate the fan sampling batch.</summary>
public sealed class NvmeHealthBackend(INvmeSmartReader? reader = null, Func<IReadOnlyList<NvmeDevice>>? discover = null,
    Func<TimeSpan>? clock = null) : ISensorBackend
{
    public static readonly NvmeHealthMetric[] Metrics =
    [
        new("used", "Endurance used", "%", HardwareKind.Load, "Vendor estimate of rated endurance consumed; may exceed 100%. Not storage space used."),
        new("spare", "Available spare", "%", HardwareKind.Load, "Normalized spare capacity; not filesystem free space."),
        new("spare_threshold", "Spare warning threshold", "%", HardwareKind.Load, "Reported spare-capacity warning threshold."),
        new("read_tb", "Lifetime data read", "TB", HardwareKind.Data, "SMART data units × 512,000 bytes, decimal terabytes; rounded units exclude metadata."),
        new("written_tb", "Lifetime data written", "TB", HardwareKind.Data, "SMART data units × 512,000 bytes, decimal terabytes; host data, not internal NAND writes."),
        new("host_reads", "Lifetime read commands", "cmd", HardwareKind.Counter, "Controller-wide host read commands."),
        new("host_writes", "Lifetime write commands", "cmd", HardwareKind.Counter, "Controller-wide host write commands."),
        new("busy_minutes", "Controller busy time", "min", HardwareKind.Counter, "Lifetime time processing I/O commands, reported in minutes."),
        new("power_cycles", "Power cycles", "cycles", HardwareKind.Counter, "Lifetime controller power-cycle count."),
        new("power_hours", "Power-on time", "h", HardwareKind.Counter, "Lifetime power-on hours; low-power states may be excluded by the drive."),
        new("unsafe_shutdowns", "Unsafe shutdowns", "events", HardwareKind.Counter, "Lifetime shutdowns without shutdown notification; not a media-error count."),
        new("media_errors", "Media / data integrity errors", "errors", HardwareKind.Counter, "Unrecovered media/data-integrity errors reported by the controller."),
        new("error_entries", "Error-log entries", "count", HardwareKind.Counter, "Includes command errors such as unsupported requests; not equivalent to media failure."),
        new("warning_minutes", "Time above warning temperature", "min", HardwareKind.Counter, "Lifetime composite temperature at/above warning and below critical; zero if threshold unsupported."),
        new("critical_minutes", "Time above critical temperature", "min", HardwareKind.Counter, "Lifetime composite temperature at/above critical, not a current overheat indication."),
        new("thermal1_events", "Thermal management stage 1", "events", HardwareKind.Counter, "Lifetime host-controlled thermal-management transitions; zero can mean unsupported or never triggered."),
        new("thermal2_events", "Thermal management stage 2", "events", HardwareKind.Counter, "Lifetime host-controlled thermal-management transitions; zero can mean unsupported or never triggered."),
        new("thermal1_seconds", "Thermal management stage 1 time", "s", HardwareKind.Counter, "Lifetime seconds in the first host-controlled thermal-management state."),
        new("thermal2_seconds", "Thermal management stage 2 time", "s", HardwareKind.Counter, "Lifetime seconds in the second host-controlled thermal-management state."),
        new("warnings", "Critical warning flags", "flags", HardwareKind.Counter, "Current flags: 1 spare, 2 temperature, 4 reliability, 8 read-only, 16 volatile-memory backup, 32 persistent-memory region. Zero means none reported."),
    ];
    private readonly INvmeSmartReader _reader = reader ?? new NvmeSmartClient();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Dictionary<string, TimeSpan> _attempts = [];
    private IReadOnlyList<NvmeDevice> _devices = [];
    private IReadOnlyDictionary<string, Sample> _samples = new Dictionary<string, Sample>();
    private Task<Dictionary<string, Sample>>? _pending;
    private volatile bool _disposed;
    private sealed record Sample(Dictionary<string, double?>? Values, DateTimeOffset At, TimeSpan MonotonicAt, string? Error);
    private TimeSpan Now => clock?.Invoke() ?? _watch.Elapsed;
    public string Name => "nvme-health";
    public bool Available => _devices.Count > 0;
    public IReadOnlyList<NvmeDevice> Devices => _devices;
    public IReadOnlyList<HardwareItem> Discover()
    {
        _devices = discover?.Invoke() ?? NvmeDevices.Discover();
        return _devices.SelectMany(d => Metrics.Select(m => new HardwareItem
        {
            Id = d.MetricId(m.Key), Name = m.Name, Kind = m.Kind, Unit = m.Unit, Backend = Name,
            Group = d.Group, CanSet = false, Source = "NVMe SMART", Details = m.Details + " · " + d.Description + " · sampled every 30 seconds",
        })).ToArray();
    }
    public static Dictionary<string, double?> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != 512 || !data.ContainsAnyExcept((byte)0)) throw new IOException("Invalid SMART log");
        // NVMe uses little-endian 128-bit unsigned counters, not 64-bit host endianness.
        static double Count(ReadOnlySpan<byte> bytes, int offset) => (double)new BigInteger(bytes.Slice(offset, 16), isUnsigned: true, isBigEndian: false);
        static double UInt(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        return new()
        {
            ["warnings"] = data[0], ["spare"] = data[3], ["spare_threshold"] = data[4], ["used"] = data[5],
            ["read_tb"] = Count(data, 32) * 512000 / 1e12, ["written_tb"] = Count(data, 48) * 512000 / 1e12,
            ["host_reads"] = Count(data, 64), ["host_writes"] = Count(data, 80), ["busy_minutes"] = Count(data, 96),
            ["power_cycles"] = Count(data, 112), ["power_hours"] = Count(data, 128), ["unsafe_shutdowns"] = Count(data, 144),
            ["media_errors"] = Count(data, 160), ["error_entries"] = Count(data, 176),
            ["warning_minutes"] = UInt(data, 192), ["critical_minutes"] = UInt(data, 196),
            ["thermal1_events"] = UInt(data, 216), ["thermal2_events"] = UInt(data, 220),
            ["thermal1_seconds"] = UInt(data, 224), ["thermal2_seconds"] = UInt(data, 228),
        };
    }
    public void ReadInto(IDictionary<string, double?> readings) => ReadInto(readings, null);
    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (_disposed) return;
        if (_pending?.IsCompleted == true)
        {
            var next = new Dictionary<string, Sample>(_samples);
            foreach (var (key, sample) in _pending.GetAwaiter().GetResult()) next[key] = sample;
            _samples = next; _pending = null;
        }
        var wanted = _devices.Where(d => Metrics.Any(m => requested is null || requested.Contains(d.MetricId(m.Key)))).ToArray();
        if (_pending is null)
        {
            var due = wanted.Where(d => !_attempts.TryGetValue(d.Identity, out var at) || Now - at >= TimeSpan.FromSeconds(30)).ToArray();
            if (due.Length > 0)
            {
                foreach (var d in due) _attempts[d.Identity] = Now;
                _pending = Task.Run(() =>
                {
                    var results = new Dictionary<string, Sample>();
                    foreach (var d in due)
                    {
                        if (_disposed) break;
                        try { results[d.Identity] = new(Parse(_reader.Read(d.Controller, d.Identity)), DateTimeOffset.UtcNow, Now, null); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
                        { results[d.Identity] = new(null, DateTimeOffset.UtcNow, Now, ex.Message); }
                    }
                    return results;
                });
            }
        }
        foreach (var d in wanted)
            foreach (var m in Metrics)
            {
                var id = d.MetricId(m.Key); if (requested is not null && !requested.Contains(id)) continue;
                readings[id] = _samples.TryGetValue(d.Identity, out var sample) && Now - sample.MonotonicAt < TimeSpan.FromSeconds(65)
                    ? sample.Values?.GetValueOrDefault(m.Key) : null;
            }
    }
    public string Status(string group)
    {
        var device = _devices.FirstOrDefault(d => d.Group == group);
        if (device is null || !_samples.TryGetValue(device.Identity, out var sample)) return "SMART: waiting for first background read · every 30 s";
        return sample.Error is { } error ? "SMART unavailable: " + error : Now - sample.MonotonicAt >= TimeSpan.FromSeconds(65)
            ? "SMART data expired; waiting for a fresh read" : Warnings((int)(sample.Values?.GetValueOrDefault("warnings") ?? 0)) + " · SMART updated " + sample.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) + " · every 30 s";
    }
    public static string Warnings(int flags)
    {
        if (flags == 0) return "No critical warnings";
        string[] labels = ["spare capacity low", "temperature warning", "reliability degraded", "drive read-only", "volatile-memory backup failure", "persistent-memory region warning"];
        var messages = labels.Where((_, i) => (flags & (1 << i)) != 0).ToList();
        if ((flags & ~63) != 0) messages.Add("other flags 0x" + (flags & ~63).ToString("X", CultureInfo.InvariantCulture));
        return "Warning: " + string.Join(", ", messages);
    }
    public void Dispose()
    {
        _disposed = true;
        try { _pending?.GetAwaiter().GetResult(); } finally { _reader.Dispose(); }
    }
}
