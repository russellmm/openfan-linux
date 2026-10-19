using System.Globalization;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

/// <summary>Read-only ECC counters exported by Linux EDAC. Kernel rank names are not guessed DIMM-slot labels.</summary>
public sealed class EdacBackend(string root = "/sys/devices/system/edac/mc") : ISensorBackend
{
    private readonly List<(HardwareItem Item, string Path)> _channels = [];
    public string Name => "edac";
    public bool Available => Directory.Exists(root);
    public IReadOnlyList<HardwareItem> Discover()
    {
        _channels.Clear();
        foreach (var controller in Directories(root, "mc*"))
        {
            var controllerName = Path.GetFileName(controller);
            Add(controller, "ce_count", controllerName, "Corrected errors", "corrected");
            Add(controller, "ue_count", controllerName, "Uncorrected errors", "uncorrected");
            foreach (var rank in Directories(controller, "rank*").Concat(Directories(controller, "dimm*")))
            {
                var rankName = Path.GetFileName(rank);
                Add(rank, "dimm_ce_count", $"{controllerName} · {rankName}", "Corrected errors", "corrected");
                Add(rank, "dimm_ue_count", $"{controllerName} · {rankName}", "Uncorrected errors", "uncorrected");
            }
        }
        return _channels.Select(c => c.Item).ToArray();
    }
    private void Add(string dir, string file, string group, string label, string kind)
    {
        var path = Path.Combine(dir, file); if (!File.Exists(path)) return;
        var relative = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, ':');
        var description = CpuFrequencyMonitor.ReadText(Path.Combine(dir, "dimm_label"));
        _channels.Add((new HardwareItem
        {
            Id = $"edac:{relative}:{kind}:count", Name = label, Kind = HardwareKind.Counter, Group = group,
            Backend = Name, Unit = "errors", Source = "EDAC", CanSet = false,
            Details = $"Kernel EDAC counter · {description} · {path}",
        }, path));
    }
    public void ReadInto(IDictionary<string, double?> readings) => ReadInto(readings, null);
    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        foreach (var (item, path) in _channels)
            if (requested is null || requested.Contains(item.Id)) readings[item.Id] = ulong.TryParse(CpuFrequencyMonitor.ReadText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
    private static string[] Directories(string root, string pattern)
    {
        try { return Directory.GetDirectories(root, pattern).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    public void Dispose() { }
}
