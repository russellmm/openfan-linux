using FluentAssertions;
using OpenFan.Core.Curves;

namespace OpenFan.Core.Tests;

public class CurvePreviewLayoutTests
{
    [Fact]
    public void TempAxis_uses_first_and_last_points_not_an_outside_live_temp()
    {
        var pts = new[] { new CurvePoint(40, 20), new CurvePoint(80, 100) };
        var (t0, t1) = CurvePreviewLayout.TempAxis(pts);
        t0.Should().Be(40);
        t1.Should().Be(80);
    }

    [Fact]
    public void TempAxis_spans_all_points()
    {
        var pts = new[] { new CurvePoint(55, 40), new CurvePoint(30, 10), new CurvePoint(90, 100) };
        var (t0, t1) = CurvePreviewLayout.TempAxis(pts);
        t0.Should().Be(30);
        t1.Should().Be(90);
    }

    [Fact]
    public void Map_keeps_every_defined_point_inside_the_box()
    {
        const double w = 256, h = 56;
        var pts = new[]
        {
            new CurvePoint(35, 0),
            new CurvePoint(50, 20),
            new CurvePoint(70, 60),
            new CurvePoint(90, 100),
        };
        var (t0, t1) = CurvePreviewLayout.TempAxis(pts);
        foreach (var p in pts)
        {
            var m = CurvePreviewLayout.Map(p.TempC, p.Percent, t0, t1, w, h);
            CurvePreviewLayout.Inside(m, w, h).Should().BeTrue($"{p.TempC}°C {p.Percent}% -> {m}");
        }
    }

    [Fact]
    public void Map_puts_first_point_on_the_left_and_last_on_the_right()
    {
        const double w = 200, h = 50, pad = 3;
        var pts = new[] { new CurvePoint(40, 30), new CurvePoint(80, 100) };
        var (t0, t1) = CurvePreviewLayout.TempAxis(pts);
        var a = CurvePreviewLayout.Map(40, 30, t0, t1, w, h, pad);
        var b = CurvePreviewLayout.Map(80, 100, t0, t1, w, h, pad);
        a.X.Should().BeApproximately(pad, 0.01);
        b.X.Should().BeApproximately(w - pad, 0.01);
    }

    [Fact]
    public void Map_keeps_zero_and_hundred_percent_off_the_clip_edge()
    {
        const double w = 100, h = 40, pad = 3;
        var lo = CurvePreviewLayout.Map(0, 0, 0, 100, w, h, pad);
        var hi = CurvePreviewLayout.Map(100, 100, 0, 100, w, h, pad);
        lo.Y.Should().BeApproximately(h - pad, 0.01);
        hi.Y.Should().BeApproximately(pad, 0.01);
        lo.Y.Should().BeLessThan(h);
        hi.Y.Should().BeGreaterThan(0);
    }
}
