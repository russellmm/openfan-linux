using OpenFan.Core.Curves;

namespace OpenFan.Core.Tests;

public class HysteresisTests
{
    [Fact]
    public void Small_temp_noise_inside_deadband_does_not_change_applied_percent()
    {
        var gate = new HysteresisGate(deadbandC: 2, holdSeconds: 1);
        var points = new[] { new CurvePoint(40, 30), new CurvePoint(80, 70) };
        var first = gate.Decide(GraphCurve.Evaluate(points, 60, 0), nowSeconds: 0, sensorTempC: 60);
        first.Should().Be(50);

        var noisy = gate.Decide(GraphCurve.Evaluate(points, 61, 0), nowSeconds: 0.2, sensorTempC: 61);
        noisy.Should().BeNull();
    }

    [Fact]
    public void After_deadband_and_hold_a_new_percent_is_emitted()
    {
        var gate = new HysteresisGate(deadbandC: 2, holdSeconds: 1);
        var points = new[] { new CurvePoint(40, 30), new CurvePoint(80, 70) };
        gate.Decide(GraphCurve.Evaluate(points, 60, 0), 0, 60).Should().Be(50);

        gate.Decide(GraphCurve.Evaluate(points, 70, 0), 0.5, 70).Should().BeNull();
        gate.Decide(GraphCurve.Evaluate(points, 70, 0), 1.5, 70).Should().Be(60);
    }

    [Fact]
    public void First_sample_always_emits()
    {
        var gate = new HysteresisGate(2, 1);
        gate.Decide(40, nowSeconds: 10, sensorTempC: 55).Should().Be(40);
    }

    [Fact]
    public void Same_percent_is_reemitted_so_superio_cannot_steal_the_fan()
    {
        var gate = new HysteresisGate(deadbandC: 0, holdSeconds: 0);
        gate.Decide(21, nowSeconds: 0, sensorTempC: 0).Should().Be(21);
        gate.Decide(21, nowSeconds: 1, sensorTempC: 0).Should().Be(21);
        gate.Decide(21, nowSeconds: 2, sensorTempC: 0).Should().Be(21);
    }
}
