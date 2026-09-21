using OpenFan.Core.Curves;

namespace OpenFan.Core.Tests;

public class GraphCurveTests
{
    [Fact]
    public void Evaluate_empty_points_returns_floor()
    {
        GraphCurve.Evaluate(Array.Empty<CurvePoint>(), tempC: 50, floor: 30)
            .Should().Be(30);
    }

    [Fact]
    public void Evaluate_single_point_is_flat_at_that_percent_clamped_to_floor()
    {
        var points = new[] { new CurvePoint(60, 20) };
        GraphCurve.Evaluate(points, 40, floor: 30).Should().Be(30);
        GraphCurve.Evaluate(points, 60, floor: 30).Should().Be(30);
        GraphCurve.Evaluate(points, 90, floor: 30).Should().Be(30);
    }

    [Fact]
    public void Evaluate_below_first_x_returns_first_y()
    {
        var points = new[] { new CurvePoint(40, 30), new CurvePoint(80, 100) };
        GraphCurve.Evaluate(points, 20, floor: 0).Should().Be(30);
    }

    [Fact]
    public void Evaluate_above_last_x_returns_last_y()
    {
        var points = new[] { new CurvePoint(40, 30), new CurvePoint(80, 100) };
        GraphCurve.Evaluate(points, 95, floor: 0).Should().Be(100);
    }

    [Fact]
    public void Evaluate_interpolates_linearly_between_neighbors()
    {
        var points = new[] { new CurvePoint(40, 30), new CurvePoint(80, 70) };
        GraphCurve.Evaluate(points, 60, floor: 0).Should().BeApproximately(50, 0.01);
    }

    [Fact]
    public void Evaluate_clamps_to_floor_and_maxSpeed()
    {
        var points = new[] { new CurvePoint(40, 10), new CurvePoint(80, 100) };
        GraphCurve.Evaluate(points, 40, floor: 30, maxSpeed: 90).Should().Be(30);
        GraphCurve.Evaluate(points, 80, floor: 30, maxSpeed: 90).Should().Be(90);
    }

    [Fact]
    public void Evaluate_does_not_require_pre_sorted_points()
    {
        var points = new[] { new CurvePoint(80, 100), new CurvePoint(40, 20) };
        GraphCurve.Evaluate(points, 60, floor: 0).Should().BeApproximately(60, 0.01);
    }
}
