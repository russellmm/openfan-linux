namespace OpenFan.Core.Hardware;

public enum HardwareKind
{
    Control,
    Temperature,
    Tach,
}

public sealed class HardwareItem
{
    public required string Id { get; init; }
    public required HardwareKind Kind { get; init; }
    public required string Name { get; init; }
    public required string Backend { get; init; }
    public string Group { get; init; } = "";
    public int MinPercent { get; init; }
    public bool CanSet { get; init; } = true;
}

public sealed class HardwareReading
{
    public required string Id { get; init; }
    public double? Value { get; init; }
}

public static class InventoryMerger
{
    public static IReadOnlyList<HardwareItem> Merge(
        IEnumerable<HardwareItem> hwmon,
        IEnumerable<HardwareItem> nvml)
    {
        var nvmlList = nvml.ToList();
        var nvmlGroups = nvmlList
            .Where(i => i.Kind == HardwareKind.Control)
            .Select(i => NormalizeGroup(i.Group))
            .Where(g => g.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var keptHwmon = hwmon.Where(item =>
        {
            if (!string.Equals(item.Backend, "hwmon", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!LooksLikeGpu(item))
                return true;
            var g = NormalizeGroup(item.Group);
            return g.Length == 0 || !nvmlGroups.Contains(g);
        });

        return keptHwmon.Concat(nvmlList).ToList();
    }

    private static bool LooksLikeGpu(HardwareItem item)
    {
        var blob = $"{item.Group} {item.Name} {item.Id}";
        return blob.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("geforce", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("nvidiagpu", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("rtx", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeGroup(string group)
        => group.Trim();
}
