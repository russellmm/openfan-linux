using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public class NvmlFanWriterTests
{
    [Fact]
    public void Zero_positive_zero_switches_firmware_manual_firmware_without_manual_zero()
    {
        var calls = new List<string>();
        var writer = new NvmlFanWriter(
            (device, fan, speed) => { calls.Add($"manual:{device}:{fan}:{speed}"); return 0; },
            (device, fan) => { calls.Add($"auto:{device}:{fan}"); return 0; });

        writer.SetPercent(123, 1, 0, 0).Should().Be(0);
        writer.SetPercent(123, 1, 45, 0).Should().Be(0);
        writer.SetPercent(123, 1, 0, 0).Should().Be(0);
        calls.Should().Equal("auto:123:1", "manual:123:1:45", "auto:123:1");
    }

    [Fact]
    public void Nonzero_hardware_floor_keeps_zero_requests_manual()
    {
        var speeds = new List<uint>();
        var writer = new NvmlFanWriter((_, _, speed) => { speeds.Add(speed); return 0; },
            (_, _) => throw new Exception("PRO floor must not switch to automatic"));
        writer.SetPercent(123, 0, 0, 30).Should().Be(0);
        writer.SetPercent(123, 0, 20, 30).Should().Be(0);
        speeds.Should().Equal(30u, 30u);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    public void Native_failure_is_propagated_without_fallback_or_fake_success(int target)
    {
        var writer = new NvmlFanWriter((_, _, _) => 4, (_, _) => 4);
        writer.SetPercent(123, 0, target, 0).Should().Be(4);
    }
}
