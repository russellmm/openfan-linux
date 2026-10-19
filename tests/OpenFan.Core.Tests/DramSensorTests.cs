using OpenFan.Core.Config;
using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public sealed class DramSensorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "of-dram-" + Guid.NewGuid().ToString("N"));

    private LibSensorChannel Hub(int index, int bus, string address = "50", string controller = "AMDI0010:01", string sub = "temp1_input", string label = "temp1")
    {
        var path = Path.Combine(_root, "hwmon", "hwmon" + index);
        var client = Path.Combine(_root, "devices", controller, "i2c-" + bus, $"{bus}-00{address}");
        var physical = Path.Combine(client, "hwmon", "hwmon" + index);
        Directory.CreateDirectory(physical); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateSymbolicLink(path, Path.GetRelativePath(Path.GetDirectoryName(path)!, physical));
        Directory.CreateSymbolicLink(Path.Combine(physical, "device"), "../../../" + Path.GetFileName(client));
        File.WriteAllText(Path.Combine(path, "name"), "spd5118");
        File.WriteAllText(Path.Combine(path, "temp1_input"), "41750");
        return new("spd5118", path, "temp1", sub, label, 2, "°C");
    }

    [Fact]
    public void HubIdentitySurvivesBothHwmonAndAdapterRenumberingAndSeparatesModules()
    {
        var before = Hub(11, 0); var after = Hub(21, 8);
        using var a = new LibSensorsBackend(reader: new Reader([before]));
        using var b = new LibSensorsBackend(reader: new Reader([after, Hub(22, 8, "52"), Hub(23, 9, controller: "AMDI0010:02")]));
        var original = Assert.Single(a.Discover()); var rebooted = b.Discover();
        Assert.Equal(original.Id, rebooted[0].Id); Assert.Equal(original.Name, rebooted[0].Name);
        Assert.Equal(3, rebooted.Select(i => i.Id).Distinct().Count());
        Assert.All(rebooted, i => { Assert.Equal("DDR5 DIMMs", i.Group); Assert.Equal("Memory temperatures", SensorPollingPlan.Group(i)); Assert.False(i.CanSet); });
        Assert.Contains("AMDI0010:01 / 0x50", original.Name); Assert.Contains("physical DIMM slot not yet mapped", original.Details);
    }

    [Fact]
    public void NativeAndFallbackShareIdsAndMissingOrReplacedHubsDoNotBecomeZero()
    {
        var channel = Hub(11, 0); var reader = new Reader([channel]);
        using var native = new LibSensorsBackend(reader: reader); using var fallback = new HwmonBackend(Path.Combine(_root, "hwmon"));
        var item = Assert.Single(native.Discover()); Assert.Equal(item.Id, Assert.Single(fallback.Discover()).Id);
        var values = new Dictionary<string, double?>();
        fallback.ReadInto(values); Assert.Equal(41.75, values[item.Id]);
        Assert.False(fallback.SetPercent(item.Id, 100)); // Measurement ids are never actuators.
        File.Delete(Path.Combine(channel.ChipPath, "temp1_input"));
        fallback.ReadInto(values); Assert.Null(values[item.Id]);
        File.WriteAllText(Path.Combine(channel.ChipPath, "temp1_input"), "49000");
        Directory.Delete(Path.Combine(channel.ChipPath, "device"));
        var other = Path.Combine(_root, "devices", "AMDI0010:02", "i2c-1", "1-0050"); Directory.CreateDirectory(other);
        Directory.CreateSymbolicLink(Path.Combine(channel.ChipPath, "device"), other);
        fallback.ReadInto(values); Assert.Null(values[item.Id]);
        reader.Value = null; native.ReadInto(values); Assert.Null(values[item.Id]);
    }

    [Fact]
    public void HideCollapseAndAliasSurviveReloadAndRediscoveryWithRenumberedHubs()
    {
        var channel = Hub(11, 0); var reader = new Reader([channel]);
        using var backend = new LibSensorsBackend(reader: reader); var item = Assert.Single(backend.Discover());
        var path = Path.Combine(_root, "settings.json");
        var settings = new AppSettings { HiddenSensorGroups = ["Memory (ECC)"], CollapsedSensorGroups = ["Memory temperatures"],
            CollapsedSensorDevices = ["GPU|GPU 1"], SensorAliases = new() { [item.Id] = "My memory" } };
        new SettingsStore(path).Save(settings);
        // A new store and hardware backend model a fresh process and changed kernel enumeration.
        settings = new SettingsStore(path).Load();
        using var rebooted = new LibSensorsBackend(reader: new Reader([Hub(24, 10)]));
        var inventory = rebooted.Discover(); var rediscovered = Assert.Single(inventory);
        Assert.Equal("My memory", settings.SensorAliases[rediscovered.Id]);
        Assert.Equal(["GPU|GPU 1"], settings.CollapsedSensorDevices);
        Assert.True(SensorPollingPlan.TableVisible(rediscovered, settings));
        var values = new Dictionary<string, double?>();
        backend.ReadInto(values, SensorPollingPlan.Create(inventory, settings)); Assert.Equal(0, reader.Reads);
        settings.CollapsedSensorGroups.Clear(); settings.HiddenSensorGroups.Add("Memory temperatures");
        new SettingsStore(path).Save(settings); settings = new SettingsStore(path).Load();
        Assert.False(SensorPollingPlan.TableVisible(rediscovered, settings));
        backend.ReadInto(values, SensorPollingPlan.Create(inventory, settings)); Assert.Equal(0, reader.Reads);
        // Explicitly enabled HUD/curves can keep their own hidden inputs alive.
        settings.HudEnabled = true; settings.HudTiles = [new() { SourceId = rediscovered.Id }];
        backend.ReadInto(values, SensorPollingPlan.Create(inventory, settings)); Assert.Equal(1, reader.Reads);
        settings.HudEnabled = false; settings.HiddenSensorGroups.Remove("Memory temperatures");
        new SettingsStore(path).Save(settings); settings = new SettingsStore(path).Load();
        backend.ReadInto(values, SensorPollingPlan.Create(inventory, settings)); Assert.Equal(2, reader.Reads); Assert.Equal(41.75, values[rediscovered.Id]);
    }

    [Fact]
    public void ConfiguredLabelsAndStatusFlagsKeepModuleIdentityAndUnavailableMetadataIsOptional()
    {
        var measurement = Hub(11, 0, label: "Memory bank");
        var alarm = measurement with { Subfeature = "temp1_max_alarm" };
        using var backend = new LibSensorsBackend(reader: new Reader([measurement, alarm]));
        var items = backend.Discover(); Assert.StartsWith("Memory bank · ", items[0].Name);
        Assert.Equal(items[0].Id + ":max_alarm", items[1].Id); Assert.Equal(HardwareKind.Status, items[1].Kind); Assert.Equal("flag", items[1].Unit);
        var settings = new AppSettings(); Assert.DoesNotContain(items[1].Id, SensorPollingPlan.Create(items, settings));
        Directory.Delete(Path.Combine(measurement.ChipPath, "device"));
        Assert.Equal("hwmon:spd5118:11:temp:1", backend.Discover()[0].Id);
    }

    [Fact]
    public void ThresholdsStaySeparateFromMeasurementsAndMatchNativeAndFallbackIncludingZeroAndNegativeValues()
    {
        var channel = Hub(11, 0);
        var expected = new Dictionary<string, double?> { ["min"] = -5, ["max"] = 55, ["lcrit"] = 0, ["crit"] = 85 };
        foreach (var (suffix, value) in expected)
            File.WriteAllText(Path.Combine(channel.ChipPath, "temp1_" + suffix), ((int)(value!.Value * 1000)).ToString());
        var channels = new[] { channel }.Concat(expected.Keys.Select(s => channel with { Subfeature = "temp1_" + s })).ToArray();
        var reader = new Reader(channels) { Values = expected.ToDictionary(e => "temp1_" + e.Key, e => e.Value) };
        using var native = new LibSensorsBackend(reader: reader);
        using var fallback = new HwmonBackend(Path.Combine(_root, "hwmon"));
        var inventory = native.Discover(); var rawInventory = fallback.Discover();
        var temperature = Assert.Single(inventory.Where(i => i.Kind == HardwareKind.Temperature));
        Assert.Equal(4, inventory.Count(i => i.Kind == HardwareKind.TemperatureLimit));
        Assert.Equal(inventory.Select(i => i.Id).Order(), rawInventory.Select(i => i.Id).Order());
        var nativeValues = new Dictionary<string, double?>(); var rawValues = new Dictionary<string, double?>();
        native.ReadInto(nativeValues); fallback.ReadInto(rawValues);
        foreach (var (suffix, value) in expected)
        {
            var id = DramTemperatureLimits.Id(temperature.Id, suffix);
            Assert.Equal(value, nativeValues[id]); Assert.Equal(value, rawValues[id]);
            Assert.False(inventory.Single(i => i.Id == id).CanSet);
            Assert.False(fallback.SetPercent(id, 100));
        }
        Assert.Equal("Limits: Low -5°C · High 55°C · Critical low 0°C · Critical high 85°C",
            DramTemperatureLimits.Format(temperature.Id, expected.Keys, id => nativeValues.GetValueOrDefault(id)));
        // Disappearing thresholds render missing; they never turn into a fabricated zero.
        File.Delete(Path.Combine(channel.ChipPath, "temp1_crit")); fallback.ReadInto(rawValues);
        Assert.Null(rawValues[DramTemperatureLimits.Id(temperature.Id, "crit")]);
        Assert.EndsWith("Critical high —", DramTemperatureLimits.Format(temperature.Id, expected.Keys, id => rawValues.GetValueOrDefault(id)));
    }

    [Fact]
    public void HiddenOrCollapsedMemoryPausesLimitsWhileHudRequestsOnlyItsActualTemperature()
    {
        var channel = Hub(11, 0);
        var reader = new Reader([channel, channel with { Subfeature = "temp1_max" }, channel with { Subfeature = "temp1_crit" }]);
        using var backend = new LibSensorsBackend(reader: reader); var inventory = backend.Discover();
        var temperature = Assert.Single(inventory.Where(i => i.Kind == HardwareKind.Temperature));
        var settings = new AppSettings { CollapsedSensorGroups = ["Memory temperatures"] };
        var values = new Dictionary<string, double?>();
        backend.ReadInto(values, SensorPollingPlan.Create(inventory, settings)); Assert.Equal(0, reader.Reads);
        settings.CollapsedSensorGroups.Clear(); settings.HiddenSensorGroups.Add("Memory temperatures");
        backend.ReadInto(values, SensorPollingPlan.Create(inventory, settings)); Assert.Equal(0, reader.Reads);
        settings.HudEnabled = true; settings.HudTiles = [new() { SourceId = temperature.Id }];
        backend.ReadInto(values, SensorPollingPlan.Create(inventory, settings)); Assert.Equal(1, reader.Reads); Assert.Single(values);
        settings.HiddenSensorGroups.Clear(); backend.ReadInto(values, SensorPollingPlan.Create(inventory, settings));
        Assert.Equal(4, reader.Reads); Assert.Equal(3, values.Count);
        Assert.Equal("Limits: High —", DramTemperatureLimits.Format(temperature.Id, ["max"], _ => null));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Reader(IReadOnlyList<LibSensorChannel> channels) : ILibSensorsReader
    {
        public bool Available => true; public string? Error => null; public int Reads;
        public double? Value = 41.75;
        public Dictionary<string, double?>? Values;
        public IReadOnlyList<LibSensorChannel> Discover() => channels;
        public double? Read(LibSensorChannel channel) { Reads++; return Values is null ? Value : Values.GetValueOrDefault(channel.Subfeature, Value); }
        public void Dispose() { }
    }
}
