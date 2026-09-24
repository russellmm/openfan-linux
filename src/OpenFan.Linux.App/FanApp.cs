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
    public AppSettings Settings { get; private set; }
    public HwmonBackend Hwmon { get; }
    public NvmlBackend Nvml { get; } = new();
    public NvmlHelperClient NvmlHelper { get; } = new();

    /// <summary>GPU writes go through the root helper when its socket exists; direct NVML
    /// remains for sudo debugging. hwmon always goes direct via the udev ACL.</summary>
    private readonly IFanActuator _nvmlRoute;

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

    private readonly LogindMonitor _logind = new();

    /// <summary>True between PrepareForSleep(true) and the re-arm after wake.</summary>
    public bool Suspended { get; private set; }

    public FanApp()
    {
        Store = new SettingsStore(SettingsStore.DefaultPath);
        ActiveConfigPath = ResolveActivePath();
        Settings = new SettingsStore(ActiveConfigPath).Load();
        Hwmon = new HwmonBackend(null, Settings.Sources);
        _nvmlRoute = NvmlHelper.IsAvailable ? NvmlHelper : Nvml;
        Actuator = new CompositeActuator(("hwmon", Hwmon), ("nvml", _nvmlRoute));

        // logind: release fans to firmware before sleep; re-arm writes after wake (EC often resets pwm_enable).
        _logind.Start(
            onSuspending: () =>
            {
                Suspended = true;
                try { Controller.RestoreAll(); } catch { }
                Console.Error.WriteLine("openfan: suspend — released fans to firmware");
            },
            onResume: () => _ = Task.Run(async () =>
            {
                await Task.Delay(1500); // let EC/driver settle
                try { Controller.ResetApplies(); } catch { }
                Suspended = false;
                Console.Error.WriteLine("openfan: resumed — curve writes re-armed");
            }));
        Controller = new FanController(Actuator);
        RefreshInventory(force: true);

        foreach (var (uuid, watts) in Settings.GpuPowerLimitsW.ToList())
            SetGpuPowerLimit(uuid, watts); // best effort; failures surface as GpuPowerError

        // CPU PPT is volatile SMU state, so a saved limit has to be re-asserted every boot. When
        // hsmp-control-apply.service is installed it usually already did this; doing it here too
        // keeps the value correct on machines that only run the app, and makes desired==live true.
        if (Settings is { CpuLimitShouldReassertOnStart: true, CpuPowerLimitW: { } cpuW })
        {
            // Only when the user asked for persistence. Re-asserting an unsolicited saved limit made
            // "keep after reboot" unchecked behave as if it were ticked — the limit came back after a
            // boot with nothing installed to bring it back. Two things must stay true here:
            //  * unchecked  → firmware's own value survives untouched;
            //  * never rewrite /etc at startup, so a boot config placed by hand or by
            //    install-persistence.sh cannot be cleared just because the app launched.
            SetCpuPowerLimit(cpuW, keepAfterReboot: true, manageBootLimit: false);
        }
    }

    /// <summary>Most recent GPU power-limit failure (null when healthy).</summary>
    public string? GpuPowerError { get; private set; }

    /// <summary>Apply + persist a GPU power limit; routes through the helper when unprivileged.</summary>
    public bool SetGpuPowerLimit(string uuid, int watts)
    {
        var ok = NvmlHelper.IsAvailable
            ? NvmlHelper.TrySetPowerLimit(uuid, watts)
            : Nvml.SetPowerLimit(uuid, watts);
        if (!ok)
        {
            GpuPowerError = NvmlHelper.IsAvailable ? NvmlHelper.LastError : Nvml.LastWriteError ?? "power limit failed";
            return false;
        }
        GpuPowerError = null;
        Settings.GpuPowerLimitsW[uuid] = watts;
        Save();
        return true;
    }

    public IReadOnlyList<HardwareItem> Inventory => _inventory;
    public double NowSeconds => _clock.Elapsed.TotalSeconds;
    public bool IsRoot => geteuid() == 0;

    private readonly CpuPowerControl _cpuDirect = new();

    /// <summary>Most recent CPU socket-power-limit failure (null when healthy).</summary>
    public string? CpuPowerError { get; private set; }

    /// <summary>What BIOS holds in flash — the limit the SMU is given at next boot. Null when the
    /// CBS variable is unreadable or PPT is Auto. Read-only: this firmware refuses OS writes to
    /// TDP/TjMax/PPT in that variable while Secure Boot is enabled.</summary>
    public int? CpuBiosDefaultMilliwatts => _cpuDirect.BiosDefaultMilliwatts();

    /// <summary>The BIOS "SMU Common Options" values for display: TDP, flash PPT and TjMax, each
    /// null when Auto or when the variable could not be validated. Never a guessed number.</summary>
    public CbsSetupReader.Reading CbsLimits => _cbs.Read();

    private readonly CbsSetupReader _cbs = new();

    /// <summary>Accepted CPU limit window, surfaced so the UI cannot offer a value the helper rejects.</summary>
    public static (int Min, int Max) CpuLimitWindowWatts => (CpuPowerControl.MinWatts, CpuPowerControl.MaxWatts);

    /// <summary>
    /// Apply a CPU socket power limit (PPT) and persist the intent. Routes through openfan-helper
    /// when it is running; direct writes only work as root, so an unprivileged session with no
    /// helper gets a clear error rather than a silently ignored click.
    /// <paramref name="keepAfterReboot"/> also maintains /etc/hsmp-control/ppt_mw for
    /// hsmp-control-apply.service; without it the limit reverts to the BIOS value at next boot,
    /// because the HSMP setting is volatile.
    /// </summary>
    /// <param name="manageBootLimit">False to re-assert only the volatile limit without touching the
    /// boot override — used at startup so the app cannot silently undo a persistence config it did
    /// not create. Only an explicit Apply changes persistence.</param>
    public bool SetCpuPowerLimit(int watts, bool keepAfterReboot, bool manageBootLimit = true)
    {
        var helper = NvmlHelper.IsAvailable;
        CpuPowerWarning = null;

        if (!(helper ? NvmlHelper.TrySetCpuPowerLimit(watts)
                      : IsRoot && _cpuDirect.SetLiveLimitWatts(watts)))
        {
            CpuPowerError = ExplainCpuFailure(helper ? NvmlHelper.LastError
                                          : IsRoot ? _cpuDirect.LastError
                                          : "openfan-helper is not running and this session is not root — the limit cannot be written");
            return false;
        }

        if (!manageBootLimit)
        {
            Settings.CpuPowerLimitW = watts;
            Save();
            return true;
        }

        if (!(helper ? NvmlHelper.TrySetCpuBootLimit(keepAfterReboot ? watts : null)
                      : _cpuDirect.SetBootLimitWatts(keepAfterReboot ? watts : null)))
        {
            // The live limit did land; say so instead of reporting a flat failure.
            CpuPowerError = ExplainCpuFailure(helper ? NvmlHelper.LastError : _cpuDirect.LastError)
                ?? "limit applied, but the keep-after-reboot setting could not be saved";
            return false;
        }

        CpuPowerError = null;
        Settings.CpuPowerLimitW = watts;
        Settings.CpuKeepAfterReboot = keepAfterReboot;
        Save();

        // The file was written, but with no unit to read it the limit still reverts at boot. Say so
        // rather than letting a green "re-applied at every boot" be a lie.
        CpuPowerWarning = keepAfterReboot && !_cpuDirect.BootPersistenceWired()
            ? $"limit applied, but {CpuPowerControl.BootUnitName} is not installed — this reverts to the BIOS value on next boot"
            : null;
        return true;
    }

    /// <summary>Non-fatal caveat from the last successful operation (null when nothing to flag).</summary>
    public string? CpuPowerWarning { get; private set; }

    /// <summary>Whether a saved CPU limit will actually survive a reboot here.</summary>
    public bool CpuBootPersistenceAvailable => _cpuDirect.BootPersistenceWired();

    /// <summary>
    /// Makes a raw helper reply actionable. The common case in practice: an openfan-helper installed
    /// before CPU power support answers "err unknown command", which otherwise reads like a mystery
    /// failure of the new control rather than a stale daemon.
    /// </summary>
    private static string ExplainCpuFailure(string? reason) => reason switch
    {
        null or "" => "CPU power limit failed",
        var r when r.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            => "openfan-helper is out of date — it does not know the CPU power commands. " +
               "Rebuild and reinstall it (see packaging/README), then restart openfan.",
        var r => r,
    };

    /// <summary>Enabled GPU fans but no helper and not root → writes cannot land.</summary>
    public bool GpuHelperMissing => !IsRoot && !NvmlHelper.IsAvailable;

    public double? Reading(string id) => _readings.GetValueOrDefault(id);

    /// <summary>Tach paired with a control for display: nvml fan0↔tach0, hwmon pwm4↔fan4.</summary>
    /// <summary>Id of the tach paired with a fan control (nvml:{uuid}:tach:n / hwmon …:fan:n).</summary>
    /// <summary>Display name for a sensor: user alias when set, hardware name otherwise.</summary>
    public string SensorLabel(HardwareItem item) =>
        Settings.SensorAliases.GetValueOrDefault(item.Id, item.Name);

    public string PairedTachId(HardwareItem control)
    {
        var cfg = Settings.Controls.FirstOrDefault(c => c.Id == control.Id);
        if (!string.IsNullOrWhiteSpace(cfg?.PairedTachId))
            return cfg!.PairedTachId!; // user override from the card's ⋮ menu
        return control.Backend.Equals("nvml", StringComparison.OrdinalIgnoreCase)
            ? control.Id.Replace(":fan:", ":tach:")
            : control.Id.Replace(":pwm:", ":fan:");
    }

    public double? PairedRpm(HardwareItem control) => _readings.GetValueOrDefault(PairedTachId(control));

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

    /// <summary>Per-backend human reason for the last failed write (spec: never silently monitor-only).</summary>
    public string? WriteError(HardwareItem item) =>
        item.Backend.Equals("nvml", StringComparison.OrdinalIgnoreCase)
            ? _nvmlRoute is NvmlHelperClient client ? client.LastError : Nvml.LastWriteError
            : Hwmon.LastWriteError;

    public bool HasWriteError(string controlId) => Controller.Errors.ContainsKey(controlId);

    /// <summary>One-open check: does this session actually have PWM write access?</summary>
    public string? StartupWriteProbe() => Hwmon.ProbeWriteAccess();

    /// <summary>Switch whole settings profile (Save/Load setup): release owned fans first,
    /// swap in the loaded profile, pin it as the active config, rescan hardware.</summary>
    public void LoadProfile(AppSettings fresh)
    {
        RestoreAll();
        Settings = fresh;
        Save();
        RefreshInventory(force: true);
    }

    /// <summary>Live output of any library curve against the latest readings (curve cards).</summary>
    public double? CurveOutput(string curveId)
    {
        var curve = Settings.Curves.FirstOrDefault(c => c.Id == curveId);
        return curve is null ? null : FanController.Evaluate(curve, Settings.Curves, _readings);
    }

    /// <summary>File the UI reads/writes right now — default config.json or a named file (e.g. myconfig.json).</summary>
    public string ActiveConfigPath { get; private set; } = SettingsStore.DefaultPath;

    /// <summary>Sidecar remembering the active named config across launches; absent = default config.</summary>
    private static string ActivePointerPath => Path.Combine(
        Path.GetDirectoryName(SettingsStore.DefaultPath)!, "active");

    private static string ResolveActivePath()
    {
        try
        {
            if (File.Exists(ActivePointerPath))
            {
                var p = File.ReadAllText(ActivePointerPath).Trim();
                if (p.Length > 0 && File.Exists(p))
                    return p;
            }
        }
        catch { }
        return SettingsStore.DefaultPath;
    }

    /// <summary>Switch the live settings to another config file (null = default) and persist that choice.</summary>
    public void SwitchConfig(string? path)
    {
        var target = string.IsNullOrWhiteSpace(path) ? SettingsStore.DefaultPath : path!;
        LoadProfile(new SettingsStore(target).Load());
        ActiveConfigPath = target;
        try
        {
            if (target == SettingsStore.DefaultPath)
            {
                if (File.Exists(ActivePointerPath)) File.Delete(ActivePointerPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ActivePointerPath)!);
                File.WriteAllText(ActivePointerPath, target);
            }
        }
        catch { }
    }

    public void Save()
    {
        new SettingsStore(ActiveConfigPath).Save(Settings);
    }

    /// <summary>Exit / Apply-off path: give every owned control back to auto.</summary>
    public void RestoreAll() => Controller.RestoreAll();

    /// <summary>Calibration direct-drive: bypasses the controller (control must be disabled first).</summary>
    public bool ManualSetPercent(string controlId, int percent) => Actuator.SetPercent(controlId, percent);

    public void ManualRestore(string controlId) => Actuator.SetDefault(controlId);

    public void Dispose()
    {
        _logind.Dispose();
        NvmlHelper.Dispose(); // clean socket close → helper restores anything we owned
        Hwmon.Dispose();
        Nvml.Dispose();
    }

    [DllImport("libc")]
    private static extern uint geteuid();
}
