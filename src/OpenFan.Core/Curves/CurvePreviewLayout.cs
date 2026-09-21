namespace OpenFan.Core.Curves;

/// <summary>
/// Maps a graph curve into a mini-preview box. The X axis is the curve's own
/// first/last points (not live temp) so the entire defined graph is visible.
/// </summary>
public static class CurvePreviewLayout
{
    public readonly record struct Mapped(double X, double Y);

    public static (double T0, double T1) TempAxis(IReadOnlyList<CurvePoint> points)
    {
        if (points.Count == 0)
            return (0, 1);
        var t0 = points[0].TempC;
        var t1 = points[0].TempC;
        foreach (var p in points)
        {
            t0 = Math.Min(t0, p.TempC);
            t1 = Math.Max(t1, p.TempC);
        }
        if (t1 <= t0)
            t1 = t0 + 1;
        return (t0, t1);
    }

    public static Mapped Map(
        double tempC,
        double percent,
        double t0,
        double t1,
        double width,
        double height,
        double pad = 3)
    {
        var span = Math.Max(1e-6, t1 - t0);
        var plotW = Math.Max(1, width - 2 * pad);
        var plotH = Math.Max(1, height - 2 * pad);
        var x = pad + (tempC - t0) / span * plotW;
        var y = pad + (100 - Math.Clamp(percent, 0, 100)) / 100.0 * plotH;
        return new Mapped(x, y);
    }

    public static bool Inside(Mapped p, double width, double height, double epsilon = 0.01)
        => p.X >= -epsilon && p.X <= width + epsilon
           && p.Y >= -epsilon && p.Y <= height + epsilon;
}
