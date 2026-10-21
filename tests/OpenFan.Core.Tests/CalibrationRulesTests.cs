using OpenFan.Core.Config;
using OpenFan.Core.Curves;

namespace OpenFan.Core.Tests;

public class CalibrationRulesTests
{
    [Theory]
    [InlineData(0, "20,30,40,50,60,70,80,90,100")]
    [InlineData(30, "30,40,50,60,70,80,90,100")]
    [InlineData(35, "35,40,50,60,70,80,90,100")]
    [InlineData(100, "100")]
    public void Sweep_respects_hardware_floor_without_duplicate_or_mislabeled_steps(int minimum, string expected)
        => string.Join(",", CalibrationRules.SweepPercents(minimum)).Should().Be(expected);

    [Fact]
    public void Requires_two_points()
    {
        CalibrationRules.HasAtLeastTwoPoints([new(10, 0)]).Should().BeFalse();
        CalibrationRules.HasAtLeastTwoPoints([new(10, 0), new(20, 300)]).Should().BeTrue();
    }

    [Fact]
    public void Rejects_descending_rpm()
    {
        CalibrationRules.RpmIsAscending([new(20, 400), new(40, 200)]).Should().BeFalse();
        CalibrationRules.RpmIsAscending([new(10, 0), new(20, 378), new(30, 560)]).Should().BeTrue();
    }

    [Fact]
    public void Avoid_must_be_one_contiguous_block()
    {
        CalibrationRules.AvoidPointsAreContiguous([
            new(10, 0, true), new(20, 100, true), new(30, 200, false),
        ]).Should().BeTrue();
        CalibrationRules.AvoidPointsAreContiguous([
            new(10, 0, true), new(20, 100, false), new(30, 200, true),
        ]).Should().BeFalse();
    }

    [Fact]
    public void Snaps_command_out_of_avoid_block()
    {
        var samples = new CalibrationSampleDto[]
        {
            new(10, 0, true), new(20, 0, true), new(30, 400), new(100, 1800),
        };
        CalibrationRules.SnapAwayFromAvoid(samples, 15).Should().Be(9);
        CalibrationRules.SnapAwayFromAvoid(samples, 50).Should().Be(50);
    }
}
