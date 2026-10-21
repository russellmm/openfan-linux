using OpenFan.Core.Config;
using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public sealed class SensorPollingPlanTests
{
    [Fact]
    public void Calibration_retains_real_tach_reads_with_hidden_groups_and_no_visible_home()
    {
        var control = new HardwareItem { Id = "nvml:gpu:fan:1", Backend = "nvml", Kind = HardwareKind.Control, Name = "Fan" };
        var tach = new HardwareItem { Id = "nvml:gpu:tach:1", Backend = "nvml", Kind = HardwareKind.Tach, Name = "RPM" };
        var custom = new HardwareItem { Id = "hwmon:board:0:fan:1", Backend = "hwmon", Kind = HardwareKind.Tach, Name = "Paired RPM" };
        var settings = new AppSettings { HiddenSensorGroups = SensorPollingPlan.GroupOrder.ToList(), Controls = [new() { Id = control.Id }] };
        HardwareItem[] inventory = [control, tach, custom];
        SensorPollingPlan.Create(inventory, settings).Should().BeEmpty();
        SensorPollingPlan.Create(inventory, settings, calibrationControlId: control.Id).Should().BeEquivalentTo([control.Id, tach.Id]);
        settings.Controls[0].PairedTachId = custom.Id;
        SensorPollingPlan.Create(inventory, settings, calibrationControlId: control.Id).Should().BeEquivalentTo([control.Id, custom.Id]);
        SensorPollingPlan.Create(inventory, settings, calibrationControlId: "missing").Should().BeEmpty();
    }

    private static readonly HardwareItem Temp = new() { Id = "hwmon:k10temp:6:temp:1", Name = "Tctl", Backend = "hwmon", Kind = HardwareKind.Temperature, Group = "k10temp (hwmon6)" };
    private static readonly HardwareItem Ecc = new() { Id = "edac:mc0:corrected:count", Name = "Corrected errors", Backend = "edac", Kind = HardwareKind.Counter, Group = "mc0" };
    private static readonly HardwareItem GpuPower = new() { Id = "nvml:gpu:metric:power", Name = "Board power", Backend = "nvml", Kind = HardwareKind.Power, Group = "GPU 1" };
    private static readonly HardwareItem Alarm = new() { Id = "hwmon:k10temp:6:temp:1:alarm", Name = "Alarm", Backend = "hwmon", Kind = HardwareKind.Status, Group = Temp.Group };
    private static readonly HardwareItem Cap = new() { Id = "hwmon:amd_hsmp_hwmon:7:power:1:cap", Name = "Power cap", Backend = "hwmon", Kind = HardwareKind.Power };
    private static readonly HardwareItem CpuPower = new() { Id = "hwmon:amd_hsmp_hwmon:7:power:1", Name = "Power", Backend = "hwmon", Kind = HardwareKind.Power };
    private static readonly HardwareItem[] Inventory = [Temp, Ecc, GpuPower, Alarm, Cap, CpuPower];

    [Fact]
    public void HiddenGroupsAndUncheckedStatusAreNotRequested()
    {
        var settings = new AppSettings { HiddenSensorGroups = ["Memory (ECC)", "GPU"] };
        var plan = SensorPollingPlan.Create(Inventory, settings);
        Assert.Contains(Temp.Id, plan); Assert.DoesNotContain(Ecc.Id, plan);
        Assert.DoesNotContain(GpuPower.Id, plan); Assert.DoesNotContain(Alarm.Id, plan);
        settings.ShowSensorStatus = true;
        Assert.Contains(Alarm.Id, SensorPollingPlan.Create(Inventory, settings));
        settings.HiddenSensorGroups.Clear();
        Assert.Contains(Ecc.Id, SensorPollingPlan.Create(Inventory, settings));
    }

    [Fact]
    public void HiddenAndCollapsedCurveInputsRemainLiveThroughNestedMixesAndCycles()
    {
        var settings = new AppSettings
        {
            ApplyCurves = true, HiddenSensorGroups = ["CPU", "GPU"], CollapsedSensorDevices = [SensorPollingPlan.CollapseKey(Temp)],
            Controls = [new() { Id = "fan", Enabled = true, CurveId = "mix" }],
            Curves = [new() { Id = "mix", Type = "mix", ChildCurveIds = ["nested", "mix"] },
                new() { Id = "nested", Type = "mix", ChildCurveIds = ["temp"] },
                new() { Id = "temp", Type = "graph", SensorId = Temp.Id }],
        };
        var inventory = Inventory.Append(new HardwareItem { Id = "fan", Backend = "hwmon", Kind = HardwareKind.Control, Name = "Fan" }).ToArray();
        var plan = SensorPollingPlan.Create(inventory, settings);
        Assert.Contains(Temp.Id, plan); Assert.DoesNotContain(GpuPower.Id, plan);
        settings.ApplyCurves = false;
        Assert.DoesNotContain(Temp.Id, SensorPollingPlan.Create(inventory, settings));
        Assert.Contains(Temp.Id, SensorPollingPlan.Create(inventory, settings, homeVisible: true));
    }

    [Fact]
    public void OverlayAndVisibleGpuPageRetainOnlyRequiredHiddenReadings()
    {
        var settings = new AppSettings { HiddenSensorGroups = SensorPollingPlan.GroupOrder.ToList(), HudEnabled = true,
            HudTiles = [new() { SourceId = "nvml:gpu:power:w" }, new() { SourceId = "cpu:temp:c" }, new() { SourceId = "cpu:pptcap:w" }] };
        var plan = SensorPollingPlan.Create(Inventory, settings);
        Assert.Equal(3, plan.Count);
        Assert.Contains(GpuPower.Id, plan); Assert.Contains(Temp.Id, plan); Assert.Contains(Cap.Id, plan);
        Assert.DoesNotContain(Ecc.Id, plan); Assert.DoesNotContain(CpuPower.Id, plan);
        settings.HudEnabled = false;
        Assert.Empty(SensorPollingPlan.Create(Inventory, settings));
        Assert.Equal([GpuPower.Id], SensorPollingPlan.Create(Inventory, settings, gpuPageVisible: true));
    }

    [Fact]
    public void CollapsingPausesPollingUntilExpandedAndDoesNotRemoveInventory()
    {
        var settings = new AppSettings { CollapsedSensorDevices = [SensorPollingPlan.CollapseKey(Ecc)] };
        Assert.DoesNotContain(Ecc.Id, SensorPollingPlan.Create(Inventory, settings));
        Assert.True(SensorPollingPlan.TableVisible(Ecc, settings)); // Keep the collapsed header reachable.
        settings.CollapsedSensorDevices.Clear();
        settings.CollapsedSensorGroups.Add("Memory (ECC)");
        Assert.DoesNotContain(Ecc.Id, SensorPollingPlan.Create(Inventory, settings));
        settings.CollapsedSensorGroups.Clear();
        Assert.Contains(Ecc.Id, SensorPollingPlan.Create(Inventory, settings));
        Assert.Contains(Ecc, Inventory);
    }

    [Fact]
    public void SelectiveNativeReadsDoNotCallTheReaderForPausedChannels()
    {
        using var reader = new CountingReader();
        using var backend = new LibSensorsBackend(reader: reader);
        var items = backend.Discover();
        var readings = new Dictionary<string, double?>();
        backend.ReadInto(readings, new HashSet<string>());
        Assert.Equal(0, reader.Reads); Assert.Empty(readings);
        backend.ReadInto(readings, new HashSet<string> { items[0].Id });
        Assert.Equal(1, reader.Reads); Assert.Single(readings); Assert.Equal(42, readings[items[0].Id]);
    }

    [Fact]
    public void VisibilityChoicesSurviveSaveLoadAndLegacyFilesUseDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "of-visibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json"); var store = new SettingsStore(path);
            store.Save(new AppSettings { HiddenSensorGroups = ["Memory (ECC)"], CollapsedSensorDevices = ["GPU|GPU 1"], CollapsedSensorGroups = ["Motherboard"], ShowSensorStatus = true });
            var loaded = store.Load(); Assert.Equal(["Memory (ECC)"], loaded.HiddenSensorGroups);
            Assert.Equal(["GPU|GPU 1"], loaded.CollapsedSensorDevices); Assert.True(loaded.ShowSensorStatus); Assert.Equal(["Motherboard"], loaded.CollapsedSensorGroups);
            File.WriteAllText(path, "{\"hiddenSensorGroups\":null,\"collapsedSensorDevices\":null}");
            loaded = store.Load(); Assert.Empty(loaded.HiddenSensorGroups); Assert.Empty(loaded.CollapsedSensorDevices); Assert.False(loaded.ShowSensorStatus); Assert.Empty(loaded.CollapsedSensorGroups);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void StaleControlsDoNotKeepHiddenSensorsPolling()
    {
        var settings = new AppSettings { ApplyCurves = true, HiddenSensorGroups = ["CPU"],
            Controls = [new() { Id = "removed-fan", Enabled = true, CurveId = "graph" }],
            Curves = [new() { Id = "graph", Type = "graph", SensorId = Temp.Id }] };
        Assert.DoesNotContain(Temp.Id, SensorPollingPlan.Create(Inventory, settings));
    }

    [Fact]
    public void LegacyCpuOverlayPowerUsesOnlyFixedFallbackReadsWhenNoPowerInventoryExists()
    {
        var settings = new AppSettings { HudEnabled = true, HiddenSensorGroups = SensorPollingPlan.GroupOrder.ToList(),
            HudTiles = [new() { SourceId = "cpu:power:w" }, new() { SourceId = "cpu:pptcap:w" }, new() { SourceId = "stale:arbitrary:path" }] };
        var plan = SensorPollingPlan.Create([Temp], settings);
        Assert.Equal(2, plan.Count);
        Assert.Contains("cpu:power:w", plan); Assert.Contains("cpu:pptcap:w", plan);
    }

    private sealed class CountingReader : ILibSensorsReader
    {
        public int Reads;
        public bool Available => true;
        public string? Error => null;
        public IReadOnlyList<LibSensorChannel> Discover() => [new("test", "/sys/class/hwmon/hwmon0", "temp1", "temp1_input", "Temp", 2, "°C")];
        public double? Read(LibSensorChannel channel) { Reads++; return 42; }
        public void Dispose() { }
    }
}
