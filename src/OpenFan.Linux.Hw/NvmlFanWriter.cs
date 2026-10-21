namespace OpenFan.Linux.Hw;

/// <summary>Keep firmware fan-stop available without disabling a curve's positive targets.</summary>
internal sealed class NvmlFanWriter(
    Func<nint, uint, uint, int> setManual,
    Func<nint, uint, int> restoreDefault)
{
    public int SetPercent(nint device, uint fan, int percent, int minimum)
    {
        var target = Math.Clamp(percent, minimum, 100);
        // NVML manual 0 can still spin at the board's minimum RPM. Firmware automatic
        // policy owns fan-stop (and may run the fan when its own thermal rules require).
        // Apply the floor first: PRO cards with a nonzero minimum stay manual.
        return target == 0
            ? restoreDefault(device, fan)
            : setManual(device, fan, (uint)target);
    }
}
