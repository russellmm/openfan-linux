using OpenFan.Core.ControlLoop;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Routes actuator calls by id prefix ("hwmon:" → HwmonBackend, "nvml:" → NvmlBackend).
/// The Windows app's CompositeHardware equivalent, minus LHM/HWiNFO (spec §3.1).
/// </summary>
public sealed class CompositeActuator : IFanActuator
{
    private readonly (string Prefix, IFanActuator Actuator)[] _routes;

    public CompositeActuator(params (string prefix, IFanActuator actuator)[] routes)
        => _routes = routes.Select(r => (r.prefix, r.actuator)).ToArray();

    public bool SetPercent(string controlId, int percent)
        => Route(controlId)?.SetPercent(controlId, percent) ?? false;

    public bool SetDefault(string controlId)
        => Route(controlId)?.SetDefault(controlId) ?? false;

    private IFanActuator? Route(string controlId)
    {
        foreach (var (prefix, actuator) in _routes)
        {
            if (controlId.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
                return actuator;
        }
        return null;
    }
}
