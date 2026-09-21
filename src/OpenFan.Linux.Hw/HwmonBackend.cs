using System.Globalization;
using System.Text.RegularExpressions;
using OpenFan.Core.Config;
using OpenFan.Core.ControlLoop;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Discovers controls / temperatures / tachs from the Linux hwmon sysfs tree and writes
/// PWM duty (spec §4.1). The root is injectable so tests run against a fixture tree.
/// Ids are stable across reboots by chip name: hwmon:{chipName}:{hwmonIndex}:pwm|temp|fan:{n}.
/// </summary>
public sealed partial class HwmonBackend : ISensorBackend, IFanActuator
{
    public const string BackendName = "hwmon";

    private static readonly Regex HwmonDirRx = HwmonPattern();
    private static readonly Regex TempInputRx = TempFanPattern("temp");
    private static readonly Regex FanInputRx = TempFanPattern("fan");
    private static readonly Regex PwmControlRx = PwmOnlyPattern();

    private readonly string _root;
    private readonly Func<string, bool> _chipEnabled;
    private readonly Dictionary<string, int> _initialEnable = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HardwareItem> _items = [];

    /// <param name="sysfsRoot">Defaults to /sys/class/hwmon; tests pass a fixture directory.</param>
    public HwmonBackend(string? sysfsRoot = null, SourceSettings? sources = null)
    {
        _root = sysfsRoot ?? "/sys/class/hwmon";
        var disabled = new HashSet<string>(
            (sources?.DisabledHwmonChips ?? []).Select(c => c.Trim().ToLowerInvariant()));
        _chipEnabled = name =>
            (sources is null || sources.Hwmon) && !disabled.Contains(name.ToLowerInvariant());
    }

    public string Name => BackendName;
    public bool Available => Directory.Exists(_root);

    /// <summary>Reason the last SetPercent/SetDefault failed (shown on cards) — null when healthy.</summary>
    public string? LastWriteError { get; private set; }

    /// <summary>Re-enumerates every tick so driver bind/unbind hot-plugs without a restart.</summary>
    public IReadOnlyList<HardwareItem> Discover()
    {
        _items.Clear();
        if (!Available)
            return _items;

        foreach (var dir in EnumerateHwmonDirs())
        {
            var chip = ReadAllText(Path.Combine(dir, "name"))?.Trim();
            if (string.IsNullOrEmpty(chip) || !_chipEnabled(chip))
                continue;
            var index = HwmonDirIndex(dir);
            var group = $"{chip} (hwmon{index})";

            foreach (var file in Directory.GetFiles(dir).OrderBy(f => f, Comparer<string>.Create(NaturalOrder)))
            {
                var fileName = Path.GetFileName(file);

                var temp = TempInputRx.Match(fileName);
                if (temp.Success)
                {
                    var n = int.Parse(temp.Groups[1].Value);
                    _items.Add(new HardwareItem
                    {
                        Id = ItemId(chip, index, "temp", n),
                        Kind = HardwareKind.Temperature,
                        Name = LabelOr(file, $"temp{n}_label", $"Temperature #{n}"),
                        Backend = BackendName,
                        Group = group,
                        CanSet = false,
                    });
                    continue;
                }

                var fan = FanInputRx.Match(fileName);
                if (fan.Success)
                {
                    var n = int.Parse(fan.Groups[1].Value);
                    _items.Add(new HardwareItem
                    {
                        Id = ItemId(chip, index, "fan", n),
                        Kind = HardwareKind.Tach,
                        Name = LabelOr(file, $"fan{n}_label", $"Fan {n}"),
                        Backend = BackendName,
                        Group = group,
                        CanSet = false,
                    });
                    continue;
                }

                // Exactly pwmN — never pwmN_enable / pwmN_auto_point* / pwmN_mode etc.
                var pwm = PwmControlRx.Match(fileName);
                if (pwm.Success)
                {
                    var n = int.Parse(pwm.Groups[1].Value);
                    var id = ItemId(chip, index, "pwm", n);
                    _items.Add(new HardwareItem
                    {
                        Id = id,
                        Kind = HardwareKind.Control,
                        Name = $"{chip} PWM {n}",
                        Backend = BackendName,
                        Group = group,
                    });
                    // Cache the chip's mode BEFORE any manual write so restore is exact —
                    // NCT6796D-family boots with enable=5 (auto), not the textbook 2.
                    if (!_initialEnable.ContainsKey(id))
                    {
                        var enable = ReadInt(Path.Combine(dir, $"pwm{n}_enable"));
                        if (enable is not null)
                            _initialEnable[id] = enable.Value;
                    }
                }
            }
        }

        return _items;
    }

