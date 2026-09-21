using OpenFan.Core.Hardware;

namespace OpenFan.Core.Hardware;

public static class TachPairing
{
    public static string? Suggest(string controlId, IEnumerable<HardwareItem> inventory)
    {
        var tachs = inventory.Where(i => i.Kind == HardwareKind.Tach).ToList();
        if (tachs.Count == 0)
            return null;

        if (controlId.Contains(":fan:", StringComparison.OrdinalIgnoreCase))
        {
            var guess = controlId.Replace(":fan:", ":tach:", StringComparison.OrdinalIgnoreCase);
            if (tachs.Any(t => t.Id.Equals(guess, StringComparison.OrdinalIgnoreCase)))
                return guess;
        }

        var control = inventory.FirstOrDefault(i => i.Id.Equals(controlId, StringComparison.OrdinalIgnoreCase));
        if (control is null)
            return null;

        var sameGroup = tachs
            .Where(t => t.Group.Equals(control.Group, StringComparison.OrdinalIgnoreCase) && t.Group.Length > 0)
            .ToList();
        if (sameGroup.Count == 1)
            return sameGroup[0].Id;

        var byName = sameGroup.FirstOrDefault(t =>
            t.Name.Contains(control.Name, StringComparison.OrdinalIgnoreCase)
            || control.Name.Contains(t.Name, StringComparison.OrdinalIgnoreCase));
        return byName?.Id;
    }
}
