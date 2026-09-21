using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

// openfan-linux — Phase 2 CLI smoke for the Linux hardware layer (spec §10 Phase 2).
//   openfan-linux --dump [--sysfs PATH]
//   openfan-linux --apply-once <pwm-id> <percent> [--seconds N] [--sysfs PATH]

if (args.Length == 0 || args[0] is "-h" or "--help")
    return Usage(args.Length == 0);

var command = args[0];
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

var backend = new HwmonBackend(sysfs);
if (!backend.Available)
{
    Console.Error.WriteLine($"hwmon root not found: {sysfs ?? "/sys/class/hwmon"}");
    return 2;
}

var items = backend.Discover();
var readings = new Dictionary<string, double?>();
backend.ReadInto(readings);

switch (command)
{
    case "--dump":
        Dump(backend, items, readings);
        return 0;

    case "--apply-once" when args.Length >= 3:
        return ApplyOnce(backend, items, args[1], args[2], seconds);

    default:
        return Usage(false);
}

static void Dump(HwmonBackend backend, IReadOnlyList<HardwareItem> items, Dictionary<string, double?> readings)
{
    Console.WriteLine($"openfan-linux --dump  ({items.Count} items, NVML arrives in Phase 3)");
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
                _ => DescribeControl(backend, item, value),
            };
            Console.WriteLine($"  {item.Kind,-6} {detail,-24} {item.Name}");
            Console.WriteLine($"         {item.Id}");
        }
    }

    var controls = items.Count(i => i.Kind == HardwareKind.Control);
    var temps = items.Count(i => i.Kind == HardwareKind.Temperature && readings.GetValueOrDefault(i.Id) is not null);
    var tachs = items.Count(i => i.Kind == HardwareKind.Tach);
    Console.WriteLine();
    Console.WriteLine($"summary: {controls} pwm controls · {temps} live temps · {tachs} tach inputs");
}

static string DescribeControl(HwmonBackend backend, HardwareItem item, double? duty)
{
    var enable = backend.ReadEnableMode(item.Id);
    var mode = enable switch
    {
        0 => "off/full",
        1 => "manual",
        null => "no-enable-file",
        _ => $"auto({enable})",
    };
    return $"{mode,-12} duty {(duty is null ? "n/a" : $"{duty:0.#}%")}";
}

static int ApplyOnce(HwmonBackend backend, IReadOnlyList<HardwareItem> items, string idArg, string percentArg, int seconds)
{
    var control = items.FirstOrDefault(i =>
        i.Kind == HardwareKind.Control &&
        string.Equals(i.Id, idArg, StringComparison.OrdinalIgnoreCase));
    if (control is null)
    {
        Console.Error.WriteLine($"No such PWM control: {idArg}");
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

    // Tach with the same header number in the same chip group (heuristic pairing).
    var fanId = control.Id.Replace(":pwm:", ":fan:");
    var hasFan = items.Any(i => i.Id == fanId);

    Console.WriteLine($"Applying {percent}% to {control.Id} for {seconds}s " +
                      $"(enable was {backend.ReadEnableMode(control.Id)?.ToString() ?? "?"}); Ctrl+C restores auto.");
    try
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (!backend.SetPercent(control.Id, percent))
                return PermissionDenied(control.Id);

            var readings = new Dictionary<string, double?>();
            backend.ReadInto(readings);
            var dutyNow = readings.GetValueOrDefault(control.Id);
            var rpm = hasFan ? readings.GetValueOrDefault(fanId) : null;
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss} enable={backend.ReadEnableMode(control.Id)} " +
                              $"duty={dutyNow:0.#}%{(hasFan ? $" fan={rpm:0} RPM" : "")}");
            Thread.Sleep(1000); // re-write every tick — same EC lesson as Windows (spec §3.2)
        }
    }
    finally
    {
        backend.SetDefault(control.Id);
        Console.WriteLine($"Restored enable={backend.ReadEnableMode(control.Id)} (pre-takeover mode).");
    }

    return 0;
}

static int PermissionDenied(string id)
{
    Console.Error.WriteLine($"Cannot write {id}: permission denied.");
    Console.Error.WriteLine("Install the PWM ACL once (see packaging/README): create group 'openfan',");
    Console.Error.WriteLine("install udev rule + pwm-acl.sh, relogin — or run this one-off command with sudo.");
    return 3;
}

static int Usage(bool ok)
{
    Console.WriteLine("""
        openfan-linux — OpenFan hardware layer (Ubuntu)

          --dump [--sysfs PATH]                    list PWM controls, temps, tachs
          --apply-once ID PERCENT [--seconds N]    hold one PWM at PERCENT (default 10 s), then restore auto

        Example:
          openfan-linux --dump
          openfan-linux --apply-once hwmon:nct6799:10:pwm:4 40 --seconds 15
        """);
    return ok ? 0 : 1;
}
