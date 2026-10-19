using System.Globalization;

namespace OpenFan.Linux.Hw;

public interface ICpuFrequencyWriter
{
    bool SetMaximum(IReadOnlyList<int>? policyIds, long? maximumKHz);
    string? LastError { get; }
}

public interface ICpuFrequencyBatchWriter : ICpuFrequencyWriter
{
    bool SetMaximums(IReadOnlyDictionary<int, long> maximumsKHz);
}

/// <summary>Root-side cpufreq ceilings; never changes boost, governor, EPP or minimum frequency.</summary>
public sealed class CpuFrequencyControl(string cpuRoot = "/sys/devices/system/cpu", Action<string, long>? writeMaximum = null) : ICpuFrequencyBatchWriter
{
    private readonly Action<string, long> _write = writeMaximum ?? ((path, value) =>
        File.WriteAllText(path, value.ToString(CultureInfo.InvariantCulture) + "\n"));
    public string? LastError { get; private set; }
    private readonly object _gate = new();
    public bool SetMaximum(IReadOnlyList<int>? policyIds, long? maximumKHz)
    {
        lock (_gate) return SetMaximumLocked(policyIds, maximumKHz);
    }
    public bool SetMaximums(IReadOnlyDictionary<int, long> maximumsKHz)
    {
        lock (_gate) return SetMaximumLocked(maximumsKHz.Keys.ToArray(), null, maximumsKHz);
    }
    private bool SetMaximumLocked(IReadOnlyList<int>? policyIds, long? maximumKHz,
        IReadOnlyDictionary<int, long>? maximumsKHz = null)
    {
        var policies = CpuFrequencyMonitor.ReadPolicies(cpuRoot)
            .Where(p => policyIds is null || policyIds.Contains(p.Id)).ToArray();
        if (policies.Length == 0 || (policyIds is not null && policyIds.Distinct().Count() != policies.Length)) return Fail("CPU frequency policy is unavailable");
        var changes = new List<(string Path, long Before, long After)>();
        foreach (var policy in policies)
        {
            if (policy.MinimumKHz is not { } min || policy.MaximumKHz is not { } before || policy.HardwareMaximumKHz is not { } max)
                return Fail($"policy{policy.Id}: frequency limits are unavailable");
            var requested = maximumsKHz is null ? maximumKHz ?? max : maximumsKHz[policy.Id];
            if (requested < min || requested > max)
                return Fail($"policy{policy.Id}: maximum must be {min / 1000.0:0.###}–{max / 1000.0:0.###} MHz");
            changes.Add((Path.Combine(cpuRoot, "cpufreq", $"policy{policy.Id}", "scaling_max_freq"), before, requested));
        }
        var applied = new List<(string Path, long Before)>();
        try
        {
            foreach (var change in changes)
            {
                // Include the attempted write in rollback: a driver can write and then clamp the result.
                applied.Add((change.Path, change.Before));
                _write(change.Path, change.After);
            }
            // Frequency QoS policy work can complete after the sysfs write returns. Verify the
            // whole batch within one deadline instead of multiplying a timeout by the CPU count.
            if (!WaitForReadback(changes.Select(c => (c.Path, Value: c.After)).ToArray()))
                throw new IOException("kernel did not retain all requested ceilings within 500 ms");
            LastError = null; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rollbackFailed = false;
            foreach (var change in applied.AsEnumerable().Reverse())
            {
                try
                {
                    _write(change.Path, change.Before);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rollbackFailed = true; }
            }
            rollbackFailed |= !WaitForReadback(applied.Select(c => (c.Path, Value: c.Before)).ToArray());
            return Fail($"frequency limit failed: {ex.Message}; " +
                (rollbackFailed ? "some ceilings could not be restored; check the live limits" : "previous ceilings retained"));
        }
    }
    private static bool WaitForReadback(IReadOnlyList<(string Path, long Value)> expected)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (expected.All(c => CpuFrequencyMonitor.ReadPositive(c.Path) == c.Value)) return true;
            Thread.Sleep(5);
        } while (clock.ElapsedMilliseconds < 500);
        return expected.All(c => CpuFrequencyMonitor.ReadPositive(c.Path) == c.Value);
    }
    private bool Fail(string message) { LastError = message; return false; }
}
