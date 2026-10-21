using OpenFan.Core.Config;

namespace OpenFan.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Calibration_points_and_avoid_flags_survive_json_save_and_reload_per_fan()
    {
        var path = Path.Combine(Path.GetTempPath(), "OpenFanTests", Guid.NewGuid().ToString("N"), "named-profile.json");
        var settings = new AppSettings
        {
            Controls = [new() { Id = "nvml:GPU-1:fan:0", Enabled = true,
                Calibration = [new(30, 1200.5), new(60, 1850.2, true), new(100, 2867)] },
                new() { Id = "nvml:GPU-1:fan:1", Enabled = false, Calibration = [new(30, 1180), new(100, 2800)] }],
        };
        try
        {
            new SettingsStore(path).Save(settings);
            var loaded = new SettingsStore(path).Load();
            loaded.Controls[0].Calibration.Should().Equal(settings.Controls[0].Calibration);
            loaded.Controls[1].Calibration.Should().Equal(settings.Controls[1].Calibration);
            loaded.Controls[0].Enabled.Should().BeTrue();
            loaded.Controls[1].Enabled.Should().BeFalse();
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var point = json.RootElement.GetProperty("controls")[0].GetProperty("calibration")[1];
            point.GetProperty("percent").GetInt32().Should().Be(60);
            point.GetProperty("rpm").GetDouble().Should().Be(1850.2);
            point.GetProperty("avoid").GetBoolean().Should().BeTrue();
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void Round_trips_controls_and_curves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenFanTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            var store = new SettingsStore(path);
            var settings = new AppSettings
            {
                StartAtLogin = true,
                ApplyCurves = true,
                StartupDelaySeconds = 15,
                PowerTargetsWatts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GPU-abc"] = 200,
                },
                Controls =
                [
                    new ControlSettings
                    {
                        Id = "nvml:GPU-1:fan:0",
                        Name = "RTX 6000 Slot 1 Fan 0",
                        Enabled = true,
                        CurveId = "graph-a",
                        PairedTachId = "nvml:GPU-1:tach:0",
                        MinPercent = 30,
                    },
                ],
                Curves =
                [
                    new CurveSettings
                    {
                        Id = "graph-a",
                        Type = "graph",
                        Name = "GPU Temp",
                        SensorId = "nvml:GPU-1:temp:core",
                        Points = [new CurvePointDto(40, 30), new CurvePointDto(80, 100)],
                    },
                ],
            };

            store.Save(settings);
            var loaded = store.Load();
            loaded.StartAtLogin.Should().BeTrue();
            loaded.ApplyCurves.Should().BeTrue();
            loaded.StartupDelaySeconds.Should().Be(15);
            loaded.PowerTargetsWatts.Should().ContainKey("GPU-abc");
            loaded.PowerTargetsWatts["GPU-abc"].Should().Be(200);
            loaded.Controls.Should().ContainSingle(c => c.Id == "nvml:GPU-1:fan:0" && c.MinPercent == 30);
            loaded.Curves.Should().ContainSingle(c => c.Id == "graph-a" && c.Points.Count == 2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_returns_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "OpenFanTests", Guid.NewGuid().ToString("N"), "nope.json");
        var loaded = new SettingsStore(path).Load();
        loaded.Controls.Should().BeEmpty();
        loaded.Curves.Should().BeEmpty();
        loaded.RefreshMs.Should().Be(1000);
    }
}
