using OpenFan.Core.ControlLoop;
using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

/// <summary>
/// Recording stand-in for the sysfs/NVML writer. Tracks every SetPercent / SetDefault
/// so tests can assert re-apply-every-tick and restore-once semantics.
/// </summary>
public sealed class RecordingActuator : IFanActuator
{
    public List<(string Id, int Percent)> Sets { get; } = [];
    public List<string> Restores { get; } = [];
    public bool FailNextSet { get; set; }

    public bool SetPercent(string controlId, int percent)
    {
        if (FailNextSet)
        {
            FailNextSet = false;
            return false;
        }
        Sets.Add((controlId, percent));
        return true;
    }

    public bool SetDefault(string controlId)
    {
        Restores.Add(controlId);
        return true;
    }
}

/// <summary>
/// In-memory stand-in for HwmonBackend / NvmlBackend (Phase 2/3 replace it with the real ones).
/// </summary>
public sealed class FakeSensorBackend : ISensorBackend
{
    private readonly List<HardwareItem> _items = [];
    private readonly Dictionary<string, double?> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; init; } = "hwmon";
    public bool Available { get; set; } = true;

    public FakeSensorBackend With(HardwareItem item, double? value)
    {
        _items.Add(item);
        _values[item.Id] = value;
        return this;
    }

    public void SetValue(string id, double? value) => _values[id] = value;

    public IReadOnlyList<HardwareItem> Discover() => _items;

    public void ReadInto(IDictionary<string, double?> readings)
    {
        foreach (var (id, value) in _values)
            readings[id] = value;
    }

    public void Dispose()
    {
    }
}