    public void ReadInto(IDictionary<string, double?> readings)
    {
        foreach (var item in _items)
        {
            var path = SysPathFor(item.Id);
            if (path is null)
                continue;

            switch (item.Kind)
            {
                case HardwareKind.Temperature:
                    // k10temp reports -40 °C when disabled, SuperIO floating pins go wild;
                    // treat implausible values as missing so curves skip (spec §3.2).
                    // Floating SuperIO pins read ~-9 °C and k10temp reports negative when
                    // disabled; any sub-zero or > 150 value is noise — treat as missing so
                    // curves skip the control instead of restoring (spec §3.2).
                    if (ReadInt(path) is not { } milli)
                        readings[item.Id] = null;
                    else
                    {
                        double c = milli / 1000.0;
                        readings[item.Id] = c is < 0 or > 150 ? null : c;
                    }
                    break;
                case HardwareKind.Tach:
                    var rpm = ReadInt(path);
                    readings[item.Id] = rpm is null or < 0 ? null : rpm.Value;
                    break;
                case HardwareKind.Control:
                    // Current duty for display only — cards prefer the commanded % (spec §5).
                    var duty = ReadInt(path);
                    readings[item.Id] = duty is null ? null : duty.Value * 100.0 / 255.0;
                    break;
            }
        }
    }

