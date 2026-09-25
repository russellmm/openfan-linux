using System.Text.Json;
using FluentAssertions;
using OpenFan.Core.Config;
using OpenFan.Core.Hud;
using Xunit;

namespace OpenFan.Core.Tests;

/// <summary>
/// Overlay logic that must be right without a display: unit inference, tile formatting, contrast choice
/// on colours the user picks freely, and that the new settings actually round-trip.
/// </summary>
public class HudTests
{
    [Theory]
    [InlineData("cpu:power:w", "W")]
    [InlineData("nvml:GPU-abc:power:w", "W")]
    [InlineData("nvml:GPU-abc:temp:core", "°C")]
    [InlineData("hwmon:k10temp:0:temp:Tctl", "°C")]
    [InlineData("hwmon:nct6799:10:fan:1", "RPM")]   // hwmon ":fan:" is a tachometer, unlike NVML
    [InlineData("nvml:GPU-abc:fan:0", "%")]
    public void Unit_is_inferred_from_source_id(string id, string expected) => HudFormat.UnitFor(id).Should().Be(expected);

    [Fact]
    public void Values_render_whole_and_never_fabricate_zero()
    {
        HudFormat.Value("cpu:power:w", 269.7).Should().Be("270 W");
        HudFormat.Value("cpu:temp:c", 41.2).Should().Be("41 °C");

        // A missing reading must not look like an idle 0 W CPU.
        HudFormat.Value("cpu:power:w", null).Should().Be("—");
        HudFormat.Value("cpu:power:w", double.NaN).Should().Be("—");
    }

    [Theory]
    [InlineData("#FFFFFF", "#141414")]   // white tile → dark text
    [InlineData("#F0A03C", "#141414")]   // accent amber is bright enough for dark text
    [InlineData("#171B1F", "#FFFFFF")]   // near-black tile → light text
    [InlineData("#2C363D", "#FFFFFF")]
    [InlineData("not-a-colour", "#FFFFFF")] // garbage must degrade safely, not throw
    public void Text_colour_stays_readable_on_the_chosen_background(string bg, string expected) =>
        HudTheme.TextColorFor(bg).Should().Be(expected);

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#AABBCC", true)]
    [InlineData("#AB", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Hex_parsing_accepts_only_real_colours(string? hex, bool ok) =>
        HudTheme.TryParse(hex, out _, out _, out _).Should().Be(ok);

    [Fact]
    public void Palette_colours_all_get_a_readable_text_colour()
    {
        foreach (var c in HudTheme.Palette)
        {
            HudTheme.TryParse(c, out _, out _, out _).Should().BeTrue(c);
            HudTheme.TextColorFor(c).Should().NotBe(c, "text must not match its own background");
        }
    }

    [Fact]
    public void Default_seed_covers_cpu_and_every_gpu_power_and_temp()
    {
        var tiles = HudDefaults.Seed(
        [
            new HudDefaults.GpuRef("GPU-1", 0, "NVIDIA RTX PRO 6000"),
            new HudDefaults.GpuRef("GPU-2", 1, "NVIDIA RTX 4090"),
        ]);

        tiles.Select(t => t.SourceId).Should().Contain(["cpu:power:w", "cpu:temp:c"]);
        tiles.Should().Contain(t => t.SourceId == "nvml:GPU-1:power:w" && t.Label == "GPU 1 power");
        tiles.Should().Contain(t => t.SourceId == "nvml:GPU-2:temp:core" && t.Label == "GPU 2 temp");

        // Neighbouring tiles must not blur together; with more GPUs than palette colours a wrap is fine.
        for (var i = 1; i < tiles.Count; i++)
            tiles[i].ColorHex.Should().NotBe(tiles[i - 1].ColorHex, $"tile {i} sits next to an identical colour");
    }

    [Fact]
    public void Overlay_settings_round_trip_and_order_is_the_list_order()
    {
        var s = new AppSettings
        {
            HudEnabled = true,
            HudColumns = 3,
            HudX = 40,
            HudY = 80,
            HudTiles =
            [
                new HudTileSettings { SourceId = "cpu:power:w", Label = "PPT", ColorHex = "#F0A03C" },
                new HudTileSettings { SourceId = "nvml:GPU-1:temp:core", ColorHex = "#B388FF" },
            ],
        };

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;

        back.HudEnabled.Should().BeTrue();
        back.HudColumns.Should().Be(3);
        (back.HudX, back.HudY).Should().Be((40, 80));
        back.HudTiles.Select(t => t.SourceId).Should().ContainInOrder("cpu:power:w", "nvml:GPU-1:temp:core");
        back.HudTiles[0].Label.Should().Be("PPT");
        back.HudTiles[1].ColorHex.Should().Be("#B388FF");
    }

    [Fact]
    public void Unset_geometry_stays_null_rather_than_becoming_the_top_left_corner()
    {
        var json = JsonSerializer.Serialize(new AppSettings());
        json.Should().NotContain("hudX", "null geometry must be omitted, not written as 0");

        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        (back.HudX, back.HudY).Should().Be(((int?)null, null));
        back.HudTiles.Should().BeEmpty();
    }
}
