using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

public sealed record NvmeDevice(string Controller, string Pci, string Model, string Firmware, string Identity, string? HwmonPath, string SysfsPath)
{
    public string Group => "nvme:pci:" + Uri.EscapeDataString(Pci);
    public string MetricId(string metric) => $"nvme-health:{Identity}:smart:{metric}";
    public string Description => $"Firmware {Firmware} · {Controller} · {Pci}";
}

/// <summary>Kernel metadata only; no admin commands, drive scans, or serial numbers in display labels.</summary>
public static class NvmeDevices
{
    public static IReadOnlyList<NvmeDevice> Discover(string root = "/sys/class/nvme")
    {
        var devices = new List<NvmeDevice>();
        try
        {
            foreach (var path in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            {
                var controller = Path.GetFileName(path);
                if (!Regex.IsMatch(controller, @"^nvme[0-9]{1,8}$", RegexOptions.CultureInvariant)) continue;
                var pci = CpuFrequencyMonitor.ReadText(Path.Combine(path, "address"));
                var model = CpuFrequencyMonitor.ReadText(Path.Combine(path, "model"));
                var serial = CpuFrequencyMonitor.ReadText(Path.Combine(path, "serial"));
                if (pci is null || string.IsNullOrEmpty(model) || string.IsNullOrEmpty(serial) || !Regex.IsMatch(pci, @"^[0-9a-fA-F]{4}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.[0-7]$")) continue;
                var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pci + "\n" + model + "\n" + serial))).ToLowerInvariant();
                string? hwmon = null;
                try
                {
                    hwmon = Directory.GetDirectories(path, "hwmon*").FirstOrDefault(p => Regex.IsMatch(Path.GetFileName(p), @"^hwmon[0-9]+$"));
                    if (hwmon is null && Directory.Exists(Path.Combine(path, "hwmon")))
                        hwmon = Directory.GetDirectories(Path.Combine(path, "hwmon"), "hwmon*").FirstOrDefault();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                devices.Add(new(controller, pci, model, CpuFrequencyMonitor.ReadText(Path.Combine(path, "firmware_rev")) ?? "unknown", identity, hwmon, path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return devices;
    }

    public static HardwareItem Describe(HardwareItem item, string hwmonPath)
    {
        var parts = item.Id.Split(':');
        if (parts.Length < 5 || parts[0] != "hwmon" || parts[1] != "nvme" || parts[3] != "temp") return item;
        string controller = "";
        try
        {
            var dir = new DirectoryInfo(hwmonPath);
            var physical = dir.ResolveLinkTarget(true) as DirectoryInfo ?? dir;
            controller = new DirectoryInfo(Path.Combine(physical.FullName, "device")).ResolveLinkTarget(true)?.Name ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var n = int.TryParse(parts[4], out var number) ? number : 0;
        var generic = Regex.IsMatch(item.Name, @"^(Composite|Sensor [0-9]+|Temperature #[0-9]+|temp[0-9]+)( · .*)?$", RegexOptions.CultureInvariant);
        var label = n == 1 ? "Drive temperature (composite)" : $"Internal temperature {n - 1} (unmapped)";
        if (item.Kind == HardwareKind.Status) label = "Drive temperature alarm";
        if (item.Kind == HardwareKind.TemperatureLimit) label = "Drive temperature limit";
        return new HardwareItem
        {
            Id = item.Id, Name = generic ? label : item.Name, Kind = item.Kind, Backend = item.Backend,
            Group = item.Group, CanSet = false, Unit = item.Unit, Source = item.Source,
            Details = item.Details + $" · {controller} · " + (n == 1
                ? "Composite is the drive's overall thermal-management reading; it need not be one physical thermometer."
                : $"NVMe SMART temperature sensor {n - 1}. Its physical location and accuracy are vendor-defined; controller/NAND mapping is not verified for this model."),
        };
    }
}
