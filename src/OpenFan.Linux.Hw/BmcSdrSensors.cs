using System.Globalization;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

public sealed record BmcSensorChannel(string Id, string Name, HardwareKind Kind, string Unit, double? Value);

/// <summary>Standard, read-only IPMI SDR measurements; threshold alarms are not missing values.</summary>
internal static class BmcSdrSensors
{
    public static BmcSensorChannel[] Parse(string output, HardwareKind kind)
    {
        if (kind is not (HardwareKind.Temperature or HardwareKind.Tach)) return [];
        var suffix = kind == HardwareKind.Temperature ? " degrees C" : " RPM";
        var sensors = new Dictionary<string, BmcSensorChannel>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length != 5 || cells[0].Length == 0 || cells[1].Length != 3 || cells[1][2] != 'h' ||
                !byte.TryParse(cells[1][..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)) continue;
            var id = "bmc:asus-wrx90:sdr:" + number.ToString("x2", CultureInfo.InvariantCulture);
            double? value = null;
            if (cells[2] is "ok" or "nc" or "cr" or "nr" or "lnc" or "lcr" or "lnr" or "unc" or "ucr" or "unr" &&
                cells[4].EndsWith(suffix, StringComparison.Ordinal) &&
                double.TryParse(cells[4][..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var reading) &&
                double.IsFinite(reading) && (kind != HardwareKind.Tach || reading >= 0)) value = reading;
            sensors[id] = new(id, cells[0], kind, kind == HardwareKind.Temperature ? "°C" : "RPM", value);
        }
        return sensors.Values.ToArray();
    }
}
