using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public sealed class BmcSdrSensorTests
{
    [Theory]
    [InlineData("ok", "43 degrees C", 43d)]
    [InlineData("ucr", "90 degrees C", 90d)]
    [InlineData("lcr", "-5 degrees C", -5d)]
    [InlineData("ok", "0 degrees C", 0d)]
    [InlineData("ns", "43 degrees C", null)]
    [InlineData("ok", "No Reading", null)]
    [InlineData("ok", "NaN degrees C", null)]
    [InlineData("ok", "Infinity degrees C", null)]
    [InlineData("ok", "43 RPM", null)]
    public void TemperaturePreservesAlarmedReadingsAndMissingRemainsMissing(string status, string text, double? expected)
    {
        var sensor = BmcSdrSensors.Parse($"DIMMA1_Temp | 04h | {status} | 8.0 | {text}", HardwareKind.Temperature).Should().ContainSingle().Subject;
        sensor.Id.Should().Be("bmc:asus-wrx90:sdr:04");
        sensor.Value.Should().Be(expected); sensor.Unit.Should().Be("°C");
    }

    [Fact]
    public void SdrIdentitySurvivesReorderingAndUnavailableSensorsRetainInventory()
    {
        var first = "DIMMA1_Temp | 04h | ok | 8.0 | 43 degrees C";
        var second = "DIMMB1_Temp | 05h | ns | 8.0 | No Reading";
        var before = BmcSdrSensors.Parse(first + "\n" + second, HardwareKind.Temperature);
        var after = BmcSdrSensors.Parse(second + "\n" + first, HardwareKind.Temperature);
        after.Should().BeEquivalentTo(before);
        BmcSdrSensors.Parse("bad line\nX | zzh | ok | 8.0 | 30 degrees C", HardwareKind.Temperature).Should().BeEmpty();
    }

    [Fact]
    public void BmcHasOwnGroupAndHiddenCalibrationRetainsCanonicalTach()
    {
        HardwareItem[] inventory = [new() { Id = "bmc:asus-wrx90:fan:2", Kind = HardwareKind.Control, Name = "Fan", Backend = "bmc" },
            new() { Id = "bmc:asus-wrx90:tach:2", Kind = HardwareKind.Tach, Name = "RPM", Backend = "bmc" }];
        var settings = new OpenFan.Core.Config.AppSettings { HiddenSensorGroups = SensorPollingPlan.GroupOrder.ToList() };
        SensorPollingPlan.Group(inventory[0]).Should().Be("BMC");
        SensorPollingPlan.GroupOrder.Should().Contain("BMC");
        SensorPollingPlan.Create(inventory, settings, homeVisible: true).Should().BeEquivalentTo(inventory.Select(i => i.Id));
        SensorPollingPlan.Create(inventory, settings, calibrationControlId: inventory[0].Id).Should().BeEquivalentTo(inventory.Select(i => i.Id));
    }
}
