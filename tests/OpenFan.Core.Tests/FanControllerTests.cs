using OpenFan.Core.Config;
using OpenFan.Core.ControlLoop;
using OpenFan.Core.Hardware;

namespace OpenFan.Core.Tests;

public class FanControllerTests
{
    private sealed class FakeActuator : IFanActuator
    {
        public List<(string Id, int Percent)> Sets { get; } = [];
        public List<string> Defaults { get; } = [];
        public bool FailNext { get; set; }

        public bool SetPercent(string controlId, int percent)
        {
            if (FailNext)
            {
                FailNext = false;
                return false;
            }
            Sets.Add((controlId, percent));
            return true;
        }

        public bool SetDefault(string controlId)
        {
            Defaults.Add(controlId);
            return true;
        }
    }

    private static HardwareItem ProFan(string id) => new()
    {
        Id = id,
        Kind = HardwareKind.Control,
        Name = "Fan 0",
        Backend = "nvml",
        Group = "RTX PRO 6000",
        MinPercent = 30,
        CanSet = true,
    };

    [Fact]
    public void Applies_flat_curve_clamped_to_pro_floor()
    {
        var actuator = new FakeActuator();
        var ctl = new FanController(actuator);
        var settings = new AppSettings
        {
            Controls = [new ControlSettings { Id = "fan0", Enabled = true, CurveId = "flat20", MinPercent = 30 }],
            Curves = [new CurveSettings { Id = "flat20", Type = "flat", Percent = 20 }],
        };

        ctl.Tick(settings, [ProFan("fan0")], new Dictionary<string, double?>(), nowSeconds: 0);
        actuator.Sets.Should().ContainSingle(s => s.Id == "fan0" && s.Percent == 30);
    }

    [Fact]
    public void Disable_restores_default()
    {
        var actuator = new FakeActuator();
        var ctl = new FanController(actuator);
        var settings = new AppSettings
        {
            Controls = [new ControlSettings { Id = "fan0", Enabled = true, CurveId = "flat50" }],
            Curves = [new CurveSettings { Id = "flat50", Type = "flat", Percent = 50 }],
        };
        var inv = new[] { ProFan("fan0") };
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 0);
        settings.Controls[0].Enabled = false;
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 1);
        actuator.Defaults.Should().Contain("fan0");
    }

    [Fact]
    public void Two_failures_restore_default()
    {
        var actuator = new FakeActuator();
        var ctl = new FanController(actuator);
        var settings = new AppSettings
        {
            Controls = [new ControlSettings { Id = "fan0", Enabled = true, CurveId = "flat50" }],
            Curves = [new CurveSettings { Id = "flat50", Type = "flat", Percent = 50 }],
        };
        var inv = new[] { ProFan("fan0") };
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 0);
        settings.Curves[0].Percent = 80;
        actuator.FailNext = true;
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 2);
        actuator.FailNext = true;
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 4);
        actuator.Defaults.Should().Contain("fan0");
    }

    [Fact]
    public void Duplicate_inventory_ids_do_not_throw()
    {
        var actuator = new FakeActuator();
        var ctl = new FanController(actuator);
        var settings = new AppSettings
        {
            Controls = [new ControlSettings { Id = "fan0", Enabled = true, CurveId = "flat50" }],
            Curves = [new CurveSettings { Id = "flat50", Type = "flat", Percent = 50 }],
        };
        var inv = new[] { ProFan("fan0"), ProFan("fan0") };
        var act = () => ctl.Tick(settings, inv, new Dictionary<string, double?>(), 0);
        act.Should().NotThrow();
        actuator.Sets.Should().ContainSingle(s => s.Id == "fan0" && s.Percent == 50);
    }

    [Fact]
    public void Reapplies_unchanged_mix_percent_every_tick()
    {
        var actuator = new FakeActuator();
        var ctl = new FanController(actuator);
        var settings = new AppSettings
        {
            Controls = [new ControlSettings { Id = "front", Enabled = true, CurveId = "mix" }],
            Curves =
            [
                new CurveSettings { Id = "mix", Type = "mix", Function = "max", ChildCurveIds = ["flat"] },
                new CurveSettings { Id = "flat", Type = "flat", Percent = 20.6 },
            ],
        };
        var inv = new[]
        {
            new HardwareItem
            {
                Id = "front",
                Kind = HardwareKind.Control,
                Name = "Front",
                Backend = "hwmon",
                CanSet = true,
            },
        };
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 0);
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 1);
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 2);
        actuator.Sets.Should().HaveCount(3);
        actuator.Sets.Should().OnlyContain(s => s.Id == "front" && s.Percent == 21);
    }

    [Fact]
    public void ResetApplies_allows_the_same_percent_to_be_treated_as_a_fresh_takeover()
    {
        var actuator = new FakeActuator();
        var ctl = new FanController(actuator);
        var settings = new AppSettings
        {
            Controls = [new ControlSettings { Id = "front", Enabled = true, CurveId = "flat21" }],
            Curves = [new CurveSettings { Id = "flat21", Type = "flat", Percent = 21 }],
        };
        var inv = new[]
        {
            new HardwareItem { Id = "front", Kind = HardwareKind.Control, Name = "Front", Backend = "hwmon", CanSet = true },
        };
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 0);
        ctl.ResetApplies();
        ctl.Tick(settings, inv, new Dictionary<string, double?>(), 1);
        actuator.Sets.Should().HaveCount(2);
    }
}
