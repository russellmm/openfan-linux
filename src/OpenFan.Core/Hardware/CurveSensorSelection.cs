using OpenFan.Core.Config;

namespace OpenFan.Core.Hardware;

/// <summary>Curve menus select from the same inventory as the Sensors table.</summary>
public static class CurveSensorSelection
{
    public static bool CanUse(HardwareItem item) => item.Kind == HardwareKind.Temperature;

    public static IReadOnlyList<HardwareItem> Available(IReadOnlyList<HardwareItem> inventory, AppSettings settings)
    {
        var selected = settings.CurveSensorIds.ToHashSet(StringComparer.Ordinal);
        return inventory.Where(i => CanUse(i) && selected.Contains(i.Id))
            .OrderBy(i => i.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
    }
}
