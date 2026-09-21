using OpenFan.Core.Hardware;

namespace OpenFan.Core.Tests;

public class InventoryMergeTests
{
    [Fact]
    public void Nvml_gpu_hides_matching_hwmon_gpu_items()
    {
        var hwmon = new[]
        {
            // The kernel "nvidia" hwmon node exposes GPU temp; NVML owns the fan, so
            // items in an NVML-covered group are dropped (spec §4.4 merge rule).
            new HardwareItem { Id = "hwmon:nvidia:3:temp:1", Kind = HardwareKind.Temperature, Name = "GPU Core Temp", Backend = "hwmon", Group = "NVIDIA GeForce RTX 5060 Ti" },
            // Board fan (nct6799 on TRX50) is untouched by the merge.
            new HardwareItem { Id = "hwmon:nct6799:10:pwm:1", Kind = HardwareKind.Control, Name = "Rear Fan", Backend = "hwmon", Group = "NCT6799D" },
        };
        var nvml = new[]
        {
            new HardwareItem { Id = "nvml:GPU-aaa:fan:0", Kind = HardwareKind.Control, Name = "RTX 5060 Ti 01:00.0 Fan 0", Backend = "nvml", Group = "NVIDIA GeForce RTX 5060 Ti" },
        };

        var merged = InventoryMerger.Merge(hwmon, nvml);
        merged.Should().Contain(i => i.Id == "nvml:GPU-aaa:fan:0");
        merged.Should().Contain(i => i.Id == "hwmon:nct6799:10:pwm:1");
        merged.Should().NotContain(i => i.Id == "hwmon:nvidia:3:temp:1");
    }

    [Fact]
    public void Hwmon_gpu_items_survive_when_nvml_group_absent()
    {
        var hwmon = new[]
        {
            new HardwareItem { Id = "hwmon:nvidia:3:temp:1", Kind = HardwareKind.Temperature, Name = "GPU Core Temp", Backend = "hwmon", Group = "NVIDIA RTX PRO 6000" },
        };
        InventoryMerger.Merge(hwmon, [])
            .Select(i => i.Id).Should().Equal("hwmon:nvidia:3:temp:1");
    }

    [Fact]
    public void Per_fan_nvml_ids_stay_distinct()
    {
        var nvml = new[]
        {
            new HardwareItem { Id = "nvml:GPU-1:fan:0", Kind = HardwareKind.Control, Name = "Fan 0", Backend = "nvml", Group = "RTX PRO 6000" },
            new HardwareItem { Id = "nvml:GPU-1:fan:1", Kind = HardwareKind.Control, Name = "Fan 1", Backend = "nvml", Group = "RTX PRO 6000" },
        };
        InventoryMerger.Merge([], nvml).Select(i => i.Id).Should().Equal(
            "nvml:GPU-1:fan:0",
            "nvml:GPU-1:fan:1");
    }

    [Fact]
    public void Nvme_hwmon_chips_are_not_mistaken_for_gpu()
    {
        var hwmon = new[]
        {
            new HardwareItem { Id = "hwmon:nvme:0:temp:1", Kind = HardwareKind.Temperature, Name = "Composite", Backend = "hwmon", Group = "nvme0" },
        };
        var nvml = new[]
        {
            new HardwareItem { Id = "nvml:GPU-aaa:fan:0", Kind = HardwareKind.Control, Name = "Fan 0", Backend = "nvml", Group = "NVIDIA GeForce RTX 5060 Ti" },
        };
        InventoryMerger.Merge(hwmon, nvml)
            .Should().Contain(i => i.Id == "hwmon:nvme:0:temp:1");
    }
}
