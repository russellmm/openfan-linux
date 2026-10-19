using OpenFan.Core.Config;

namespace OpenFan.Core.Hardware;

/// <summary>Presentation groups and read demand share one identity; hiding never removes control inventory.</summary>
public static class SensorPollingPlan
{
    public static readonly string[] GroupOrder =
        ["CPU", "GPU", "Motherboard", "Chipset / ASUS EC", "AMD HSMP (SoC)", "Memory temperatures", "Memory (ECC)", "Storage (NVMe)", "Drive activity (NVMe)", "Drive health (NVMe)", "Network", "Other"];

    public static string Group(HardwareItem item)
    {
        if (item.Backend == "edac") return "Memory (ECC)";
        if (item.Backend == "nvml") return "GPU";
        if (item.Backend == "nvme-health") return "Drive health (NVMe)";
        if (item.Backend == "nvme-activity") return "Drive activity (NVMe)";
        var chip = item.Id.Split(':').ElementAtOrDefault(1) ?? "";
        return chip switch
        {
            "k10temp" => "CPU",
            "nct6799" or "nct6775" or "nct6798" or "nct6796" or "it87" => "Motherboard",
            "asusec" => "Chipset / ASUS EC", "nvme" => "Storage (NVMe)",
            "amd_hsmp_hwmon" => "AMD HSMP (SoC)", "spd5118" or "jc42" => "Memory temperatures",
            _ when chip.StartsWith("enp", StringComparison.OrdinalIgnoreCase) || chip.StartsWith("eth", StringComparison.OrdinalIgnoreCase) => "Network",
            _ => "Other",
        };
    }

    public static string DeviceKey(HardwareItem item)
    {
        var parts = item.Id.Split(':');
        return parts.Length > 2 && parts[1] == "nvme" ? "nvme:" + parts[2] : item.Group;
    }
    public static string CollapseKey(HardwareItem item) => Group(item) + "|" + DeviceKey(item);
    public static bool TableVisible(HardwareItem item, AppSettings settings) =>
        !settings.HiddenSensorGroups.Contains(Group(item), StringComparer.Ordinal) &&
        (settings.ShowSensorStatus || item.Kind != HardwareKind.Status);

    public static string ResolveHudId(IReadOnlyList<HardwareItem> inventory, string sourceId)
    {
        if (sourceId is "cpu:power:w" or "cpu:pptcap:w")
            return inventory.FirstOrDefault(i => i.Id.StartsWith("hwmon:amd_hsmp_hwmon:", StringComparison.OrdinalIgnoreCase) &&
                i.Kind == HardwareKind.Power && i.Id.EndsWith(sourceId == "cpu:pptcap:w" ? ":cap" : ":power:1", StringComparison.Ordinal))?.Id ?? sourceId;
        if (sourceId == "cpu:temp:c")
            return inventory.FirstOrDefault(i => i.Id.StartsWith("hwmon:k10temp:", StringComparison.OrdinalIgnoreCase) && i.Id.EndsWith(":temp:1"))?.Id
                ?? inventory.FirstOrDefault(i => i.Kind == HardwareKind.Temperature && (i.Name.Equals("Tctl", StringComparison.OrdinalIgnoreCase) || i.Name.Equals("Tdie", StringComparison.OrdinalIgnoreCase) || i.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase)))?.Id
                ?? sourceId;
        var parts = sourceId.Split(':');
        return parts.Length == 4 && parts[0] == "nvml" && parts[2] == "power" && parts[3] == "w"
            ? $"nvml:{parts[1]}:metric:power" : sourceId;
    }

    public static HashSet<string> Create(IReadOnlyList<HardwareItem> inventory, AppSettings settings,
        bool homeVisible = false, bool gpuPageVisible = false)
    {
        var requested = inventory.Where(i => TableVisible(i, settings) && !settings.CollapsedSensorGroups.Contains(Group(i)) && !settings.CollapsedSensorDevices.Contains(CollapseKey(i)))
            .Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var curves = settings.Curves.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void RequireCurve(string? id)
        {
            if (id is null || !visited.Add(id) || !curves.TryGetValue(id, out var curve)) return;
            if (curve.Type.Equals("graph", StringComparison.OrdinalIgnoreCase) && curve.SensorId is { } sensor) requested.Add(sensor);
            if (curve.Type.Equals("mix", StringComparison.OrdinalIgnoreCase))
                foreach (var child in curve.ChildCurveIds) RequireCurve(child);
        }
        if (settings.ApplyCurves)
        {
            var presentControls = inventory.Where(i => i.Kind == HardwareKind.Control).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var control in settings.Controls.Where(c => c.Enabled && presentControls.Contains(c.Id))) RequireCurve(control.CurveId);
        }
        if (settings.HudEnabled)
            foreach (var tile in settings.HudTiles) requested.Add(ResolveHudId(inventory, tile.SourceId));
        if (homeVisible)
        {
            foreach (var curve in settings.Curves) RequireCurve(curve.Id);
            foreach (var item in inventory.Where(i => i.Kind == HardwareKind.Control))
            {
                if (settings.Controls.Any(c => c.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) && c.Hidden)) continue;
                requested.Add(item.Id);
                var configured = settings.Controls.FirstOrDefault(c => c.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))?.PairedTachId;
                requested.Add(!string.IsNullOrWhiteSpace(configured) ? configured : (item.Backend == "nvml" ? item.Id.Replace(":fan:", ":tach:") : item.Id.Replace(":pwm:", ":fan:")));
            }
        }
        if (gpuPageVisible)
            foreach (var item in inventory.Where(i => i.Backend == "nvml")) requested.Add(item.Id);
        // Drop stale references. The two fixed legacy CPU power ids support the raw sysfs fallback.
        requested.IntersectWith(inventory.Select(i => i.Id).Concat(settings.HudEnabled
            ? settings.HudTiles.Select(t => t.SourceId).Where(id => id is "cpu:power:w" or "cpu:pptcap:w") : []));
        return requested;
    }
}
