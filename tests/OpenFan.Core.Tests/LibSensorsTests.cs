using OpenFan.Core.Config;
using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public sealed class LibSensorsTests
{
    private static LibSensorChannel Channel(string feature = "temp1", string subfeature = "temp1_input", int type = 2, string unit = "°C") =>
        new("nct6799", "/sys/class/hwmon/hwmon10", feature, subfeature, "Configured label", type, unit);

    [Fact]
    public void RetainsLegacyTemperatureAndTachIdsAndUsesLibraryConvertedValues()
    {
        using var reader = new FakeReader([Channel(), Channel("fan1", "fan1_input", 1, "RPM")]);
        using var backend = new LibSensorsBackend(reader: reader);
        var items = backend.Discover(); var values = new Dictionary<string, double?>(); backend.ReadInto(values);
        Assert.Equal("hwmon:nct6799:10:temp:1", items[0].Id); Assert.Equal("hwmon:nct6799:10:fan:1", items[1].Id);
        Assert.Equal("Configured label", items[0].Name); Assert.Equal(0.792, values[items[0].Id]);
        Assert.Equal("libsensors", items[0].Source); Assert.All(items, item => Assert.False(item.CanSet));
    }

    [Theory]
    [InlineData("in0", "in0_input", 0, "V", HardwareKind.Voltage)]
    [InlineData("curr1", "curr1_input", 5, "A", HardwareKind.Current)]
    [InlineData("power1", "power1_input", 3, "W", HardwareKind.Power)]
    [InlineData("energy1", "energy1_input", 4, "J", HardwareKind.Energy)]
    [InlineData("humidity1", "humidity1_input", 6, "%RH", HardwareKind.Humidity)]
    public void DescribesAdditionalMeasurementUnits(string feature, string sub, int type, string unit, HardwareKind expected)
    {
        var item = LibSensorsBackend.Describe(Channel(feature, sub, type, unit))!;
        Assert.Equal(expected, item.Kind); Assert.Equal(unit, item.Unit);
    }

    [Fact]
    public void SeparatesPowerCapsAndStatusFromMeasurementsAndExcludesSettings()
    {
        var power = LibSensorsBackend.Describe(Channel("power1", "power1_input", 3, "W"))!;
        var cap = LibSensorsBackend.Describe(Channel("power1", "power1_cap", 3, "W"))!;
        var alarm = LibSensorsBackend.Describe(Channel("temp1", "temp1_alarm"))!;
        Assert.NotEqual(power.Id, cap.Id); Assert.EndsWith(":cap", cap.Id);
        Assert.Contains("power limit", cap.Name); Assert.Equal(HardwareKind.Status, alarm.Kind);
        Assert.Null(LibSensorsBackend.Describe(Channel("temp1", "temp1_crit")));
        Assert.Null(LibSensorsBackend.Describe(Channel("pwm1", "pwm1", 7, "%")));
    }

    [Fact]
    public void HonorsSourceSettingsAndDoesNotTurnFailedReadsIntoZeros()
    {
        using var reader = new FakeReader([Channel()]); using var backend = new LibSensorsBackend(reader: reader);
        var item = Assert.Single(backend.Discover()); reader.Value = null;
        var values = new Dictionary<string, double?>(); backend.ReadInto(values); Assert.Null(values[item.Id]);
        reader.Value = double.PositiveInfinity; backend.ReadInto(values); Assert.Null(values[item.Id]);
        reader.Value = -9; backend.ReadInto(values); Assert.Equal(-9, values[item.Id]); // Raw display, curve validation is separate.
        backend.Sources = new SourceSettings { DisabledHwmonChips = ["nct6799"] }; Assert.Empty(backend.Discover());
        backend.Sources = new SourceSettings { Hwmon = false }; Assert.Empty(backend.Discover());
    }

    [Fact]
    public void EdacCountersAreReadOnlyAndMissingReadingsStayMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "of-edac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mc0/rank0"));
        try
        {
            var file = Path.Combine(root, "mc0/rank0/dimm_ce_count"); File.WriteAllText(file, "0");
            using var edac = new EdacBackend(root); var item = Assert.Single(edac.Discover());
            Assert.Equal(HardwareKind.Counter, item.Kind); Assert.False(item.CanSet);
            var readings = new Dictionary<string, double?>(); edac.ReadInto(readings); Assert.Equal(0, readings[item.Id]);
            File.Delete(file); edac.ReadInto(readings); Assert.Null(readings[item.Id]);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeReader(IReadOnlyList<LibSensorChannel> channels) : ILibSensorsReader
    {
        public bool Available => true;
        public string? Error => null;
        public double? Value { get; set; } = 0.792;
        public IReadOnlyList<LibSensorChannel> Discover() => channels;
        public double? Read(LibSensorChannel channel) => Value;
        public void Dispose() { }
    }
}
