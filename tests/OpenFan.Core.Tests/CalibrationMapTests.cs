using OpenFan.Core.Curves;

namespace OpenFan.Core.Tests;

public class CalibrationMapTests
{
    [Fact]
    public void Interpolates_rpm_between_samples()
    {
        var map = new CalibrationMap(new[]
        {
            new CalibrationSample(30, 400),
            new CalibrationSample(100, 1800),
        });
        map.RpmAt(65).Should().BeApproximately(1100, 0.5);
    }

    [Fact]
    public void Below_first_sample_returns_first_rpm()
    {
        var map = new CalibrationMap(new[]
        {
            new CalibrationSample(30, 400),
            new CalibrationSample(100, 1800),
        });
        map.RpmAt(10).Should().Be(400);
    }

    [Fact]
    public void Empty_map_returns_null()
    {
        new CalibrationMap(Array.Empty<CalibrationSample>()).RpmAt(50).Should().BeNull();
    }
}
