using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;
using System.Text.Json;

// openfan-linux — Linux hardware layer CLI (spec §10 Phases 2-3).
//   openfan-linux --dump [--sysfs PATH]
//   openfan-linux --apply-once <control-id> <percent> [--seconds N] [--sysfs PATH]

if (args.Length == 0 || args[0] is "-h" or "--help")
    return Usage(args.Length == 0);

var command = args[0];
if (command == "--cpu-power")
{
    if (args.Length != 1)
        return Usage(false);
    var reading = new HsmpPowerReader().Read();
    if (reading is null || (reading.PowerW is null && reading.PptCapW is null))
    {
        Console.Error.WriteLine("amd_hsmp_hwmon CPU socket power telemetry unavailable.");
        return 2;
    }
    // BIOS-side limits are read in-process, unprivileged: the CBS variable is world-readable.
    // socketPowerCapW is the live (volatile) HSMP limit; pptBiosMw is what firmware programs at
    // boot, so they differ whenever something has SET the limit since this boot started.
    var cbs = new CbsSetupReader().Read();
    var desiredMw = new HsmpLimitConfig().ReadDesiredMw();
    var bootWired = new CpuPowerControl().BootPersistenceWired();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        source = reading.Source,
        socketPowerW = reading.PowerW,
        socketPowerCapW = reading.PptCapW,
        pptDesiredMw = desiredMw,
        bootPersistenceInstalled = bootWired,
        tdpMw = cbs.TdpMw,
        pptBiosMw = cbs.PptBiosMw,
        tjmaxC = cbs.TjMaxCelsius,
        tdpControl = cbs.TdpControl,
        pptBiosControl = cbs.PptControl,
        tjmaxControl = cbs.TjMaxControl,
        cbsOk = cbs.Ok,
        cbsReason = cbs.Reason,
    }));
    return 0;
}
string? sysfs = null;
int seconds = 10;

if (command == "--apply-once" && args.Length < 3)
    return Usage(false);

// --apply-once carries two positional args (ID PERCENT); options follow them.
for (var i = command == "--apply-once" ? 3 : 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sysfs" when i + 1 < args.Length:
            sysfs = args[++i];
            break;
        case "--seconds" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out seconds) || seconds < 1)
            {
                Console.Error.WriteLine("--seconds must be a positive integer.");
                return 1;
            }
            break;
    }
}

using var hwmon = new HwmonBackend(sysfs);
using var nvml = new NvmlBackend();

if (!hwmon.Available && !nvml.Available)
{
    Console.Error.WriteLine($"No hardware backends: {sysfs ?? "/sys/class/hwmon"} missing and NVML unavailable.");
    return 2;
}

// Spec §4.4 merge: hwmon first, NVML wins over nvidia-looking hwmon items.
var items = InventoryMerger.Merge(hwmon.Discover(), nvml.Available ? nvml.Discover() : []);
var readings = new Dictionary<string, double?>();
hwmon.ReadInto(readings);
nvml.ReadInto(readings);

switch (command)
{
    case "--procs":
    {
        foreach (var snap in new OpenFan.Linux.Hw.NvmlBackend().SnapshotAll())
        {
            Console.WriteLine($"{snap.Index} {snap.Name}: {snap.Processes.Count} procs");
            foreach (var pr in snap.Processes)
                Console.WriteLine($"   pid={pr.Pid} name={pr.Name} compute={pr.Compute} mem={pr.MemBytes / 1048576.0:0.#}MB");
        }
        return 0;
    }

    case "--dump":
        Dump(items, readings, hwmon, nvml);
        return 0;

    case "--apply-once":
        return ApplyOnce(items, args[1], args[2], seconds, hwmon, nvml);

    default:
        return Usage(false);
}

static void Dump(IReadOnlyList<HardwareItem> items, Dictionary<string, double?> readings,
    HwmonBackend hwmon, NvmlBackend nvml)
{
    var nvmlNote = nvml.Available ? $"NVML driver {nvml.DriverVersion}" : "NVML unavailable (no driver / init failed)";
    Console.WriteLine($"openfan-linux --dump  ({items.Count} items · {nvmlNote})");

    foreach (var group in items.GroupBy(i => i.Group))
    {
        Console.WriteLine();
        Console.WriteLine($"[{group.Key}]");
        foreach (var item in group.OrderBy(i => i.Kind).ThenBy(i => i.Id))
        {
            var value = readings.TryGetValue(item.Id, out var v) ? v : null;
            var detail = item.Kind switch
            {
                HardwareKind.Temperature => value is null ? "n/a" : $"{value:0.#} °C",
                HardwareKind.Tach => value is null ? "n/a" : $"{value:0} RPM",
                _ => DescribeControl(item, value, hwmon),
            };
            Console.WriteLine($"  {item.Kind,-6} {detail,-28} {item.Name}");
            Console.WriteLine($"         {item.Id}");
        }
    }

    var controls = items.Count(i => i.Kind == HardwareKind.Control);
    var temps = items.Count(i => i.Kind == HardwareKind.Temperature && readings.GetValueOrDefault(i.Id) is not null);
    var tachs = items.Count(i => i.Kind == HardwareKind.Tach);
    Console.WriteLine();
    Console.WriteLine($"summary: {controls} fan controls · {temps} live temps · {tachs} tach inputs");
}

