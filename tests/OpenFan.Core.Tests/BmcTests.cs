using System.Globalization;
using System.Text.Json;
using OpenFan.Core.ControlLoop;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public sealed class BmcTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openfan-bmc-").FullName;
    private static BmcProbeReport Fixture() => JsonSerializer.Deserialize<BmcProbeReport>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "asus-wrx90-bmc.json")))!;
    private string Journal => Path.Combine(_root, "state", "restore.json");
    private static string Id(int index) => AsusBmcController.IdPrefix + index;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Firmware
    {
        public readonly BmcProbeReport Report = Fixture();
        public readonly Dictionary<int, byte[]> States;
        public readonly List<string[]> Writes = [];
        public bool FailWrite;
        public bool FailReadbackOnce;
        private bool _afterWrite;

        public Firmware() => States = Report.Commands.Where(c => c.Arguments.Length == 6 && c.Arguments[2..5].SequenceEqual(new[] { "raw", "0x30", "0x0f" }))
            .ToDictionary(c => int.Parse(c.Arguments[5][2..], NumberStyles.HexNumber), c => AsusBmcController.ParseHex(c.Output));
        public BmcCommandResult Run(string[] args)
        {
            if (args.Length >= 5 && args[2..5].SequenceEqual(new[] { "raw", "0x30", "0x0e" }))
            {
                Writes.Add(args);
                if (FailWrite) return new(args, 1, "", "write refused");
                States[Convert.ToInt32(args[6], 16)] = [4, .. args.Skip(7).Select(a => Convert.ToByte(a, 16))];
                _afterWrite = true;
                return new(args, 0, "", "");
            }
            if (args.Length == 6 && args[2..5].SequenceEqual(new[] { "raw", "0x30", "0x0f" }))
            {
                if (_afterWrite && FailReadbackOnce)
                { FailReadbackOnce = false; _afterWrite = false; return new(args, 1, "", "read failed after write"); }
                return new(args, 0, Convert.ToHexString(States[Convert.ToInt32(args[5], 16)]).Chunk(2)
                    .Select(c => new string(c)).Aggregate((a, b) => a + " " + b), "");
            }
            return Report.Commands.FirstOrDefault(c => c.Arguments.SequenceEqual(args)) ?? new(args, 1, "", "unsupported");
        }
    }
    private AsusBmcController Controller(Firmware fw, string identity = "test-machine") => new(fw.Report, fw.Run, Journal, identity);

    [Fact]
    public void ProbeStopsAtPermissionFailureWithoutOemRequests()
    {
        var calls = new List<string[]>();
        var probe = new BmcProbe(args => { calls.Add(args); return new(args, 1, "", "permission denied"); }, _root, _root);
        probe.Read().Error.Should().Contain("root");
        calls.Should().ContainSingle().Which.Should().Equal("-I", "open", "mc", "info");
    }

    [Fact]
    public void ProbeUsesAdvertisedSlotsAndOnlyReadOnlyCommands()
    {
        var fw = new Firmware();
        foreach (var pair in fw.Report.Board) File.WriteAllText(Path.Combine(_root, pair.Key), pair.Value);
        var calls = new List<string[]>();
        var probe = new BmcProbe(args =>
        {
            calls.Add(args);
            if (args.SequenceEqual(new[] { "-I", "open", "sdr", "type", "Temperature" })) return new(args, 0, "", "");
            return fw.Run(args);
        }, _root, Path.Combine(_root, "missing-hwmon"));
        probe.Read().Error.Should().BeNull();
        fw.Writes.Should().BeEmpty();
        foreach (var args in calls.Where(a => a[2] == "raw"))
        {
            args[3].Should().Be("0x30");
            if (args[4] == "0x13") args[5].Should().BeOneOf("0x00", "0x01", "0x03");
            else args[4].Should().Be("0x0f");
            Convert.ToInt32(args[^1], 16).Should().BeLessThan(8);
        }
    }

    [Fact]
    public void UnvalidatedFirmwareRejectsControlWithoutWrites()
    {
        var fw = new Firmware();
        var report = fw.Report with { Commands = fw.Report.Commands.Select(c => c.Arguments[2] == "mc"
            ? c with { Output = c.Output.Replace("2.01", "9.99") } : c).ToList() };
        using var driver = new AsusBmcController(report, fw.Run, Journal, "test-machine");
        driver.Available.Should().BeFalse();
        driver.SetPercent(Guid.NewGuid(), Id(1), 60).Should().BeFalse();
        fw.Writes.Should().BeEmpty();
    }

    [Fact]
    public void TargetUsesOnlyOneZoneAndRestoresExactCurve()
    {
        var fw = new Firmware();
        var originals = fw.States.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone());
        using var driver = Controller(fw);
        driver.Available.Should().BeTrue(driver.LastError);
        var owner = Guid.NewGuid();
        driver.SetPercent(owner, Id(1), 70).Should().BeTrue(driver.LastError);
        File.Exists(Journal).Should().BeTrue();
        fw.States[1].Should().Equal(4, 20, 70, 45, 70, 65, 70, 70, 70, 100, 70);
        foreach (var index in originals.Keys.Where(i => i != 1)) fw.States[index].Should().Equal(originals[index]);
        var snapshot = driver.Snapshot(owner);
        snapshot.Channels.Single(c => c.Index == 1).TargetPercent.Should().Be(70);
        snapshot.Channels.Single(c => c.Index == 1).Rpm.Should().Be(2040);
        driver.SetDefault(owner, Id(1)).Should().BeTrue();
        fw.States[1].Should().Equal(originals[1]);
        File.Exists(Journal).Should().BeFalse();
    }

    [Theory]
    [InlineData(1, -1)]
    [InlineData(0, 101)]
    [InlineData(6, -1)]
    [InlineData(1, 101)]
    public void OutOfRangeTargetsRejectWrites(int index, int percent)
    {
        var fw = new Firmware(); using var driver = Controller(fw);
        driver.SetPercent(Guid.NewGuid(), Id(index), percent).Should().BeFalse();
        fw.Writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void EveryBmcZoneAcceptsZeroAndRestoresItsOriginalCurve(int index)
    {
        var fw = new Firmware(); using var driver = Controller(fw);
        var before = fw.States[index].ToArray(); var owner = Guid.NewGuid();
        driver.Snapshot(owner).Channels.Should().OnlyContain(c => c.MinPercent == 0);
        driver.SetPercent(owner, Id(index), 0).Should().BeTrue(driver.LastError);
        Enumerable.Range(0, 5).Select(i => fw.States[index][2 + 2 * i]).Should().OnlyContain(d => d == 0);
        driver.Snapshot(owner).Channels.Single(c => c.Index == index).TargetPercent.Should().Be(0);
        driver.SetDefault(owner, Id(index)).Should().BeTrue();
        fw.States[index].Should().Equal(before);
    }

    [Fact]
    public void FailedJournalWriteDoesNotTouchHardware()
    {
        var fw = new Firmware(); using var driver = Controller(fw);
        Directory.CreateDirectory(Journal + ".tmp");
        driver.SetPercent(Guid.NewGuid(), Id(1), 60).Should().BeFalse();
        fw.Writes.Should().BeEmpty();
    }

    [Fact]
    public void ReadbackFailureRollsBackAndReportsFailure()
    {
        var fw = new Firmware { FailReadbackOnce = true };
        var before = fw.States[1].ToArray();
        using var driver = Controller(fw);
        driver.SetPercent(Guid.NewGuid(), Id(1), 60).Should().BeFalse();
        fw.States[1].Should().Equal(before);
        driver.LastError.Should().Contain("Cannot read");
    }

    [Fact]
    public void LeaseExpiryRestoresOnlyExpiredOwner()
    {
        var fw = new Firmware(); using var driver = Controller(fw);
        var before = fw.States[1].ToArray();
        driver.SetPercent(Guid.NewGuid(), Id(1), 60).Should().BeTrue();
        driver.ExpireLeases(DateTimeOffset.UtcNow.AddSeconds(16));
        fw.States[1].Should().Equal(before);
        driver.Snapshot(Guid.NewGuid()).Channels.Single(c => c.Index == 1).TargetPercent.Should().BeNull();
    }

    [Fact]
    public void SessionsCannotOverwriteOrReleaseEachOthersZone()
    {
        var fw = new Firmware(); using var driver = Controller(fw);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        driver.SetPercent(first, Id(1), 60).Should().BeTrue();
        driver.SetPercent(second, Id(1), 80).Should().BeFalse();
        driver.SetDefault(second, Id(1)).Should().BeFalse();
        driver.Snapshot(first).Channels.Single(c => c.Index == 1).TargetPercent.Should().Be(60);
        driver.RestoreSession(first);
        driver.Snapshot(second).Channels.Single(c => c.Index == 1).TargetPercent.Should().BeNull();
    }

    [Fact]
    public void StartupRecoversJournalLeftByKilledHelper()
    {
        var fw = new Firmware(); var before = fw.States[1].ToArray();
        string journal; byte[] pinned;
        using (var driver = Controller(fw))
        {
            driver.SetPercent(Guid.NewGuid(), Id(1), 60).Should().BeTrue();
            journal = File.ReadAllText(Journal); pinned = fw.States[1].ToArray();
        }
        File.WriteAllText(Journal, journal); fw.States[1] = pinned;
        using var restarted = Controller(fw);
        restarted.Available.Should().BeTrue(restarted.LastError);
        fw.States[1].Should().Equal(before);
        File.Exists(Journal).Should().BeFalse();
    }

    [Fact]
    public void RecoveryRejectsDifferentMachineWithoutHardwareWrites()
    {
        var fw = new Firmware(); string journal;
        using (var driver = Controller(fw))
        { driver.SetPercent(Guid.NewGuid(), Id(1), 60); journal = File.ReadAllText(Journal); }
        File.WriteAllText(Journal, journal); fw.Writes.Clear();
        using var other = Controller(fw, "different-machine");
        other.Available.Should().BeFalse(); fw.Writes.Should().BeEmpty();
    }

    private sealed class NoFans : IFanActuator
    {
        public bool SetPercent(string id, int percent) => false;
        public bool SetDefault(string id) => false;
    }

    [Fact]
    public async Task HelperClientDiscoversWritesAndRestoresOnDisconnect()
    {
        var fw = new Firmware(); using var driver = Controller(fw);
        var before = fw.States[1].ToArray();
        await using var server = new HelperServer(new NoFans(), Path.Combine(_root, "helper.sock"), bmc: driver);
        server.Start();
        using (var client = new BmcHelperClient(server.SocketPath))
        {
            var items = client.Discover();
            items.Should().HaveCount(14);
            items.Where(i => i.Kind == OpenFan.Core.Hardware.HardwareKind.Control).Should().OnlyContain(i => i.MinPercent == 0);
            client.SetPercent(Id(1), 0).Should().BeTrue(client.LastError);
            var readings = new Dictionary<string, double?>(); client.ReadInto(readings);
            readings[Id(1)].Should().Be(0);
            client.TakeReapplyRequest().Should().BeFalse();
            driver.ExpireLeases(DateTimeOffset.UtcNow.AddSeconds(16));
            client.ReadInto(readings);
            client.TakeReapplyRequest().Should().BeTrue("the control loop must re-arm after lease expiry/helper restart");
            client.SetPercent(Id(1), 65).Should().BeTrue();
        }
        await server.DisposeAsync();
        fw.States[1].Should().Equal(before);
    }

    [Fact]
    public void RestoreFailureKeepsJournalForRetry()
    {
        var fw = new Firmware(); using var driver = Controller(fw);
        var owner = Guid.NewGuid(); driver.SetPercent(owner, Id(1), 60).Should().BeTrue();
        fw.FailWrite = true;
        driver.SetDefault(owner, Id(1)).Should().BeFalse();
        File.Exists(Journal).Should().BeTrue();
        fw.FailWrite = false;
        driver.SetDefault(owner, Id(1)).Should().BeTrue();
        File.Exists(Journal).Should().BeFalse();
    }
}
