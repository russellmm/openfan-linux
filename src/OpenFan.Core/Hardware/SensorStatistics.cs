namespace OpenFan.Core.Hardware;

/// <summary>Sample statistics shared by views; recording happens once per hardware poll, never per repaint.</summary>
public sealed class SensorStatistics
{
    public readonly record struct Snapshot(double? Current, double? Minimum, double? Maximum, double? Average, long Samples);
    private readonly Dictionary<string, Snapshot> _values = new(StringComparer.OrdinalIgnoreCase);
    public Snapshot Get(string id) => _values.GetValueOrDefault(id);
    public void Reset() => _values.Clear();
    public void Record(IReadOnlyDictionary<string, double?> readings)
    {
        foreach (var id in _values.Keys.Where(id => !readings.ContainsKey(id)).ToArray()) _values.Remove(id);
        foreach (var (id, value) in readings)
        {
            var previous = Get(id);
            if (value is not { } current || !double.IsFinite(current))
            {
                _values[id] = previous with { Current = null };
                continue;
            }
            var count = previous.Samples + 1;
            _values[id] = new Snapshot(current, Math.Min(previous.Minimum ?? current, current),
                Math.Max(previous.Maximum ?? current, current),
                previous.Average is { } average ? average + (current - average) / count : current, count);
        }
    }
}

/// <summary>Separates slow inventory discovery from live sampling. Callers can always force a rescan.</summary>
public sealed class InventoryRefreshCadence(TimeSpan interval)
{
    private TimeSpan? _last;
    public bool IsDue(TimeSpan now, bool force = false)
    {
        if (!force && _last is { } last && now - last < interval) return false;
        _last = now;
        return true;
    }
}
