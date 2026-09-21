using OpenFan.Core.Config;
using OpenFan.Core.ControlLoop;
using OpenFan.Core.Hardware;

namespace OpenFan.Core.Tests;

/// <summary>
/// Apply-loop semantics that matter on Linux (spec §3.2/§5): re-write PWM every tick
/// (the EC lesson from Windows NCT67xx), skip-not-restore on missing temp, restore once
/// when a control is disabled or the app exits, and honor per-device floors.
/// </summary>
public class ApplyLoopTests
{
    private const string BoardPwm = "hwmon:nct6799:10:pwm:4";
    private const string CpuTemp = "hwmon:k10temp:5:temp:1";
    private const string GpuTemp = "nvml:GPU-abc:temp:core";
    private const string ProFan = "nvml:GPU-abc:fan:0";

    private static HardwareItem Control(string id, string backend = "hwmon", int minPercent = 0) => new()
    {
        Id = id,
        Kind = HardwareKind.Control,
        Name = id,
        Backend = backend,
        MinPercent = minPercent,
    };

    private static HardwareItem Temp(string id, string backend = "hwmon") => new()
    {
        Id = id,
        Kind = HardwareKind.Temperature,
        Name = id,
        Backend = backend,
        CanSet = false,
    };

    private static AppSettings SettingsWithControl(
        string controlId,
        CurveSettings curve,
        int cfgMinPercent = 0)
    {
        var settings = new AppSettings { ApplyCurves = true };
        settings.Controls.Add(new ControlSettings
        {
            Id = controlId,
            Enabled = true,
            CurveId = curve.Id,
            MinPercent = cfgMinPercent,
        });
        settings.Curves.Add(curve);
        return settings;
    }

    [Fact]
    public void Flat_reapplies_same_percent_every_tick()
    {
        var actuator = new RecordingActuator();
        var controller = new FanController(actuator);
        var inventory = new[] { Control(BoardPwm) };
        var settings = SettingsWithControl(BoardPwm, new CurveSettings
        {
            Id = "flat-40",
            Type = "flat",
            Percent = 40,
        });

        for (var tick = 0; tick < 3; tick++)
            controller.Tick(settings, inventory, new Dictionary<string, double?>(), tick * 1.0);

        // Must be three writes, not one: skipping unchanged % is what the EC punishes.
        actuator.Sets.Should().Equal((BoardPwm, 40), (BoardPwm, 40), (BoardPwm, 40));
    }

    [Fact]
    public void Missing_temp_skips_control_and_does_not_restore()
    {
        var actuator = new RecordingActuator();
        var controller = new FanController(actuator);
        var inventory = new[] { Control(BoardPwm), Temp(CpuTemp) };
        var readings = new Dictionary<string, double?> { [CpuTemp] = 65 };
        var settings = SettingsWithControl(BoardPwm, new CurveSettings
        {
            Id = "graph-a",
            Type = "graph",
            SensorId = CpuTemp,
            Points = [new CurvePointDto(40, 20), new CurvePointDto(80, 60)],
        });

        controller.Tick(settings, inventory, readings, 0);
        actuator.Sets.Should().ContainSingle();

        // k10temp vanishes (driver hiccup): skip every later tick, never restore auto.
        readings[CpuTemp] = null;
        controller.Tick(settings, inventory, readings, 1);
        controller.Tick(settings, inventory, readings, 2);

        actuator.Sets.Should().ContainSingle();
        actuator.Restores.Should().BeEmpty();
    }

    [Fact]
    public void Disabling_control_restores_auto_exactly_once()
    {
        var actuator = new RecordingActuator();
        var controller = new FanController(actuator);
        var inventory = new[] { Control(BoardPwm) };
        var settings = SettingsWithControl(BoardPwm, new CurveSettings
        {
            Id = "flat-50",
            Type = "flat",
            Percent = 50,
        });

        controller.Tick(settings, inventory, new Dictionary<string, double?>(), 0);
        actuator.Sets.Should().ContainSingle();

        settings.Controls[0].Enabled = false;
        controller.Tick(settings, inventory, new Dictionary<string, double?>(), 1);
        controller.Tick(settings, inventory, new Dictionary<string, double?>(), 2);

        actuator.Restores.Should().Equal(BoardPwm);
    }

