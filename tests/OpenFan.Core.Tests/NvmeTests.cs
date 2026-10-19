using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenFan.Core.Config;
using OpenFan.Core.ControlLoop;
using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public sealed class NvmeTests
{
    private static readonly NvmeDevice Drive = new("nvme0", "0000:a9:00.0", "Test SSD", "1.0", new string('a', 64), null, "/sys/class/nvme/nvme0");
    private static byte[] Log()
    {
        var bytes = new byte[512]; bytes[3] = 100; bytes[4] = 10; bytes[5] = 101;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), 320);
        return bytes;
    }
    [Fact]
    public void Parses128BitCountersDecimalUnitsAndZeroWithoutInventingHealth()
    {
        var log = Log();
        new BigInteger(1_000_000).TryWriteBytes(log.AsSpan(32, 16), out _, isUnsigned: true);
        (BigInteger.One << 80).TryWriteBytes(log.AsSpan(48, 16), out _, isUnsigned: true);
        BinaryPrimitives.WriteUInt32LittleEndian(log.AsSpan(196), 5);
        var parsed = NvmeHealthBackend.Parse(log);
        Assert.Equal(0.512, parsed["read_tb"]); Assert.Equal(Math.Pow(2, 80) * 512000 / 1e12, parsed["written_tb"]);
        Assert.Equal(101, parsed["used"]); Assert.Equal(0, parsed["media_errors"]); Assert.Equal(5, parsed["critical_minutes"]);
        Assert.Throws<IOException>(() => NvmeHealthBackend.Parse(new byte[512]));
        Assert.Throws<IOException>(() => NvmeHealthBackend.Parse(new byte[511]));
        Assert.Equal(72, Marshal.SizeOf<NativeNvmeSmartReader.Command>());
        Assert.Equal(40, Marshal.OffsetOf<NativeNvmeSmartReader.Command>(nameof(NativeNvmeSmartReader.Command.Cdw10)).ToInt32());
    }

    [Fact]
    public async Task HealthReadsAreIndependentThrottledAndPausedAndFailuresClearValues()
    {
        var time = TimeSpan.Zero; var reader = new FakeSmart();
        var backend = new NvmeHealthBackend(reader, () => [Drive], () => time);
        try
        {
            var items = backend.Discover(); var id = Drive.MetricId("spare"); var readings = new Dictionary<string, double?>();
            reader.Hold = true;
            backend.ReadInto(readings, new HashSet<string> { id }); // Must return while the reader remains blocked.
            Assert.Null(readings[id]); Assert.True(reader.Started.Wait(1000));
            Assert.Equal(1, reader.Calls); Assert.All(items, i => Assert.False(i.CanSet));
            backend.ReadInto(readings, new HashSet<string>()); Assert.Equal(1, reader.Calls);
            reader.Gate.Set();
            for (var i = 0; i < 100 && readings[id] is null; i++) { await Task.Delay(10); backend.ReadInto(readings, new HashSet<string> { id }); }
            Assert.Equal(100, readings[id]);
            time = TimeSpan.FromSeconds(29); backend.ReadInto(readings); Assert.Equal(1, reader.Calls);
            time = TimeSpan.FromSeconds(60); backend.ReadInto(readings, new HashSet<string>()); Assert.Equal(1, reader.Calls);
            reader.Fail = true; backend.ReadInto(readings, new HashSet<string> { id });
            for (var i = 0; i < 100 && !backend.Status(Drive.Group).Contains("simulated failure"); i++) { await Task.Delay(10); backend.ReadInto(readings, new HashSet<string> { id }); }
            Assert.Equal(2, reader.Calls); Assert.Null(readings[id]); Assert.Contains("simulated failure", backend.Status(Drive.Group));
            var settings = new AppSettings { HiddenSensorGroups = ["Drive health (NVMe)"] };
            Assert.Empty(SensorPollingPlan.Create(items, settings));
            settings.HiddenSensorGroups.Clear(); settings.CollapsedSensorDevices.Add(SensorPollingPlan.CollapseKey(items[0]));
            Assert.Empty(SensorPollingPlan.Create(items, settings));
        }
        finally { reader.Gate.Set(); backend.Dispose(); }
    }

    [Fact]
    public async Task DedicatedReadOnlyProtocolValidatesIdentityAndHasNoFanOwnership()
    {
        using var storage = new FakeSmart(); var actuator = new NoActuator(); var session = new HelperSession(actuator, storage: storage);
        Assert.StartsWith("err", session.HandleLine("nvmesmart ../../etc/shadow " + Drive.Identity));
        Assert.StartsWith("err", session.HandleLine("nvmesmart nvme0 bad")); Assert.Equal(0, storage.Calls);
        using var native = new NativeNvmeSmartReader();
        Assert.Throws<IOException>(() => native.Read("../../x", Drive.Identity));
        var path = Path.Combine(Path.GetTempPath(), "of-nvme-" + Guid.NewGuid().ToString("N") + ".sock");
        await using var server = new HelperServer(actuator, path, storage: storage); server.Start();
        using var client = new NvmeSmartClient(path);
        var bytes = await Task.Run(() => client.Read(Drive.Controller, Drive.Identity));
        Assert.Equal(Log(), bytes); Assert.Equal(1, storage.Calls); Assert.Empty(session.Owned); Assert.Equal(0, actuator.Calls);
    }

    [Fact]
    public void TemperatureNamesExplainCompositeAndUnmappedProbesWithoutChangingIdsOrCustomLabels()
    {
        var channel = new LibSensorChannel("nvme", "/not-present/hwmon4", "temp1", "temp1_input", "Composite", 2, "°C");
        var item = LibSensorsBackend.Describe(channel)!; var renamed = NvmeDevices.Describe(item, channel.ChipPath);
        Assert.Equal(item.Id, renamed.Id); Assert.Equal("Drive temperature (composite)", renamed.Name);
        var internalChannel = channel with { Feature = "temp2", Subfeature = "temp2_input", Label = "Sensor 1" };
        var probe = NvmeDevices.Describe(LibSensorsBackend.Describe(internalChannel)!, internalChannel.ChipPath);
        Assert.Equal("Internal temperature 1 (unmapped)", probe.Name); Assert.Contains("not verified", probe.Details);
        var custom = internalChannel with { Label = "Verified custom name" };
        Assert.Equal(custom.Label, NvmeDevices.Describe(LibSensorsBackend.Describe(custom)!, custom.ChipPath).Name);
        Assert.Equal(HardwareKind.TemperatureLimit, LibSensorsBackend.Describe(channel with { Subfeature = "temp1_crit" })!.Kind);
        Assert.Null(LibSensorsBackend.Describe(internalChannel with { Subfeature = "temp2_max" })); // 65,261°C sentinel isn't a useful threshold.
        Assert.Null(LibSensorsBackend.Describe(channel with { Subfeature = "temp1_min" })); // Kelvin-zero sentinel.
    }

    [Fact]
    public void CatalogFindsDirectHwmonAndStableIdentitiesAcrossKernelRenumberingButNotReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "of-nvme-meta-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "nvme0"); Directory.CreateDirectory(Path.Combine(path, "hwmon4"));
        try
        {
            File.WriteAllText(Path.Combine(path, "address"), "0000:a9:00.0\n");
            File.WriteAllText(Path.Combine(path, "model"), "Test SSD  \n"); File.WriteAllText(Path.Combine(path, "serial"), "hidden serial\n");
            File.WriteAllText(Path.Combine(path, "firmware_rev"), "1.0\n");
            var first = Assert.Single(NvmeDevices.Discover(root)); Assert.Equal("hwmon4", Path.GetFileName(first.HwmonPath));
            Directory.Move(path, Path.Combine(root, "nvme7")); path = Path.Combine(root, "nvme7");
            Directory.Move(Path.Combine(path, "hwmon4"), Path.Combine(path, "hwmon12"));
            var rebooted = Assert.Single(NvmeDevices.Discover(root)); Assert.Equal(first.Identity, rebooted.Identity); Assert.Equal(first.Group, rebooted.Group);
            Assert.DoesNotContain("hidden serial", rebooted.Description); Assert.Equal("nvme7", rebooted.Controller);
            File.WriteAllText(Path.Combine(path, "serial"), "replacement");
            Assert.NotEqual(first.Identity, Assert.Single(NvmeDevices.Discover(root)).Identity);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ActivityHandlesUnitsConcurrentIoFirstSampleResetAndInvalidData()
    {
        var c = NvmeActivityBackend.Counters.Parse("1 0 2000 200 2 0 4000 100 0 150 0")!.Value;
        var first = NvmeActivityBackend.Values(c, null, 0); Assert.Null(first["read_rate"]); Assert.Equal(.001024, first["read_total"]);
        var next = new NvmeActivityBackend.Counters(4000, 8000, 4200, 1100, 1150);
        var values = NvmeActivityBackend.Values(next, c, 2);
        Assert.Equal(.512, values["read_rate"]); Assert.Equal(1.024, values["write_rate"]);
        Assert.Equal(200, values["read_activity"]); Assert.Equal(50, values["write_activity"]); Assert.Equal(50, values["total_activity"]);
        Assert.Null(NvmeActivityBackend.Values(c, next, 2)["read_rate"]); Assert.Null(NvmeActivityBackend.Counters.Parse("invalid"));
    }

    [Fact]
    public void LargeCountersFitTheTableAndMissingValuesStayMissing()
    {
        Assert.Equal("149.073M", SensorCountFormat.Format(149_072_884)); Assert.Equal("4.231B", SensorCountFormat.Format(4_231_416_309));
        Assert.Equal("1T", SensorCountFormat.Format(1e12)); Assert.Equal("0", SensorCountFormat.Format(0));
        Assert.Equal("—", SensorCountFormat.Format(null)); Assert.Equal("—", SensorCountFormat.Format(double.NaN));
        Assert.Equal("No critical warnings", NvmeHealthBackend.Warnings(0));
        Assert.Equal("Warning: spare capacity low, temperature warning", NvmeHealthBackend.Warnings(3));
        Assert.Contains("0x80", NvmeHealthBackend.Warnings(128));
    }

    [Fact]
    public void ActivityPauseDoesNotReadMissingFilesOrAverageAcrossHiddenIntervals()
    {
        var root = Path.Combine(Path.GetTempPath(), "of-io-" + Guid.NewGuid().ToString("N")); var ns = Path.Combine(root, "nvme0n1");
        Directory.CreateDirectory(ns); var path = Path.Combine(ns, "stat"); var time = TimeSpan.Zero;
        try
        {
            File.WriteAllText(path, "1 0 1000 100 1 0 2000 200 0 250 0");
            using var backend = new NvmeActivityBackend(() => [Drive], root, () => time); var items = backend.Discover();
            Assert.Equal(7, items.Count); var values = new Dictionary<string, double?>(); var id = NvmeActivityBackend.Id(Drive, "read_rate");
            backend.ReadInto(values); Assert.Null(values[id]);
            time = TimeSpan.FromSeconds(1); File.WriteAllText(path, "1 0 3000 200 1 0 4000 300 0 400 0"); backend.ReadInto(values); Assert.Equal(1.024, values[id]);
            File.Delete(path); backend.ReadInto(values, new HashSet<string>()); Assert.Equal(1.024, values[id]);
            File.WriteAllText(path, "1 0 9000 300 1 0 10000 400 0 500 0"); time = TimeSpan.FromSeconds(10);
            backend.ReadInto(values); Assert.Null(values[id]);
            File.Delete(path); backend.ReadInto(values); Assert.Null(values[NvmeActivityBackend.Id(Drive, "read_total")]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NamespaceChangesResetRatesAndPartitionsAreNeverDoubleCounted()
    {
        var root = Path.Combine(Path.GetTempPath(), "of-io-ns-" + Guid.NewGuid().ToString("N")); var time = TimeSpan.Zero;
        void Write(string name, ulong sectors)
        {
            var path = Path.Combine(root, name); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "stat"), $"1 0 {sectors} 100 1 0 2000 100 0 200 0");
        }
        try
        {
            Write("nvme0n1", 1000); Write("nvme0n1p1", 999999);
            using var backend = new NvmeActivityBackend(() => [Drive], root, () => time); backend.Discover();
            var values = new Dictionary<string, double?>(); backend.ReadInto(values);
            Assert.Equal(.000512, values[NvmeActivityBackend.Id(Drive, "read_total")]);
            time = TimeSpan.FromSeconds(1); Write("nvme0n1", 3000); backend.ReadInto(values);
            Assert.Equal(1.024, values[NvmeActivityBackend.Id(Drive, "read_rate")]);
            Write("nvme0n2", 1000000); backend.Discover(); time = TimeSpan.FromSeconds(2); backend.ReadInto(values);
            Assert.Null(values[NvmeActivityBackend.Id(Drive, "read_rate")]);
            Assert.Equal(1003000 * 512d / 1e9, values[NvmeActivityBackend.Id(Drive, "read_total")]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeSmart : INvmeSmartReader
    {
        public int Calls; public bool Hold, Fail;
        public ManualResetEventSlim Started = new(), Gate = new();
        public byte[] Read(string controller, string identity)
        { Interlocked.Increment(ref Calls); Started.Set(); if (Hold) Gate.Wait(); if (Fail) throw new IOException("simulated failure"); return Log(); }
        public void Dispose() { Gate.Set(); }
    }
    private sealed class NoActuator : IFanActuator
    {
        public int Calls;
        public bool SetPercent(string id, int percent) { Calls++; return true; }
        public bool SetDefault(string id) { Calls++; return true; }
    }
}
