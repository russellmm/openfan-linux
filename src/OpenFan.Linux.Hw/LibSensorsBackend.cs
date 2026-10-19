using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OpenFan.Core.Config;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

public sealed record LibSensorChannel(string Chip, string ChipPath, string Feature, string Subfeature, string Label, int FeatureType, string Unit);
public interface ILibSensorsReader : IDisposable
{
    bool Available { get; }
    string? Error { get; }
    IReadOnlyList<LibSensorChannel> Discover();
    double? Read(LibSensorChannel channel);
}

/// <summary>Read-only libsensors integration. Native configuration conversions, labels and ignore rules are honored.</summary>
public sealed class LibSensorsBackend(SourceSettings? sources = null, ILibSensorsReader? reader = null) : ISensorBackend
{
    public SourceSettings? Sources { get; set; } = sources;
    private readonly ILibSensorsReader _reader = reader ?? new NativeLibSensorsReader();
    private readonly List<(HardwareItem Item, LibSensorChannel Channel)> _channels = [];
    public string Name => "hwmon"; // Retain the existing ids used by curves/aliases and the hwmon actuator.
    public bool Available => _reader.Available;
    public string? Error => _reader.Error;
    public IReadOnlyList<HardwareItem> Discover()
    {
        _channels.Clear();
        if (!Available || Sources?.Hwmon == false) return [];
        foreach (var channel in _reader.Discover())
        {
            if (Sources?.DisabledHwmonChips.Any(c => c.Equals(channel.Chip, StringComparison.OrdinalIgnoreCase)) == true) continue;
            var item = Describe(channel);
            if (item is not null) _channels.Add((NvmeDevices.Describe(DramSensorIdentity.Describe(item, channel.ChipPath), channel.ChipPath), channel));
        }
        return _channels.Select(c => c.Item).ToArray();
    }
    public void ReadInto(IDictionary<string, double?> readings) => ReadInto(readings, null);
    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        foreach (var (item, channel) in _channels)
        {
            if (requested is not null && !requested.Contains(item.Id)) continue;
            var value = _reader.Read(channel);
            readings[item.Id] = value is { } v && double.IsFinite(v) ? v : null;
        }
    }
    public static HardwareItem? Describe(LibSensorChannel channel)
    {
        // SPD thresholds have a distinct kind: they are configuration readbacks, never curve inputs.
        // PWM control remains with the existing validated hwmon writer.
        var match = Regex.Match(channel.Feature, @"^([a-z]+)([0-9]+)$", RegexOptions.CultureInvariant);
        var index = Regex.Match(Path.GetFileName(channel.ChipPath), @"^hwmon([0-9]+)$", RegexOptions.CultureInvariant);
        if (!match.Success || !index.Success || channel.FeatureType == 7) return null;
        var sub = channel.Subfeature;
        var status = sub.EndsWith("_alarm", StringComparison.Ordinal) || sub.EndsWith("_fault", StringComparison.Ordinal);
        var limit = sub.EndsWith("_cap", StringComparison.Ordinal);
        var temperatureLimit = channel.FeatureType == 2 &&
            (channel.Chip == "spd5118" && DramTemperatureLimits.Suffixes.Any(s => sub == channel.Feature + "_" + s) ||
             channel.Chip == "nvme" && channel.Feature == "temp1" && sub is "temp1_max" or "temp1_crit");
        if (!status && !limit && !temperatureLimit && !sub.EndsWith("_input", StringComparison.Ordinal) &&
            !sub.EndsWith("_average", StringComparison.Ordinal) && channel.FeatureType != 0x10) return null;
        var kind = status ? HardwareKind.Status : temperatureLimit ? HardwareKind.TemperatureLimit : channel.FeatureType switch
        {
            0 or 0x10 => HardwareKind.Voltage, 1 => HardwareKind.Tach, 2 => HardwareKind.Temperature,
            3 => HardwareKind.Power, 4 => HardwareKind.Energy, 5 => HardwareKind.Current,
            6 => HardwareKind.Humidity, 0x19 => HardwareKind.Frequency, _ => HardwareKind.Status,
        };
        var channelKind = match.Groups[1].Value;
        var suffix = status ? ":" + sub[(channel.Feature.Length + 1)..] : limit ? ":cap" :
            temperatureLimit ? ":limit:" + sub[(channel.Feature.Length + 1)..] : sub.EndsWith("_average") ? ":average" : "";
        var label = string.IsNullOrWhiteSpace(channel.Label) ? channel.Feature : channel.Label;
        if (status) label += " · " + sub[(channel.Feature.Length + 1)..].Replace('_', ' ');
        else if (limit) label += " · power limit";
        else if (temperatureLimit) label += " · " + DramTemperatureLimits.Label(sub[(channel.Feature.Length + 1)..]).ToLowerInvariant() + " limit";
        else if (sub.EndsWith("_average")) label += " · reported average";
        return new HardwareItem
        {
            Id = $"hwmon:{channel.Chip}:{index.Groups[1].Value}:{channelKind}:{match.Groups[2].Value}{suffix}",
            Name = label, Kind = kind, Backend = "hwmon", Group = $"{channel.Chip} (hwmon{index.Groups[1].Value})",
            Unit = status ? "flag" : channel.Unit, Source = "libsensors", CanSet = false,
            Details = $"libsensors · {channel.Subfeature}" + (limit ? " · configured ceiling, not power draw" : "") +
                (temperatureLimit ? " · configured alarm threshold, not a measured minimum/maximum or a guaranteed safe operating temperature" : "") +
                (kind == HardwareKind.Voltage && label == channel.Feature ? " · unverified motherboard rail mapping" : ""),
        };
    }
    public void Dispose() => _reader.Dispose();
}

