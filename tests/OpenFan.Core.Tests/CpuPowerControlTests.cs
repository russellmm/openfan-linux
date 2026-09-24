using System.Buffers.Binary;
using FluentAssertions;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

/// <summary>Fake sysfs chip, CBS variable and boot config: no root, no real SMU.</summary>
internal static class FakeSysfs
{
    public static string TempRoot() => Path.Combine(Path.GetTempPath(), $"openfan-cpupwr-{Guid.NewGuid():N}");

    public static (string Root, string CapFile) Hwmon(long capMicrowatts = 295_000_000, long maxMicrowatts = 2_000_000_000)
    {
        var root = TempRoot();
        var chip = Directory.CreateDirectory(Path.Combine(root, "hwmon7")).FullName;
        File.WriteAllText(Path.Combine(chip, "name"), "amd_hsmp_hwmon\n");
        var cap = Path.Combine(chip, "power1_cap");
        File.WriteAllText(cap, capMicrowatts + "\n");
        File.WriteAllText(Path.Combine(chip, "power1_cap_max"), maxMicrowatts + "\n");
        return (root, cap);
    }

    public static string CbsBlob(int pptMw = 295_000)
    {
        var path = Path.Combine(Path.GetTempPath(), $"openfan-cbs-{Guid.NewGuid():N}");
        var data = new byte[2438];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 0xE5AF127C);
        data[1048] = 1;                                        // PPT control: manual
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1049), (uint)pptMw);
        var file = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0), 0x07);
        data.CopyTo(file, 4);
        File.WriteAllBytes(path, file);
        return path;
    }
}