    /// <summary>Manual mode + duty, every tick while applied (spec §4.1: re-fire like SetSoftware).</summary>
    public bool SetPercent(string controlId, int percent)
    {
        var path = SysPathFor(controlId);
        if (path is null || !Path.GetFileName(path).StartsWith("pwm", StringComparison.Ordinal))
            return false;

        var clamped = Math.Clamp(percent, 0, 100);
        try
        {
            var enablePath = path + "_enable";
            if (File.Exists(enablePath))
            {
                // Lazy-capture initial mode even if SetPercent runs before Discover.
                if (!_initialEnable.ContainsKey(controlId))
                {
                    var initial = ReadInt(enablePath);
                    if (initial is not null)
                        _initialEnable[controlId] = initial.Value;
                }
                File.WriteAllText(enablePath, "1"); // manual
            }

            var duty = (int)Math.Round(clamped * 255.0 / 100.0, MidpointRounding.AwayFromZero);
            File.WriteAllText(path, duty.ToString(CultureInfo.InvariantCulture));
            LastWriteError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // EACCES (session predates the openfan group?) / EIO: surface a precise reason.
            LastWriteError = ex is UnauthorizedAccessException
                ? "permission denied — is this session in the 'openfan' group? (log out/in)"
                : $"sysfs write failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Restore the cached pre-takeover mode (5 on this board); 2 if unknown.</summary>
    public bool SetDefault(string controlId)
    {
        var path = SysPathFor(controlId);
        if (path is null)
            return false;

        var enablePath = path + "_enable";
        if (!File.Exists(enablePath))
            return true; // no auto concept on this chip — nothing to restore

        try
        {
            var mode = _initialEnable.TryGetValue(controlId, out var cached) ? cached : 2;
            File.WriteAllText(enablePath, mode.ToString(CultureInfo.InvariantCulture));
            LastWriteError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastWriteError = ex is UnauthorizedAccessException
                ? "permission denied restoring auto — 'openfan' group needed"
                : $"sysfs write failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Cheap startup check: can THIS process write any discovered pwm node?
    /// Returns null when writable, else a user-readable reason (spec §3.4 message).
    /// </summary>
    public string? ProbeWriteAccess()
    {
        HardwareItem? firstControl = null;
        foreach (var item in _items)
        {
            if (item.Kind == HardwareKind.Control) { firstControl = item; break; }
        }
        if (firstControl is null)
            return Available ? "no PWM controls found" : "hwmon not available";

        var path = SysPathFor(firstControl.Id);
        if (path is null)
            return "PWM node path unavailable";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "PWM nodes not writable — this session lacks the 'openfan' group (log out and back in)";
        }
        catch (IOException ex)
        {
            return $"PWM nodes not writable: {ex.Message}";
        }
    }

    /// <summary>Current pwmN_enable for diagnostics (--dump): 1 manual, ≥2 auto variants.</summary>
    public int? ReadEnableMode(string controlId)
    {
        var path = SysPathFor(controlId);
        if (path is null)
            return null;
        var enablePath = path + "_enable";
        return File.Exists(enablePath) ? ReadInt(enablePath) : null;
    }

    /// <summary>No persistent handles — sysfs nodes are opened per read.</summary>
    public void Dispose()
    {
    }

    private IEnumerable<string> EnumerateHwmonDirs()
    {
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(_root, "hwmon*");
        }
        catch (IOException)
        {
            return [];
        }

        return dirs
            .Where(d => HwmonDirRx.IsMatch(Path.GetFileName(d)))
            .OrderBy(HwmonDirIndex);
    }

    private string? SysPathFor(string id)
    {
        // hwmon:{chipName}:{hwmonIndex}:temp|fan|pwm:{n}
        var parts = id.Split(':');
        if (parts.Length != 5 || parts[0] != BackendName)
            return null;
        if (!int.TryParse(parts[2], out var index))
            return null;
        if (!int.TryParse(parts[4], out var n))
            return null;

        var leaf = parts[3] switch
        {
            "temp" => $"temp{n}_input",
            "fan" => $"fan{n}_input",
            "pwm" => $"pwm{n}",
            _ => null,
        };
        if (leaf is null)
            return null;

        var dir = Path.Combine(_root, $"hwmon{index}");
        // Guard against a stale index after reboot: the chip name must still match.
        var chip = ReadAllText(Path.Combine(dir, "name"))?.Trim();
        if (!string.Equals(chip, parts[1], StringComparison.OrdinalIgnoreCase))
            return null;

        return Path.Combine(dir, leaf);
    }

    private static string ItemId(string chip, int index, string kind, int n)
        => $"{BackendName}:{chip}:{index}:{kind}:{n}";

    private static int HwmonDirIndex(string dir)
        => int.Parse(Path.GetFileName(dir).AsSpan("hwmon".Length), CultureInfo.InvariantCulture);

    private static string LabelOr(string dirFile, string labelName, string fallback)
    {
        var label = ReadAllText(Path.Combine(Path.GetDirectoryName(dirFile)!, labelName))?.Trim();
        return string.IsNullOrEmpty(label) ? fallback : label;
    }

    private static int? ReadInt(string path)
    {
        var text = ReadAllText(path);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null; // device vanished mid-scan
        }
    }

    /// <summary>Natural sort so temp2 sorts before temp10.</summary>
    private static int NaturalOrder(string a, string b)
    {
        var (pa, na) = SplitFirstNumber(a);
        var (pb, nb) = SplitFirstNumber(b);
        var cmp = string.Compare(pa, pb, StringComparison.Ordinal);
        return cmp != 0 ? cmp : na.CompareTo(nb);
    }

    private static (string Prefix, int Number) SplitFirstNumber(string s)
    {
        var m = Regex.Match(s, @"\d+");
        if (!m.Success)
            return (s, -1);
        // Never throw from a comparer: absurd digit runs sort as 0 rather than Overflow.
        int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num);
        return (s.Remove(m.Index, m.Length), num);
    }

    [GeneratedRegex(@"^hwmon\d+$")]
    private static partial Regex HwmonPattern();

    private static Regex TempFanPattern(string kind) =>
        new($@"^{kind}(\d+)_input$", RegexOptions.Compiled);

    private static Regex PwmOnlyPattern() =>
        new(@"^pwm(\d+)$", RegexOptions.Compiled);
}
