using OpenFan.Core.Config;
using OpenFan.Core.Hardware;

namespace OpenFan.Core.Tests;

public sealed class CurveSensorSelectionTests
{
    private static readonly HardwareItem Cpu = new() { Id = "hwmon:k10temp:6:temp:1", Kind = HardwareKind.Temperature, Name = "Tctl", Group = "CPU", Backend = "hwmon" };
    private static readonly HardwareItem Board = new() { Id = "hwmon:asusec:3:temp:1", Kind = HardwareKind.Temperature, Name = "CPU Package", Group = "ASUS EC", Backend = "hwmon" };
    private static readonly HardwareItem Tach = new() { Id = "hwmon:asusec:3:fan:1", Kind = HardwareKind.Tach, Name = "M.2", Group = "ASUS EC", Backend = "hwmon" };
    private static readonly HardwareItem Limit = new() { Id = "temp:critical", Kind = HardwareKind.TemperatureLimit, Name = "Critical", Backend = "hwmon" };
    private static readonly HardwareItem[] Inventory = [Cpu, Board, Tach, Limit];

    [Fact]
    public void DefaultsListNothingEvenWithExistingCurveAssignments()
    {
        var settings = new AppSettings { Curves = [new() { Id = "cpu", Type = "graph", SensorId = Cpu.Id }] };
        CurveSensorSelection.Available(Inventory, settings).Should().BeEmpty();
        settings.Curves[0].SensorId.Should().Be(Cpu.Id);
    }

    [Fact]
    public void OnlySelectedTemperaturesFromSharedInventoryAreListedOnce()
    {
        var settings = new AppSettings { CurveSensorIds = [Board.Id, Board.Id, Tach.Id, Limit.Id, "missing"] };
        CurveSensorSelection.Available(Inventory, settings).Should().ContainSingle().Which.Should().BeSameAs(Board);
        CurveSensorSelection.CanUse(Tach).Should().BeFalse();
        CurveSensorSelection.CanUse(Limit).Should().BeFalse();
    }

    [Fact]
    public void TableVisibilityDoesNotOverrideExplicitSourceSelections()
    {
        var settings = new AppSettings { CurveSensorIds = [Board.Id], HiddenSensorGroups = ["Chipset / ASUS EC"],
            CollapsedSensorGroups = ["Chipset / ASUS EC"], CollapsedSensorDevices = [SensorPollingPlan.CollapseKey(Board)] };
        CurveSensorSelection.Available(Inventory, settings).Should().Equal(Board);
        SensorPollingPlan.Create(Inventory, settings, homeVisible: true).Should().Contain(Board.Id);
        SensorPollingPlan.Create(Inventory, settings).Should().NotContain(Board.Id);
        SensorPollingPlan.Create(Inventory, settings, curveEditorVisible: true).Should().Contain(Board.Id);
    }

    [Fact]
    public void UncheckingAssignedSourceDoesNotUnbindOrStopItsActiveCurve()
    {
        var settings = new AppSettings { ApplyCurves = true, CurveSensorIds = [Cpu.Id],
            HiddenSensorGroups = SensorPollingPlan.GroupOrder.ToList(),
            Curves = [new() { Id = "cpu", Type = "graph", SensorId = Cpu.Id }],
            Controls = [new() { Id = "fan", Enabled = true, CurveId = "cpu" }] };
        var inventory = Inventory.Append(new HardwareItem { Id = "fan", Kind = HardwareKind.Control, Name = "Fan", Backend = "hwmon" }).ToArray();
        settings.CurveSensorIds.Clear();
        CurveSensorSelection.Available(inventory, settings).Should().BeEmpty();
        settings.Curves[0].SensorId.Should().Be(Cpu.Id);
        SensorPollingPlan.Create(inventory, settings).Should().Contain(Cpu.Id);
        settings.ApplyCurves = false;
        SensorPollingPlan.Create(inventory, settings, homeVisible: true).Should().Contain(Cpu.Id);
    }

    [Fact]
    public void DisconnectedSelectionsRemainSavedAndReappearWhenPresent()
    {
        var settings = new AppSettings { CurveSensorIds = [Board.Id] };
        CurveSensorSelection.Available([Cpu], settings).Should().BeEmpty();
        settings.CurveSensorIds.Should().Equal(Board.Id);
        CurveSensorSelection.Available(Inventory, settings).Should().Equal(Board);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"curveSensorIds\":null}")]
    public void LegacyAndNullSelectionsDefaultUncheckedWithoutEditingCurves(string json)
    {
        WithStore((path, store) =>
        {
            File.WriteAllText(path, json);
            store.Load().CurveSensorIds.Should().BeEmpty();
        });
    }

    [Fact]
    public void ProfileRoundTripPreservesSelectionsAliasesAndUncheckedAssignments()
    {
        WithStore((_, store) =>
        {
            store.Save(new AppSettings { CurveSensorIds = [Board.Id], SensorAliases = new() { [Board.Id] = "Case" },
                Curves = [new() { Id = "cpu", Type = "graph", SensorId = Cpu.Id }] });
            var loaded = store.Load();
            loaded.CurveSensorIds.Should().Equal(Board.Id);
            loaded.SensorAliases[Board.Id].Should().Be("Case");
            loaded.Curves[0].SensorId.Should().Be(Cpu.Id);
            CurveSensorSelection.Available(Inventory, loaded).Should().Equal(Board);
        });
    }

    private static void WithStore(Action<string, SettingsStore> check)
    {
        var root = Directory.CreateTempSubdirectory("openfan-curve-sensors-").FullName;
        try { var path = Path.Combine(root, "profile.json"); check(path, new SettingsStore(path)); }
        finally { Directory.Delete(root, true); }
    }
}