public sealed class CpuPowerControlTests
{
    [Fact]
    public void WritesWholeWattsAsMicrowattsAndVerifiesReadback()
    {
        var (root, cap) = FakeSysfs.Hwmon();
        try
        {
            var ctl = new CpuPowerControl(root);
            ctl.SetLiveLimitWatts(250).Should().BeTrue(ctl.LastError ?? "ok");
            long.Parse(File.ReadAllText(cap).Trim()).Should().Be(250_000_000);
            ctl.LiveLimitWatts().Should().Be(250);
            ctl.LastError.Should().BeNull();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(99)]
    [InlineData(301)]
    [InlineData(2000)]           // firmware would accept it; the product window does not
    public void RefusesOutsideTheAgreedWindowWithoutWriting(int watts)
    {
        var (root, cap) = FakeSysfs.Hwmon();
        try
        {
            var ctl = new CpuPowerControl(root);
            ctl.SetLiveLimitWatts(watts).Should().BeFalse();
            ctl.LastError.Should().Contain("100-300");
            long.Parse(File.ReadAllText(cap).Trim()).Should().Be(295_000_000, "a refused request must not touch the limit");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RefusesAboveFirmwareMaximumAndReportsMissingChip()
    {
        var (root, cap) = FakeSysfs.Hwmon(maxMicrowatts: 220_000_000);
        try
        {
            var ctl = new CpuPowerControl(root);
            ctl.SetLiveLimitWatts(250).Should().BeFalse();
            ctl.LastError.Should().Contain("firmware maximum");
            long.Parse(File.ReadAllText(cap).Trim()).Should().Be(295_000_000);
        }
        finally { Directory.Delete(root, recursive: true); }

        var absent = new CpuPowerControl(FakeSysfs.TempRoot());      // never created
        absent.SetLiveLimitWatts(250).Should().BeFalse();
        absent.LastError.Should().Contain("amd_hsmp_hwmon");
    }

    [Fact]
    public void BootConfigIsWrittenInMilliwattsAndCanBeCleared()
    {
        var root = FakeSysfs.TempRoot();
        Directory.CreateDirectory(root);
        var cfg = Path.Combine(root, "ppt_mw");
        try
        {
            var ctl = new CpuPowerControl(FakeSysfs.TempRoot(), cfg);
            ctl.SetBootLimitWatts(275).Should().BeTrue(ctl.LastError ?? "ok");
            File.ReadAllText(cfg).Trim().Should().Be("275000");     // hsmp-control reads mW

            ctl.SetBootLimitWatts(null).Should().BeTrue();
            File.Exists(cfg).Should().BeFalse();

            ctl.SetBootLimitWatts(400).Should().BeFalse();          // same window as the live write
            ctl.LastError.Should().Contain("100-300");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RestoreDefaultUsesTheFlashValueEvenOutsideTheWindow()
    {
        var (root, cap) = FakeSysfs.Hwmon();
        var cbsPath = FakeSysfs.CbsBlob(pptMw: 350_000);            // stock default above our window
        try
        {
            var ctl = new CpuPowerControl(root, Path.Combine(FakeSysfs.TempRoot(), "ppt_mw"), new CbsSetupReader(cbsPath));
            ctl.BiosDefaultMilliwatts().Should().Be(350_000);
            ctl.SetLiveLimitWatts(350).Should().BeFalse();          // window still guards user input
            ctl.RestoreBiosDefault().Should().BeTrue(ctl.LastError ?? "ok");
            long.Parse(File.ReadAllText(cap).Trim()).Should().Be(350_000_000);
        }
        finally { Directory.Delete(root, recursive: true); File.Delete(cbsPath); }
    }

    [Fact]
    public void RestoreDefaultFailsHonestlyWhenFlashValueIsUnknown()
    {
        var (root, _) = FakeSysfs.Hwmon();
        try
        {
            var ctl = new CpuPowerControl(root, Path.Combine(FakeSysfs.TempRoot(), "ppt_mw"),
                new CbsSetupReader(Path.Combine(FakeSysfs.TempRoot(), "absent")));
            ctl.RestoreBiosDefault().Should().BeFalse();
            ctl.LastError.Should().Contain("not readable");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Guards the unit bug that would have displayed 250 W as 250000 W.</summary>
    [Fact]
    public void LiveLimitIsReportedInWattsNotMicrowattsOrMilliwatts()
    {
        var (root, _) = FakeSysfs.Hwmon(capMicrowatts: 295_000_000);
        try { new CpuPowerControl(root).LiveLimitWatts().Should().Be(295); }
        finally { Directory.Delete(root, recursive: true); }
    }
}

/// <summary>
/// End-to-end over a real Unix socket: NvmlHelperClient → HelperServer → CpuPowerControl → fake
/// sysfs. This is the exact path the CPU tab's Apply button takes; per-class unit tests alone left
/// the client methods and the command wiring between them untested.
/// </summary>
public class CpuPowerSocketTests
{
    private static string TempSocketPath() =>
        Path.Combine(Path.GetTempPath(), $"openfan-cpusock-{Guid.NewGuid():N}.sock");

    [Fact]
    public async Task Client_set_and_boot_limit_reach_the_control()
    {
        var (root, cap) = FakeSysfs.Hwmon();
        var cfgDir = FakeSysfs.TempRoot();
        Directory.CreateDirectory(cfgDir);
        var cfg = Path.Combine(cfgDir, "ppt_mw");
        var server = new HelperServer(new RecordingActuator(), TempSocketPath(), null,
            new CpuPowerControl(root, cfg));
        server.Start();
        try
        {
            using var client = new NvmlHelperClient(server.SocketPath);

            client.TrySetCpuPowerLimit(250).Should().BeTrue(client.LastError ?? "ok");
            long.Parse(File.ReadAllText(cap).Trim()).Should().Be(250_000_000);

            client.TrySetCpuBootLimit(275).Should().BeTrue(client.LastError ?? "ok");
            File.ReadAllText(cfg).Trim().Should().Be("275000");

            client.TrySetCpuBootLimit(null).Should().BeTrue();
            File.Exists(cfg).Should().BeFalse();
        }
        finally
        {
            await server.DisposeAsync();
            Directory.Delete(root, recursive: true);
            Directory.Delete(cfgDir, recursive: true);
        }
    }

    [Fact]
    public async Task Client_sees_window_and_firmware_maximum_reasons()
    {
        var (root, cap) = FakeSysfs.Hwmon(maxMicrowatts: 220_000_000);
        var server = new HelperServer(new RecordingActuator(), TempSocketPath(), null,
            new CpuPowerControl(root, Path.Combine(FakeSysfs.TempRoot(), "ppt_mw")));
        server.Start();
        try
        {
            using var client = new NvmlHelperClient(server.SocketPath);

            client.TrySetCpuPowerLimit(999).Should().BeFalse();      // rejected before any write
            long.Parse(File.ReadAllText(cap).Trim()).Should().Be(295_000_000);

            client.TrySetCpuPowerLimit(250).Should().BeFalse();      // above firmware max
            client.LastError.Should().Contain("firmware maximum");
        }
        finally
        {
            await server.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Client_reports_a_helper_without_cpu_capability()
    {
        var server = new HelperServer(new RecordingActuator(), TempSocketPath());   // GPU-only helper
        server.Start();
        try
        {
            using var client = new NvmlHelperClient(server.SocketPath);
            client.TrySetCpuPowerLimit(250).Should().BeFalse();
            client.LastError.Should().Contain("cpu power control not available");
        }
        finally { await server.DisposeAsync(); }
    }

    /// <summary>Reproduces the stale-daemon failure the GUI actually hit: an older helper answers
    /// "err unknown command", which must reach the caller verbatim so the UI can name the cause.</summary>
    [Fact]
    public async Task Stale_helper_unknown_command_surfaces_to_the_client()
    {
        var server = new HelperServer(new RecordingActuator(), TempSocketPath());   // no cpu capability
        server.Start();
        try
        {
            using var client = new NvmlHelperClient(server.SocketPath);
            client.TrySetCpuPowerLimit(250).Should().BeFalse();
            client.LastError.Should().NotBeNullOrWhiteSpace();
        }
        finally { await server.DisposeAsync(); }
    }
}

/// <summary>Protocol-level checks with a fake writer: validation must happen before the writer is
/// touched, and the writer's reason must reach the client verbatim.</summary>
public sealed class CpuPowerProtocolTests
{
    private sealed record Call(string Op, int? Watts);

    private sealed class FakeCpu : ICpuPowerWriter
    {
        public List<Call> Calls { get; } = [];
        public bool Succeed { get; set; } = true;
        public string? LastError { get; set; }

        public bool SetLiveLimitWatts(int watts) { Calls.Add(new Call("live", watts)); return Succeed || Fail("firmware settled at 240 W instead of 250 W (clamped)"); }
        public bool RestoreBiosDefault() { Calls.Add(new Call("default", null)); return Succeed; }
        public bool SetBootLimitWatts(int? watts) { Calls.Add(new Call("boot", watts)); return Succeed || Fail("not permitted to write /etc/hsmp-control/ppt_mw"); }

        private bool Fail(string why) { LastError = why; return false; }
    }

    [Theory]
    [InlineData("cpupower 250", "live", 250)]
    [InlineData("cpupower 100", "live", 100)]
    [InlineData("cpuboot 275", "boot", 275)]
    public void AcceptedCommandsForwardValues(string line, string op, int watts)
    {
        var cpu = new FakeCpu();
        new HelperSession(new RecordingActuator(), null, cpu).HandleLine(line).Should().Be("ok");
        cpu.Calls.Should().Equal(new Call(op, watts));
    }

    [Theory]
    [InlineData("cpupower 99")]
    [InlineData("cpupower 301")]
    [InlineData("cpupower lots")]
    [InlineData("cpuboot 400")]
    public void OutOfWindowOrMalformedNeverReachesTheWriter(string line)
    {
        var cpu = new FakeCpu();
        new HelperSession(new RecordingActuator(), null, cpu).HandleLine(line).Should().StartWith("err");
        cpu.Calls.Should().BeEmpty();
    }

    [Fact]
    public void DefaultAndClearMapToTheirOwnOperations()
    {
        var cpu = new FakeCpu();
        var session = new HelperSession(new RecordingActuator(), null, cpu);
        session.HandleLine("cpupower default").Should().Be("ok");
        session.HandleLine("cpuboot clear").Should().Be("ok");
        cpu.Calls.Should().Equal(new Call("default", null), new Call("boot", null));
    }

    [Fact]
    public void WriterReasonsAreSurfacedToTheClient()
    {
        var cpu = new FakeCpu { Succeed = false };
        var session = new HelperSession(new RecordingActuator(), null, cpu);
        session.HandleLine("cpupower 250").Should().Contain("clamped");
        session.HandleLine("cpuboot 250").Should().Contain("/etc/hsmp-control/ppt_mw");
    }

    [Fact]
    public void CommandsFailClosedWhenTheCapabilityIsAbsent()
    {
        var session = new HelperSession(new RecordingActuator());     // no cpu writer wired
        session.HandleLine("cpupower 250").Should().StartWith("err");
        session.HandleLine("cpuboot 250").Should().StartWith("err");
    }
}

/// <summary>"Keep after reboot" is only honest if something will execute it at boot.</summary>
public sealed class CpuBootPersistenceDetectionTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"openfan-units-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void WiredOnlyWhenTheUnitExists()
    {
        var empty = TempDir();
        var installed = TempDir();
        try
        {
            new CpuPowerControl(unitSearchDirs: new[] { empty })
                .BootPersistenceWired().Should().BeFalse();

            File.WriteAllText(Path.Combine(installed, CpuPowerControl.BootUnitName), "[Unit]\n");
            new CpuPowerControl(unitSearchDirs: new[] { empty, installed })
                .BootPersistenceWired().Should().BeTrue();
        }
        finally { Directory.Delete(empty, true); Directory.Delete(installed, true); }
    }

    [Fact]
    public void MaskedUnitCountsAsAbsent()
    {
        var dir = TempDir();
        try
        {
            // systemctl mask == symlink to /dev/null; someone turned the re-assertion off.
            File.CreateSymbolicLink(Path.Combine(dir, CpuPowerControl.BootUnitName), "/dev/null");
            new CpuPowerControl(unitSearchDirs: new[] { dir })
                .BootPersistenceWired().Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RealSystemStateIsReadableWithoutRoot()
    {
        // Must not throw as an unprivileged user: the UI asks this on every apply.
        var _ = new CpuPowerControl().BootPersistenceWired();
    }
}

/// <summary>Regression: stopping the helper with a client still attached (systemctl stop while the
/// GUI is open) used to throw OperationCanceledException out of DisposeAsync and skip socket
/// cleanup. Shutdown must be clean, and it must restore what that session owned.</summary>
public sealed class HelperShutdownRaceTests
{
    private const string FanId = "nvml:GPU-abc-1234:fan:1";

    [Fact]
    public async Task Disposing_with_an_open_client_connection_shuts_down_cleanly()
    {
        var actuator = new RecordingActuator();
        var path = Path.Combine(Path.GetTempPath(), $"openfan-stop-{Guid.NewGuid():N}.sock");
        var server = new HelperServer(actuator, path);
        server.Start();

        using var client = new NvmlHelperClient(path);
        client.Ping().Should().BeTrue();
        client.SetPercent(FanId, 60).Should().BeTrue();

        await server.DisposeAsync();          // used to throw here

        File.Exists(path).Should().BeFalse("the socket file must be removed on shutdown");
        actuator.Restores.Should().Contain(FanId);
    }
}
