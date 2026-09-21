using OpenFan.Core.Hardware;

namespace OpenFan.Core.Tests;

public class GpuFormatTests
{
    [Fact]
    public void Pci_bus_is_compacted_and_appended_to_the_gpu_label()
    {
        GpuFormat.CompactPciBus("00000000:2D:00.0").Should().Be("2D:00.0");
        GpuFormat.CompactPciBus("0000:01:00.0").Should().Be("01:00.0");
        GpuFormat.CompactPciBus("").Should().Be("");
        GpuFormat.GpuLabel("NVIDIA RTX PRO 6000 Blackwell", "00000000:2D:00.0")
            .Should().Be("RTX PRO 6000 Blackwell 2D:00.0");
        GpuFormat.ShouldAdoptInventoryName("RTX PRO 6000 Fan 0", "RTX PRO 6000 2D:00.0 Fan 0")
            .Should().BeTrue();
        GpuFormat.ShouldAdoptInventoryName("Slot 1", "RTX PRO 6000 2D:00.0 Fan 0")
            .Should().BeFalse();
    }

    [Fact]
    public void JoinFans_uses_middle_dot()
    {
        GpuFormat.JoinFans([30, 30], "%").Should().Be("30·30%");
        GpuFormat.JoinFans([0], "%").Should().Be("0%");
    }

    [Fact]
    public void Bytes_and_throughput()
    {
        GpuFormat.Bytes(0).Should().Be("0 B");
        GpuFormat.Bytes(1024).Should().Be("1 KiB");
        GpuFormat.Throughput(1024).Should().Be("1 MiB/s");
    }

    [Fact]
    public void Parse_and_clamp_watts()
    {
        GpuFormat.TryParseWatts("200", out var w).Should().BeTrue();
        w.Should().Be(200);
        GpuFormat.TryParseWatts("nope", out _).Should().BeFalse();
        GpuFormat.ClampWatts(50, 100, 600).Should().Be(100);
        GpuFormat.ClampWatts(900, 100, 600).Should().Be(600);
    }
}