/// <summary>All libsensors access is serialized: its configuration and chip pointers are process-global.</summary>
public sealed class NativeLibSensorsReader : ILibSensorsReader
{
    private static readonly object Gate = new();
    private static int _users, _generation;
    private static bool _initialized;
    private bool _disposed;
    private readonly Dictionary<string, (nint Chip, int Number, int Generation)> _native = [];
    public bool Available { get; private set; }
    public string? Error { get; private set; }
    public NativeLibSensorsReader()
    {
        lock (Gate)
        {
            try
            {
                if (!_initialized)
                {
                    var result = Native.sensors_init(0);
                    if (result != 0) { Native.sensors_cleanup(); Error = $"libsensors initialization failed ({result})"; return; }
                    _initialized = true; _generation++;
                }
                _users++; Available = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { Error = "libsensors unavailable: " + ex.Message; }
        }
    }
    public IReadOnlyList<LibSensorChannel> Discover()
    {
        lock (Gate)
        {
            _native.Clear();
            if (_disposed || !Available) return [];
            // Reload on inventory scans so driver additions/removals and /etc/sensors.d are seen.
            Native.sensors_cleanup(); _initialized = false; _generation++;
            var result = Native.sensors_init(0);
            if (result != 0) { Error = $"libsensors rescan failed ({result})"; return []; }
            _initialized = true; Error = null;
            var channels = new List<LibSensorChannel>();
            var chipNumber = 0;
            nint chipPointer;
            while ((chipPointer = Native.sensors_get_detected_chips(0, ref chipNumber)) != 0)
            {
                var chip = Marshal.PtrToStructure<Native.Chip>(chipPointer);
                var prefix = Text(chip.Prefix); var path = Text(chip.Path);
                var featureNumber = 0; nint featurePointer;
                while ((featurePointer = Native.sensors_get_features(chipPointer, ref featureNumber)) != 0)
                {
                    var feature = Marshal.PtrToStructure<Native.Feature>(featurePointer);
                    var featureName = Text(feature.Name);
                    var labelPointer = Native.sensors_get_label(chipPointer, featurePointer);
                    var label = Text(labelPointer);
                    if (labelPointer != 0) Native.free(labelPointer);
                    var subNumber = 0; nint subPointer;
                    while ((subPointer = Native.sensors_get_all_subfeatures(chipPointer, featurePointer, ref subNumber)) != 0)
                    {
                        var sub = Marshal.PtrToStructure<Native.Subfeature>(subPointer);
                        if ((sub.Flags & 1) == 0) continue;
                        var subName = Text(sub.Name);
                        string unit;
                        try { unit = Text(Native.sensors_get_quantity_unit(Native.sensors_get_subfeature_quantity(sub.Type))); }
                        catch (EntryPointNotFoundException) { unit = UnitFor(feature.Type); }
                        var channel = new LibSensorChannel(prefix, path, featureName, subName, label, feature.Type, unit);
                        channels.Add(channel);
                        _native[Key(channel)] = (chipPointer, sub.Number, _generation);
                    }
                }
            }
            return channels;
        }
    }
    public double? Read(LibSensorChannel channel)
    {
        lock (Gate)
        {
            // Another reader's reload invalidates old pointers. Return missing, never dereference them.
            if (_disposed || !_initialized || !_native.TryGetValue(Key(channel), out var native) || native.Generation != _generation) return null;
            return Native.sensors_get_value(native.Chip, native.Number, out var value) == 0 && double.IsFinite(value) ? value : null;
        }
    }
    private static string Key(LibSensorChannel c) => c.ChipPath + "/" + c.Subfeature;
    private static string Text(nint pointer) => pointer == 0 ? "" : Marshal.PtrToStringUTF8(pointer) ?? "";
    private static string UnitFor(int kind) => kind switch
    { 0 or 0x10 => "V", 1 => "RPM", 2 => "°C", 3 => "W", 4 => "J", 5 => "A", 6 => "%RH", 0x19 => "Hz", _ => "" };
    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (Available && --_users == 0)
            {
                if (_initialized) Native.sensors_cleanup();
                _initialized = false; _generation++;
            }
            Available = false;
        }
    }
    private static class Native
    {
        private const string Library = "libsensors.so.5";
        [StructLayout(LayoutKind.Sequential)] public struct Bus { public short Type, Number; }
        [StructLayout(LayoutKind.Sequential)] public struct Chip { public nint Prefix; public Bus Bus; public int Address; public nint Path; }
        [StructLayout(LayoutKind.Sequential)] public struct Feature { public nint Name; public int Number, Type, FirstSubfeature, Padding; }
        [StructLayout(LayoutKind.Sequential)] public struct Subfeature { public nint Name; public int Number, Type, Mapping; public uint Flags; }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int sensors_init(nint file);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void sensors_cleanup();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nint sensors_get_detected_chips(nint match, ref int number);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nint sensors_get_features(nint chip, ref int number);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nint sensors_get_all_subfeatures(nint chip, nint feature, ref int number);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nint sensors_get_label(nint chip, nint feature);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int sensors_get_value(nint chip, int number, out double value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int sensors_get_subfeature_quantity(int type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nint sensors_get_quantity_unit(int quantity);
        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)] public static extern void free(nint memory);
    }
}
