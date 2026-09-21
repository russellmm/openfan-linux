namespace OpenFan.Core.Curves;

public enum MixFunction
{
    Max,
    Min,
    Average,
}

public static class MixCurve
{
    public static double? Combine(MixFunction fn, IReadOnlyList<double> childPercents)
        => Combine(fn, childPercents.Select(v => (double?)v).ToArray());

    public static double? Combine(MixFunction fn, IReadOnlyList<double?> childPercents)
    {
        var values = childPercents.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (values.Length == 0)
            return null;

        return fn switch
        {
            MixFunction.Min => values.Min(),
            MixFunction.Average => values.Average(),
            _ => values.Max(),
        };
    }
}
