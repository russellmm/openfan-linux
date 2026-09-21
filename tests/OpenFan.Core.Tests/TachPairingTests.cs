using OpenFan.Core.Hardware;

namespace OpenFan.Core.Tests;

public class TachPairingTests
{
    [Fact]
    public void Suggests_nvml_tach_for_matching_fan_index()
    {
        var items = new[]
        {
            new HardwareItem { Id = "nvml:GPU-1:fan:1", Kind = HardwareKind.Control, Name = "Fan 1", Backend = "nvml", Group = "PRO 6000" },
            new HardwareItem { Id = "nvml:GPU-1:tach:0", Kind = HardwareKind.Tach, Name = "Fan 0 RPM", Backend = "nvml", Group = "PRO 6000" },
            new HardwareItem { Id = "nvml:GPU-1:tach:1", Kind = HardwareKind.Tach, Name = "Fan 1 RPM", Backend = "nvml", Group = "PRO 6000" },
        };
        TachPairing.Suggest("nvml:GPU-1:fan:1", items).Should().Be("nvml:GPU-1:tach:1");
    }

    [Fact]
    public void Suggests_lone_tach_in_same_lhm_group()
    {
        var items = new[]
        {
            new HardwareItem { Id = "lhm-ctl:0", Kind = HardwareKind.Control, Name = "Rear", Backend = "hwmon", Group = "Nuvoton" },
            new HardwareItem { Id = "hwmon:/lpc/fan/0", Kind = HardwareKind.Tach, Name = "Fan #1", Backend = "hwmon", Group = "Nuvoton" },
            new HardwareItem { Id = "hwmon:/lpc/other/fan/0", Kind = HardwareKind.Tach, Name = "Other", Backend = "hwmon", Group = "SomethingElse" },
        };
        TachPairing.Suggest("lhm-ctl:0", items).Should().Be("hwmon:/lpc/fan/0");
    }

    [Fact]
    public void Returns_null_when_no_tach_exists()
    {
        var items = new[]
        {
            new HardwareItem { Id = "lhm-ctl:0", Kind = HardwareKind.Control, Name = "Rear", Backend = "hwmon", Group = "Nuvoton" },
        };
        TachPairing.Suggest("lhm-ctl:0", items).Should().BeNull();
    }
}
