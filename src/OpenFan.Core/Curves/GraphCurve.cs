namespace OpenFan.Core.Curves;

public readonly record struct CurvePoint(double TempC, double Percent);

public static class GraphCurve
{
    public static double Evaluate(
        IReadOnlyList<CurvePoint> points,
        double tempC,
        int floor = 0,
        int maxSpeed = 100)
    {
        var lo = Math.Clamp(floor, 0, 100);
        var hi = Math.Clamp(maxSpeed, lo, 100);
        if (points.Count == 0)
            return lo;

        var ordered = points.OrderBy(p => p.TempC).ToArray();
        double y;
        if (ordered.Length == 1 || tempC <= ordered[0].TempC)
            y = ordered[0].Percent;
        else if (tempC >= ordered[^1].TempC)
            y = ordered[^1].Percent;
        else
        {
            var i = 0;
            while (i < ordered.Length - 1 && ordered[i + 1].TempC < tempC)
                i++;
            var a = ordered[i];
            var b = ordered[i + 1];
            var span = b.TempC - a.TempC;
            var t = span <= 0 ? 0 : (tempC - a.TempC) / span;
            y = a.Percent + t * (b.Percent - a.Percent);
        }

        return Math.Clamp(y, lo, hi);
    }
}
