using OpenFan.Core.ControlLoop;

namespace OpenFan.Core.Tests;

public class ControlLiveDisplayTests
{
    [Fact]
    public void Prefers_commanded_curve_percent_over_hardware_software_value()
    {
        ControlLiveDisplay.Format(commandedPercent: 20.6, hardwarePercent: 0, rpm: 1150)
            .Should().Be("20.6 %    1150 RPM");
    }

    [Fact]
    public void Falls_back_to_hardware_when_no_curve()
    {
        ControlLiveDisplay.Format(commandedPercent: null, hardwarePercent: 50, rpm: 1687)
            .Should().Be("50 %    1687 RPM");
    }
}
