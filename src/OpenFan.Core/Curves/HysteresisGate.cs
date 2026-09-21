namespace OpenFan.Core.Curves;

public sealed class HysteresisGate
{
    private readonly double _deadbandC;
    private readonly double _holdSeconds;
    private bool _hasSample;
    private int _lastPercent;
    private double _lastTempC;
    private double _lastEmitSeconds;

    public HysteresisGate(double deadbandC, double holdSeconds)
    {
        _deadbandC = deadbandC;
        _holdSeconds = holdSeconds;
    }

    /// <summary>Returns the integer percent to apply, or null to keep the last command.</summary>
    public int? Decide(double targetPercent, double nowSeconds, double sensorTempC)
    {
        var pct = (int)Math.Round(targetPercent, MidpointRounding.AwayFromZero);
        pct = Math.Clamp(pct, 0, 100);

        if (!_hasSample)
        {
            Commit(pct, sensorTempC, nowSeconds);
            return pct;
        }

        if (pct == _lastPercent)
            return pct;
        if (Math.Abs(sensorTempC - _lastTempC) < _deadbandC)
            return null;
        if (nowSeconds - _lastEmitSeconds < _holdSeconds)
            return null;

        Commit(pct, sensorTempC, nowSeconds);
        return pct;
    }

    public void Invalidate()
    {
        _lastPercent = int.MinValue;
    }

    private void Commit(int pct, double tempC, double nowSeconds)
    {
        _hasSample = true;
        _lastPercent = pct;
        _lastTempC = tempC;
        _lastEmitSeconds = nowSeconds;
    }
}
