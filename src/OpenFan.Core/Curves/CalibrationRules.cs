using OpenFan.Core.Config;

namespace OpenFan.Core.Curves;

public static class CalibrationRules
{
    public static bool HasAtLeastTwoPoints(IReadOnlyList<CalibrationSampleDto> samples)
        => samples.Count >= 2;

    public static bool RpmIsAscending(IReadOnlyList<CalibrationSampleDto> samples)
    {
        var ordered = samples.OrderBy(s => s.Percent).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Rpm + 0.01 < ordered[i - 1].Rpm)
                return false;
        }
        return ordered.Length > 0;
    }

    public static bool AvoidPointsAreContiguous(IReadOnlyList<CalibrationSampleDto> samples)
    {
        var ordered = samples.OrderBy(s => s.Percent).ToArray();
        var seenAvoid = false;
        var leftAvoid = false;
        foreach (var s in ordered)
        {
            if (s.Avoid)
            {
                if (leftAvoid)
                    return false;
                seenAvoid = true;
            }
            else if (seenAvoid)
                leftAvoid = true;
        }
        return true;
    }

    public static bool IsValid(IReadOnlyList<CalibrationSampleDto> samples)
        => HasAtLeastTwoPoints(samples) && RpmIsAscending(samples) && AvoidPointsAreContiguous(samples);

    /// <summary>If commanded % sits in an avoid block, snap to the nearer edge outside it.</summary>
    public static int SnapAwayFromAvoid(IReadOnlyList<CalibrationSampleDto> samples, int percent)
    {
        var ordered = samples.OrderBy(s => s.Percent).ToArray();
        var avoid = ordered.Where(s => s.Avoid).Select(s => s.Percent).ToArray();
        if (avoid.Length == 0)
            return percent;
        var lo = avoid[0];
        var hi = avoid[^1];
        if (percent < lo || percent > hi)
            return percent;
        return percent - lo <= hi - percent ? Math.Max(0, lo - 1) : Math.Min(100, hi + 1);
    }
}
