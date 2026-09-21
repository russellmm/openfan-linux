using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

/// <summary>
/// A source of inventory items and live readings (hwmon chip set, NVML GPUs).
/// Implementations must tolerate being re-enumerated every tick so hot-plugged
/// devices (GPU bind, USB AIO) appear without an app restart (spec §4.4).
/// </summary>
public interface ISensorBackend : IDisposable
{
    /// <summary>Stable discriminator stored in <see cref="HardwareItem.Backend"/> ("hwmon", "nvml").</summary>
    string Name { get; }

    /// <summary>False when the backing driver/driver node is unavailable (degrade, don't crash).</summary>
    bool Available { get; }

    /// <summary>Re-enumerate controls / temperatures / tachs. Cheap enough to call every tick.</summary>
    IReadOnlyList<HardwareItem> Discover();

    /// <summary>Read current values for all discovered items into <paramref name="readings"/>, keyed by item Id.</summary>
    void ReadInto(IDictionary<string, double?> readings);
}