static string DescribeControl(HardwareItem item, double? duty, HwmonBackend hwmon)
{
    if (item.Backend.Equals("nvml", StringComparison.OrdinalIgnoreCase))
    {
        var floor = item.MinPercent > 0 ? $"floor {item.MinPercent}% " : "";
        return $"{floor}duty {(duty is null ? "n/a" : $"{duty:0.#}%")}";
    }

    var enable = hwmon.ReadEnableMode(item.Id);
    var mode = enable switch
    {
        0 => "off/full",
        1 => "manual",
        null => "no-enable-file",
        _ => $"auto({enable})",
    };
    return $"{mode,-12} duty {(duty is null ? "n/a" : $"{duty:0.#}%")}";
}

static int ApplyOnce(IReadOnlyList<HardwareItem> items, string idArg, string percentArg,
    int seconds, HwmonBackend hwmon, NvmlBackend nvml)
{
    var control = items.FirstOrDefault(i =>
        i.Kind == HardwareKind.Control &&
        string.Equals(i.Id, idArg, StringComparison.OrdinalIgnoreCase));
    if (control is null)
    {
        Console.Error.WriteLine($"No such fan control: {idArg}");
        Console.Error.WriteLine("Available controls:");
        foreach (var c in items.Where(i => i.Kind == HardwareKind.Control))
            Console.Error.WriteLine($"  {c.Id}  ({c.Name})");
        return 1;
    }

    if (!int.TryParse(percentArg, out var percent) || percent is < 0 or > 100)
    {
        Console.Error.WriteLine("percent must be an integer 0-100.");
        return 1;
    }

    var helper = new NvmlHelperClient();
    var actuator = new CompositeActuator(
        ("hwmon", hwmon),
        ("nvml", helper.IsAvailable ? helper : (OpenFan.Core.ControlLoop.IFanActuator)nvml));
    var isNvml = control.Backend.Equals("nvml", StringComparison.OrdinalIgnoreCase);
    // Paired read-backs: hwmon pwm4↔fan4 by index; nvml fan0↔tach0.
    var pairedId = isNvml
        ? control.Id.Replace(":fan:", ":tach:")
        : control.Id.Replace(":pwm:", ":fan:");
    var hasPaired = items.Any(i => i.Id == pairedId);

    Console.WriteLine($"Applying {percent}% to {control.Id} for {seconds}s; Ctrl+C restores default.");
    try
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (!actuator.SetPercent(control.Id, percent))
                return PermissionDenied(control.Id);

            var readings = new Dictionary<string, double?>();
            hwmon.ReadInto(readings);
            nvml.ReadInto(readings);
            var dutyNow = readings.GetValueOrDefault(control.Id);
            var paired = hasPaired ? readings.GetValueOrDefault(pairedId) : null;
            var extra = isNvml
                ? (hasPaired ? $" tach={paired:0} RPM" : "")
                : $" enable={hwmon.ReadEnableMode(control.Id)}{(hasPaired ? $" fan={paired:0} RPM" : "")}";
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss} duty={dutyNow:0.#}%{extra}");
            Thread.Sleep(1000); // re-write every tick — same EC lesson as Windows (spec §3.2)
        }
    }
    finally
    {
        actuator.SetDefault(control.Id);
        Console.WriteLine(isNvml
            ? "Restored NVML default fan control."
            : $"Restored enable={hwmon.ReadEnableMode(control.Id)} (pre-takeover mode).");
    }

    return 0;
}

static int PermissionDenied(string id)
{
    Console.Error.WriteLine($"Cannot write {id}: permission denied or unsupported by the driver.");
    Console.Error.WriteLine("For hwmon PWM: check 'openfan' group membership (relogin after usermod).");
    Console.Error.WriteLine("For nvml fans: start openfan-helper (packaging/README) or run under sudo.");
    return 3;
}

static int Usage(bool ok)
{
    Console.WriteLine("""
        openfan-linux — OpenFan hardware layer (Ubuntu)

          --dump [--sysfs PATH]                      list PWM + GPU controls, temps, tachs
          --cpu-power                                read socket power and cap as JSON (never writes)
          --apply-once ID PERCENT [--seconds N]      hold one fan at PERCENT (default 10 s), then restore

        Examples:
          openfan-linux --dump
          openfan-linux --apply-once hwmon:nct6799:10:pwm:4 40 --seconds 15
          openfan-linux --apply-once nvml:<GPU-uuid>:fan:0 50 --seconds 8
        """);
    return ok ? 0 : 1;
}
