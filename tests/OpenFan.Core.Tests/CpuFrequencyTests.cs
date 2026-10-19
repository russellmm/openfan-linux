using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public sealed class CpuFrequencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openfan-freq-" + Guid.NewGuid().ToString("N"));
    private string Info => Path.Combine(_root, "cpuinfo");
    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text);
    }
    private void Cpu(int id, int core, string siblings, string? clock = "3000000")
    {
        Write($"cpu{id}/topology/core_id", core.ToString()); Write($"cpu{id}/topology/physical_package_id", "0");
        Write($"cpu{id}/topology/thread_siblings_list", siblings);
        if (clock is not null) Write($"cpu{id}/cpufreq/scaling_cur_freq", clock);
    }
    private void Policy(int id, string cpus, string minimum = "1200000", string maximum = "4000000", string hardwareMaximum = "4000000")
    {
        Write($"cpufreq/policy{id}/related_cpus", cpus); Write($"cpufreq/policy{id}/scaling_min_freq", minimum);
        Write($"cpufreq/policy{id}/scaling_max_freq", maximum); Write($"cpufreq/policy{id}/cpuinfo_max_freq", hardwareMaximum);
        Write($"cpufreq/policy{id}/scaling_driver", "amd-pstate-epp"); Write($"cpufreq/policy{id}/boost", "0");
    }
    private string Cap(int id) => File.ReadAllText(Path.Combine(_root, $"cpufreq/policy{id}/scaling_max_freq")).Trim();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void GroupsSmtSiblingsIntoPhysicalCoresAndSortsNumerically()
    {
        Cpu(32, 0, "0,32", "2500000"); Cpu(10, 10, "10,42"); Cpu(0, 0, "0,32", "3500000"); Cpu(42, 10, "10,42");
        Policy(0, "0"); Policy(32, "32");
        var cores = new CpuFrequencyMonitor(_root, Info).ReadCores();
        Assert.Equal(2, cores.Count); Assert.Equal(0, cores[0].Core); Assert.Equal(10, cores[1].Core);
        Assert.Equal(new[] { 0, 32 }, cores[0].Threads.Select(t => t.Id)); Assert.Equal(3500, cores[0].CurrentMHz);
        Assert.Equal(1, cores[0].Threads[1].Thread); Assert.Equal(32, cores[0].Threads[1].FrequencyPolicy!.Id);
    }

    [Fact]
    public void CoreStatisticsUseOneMaximumSiblingSamplePerIntervalAndReset()
    {
        Cpu(0, 0, "0,32", "3000000"); Cpu(32, 0, "0,32", "2000000");
        var monitor = new CpuFrequencyMonitor(_root, Info); monitor.ReadCores();
        Write("cpu0/cpufreq/scaling_cur_freq", "1800000"); Write("cpu32/cpufreq/scaling_cur_freq", "2000000");
        var row = Assert.Single(monitor.ReadCores());
        Assert.Equal(2000, row.MinimumMHz); Assert.Equal(3000, row.MaximumMHz); Assert.Equal(2500, row.AverageMHz);
        monitor.ResetStatistics(); row = Assert.Single(monitor.ReadCores()); Assert.Equal(2000, row.MaximumMHz);
    }

    [Fact]
    public void PrefersHardwareFrequencyAndRejectsInvalidReadingsWithoutInventingZero()
    {
        Cpu(0, 0, "0"); Write("cpu0/cpufreq/cpuinfo_cur_freq", "3200000");
        var monitor = new CpuFrequencyMonitor(_root, Info);
        var row = Assert.Single(monitor.Read()); Assert.Equal(3200, row.CurrentMHz); Assert.Equal("cpuinfo_cur_freq", row.Source);
        Write("cpu0/cpufreq/cpuinfo_cur_freq", "-50"); Write("cpu0/cpufreq/scaling_cur_freq", "not a clock");
        row = Assert.Single(monitor.Read()); Assert.Null(row.CurrentMHz); Assert.Equal(3200, row.MinimumMHz);
    }

    [Fact]
    public void OfflineCpuHasNoCurrentClockAndDoesNotFallBackToStaleProcValue()
    {
        Cpu(2, 1, "2", "3200000"); Write("cpu2/online", "0"); Write("cpuinfo", "processor : 2\ncpu MHz : 3500\n");
        var row = Assert.Single(new CpuFrequencyMonitor(_root, Info).Read());
        Assert.False(row.Online); Assert.Null(row.CurrentMHz); Assert.Null(row.MinimumMHz);
    }

    [Fact]
    public void FallsBackToProcByProcessorIdAndDoesNotDependOnCulture()
    {
        Cpu(7, 3, "7", null); Cpu(20, 10, "20", null);
        Write("cpuinfo", "processor : 20\ncpu MHz : 2999.125\n\nprocessor : 7\ncpu MHz : 1555.5\n");
        var rows = new CpuFrequencyMonitor(_root, Info).Read();
        Assert.Equal(new[] { 7, 20 }, rows.Select(r => r.Id)); Assert.Equal(1555.5, rows[0].CurrentMHz); Assert.Equal(2999.125, rows[1].CurrentMHz);
    }

    [Fact]
    public void SharedPoliciesUseRelatedCpuMembershipAndIgnoreUnrelatedDirectories()
    {
        Cpu(2, 1, "2,34"); Cpu(34, 1, "2,34"); Policy(9, "2 34"); Write("cpuidle/unrelated", "1");
        var rows = new CpuFrequencyMonitor(_root, Info).Read();
        Assert.Equal(2, rows.Count); Assert.All(rows, row => Assert.Equal(9, row.FrequencyPolicy!.Id));
    }

    [Fact]
    public void MissingTopologyNeverMergesUnknownCoresAndMissingRootIsEmpty()
    {
        Assert.Empty(new CpuFrequencyMonitor(_root, Info).Read());
        Write("cpu0/cpufreq/scaling_cur_freq", "3000000"); Write("cpu1/cpufreq/scaling_cur_freq", "3000000");
        Assert.Equal(2, new CpuFrequencyMonitor(_root, Info).ReadCores().Count);
    }

    [Theory]
    [InlineData("0-3,8 10-11", "0,1,2,3,8,10,11")]
    [InlineData("3,1,3", "1,3")]
    [InlineData("-1,7-3,x,2147483647,0-9999999", "")]
    public void ParsesCpuListsWithBounds(string input, string expected) => Assert.Equal(expected, string.Join(",", CpuFrequencyMonitor.ParseCpuList(input)));

    [Fact]
    public void CapTargetsBothSiblingPoliciesAndLeavesOtherSettingsAlone()
    {
        Policy(0, "0"); Policy(32, "32"); Policy(1, "1");
        var control = new CpuFrequencyControl(_root);
        Assert.True(control.SetMaximum(new[] { 0, 32 }, 3500000));
        Assert.Equal("3500000", Cap(0)); Assert.Equal("3500000", Cap(32)); Assert.Equal("4000000", Cap(1));
        Assert.Equal("1200000", File.ReadAllText(Path.Combine(_root, "cpufreq/policy0/scaling_min_freq")));
        Assert.Equal("0", File.ReadAllText(Path.Combine(_root, "cpufreq/policy0/boost")));
        Assert.True(control.SetMaximum(new[] { 0, 32 }, null)); Assert.Equal("4000000", Cap(0));
    }

    [Fact]
    public void AllPoliciesAreValidatedBeforeAnyWriteAndUseCurrentMinimum()
    {
        Policy(0, "0"); Policy(1, "1", minimum: "3000000");
        var control = new CpuFrequencyControl(_root);
        Assert.False(control.SetMaximum(null, 2500000)); Assert.Contains("policy1", control.LastError);
        Assert.Equal("4000000", Cap(0)); Assert.Equal("4000000", Cap(1));
        Assert.True(control.SetMaximum(null, 3200000)); Assert.Equal("3200000", Cap(0)); Assert.Equal("3200000", Cap(1));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(4000001)] [InlineData(long.MaxValue)]
    public void RejectsOutOfRangeMaximum(long khz)
    {
        Policy(0, "0"); Assert.False(new CpuFrequencyControl(_root).SetMaximum(new[] { 0 }, khz)); Assert.Equal("4000000", Cap(0));
    }

    [Fact]
    public void MissingPolicyOrIncompleteLimitsFailWithoutWriting()
    {
        Policy(0, "0"); var control = new CpuFrequencyControl(_root);
        Assert.False(control.SetMaximum(new[] { 0, 99 }, 2500000)); Assert.Equal("4000000", Cap(0));
        Write("cpufreq/policy0/cpuinfo_max_freq", "malformed"); Assert.False(control.SetMaximum(null, null));
    }

    [Fact]
    public async Task FrequencyCeilingRoundTripsThroughHelperSocket()
    {
        Policy(0, "0"); Policy(32, "32");
        var socket = Path.Combine(Path.GetTempPath(), $"of-freq-{Guid.NewGuid():N}.sock");
        await using var server = new HelperServer(new RecordingActuator(), socket, frequency: new CpuFrequencyControl(_root));
        server.Start(); using var client = new NvmlHelperClient(socket);
        Assert.True(client.TrySetCpuFrequencyMaximum(new[] { 0, 32 }, 3500000)); Assert.Equal("3500000", Cap(32));
        Assert.False(client.TrySetCpuFrequencyMaximum(null, 9999999)); Assert.Contains("maximum must be", client.LastError);
        Assert.True(client.TrySetCpuFrequencyMaximum(null, null)); Assert.Equal("4000000", Cap(0));
    }

    [Fact]
    public void KernelClampRollsBackAllPreviouslyChangedPolicies()
    {
        Policy(0, "0"); Policy(1, "1");
        var control = new CpuFrequencyControl(_root, (path, khz) =>
            File.WriteAllText(path, (path.Contains("policy1/") && khz == 3500000 ? 3400000 : khz).ToString()));
        Assert.False(control.SetMaximum(null, 3500000));
        Assert.Equal("4000000", Cap(0)); Assert.Equal("4000000", Cap(1));
        Assert.Contains("previous ceilings retained", control.LastError);
    }

    [Fact]
    public void WaitsForAsynchronousKernelPolicyReadback()
    {
        Policy(0, "0");
        Task? pending = null;
        var control = new CpuFrequencyControl(_root, (path, khz) =>
            pending = Task.Run(async () => { await Task.Delay(40); File.WriteAllText(path, khz.ToString()); }));
        Assert.True(control.SetMaximum(null, 3500000));
        pending!.GetAwaiter().GetResult(); Assert.Equal("3500000", Cap(0));
    }

    [Fact]
    public void RollbackFailureIsReportedRatherThanClaimingRestoration()
    {
        Policy(0, "0"); Policy(1, "1");
        var control = new CpuFrequencyControl(_root, (path, khz) =>
        {
            if (path.Contains("policy1/")) throw new IOException("device removed");
            File.WriteAllText(path, khz.ToString());
        });
        Assert.False(control.SetMaximum(null, 3500000)); Assert.Equal("4000000", Cap(0));
        Assert.Contains("could not be restored", control.LastError);
    }

    [Fact]
    public async Task DifferentCoreCapsRoundTripInOneBatch()
    {
        Policy(0, "0"); Policy(32, "32"); Policy(1, "1"); Policy(33, "33");
        var socket = Path.Combine(Path.GetTempPath(), $"of-batch-{Guid.NewGuid():N}.sock");
        await using var server = new HelperServer(new RecordingActuator(), socket, frequency: new CpuFrequencyControl(_root));
        server.Start(); using var client = new NvmlHelperClient(socket);
        Assert.True(client.TrySetCpuFrequencyMaximums(new Dictionary<int, long>
            { [0] = 3000000, [32] = 3000000, [1] = 2800000, [33] = 2800000 }));
        Assert.Equal("3000000", Cap(0)); Assert.Equal("3000000", Cap(32));
        Assert.Equal("2800000", Cap(1)); Assert.Equal("2800000", Cap(33));
    }

    [Fact]
    public void InvalidIndividualBatchChangesNothing()
    {
        Policy(0, "0"); Policy(1, "1"); var control = new CpuFrequencyControl(_root);
        Assert.False(control.SetMaximums(new Dictionary<int, long> { [0] = 3000000, [1] = 9999999 }));
        Assert.Equal("4000000", Cap(0)); Assert.Equal("4000000", Cap(1));
        Assert.False(control.SetMaximums(new Dictionary<int, long> { [0] = 3000000, [99] = 3000000 }));
        Assert.Equal("4000000", Cap(0)); Assert.False(control.SetMaximums(new Dictionary<int, long>()));
    }

    [Fact]
    public void IndividualBatchFailureRestoresEachPreviousCeiling()
    {
        Policy(0, "0", maximum: "3700000"); Policy(1, "1", maximum: "3900000");
        var control = new CpuFrequencyControl(_root, (path, khz) =>
            File.WriteAllText(path, (path.Contains("policy1/") && khz == 2800000 ? 2700000 : khz).ToString()));
        Assert.False(control.SetMaximums(new Dictionary<int, long> { [0] = 3000000, [1] = 2800000 }));
        Assert.Equal("3700000", Cap(0)); Assert.Equal("3900000", Cap(1));
    }

    [Theory]
    [InlineData("cpufreqbatch 0=3000000,0=2800000")]
    [InlineData("cpufreqbatch ../0=3000000")]
    [InlineData("cpufreqbatch 0=-1")]
    [InlineData("cpufreqbatch 0=3000000,1=9999999")]
    [InlineData("cpufreqbatch 0=3000000 trailing")]
    public void RejectsMalformedOrInvalidBatchesBeforeWriting(string command)
    {
        Policy(0, "0"); Policy(1, "1");
        var session = new HelperSession(new RecordingActuator(), frequency: new CpuFrequencyControl(_root));
        Assert.StartsWith("err ", session.HandleLine(command)); Assert.Equal("4000000", Cap(0)); Assert.Equal("4000000", Cap(1));
    }

    [Theory]
    [InlineData("cpufreq ../0 3000000")]
    [InlineData("cpufreq -1 3000000")]
    [InlineData("cpufreq 0,garbage 3000000")]
    [InlineData("cpufreq 0 -1")]
    [InlineData("cpufreq 0 3500000 extra")]
    public void ProtocolRejectsInvalidTargetsAndFrequencies(string command)
    {
        Policy(0, "0"); var session = new HelperSession(new RecordingActuator(), frequency: new CpuFrequencyControl(_root));
        Assert.StartsWith("err ", session.HandleLine(command)); Assert.Equal("4000000", Cap(0));
    }
}
