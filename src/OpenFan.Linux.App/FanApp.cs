using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenFan.Core.Config;
using OpenFan.Core.ControlLoop;
using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenFan.Linux.App;

/// <summary>
/// App-side composition: backends, merge, controller, settings, tick.
/// Mirrors the Windows MainWindow tick without LHM/HWiNFO/WPF.
/// </summary>
public sealed class FanApp : IDisposable
{
    public SettingsStore Store { get; }
    public AppSettings Settings { get; }
    public HwmonBackend Hwmon { get; }
    public NvmlBackend Nvml { get; } = new();
    public CompositeActuator Actuator { get; }
    public FanController Controller { get; }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, double?> _readings = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<HardwareItem> _inventory = [];
    private string _fingerprint = "";

    /// <summary>Control id set changed — UI rebuilds cards.</summary>
    public event Action? InventoryChanged;

    /// <summary>One read/apply cycle finished — UI refreshes values.</summary>
    public event Action? Ticked;

    public FanApp()
    {
        Store = new SettingsStore(SettingsStore.DefaultPath);
        Settings = Store.Load();
        Hwmon = new HwmonBackend(null, Settings.Sources);
        Actuator = new CompositeActuator(("hwmon", Hwmon), ("nvml", Nvml));
        Controller = new FanController(Actuator);
        RefreshInventory(force: true);
    }

    public IReadOnlyList<HardwareItem> Inventory => _inventory;
    public double NowSeconds => _clock.Elapsed.TotalSeconds;
    public bool IsRoot => geteuid() == 0;

    public double? Reading(string id) => _readings.GetValueOrDefault(id);

    /// <summary>Tach paired with a control for display: nvml fan0↔tach0, hwmon pwm4↔fan4.</summary>
    public double? PairedRpm(HardwareItem control)
    {
        var pairedId = control.Backend.Equals("nvml", StringComparison.OrdinalIgnoreCase)
            ? control.Id.Replace(":fan:", ":tach:")
            : control.Id.Replace(":pwm:", ":fan:");
        return _readings.GetValueOrDefault(pairedId);
    }

    public void RefreshInventory(bool force = false)
    {
        var merged = InventoryMerger.Merge(Hwmon.Discover(), Nvml.Available ? Nvml.Discover() : []);
        var fingerprint = string.Join('|', merged.Select(i => i.Id).OrderBy(x => x, StringComparer.Ordinal));
        if (force || fingerprint != _fingerprint)
        {
            _inventory = merged;
            _fingerprint = fingerprint;
            InventoryChanged?.Invoke();
        }
    }

    public void Tick()
    {
        RefreshInventory();
        _readings.Clear();
        Hwmon.ReadInto(_readings);
        Nvml.ReadInto(_readings);

        if (Settings.ApplyCurves)
            Controller.Tick(Settings, _inventory, _readings, NowSeconds);

        Ticked?.Invoke();
    }

    /// <summary>Commanded % for the card display — curve evaluation, not the sysfs read-back (spec §5).</summary>
    public double? CommandedPercent(ControlSettings cfg)
    {
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.CurveId))
            return null;
        var curve = Settings.Curves.FirstOrDefault(c => c.Id == cfg.CurveId);
        return curve is null ? null : FanController.Evaluate(curve, Settings.Curves, _readings);
    }

    public void Save() => Store.Save(Settings);

    /// <summary>Exit / Apply-off path: give every owned control back to auto.</summary>
    public void RestoreAll() => Controller.RestoreAll();

    public void Dispose()
    {
        Hwmon.Dispose();
        Nvml.Dispose();
    }

    [DllImport("libc")]
    private static extern uint geteuid();
}
