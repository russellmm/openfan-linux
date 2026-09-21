using OpenFan.Core.Config;
using OpenFan.Core.Curves;
using OpenFan.Core.Hardware;

namespace OpenFan.Core.ControlLoop;

public interface IFanActuator
{
    bool SetPercent(string controlId, int percent);
    bool SetDefault(string controlId);
}

public sealed class FanController
{
    private readonly IFanActuator _actuator;
    private readonly Dictionary<string, HysteresisGate> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _gateCurveIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _failCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    public FanController(IFanActuator actuator) => _actuator = actuator;

    public string? PausedControlId { get; set; }

    public IReadOnlyDictionary<string, string> Errors => _errors;

    public int? Tick(
        AppSettings settings,
        IReadOnlyList<HardwareItem> inventory,
        IReadOnlyDictionary<string, double?> readings,
        double nowSeconds)
    {
        int? lastApplied = null;
        var controls = inventory
            .Where(i => i.Kind == HardwareKind.Control)
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var curves = settings.Curves
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var cfg in settings.Controls)
        {
            if (!controls.TryGetValue(cfg.Id, out var item))
                continue;
            if (cfg.Hidden)
                continue;
            if (PausedControlId is not null && cfg.Id.Equals(PausedControlId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.CurveId) || !item.CanSet)
            {
                RestoreIfOwned(cfg.Id);
                continue;
            }

            if (!curves.TryGetValue(cfg.CurveId, out var curve))
            {
                RestoreIfOwned(cfg.Id);
                continue;
            }

            var output = Evaluate(curve, settings.Curves, readings);
            if (output is null)
            {
                _errors[cfg.Id] = "curve has no value";
                continue;
            }

            var floor = Math.Max(cfg.MinPercent, item.MinPercent);
            var target = (int)Math.Round(Math.Clamp(output.Value, floor, 100), MidpointRounding.AwayFromZero);
            if (cfg.Calibration.Count > 0)
                target = Math.Clamp(CalibrationRules.SnapAwayFromAvoid(cfg.Calibration, target), floor, 100);

            if (!_gates.TryGetValue(cfg.Id, out var gate)
                || !_gateCurveIds.TryGetValue(cfg.Id, out var boundCurve)
                || !boundCurve.Equals(cfg.CurveId, StringComparison.OrdinalIgnoreCase))
            {
                var isGraph = curve.Type.Equals("graph", StringComparison.OrdinalIgnoreCase);
                gate = new HysteresisGate(
                    isGraph ? curve.HysteresisC : 0,
                    isGraph ? curve.HysteresisS : 0);
                _gates[cfg.Id] = gate;
                _gateCurveIds[cfg.Id] = cfg.CurveId!;
            }

            var sensorTemp = curve.SensorId is { } sid && readings.TryGetValue(sid, out var t) ? t ?? 0 : 0;
            var decided = gate.Decide(target, nowSeconds, sensorTemp);
            if (decided is null)
                continue;

            if (_actuator.SetPercent(cfg.Id, decided.Value))
            {
                _owned.Add(cfg.Id);
                _failCounts[cfg.Id] = 0;
                _errors.Remove(cfg.Id);
                lastApplied = decided.Value;
            }
            else
            {
                gate.Invalidate();
                RegisterFail(cfg.Id, "set failed");
            }
        }

        return lastApplied;
    }

    public void RestoreAll()
    {
        foreach (var id in _owned.ToArray())
            RestoreIfOwned(id);
    }

    public void ResetApplies()
    {
        foreach (var id in _gates.Keys.ToArray())
        {
            _gates.Remove(id);
            _gateCurveIds.Remove(id);
        }
    }

    private void RestoreIfOwned(string id)
    {
        if (!_owned.Remove(id))
            return;
        _actuator.SetDefault(id);
        _gates.Remove(id);
        _gateCurveIds.Remove(id);
        _failCounts.Remove(id);
    }

    private void RegisterFail(string id, string message)
    {
        _failCounts.TryGetValue(id, out var n);
        n++;
        _failCounts[id] = n;
        _errors[id] = message;
        if (n >= 2)
            RestoreIfOwned(id);
    }

    public static double? Evaluate(
        CurveSettings curve,
        IReadOnlyList<CurveSettings> all,
        IReadOnlyDictionary<string, double?> readings)
    {
        var map = all
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return EvaluateCurve(curve, map, readings);
    }

    private static double? EvaluateCurve(
        CurveSettings curve,
        IReadOnlyDictionary<string, CurveSettings> all,
        IReadOnlyDictionary<string, double?> readings)
        => EvaluateCurve(curve, all, readings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static double? EvaluateCurve(
        CurveSettings curve,
        IReadOnlyDictionary<string, CurveSettings> all,
        IReadOnlyDictionary<string, double?> readings,
        HashSet<string> stack)
    {
        if (!stack.Add(curve.Id))
            return null;

        if (curve.Type.Equals("flat", StringComparison.OrdinalIgnoreCase))
            return curve.Percent;

        if (curve.Type.Equals("graph", StringComparison.OrdinalIgnoreCase))
        {
            if (curve.SensorId is null || !readings.TryGetValue(curve.SensorId, out var temp) || temp is null)
                return null;
            var points = curve.Points.Select(p => new CurvePoint(p.TempC, p.Percent)).ToArray();
            return GraphCurve.Evaluate(points, temp.Value, floor: 0, maxSpeed: curve.MaxSpeedPercent <= 0 ? 100 : curve.MaxSpeedPercent);
        }

        if (curve.Type.Equals("mix", StringComparison.OrdinalIgnoreCase))
        {
            var fn = curve.Function.ToLowerInvariant() switch
            {
                "min" => MixFunction.Min,
                "average" or "avg" => MixFunction.Average,
                _ => MixFunction.Max,
            };
            var children = new List<double?>();
            foreach (var id in curve.ChildCurveIds)
            {
                if (!all.TryGetValue(id, out var child))
                {
                    children.Add(null);
                    continue;
                }
                children.Add(EvaluateCurve(child, all, readings, stack));
            }
            return MixCurve.Combine(fn, children);
        }

        return null;
    }
}
