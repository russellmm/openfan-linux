using System.Text.RegularExpressions;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Identifies already-bound DDR5 SPD hubs. No bus probing or EEPROM access: measurement reads
/// remain with libsensors (or the existing sysfs fallback). Kernel enumeration numbers are not identity.
/// </summary>
public static class DramSensorIdentity
{
    public static HardwareItem Describe(HardwareItem item, string hwmonPath)
    {
        var parts = item.Id.Split(':');
        if (parts.Length < 5 || parts[0] != "hwmon" || parts[1] != "spd5118" || parts[3] != "temp") return item;
        try
        {
            // Resolve the class link first: relative 'device' links are based on its physical
            // directory. Resolving the child directly through /sys/class can yield /sys/0-0050.
            var hwmon = new DirectoryInfo(hwmonPath);
            var physical = hwmon.ResolveLinkTarget(true) as DirectoryInfo ?? hwmon;
            var device = new DirectoryInfo(Path.Combine(physical.FullName, "device")).ResolveLinkTarget(true) as DirectoryInfo;
            if (device is null) return item;
            var client = Regex.Match(device.Name, @"^(\d+)-00([0-9a-f]{2})$", RegexOptions.CultureInvariant);
            var adapter = device.Parent;
            if (!client.Success || adapter?.Name != "i2c-" + client.Groups[1].Value || adapter.Parent is not { } controller) return item;
            var address = client.Groups[2].Value;
            // The controller's physical sysfs path plus hub address survives hwmon AND adapter renumbering.
            var identity = "device-" + Uri.EscapeDataString(controller.FullName + "/" + address);
            var who = $"{controller.Name} / 0x{address}";
            var generic = item.Name == "temp" + parts[4] || item.Name == "Temperature #" + parts[4];
            // Keep labels configured in sensors.d, including the suffixes on optional status flags.
            var label = generic ? "DRAM" : item.Name.StartsWith("temp" + parts[4] + " · ", StringComparison.Ordinal)
                ? "DRAM" + item.Name[("temp" + parts[4]).Length..] : item.Name;
            return new HardwareItem
            {
                Id = string.Join(':', parts.Take(2).Append(identity).Concat(parts.Skip(3))),
                Name = $"{label} · {who}", Kind = item.Kind, Backend = item.Backend,
                Group = "DDR5 DIMMs", CanSet = false, Unit = item.Unit.Length == 0 ? "°C" : item.Unit,
                Source = item.Source,
                Details = $"DDR5 SPD hub temperature · controller {controller.FullName} · I2C {client.Groups[1].Value}, address 0x{address}" +
                    " · physical DIMM slot not yet mapped · " + item.Details,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return item; // Metadata is optional; a missing link must not hide a valid library channel.
        }
    }
}
