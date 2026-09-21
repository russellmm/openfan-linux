using OpenFan.Core.Curves;

namespace OpenFan.Core.Tests;

public class MixCurveTests
{
    [Fact]
    public void Max_returns_largest_child()
    {
        MixCurve.Combine(MixFunction.Max, new[] { 20.0, 55.0, 33.0 }).Should().Be(55);
    }

    [Fact]
    public void Min_returns_smallest_child()
    {
        MixCurve.Combine(MixFunction.Min, new[] { 20.0, 55.0, 33.0 }).Should().Be(20);
    }

    [Fact]
    public void Average_returns_mean()
    {
        MixCurve.Combine(MixFunction.Average, new[] { 20.0, 40.0 }).Should().Be(30);
    }

    [Fact]
    public void Missing_children_are_ignored()
    {
        MixCurve.Combine(MixFunction.Max, new double?[] { null, 40, null }).Should().Be(40);
    }

    [Fact]
    public void All_missing_returns_null()
    {
        MixCurve.Combine(MixFunction.Max, new double?[] { null, null }).Should().BeNull();
        MixCurve.Combine(MixFunction.Max, Array.Empty<double>()).Should().BeNull();
    }
}