    [Fact]
    public void Exit_restore_all_returns_owned_controls_to_auto()
    {
        var actuator = new RecordingActuator();
        var controller = new FanController(actuator);
        var inventory = new[] { Control(BoardPwm) };
        var settings = SettingsWithControl(BoardPwm, new CurveSettings
        {
            Id = "flat-70",
            Type = "flat",
            Percent = 70,
        });

        controller.Tick(settings, inventory, new Dictionary<string, double?>(), 0);
        controller.RestoreAll();

        actuator.Restores.Should().Equal(BoardPwm);
    }

    [Fact]
    public void Pro6000_device_floor_clamps_flat_request()
    {
        var actuator = new RecordingActuator();
        var controller = new FanController(actuator);
        // NVML backend reports 30% MinPercent for PRO 6000 cards (spec §4.2).
        var inventory = new[] { Control(ProFan, "nvml", minPercent: 30) };
        var settings = SettingsWithControl(ProFan, new CurveSettings
        {
            Id = "flat-10",
            Type = "flat",
            Percent = 10,
        });

        controller.Tick(settings, inventory, new Dictionary<string, double?>(), 0);

        actuator.Sets.Should().Equal((ProFan, 30));
    }

    [Fact]
    public void Mix_of_cpu_graph_and_gpu_graph_drives_board_pwm()
    {
        // Spec success criterion #3: Mix(Max)(CPU graph + GPU graph) → motherboard fan.
        var actuator = new RecordingActuator();
        var controller = new FanController(actuator);
        var inventory = new[] { Control(BoardPwm), Temp(CpuTemp, "hwmon"), Temp(GpuTemp, "nvml") };

        var cpuGraph = new CurveSettings
        {
            Id = "cpu-graph",
            Type = "graph",
            SensorId = CpuTemp,
            Points = [new CurvePointDto(40, 20), new CurvePointDto(80, 60)],
        };
        var gpuGraph = new CurveSettings
        {
            Id = "gpu-graph",
            Type = "graph",
            SensorId = GpuTemp,
            Points = [new CurvePointDto(30, 10), new CurvePointDto(90, 70)],
        };
        var mix = new CurveSettings
        {
            Id = "mix-max",
            Type = "mix",
            Function = "max",
            ChildCurveIds = ["cpu-graph", "gpu-graph"],
        };

        var settings = SettingsWithControl(BoardPwm, mix);
        settings.Curves.Add(cpuGraph);
        settings.Curves.Add(gpuGraph);

        // CPU 70 °C → 50 %; GPU 60 °C → 40 %; max wins.
        var readings = new Dictionary<string, double?> { [CpuTemp] = 70, [GpuTemp] = 60 };
        controller.Tick(settings, inventory, readings, 0);

        actuator.Sets.Should().Equal((BoardPwm, 50));
    }

    [Fact]
    public void Two_set_failures_after_ownership_release_control_to_auto()
    {
        var actuator = new RecordingActuator();
        var controller = new FanController(actuator);
        var inventory = new[] { Control(BoardPwm) };
        var settings = SettingsWithControl(BoardPwm, new CurveSettings
        {
            Id = "flat-60",
            Type = "flat",
            Percent = 60,
        });

        controller.Tick(settings, inventory, new Dictionary<string, double?>(), 0); // success: control is owned
        actuator.FailNextSet = true;
        controller.Tick(settings, inventory, new Dictionary<string, double?>(), 1); // fail #1: error recorded, still owned
        actuator.FailNextSet = true;
        controller.Tick(settings, inventory, new Dictionary<string, double?>(), 2); // fail #2: give up, restore auto

        actuator.Restores.Should().Equal(BoardPwm);
        actuator.Sets.Should().ContainSingle(); // only the initial success was written
    }
}
