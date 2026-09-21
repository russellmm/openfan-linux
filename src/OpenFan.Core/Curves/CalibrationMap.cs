namespace OpenFan.Core.Curves;

public readonly record struct CalibrationSample(int Percent, double Rpm);

public sealed class CalibrationMap
{
    private readonly CalibrationSample[] _samples;

    public CalibrationMap(IEnumerable<CalibrationSample> samples)
    {
        _samples = samples.OrderBy(s => s.Percent).ToArray();
    }

    public IReadOnlyList<CalibrationSample> Samples => _samples;

    public double? RpmAt(double percent)
    {
        if (_samples.Length == 0)
            return null;
        if (percent <= _samples[0].Percent)
            return _samples[0].Rpm;
        if (percent >= _samples[^1].Percent)
            return _samples[^1].Rpm;

        var i = 0;
        while (i < _samples.Length - 1 && _samples[i + 1].Percent < percent)
            i++;
        var a = _samples[i];
        var b = _samples[i + 1];
        var span = b.Percent - a.Percent;
        var t = span <= 0 ? 0 : (percent - a.Percent) / span;
        return a.Rpm + t * (b.Rpm - a.Rpm);
    }
}
