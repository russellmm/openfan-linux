namespace OpenFan.Core.ControlLoop;

public static class ControlLiveDisplay
{
    public static string Format(double? commandedPercent, double? hardwarePercent, double? rpm)
    {
        var pct = commandedPercent ?? hardwarePercent;
        var pctText = pct is null ? "—" : $"{pct:0.#} %";
        var rpmText = rpm is null ? "— RPM" : $"{rpm:0} RPM";
        return $"{pctText}    {rpmText}";
    }
}
