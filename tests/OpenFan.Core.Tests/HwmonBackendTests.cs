using OpenFan.Core.Config;
using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

/// <summary>
/// TDD against a fixture sysfs tree shaped like the real TRX50 box:
/// nct6799 SuperIO (enable=5 auto), k10temp, nvme, asusec — spec §9.
/// </summary>
public sealed class HwmonBackendTests : IDisposable
{
    private const string NctPwm1 = "hwmon:nct6799:10:pwm:1";
    private readonly string _root;

    public HwmonBackendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "openfan-hwmon-fixture", Guid.NewGuid().ToString("N"));

        // nvme composite temp, no label
        Write("hwmon0/name", "nvme\n");
        Write("hwmon0/temp1_input", "45000\n");

        // k10temp with Tctl label (matches this kernel)
        Write("hwmon5/name", "k10temp\n");
        Write("hwmon5/temp1_input", "62500\n");
        Write("hwmon5/temp1_label", "Tctl\n");

        // ASUS EC: tach + temps, no pwm (read-only view)
        Write("hwmon7/name", "asusec\n");
        Write("hwmon7/fan1_input", "3271\n");
        Write("hwmon7/fan1_label", "CPU_Opt\n");

        // NCT6796D-S as bound by nct6775: 7-pwm style node, auto mode 5, decoy knob files
        Write("hwmon10/name", "nct6799\n");
        Write("hwmon10/pwm1", "102\n");
        Write("hwmon10/pwm1_enable", "5\n");
        Write("hwmon10/pwm1_auto_point1_pwm", "51\n");
        Write("hwmon10/pwm1_mode", "1\n");
        Write("hwmon10/fan1_input", "1580\n");
        Write("hwmon10/temp1_input", "33000\n");
        Write("hwmon10/temp1_label", "SYSTIN\n");
        Write("hwmon10/temp6_input", "-9000\n"); // floating AUXTIN — implausible
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative)).Trim();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Discovers_items_with_spec_stable_ids()
    {
        var items = new HwmonBackend(_root).Discover();

        items.Should().Contain(i => i.Id == NctPwm1 && i.Kind == HardwareKind.Control && i.CanSet);
        items.Should().Contain(i => i.Id == "hwmon:k10temp:5:temp:1" && i.Kind == HardwareKind.Temperature);
        items.Should().Contain(i => i.Id == "hwmon:nct6799:10:fan:1" && i.Kind == HardwareKind.Tach);
        items.Should().Contain(i => i.Id == "hwmon:nvme:0:temp:1");

        // Decoy files (pwm1_enable, pwm1_auto_point1_pwm, pwm1_mode) are NOT controls.
        items.Count(i => i.Kind == HardwareKind.Control).Should().Be(1);
    }

    [Fact]
    public void Labels_prefer_sysfs_with_lhm_style_fallback()
    {
        var items = new HwmonBackend(_root).Discover();

        items.Single(i => i.Id == "hwmon:k10temp:5:temp:1").Name.Should().Be("Tctl");
        items.Single(i => i.Id == "hwmon:nct6799:10:fan:1").Name.Should().Be("Fan 1"); // no fan label on this chip
        items.Single(i => i.Id == "hwmon:nvme:0:temp:1").Name.Should().Be("Temperature #1");
    }

    [Fact]
    public void ReadInto_converts_units_and_nulls_implausible_temps()
    {
        var backend = new HwmonBackend(_root);
        backend.Discover();
        var readings = new Dictionary<string, double?>();
        backend.ReadInto(readings);

        readings["hwmon:k10temp:5:temp:1"].Should().Be(62.5);   // milli-°C → °C
        readings["hwmon:nct6799:10:fan:1"].Should().Be(1580);   // RPM as-is
        readings["hwmon:nct6799:10:pwm:1"].Should().BeApproximately(40.0, 0.1); // duty 102 ≈ 40 %
        readings["hwmon:nct6799:10:temp:6"].Should().BeNull();  // floating pin → missing
    }

    [Fact]
    public void SetPercent_writes_manual_enable_then_duty()
    {
        var backend = new HwmonBackend(_root);
        backend.Discover();

        backend.SetPercent(NctPwm1, 60).Should().BeTrue();

        Read("hwmon10/pwm1_enable").Should().Be("1");           // manual
        Read("hwmon10/pwm1").Should().Be("153");                // round(60 * 255 / 100)
    }

    [Fact]
    public void SetDefault_restores_cached_auto_mode_not_hardcoded_two()
    {
        var backend = new HwmonBackend(_root);
        backend.Discover();                                      // caches enable=5 (NCT6796D auto)

        backend.SetPercent(NctPwm1, 40);
        Read("hwmon10/pwm1_enable").Should().Be("1");

        backend.SetDefault(NctPwm1).Should().BeTrue();
        Read("hwmon10/pwm1_enable").Should().Be("5");            // the pre-takeover mode
    }

    [Fact]
    public void SetPercent_clamps_percent_and_rejects_bad_ids()
    {
        var backend = new HwmonBackend(_root);
        backend.Discover();

        backend.SetPercent(NctPwm1, 150);
        Read("hwmon10/pwm1").Should().Be("255");

        backend.SetPercent("nvml:GPU-1:fan:0", 50).Should().BeFalse();
        // Stale index guard: chip name in the id must still match the directory.
        backend.SetPercent("hwmon:nct6701:10:pwm:1", 50).Should().BeFalse();
    }

    [Fact]
    public void Disabled_chips_are_excluded_from_inventory()
    {
        var sources = new SourceSettings { DisabledHwmonChips = ["nvme"] };
        var items = new HwmonBackend(_root, sources).Discover();

        items.Should().NotContain(i => i.Backend == "hwmon" && i.Group.StartsWith("nvme", StringComparison.Ordinal));
        items.Should().Contain(i => i.Id == NctPwm1);
    }

    [Fact]
    public void Hwmon_source_off_yields_nothing()
    {
        var sources = new SourceSettings { Hwmon = false };
        new HwmonBackend(_root, sources).Discover().Should().BeEmpty();
    }
}
